# nullable enable

using System;
using System.Collections;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;
using RealityLog.Camera;
using RealityLog.Common;

namespace RealityLog.Network
{
    /// <summary>
    /// Periodically uploads camera snapshots to Cloudflare during recording.
    /// Runs independently from CloudRelayService heartbeat to avoid blocking it.
    /// </summary>
    public class SnapshotUploadService : MonoBehaviour
    {
        private const string API_BASE = "https://fielddata-pro-api.sarthak-46e.workers.dev";
        // Externalised — see AuthTokenManager.DefaultRelayKeyPath. Shared with
        // CloudRelayService so rotating the file rotates both.
        private static string? _apiKey;
        private const float UPLOAD_INTERVAL = 4f;
        private const int JPEG_QUALITY = 15;
        // Back off when the backend or network keeps failing, so we don't
        // hammer the radio on a device that can't reach the relay right now.
        private const int BACKOFF_AFTER_CONSECUTIVE_FAILS = 5;
        private const float BACKOFF_INTERVAL = 30f;

        private string deviceId = "";
        private RecordingManager? recordingManager;
        private ImageReaderSurfaceProvider? imageReader;
        private bool isRunning = false;
        private bool isUploading = false;
        private int uploadCount = 0;
        private int failCount = 0;
        private int consecutiveFails = 0;
        private string lastError = "";

        /// <summary>Diagnostic string for heartbeat reporting.</summary>
        public static string DiagStatus { get; private set; } = "not_started";

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            var go = new GameObject("SnapshotUploadService");
            go.AddComponent<SnapshotUploadService>();
            DontDestroyOnLoad(go);
        }

        private void Awake()
        {
            deviceId = SystemInfo.deviceUniqueIdentifier;
        }

        private IEnumerator Start()
        {
            yield return new WaitForSeconds(3f);

            recordingManager = FindFirstObjectByType<RecordingManager>();
            imageReader = FindFirstObjectByType<ImageReaderSurfaceProvider>();

            if (recordingManager == null)
            {
                DiagStatus = "err:no_RecordingManager";
                Debug.LogWarning($"[{Constants.LOG_TAG}] SnapshotUpload: RecordingManager not found");
                yield break;
            }

            if (imageReader == null)
            {
                DiagStatus = "err:no_ImageReader";
                Debug.LogWarning($"[{Constants.LOG_TAG}] SnapshotUpload: ImageReaderSurfaceProvider not found — snapshots disabled");
                yield break;
            }

            isRunning = true;
            DiagStatus = "running";
            Debug.Log($"[{Constants.LOG_TAG}] SnapshotUpload: Started — uploading every {UPLOAD_INTERVAL}s during recording");
            StartCoroutine(UploadLoop());
        }

        private void OnDestroy()
        {
            isRunning = false;
        }

        private IEnumerator UploadLoop()
        {
            while (isRunning)
            {
                if (recordingManager != null && recordingManager.IsRecording && !isUploading)
                {
                    // Upload is wrapped in its own try/catch — this yield is
                    // intentionally outside try so the coroutine itself can never
                    // throw and kill the service.
                    yield return UploadSnapshot();
                }
                // Back off hard when the relay is unreachable — keeps radio quiet.
                var delay = consecutiveFails >= BACKOFF_AFTER_CONSECUTIVE_FAILS
                    ? BACKOFF_INTERVAL
                    : UPLOAD_INTERVAL;
                yield return new WaitForSeconds(delay);
            }
        }

        private IEnumerator UploadSnapshot()
        {
            if (imageReader == null) yield break;

            // Capturing the JPEG crosses JNI into Kotlin — any exception here
            // must be contained so the service never kills the VR app.
            byte[]? jpegBytes = null;
            try
            {
                jpegBytes = imageReader.GetSnapshotJpeg(JPEG_QUALITY);
            }
            catch (Exception ex)
            {
                failCount++;
                consecutiveFails++;
                lastError = $"jpeg_ex:{ex.Message}";
                DiagStatus = $"running:{lastError}(ok={uploadCount},fail={failCount})";
                yield break;
            }

            if (jpegBytes == null || jpegBytes.Length == 0)
            {
                DiagStatus = $"running:jpeg_null(ok={uploadCount},fail={failCount})";
                yield break;
            }

            isUploading = true;

            UnityWebRequest? request = null;
            try
            {
                var url = $"{API_BASE}/api/v1/relay/devices/{deviceId}/snapshot";
                request = new UnityWebRequest(url, "PUT");
                request.SetRequestHeader("Content-Type", "image/jpeg");
                var apiKey = ResolveApiKey();
                if (apiKey == null)
                {
                    DiagStatus = "err:relay_key_missing";
                    request.Dispose();
                    request = null;
                    isUploading = false;
                    yield break;
                }
                request.SetRequestHeader("X-API-Key", apiKey);
                request.uploadHandler = new UploadHandlerRaw(jpegBytes);
                request.downloadHandler = new DownloadHandlerBuffer();
                request.timeout = 5;
            }
            catch (Exception ex)
            {
                failCount++;
                consecutiveFails++;
                lastError = $"req_ex:{ex.Message}";
                DiagStatus = $"running:{lastError}(ok={uploadCount},fail={failCount})";
                request?.Dispose();
                isUploading = false;
                yield break;
            }

            yield return request.SendWebRequest();

            try
            {
                if (request.result != UnityWebRequest.Result.Success)
                {
                    failCount++;
                    consecutiveFails++;
                    lastError = request.error ?? "unknown";
                    DiagStatus = $"running:upload_err={lastError}(ok={uploadCount},fail={failCount})";
                    Debug.LogWarning($"[{Constants.LOG_TAG}] SnapshotUpload: Failed: {request.error}");
                }
                else
                {
                    uploadCount++;
                    consecutiveFails = 0;
                    DiagStatus = $"running:ok(ok={uploadCount},fail={failCount},size={jpegBytes.Length})";
                }
            }
            catch (Exception ex)
            {
                failCount++;
                consecutiveFails++;
                lastError = $"post_ex:{ex.Message}";
                DiagStatus = $"running:{lastError}(ok={uploadCount},fail={failCount})";
            }
            finally
            {
                request.Dispose();
                isUploading = false;
            }
        }

        private static string? ResolveApiKey()
        {
            if (_apiKey != null) return _apiKey;
            _apiKey = AuthTokenManager.LoadRelayKey(AuthTokenManager.DefaultRelayKeyPath);
            if (_apiKey != null) return _apiKey;
            var sandbox = Path.Combine(UnityEngine.Application.persistentDataPath, "fielddata", "relay_api_key.txt");
            _apiKey = AuthTokenManager.LoadRelayKey(sandbox);
            return _apiKey;
        }
    }
}
