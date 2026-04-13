# nullable enable

using System;
using System.Collections;
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
        private const string API_KEY = "fielddata-pro-2024";
        private const float UPLOAD_INTERVAL = 2f;
        private const int JPEG_QUALITY = 15;

        private string deviceId = "";
        private RecordingManager? recordingManager;
        private ImageReaderSurfaceProvider? imageReader;
        private bool isRunning = false;
        private bool isUploading = false;
        private int uploadCount = 0;
        private int failCount = 0;
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
                    yield return UploadSnapshot();
                }
                yield return new WaitForSeconds(UPLOAD_INTERVAL);
            }
        }

        private IEnumerator UploadSnapshot()
        {
            if (imageReader == null) yield break;

            byte[]? jpegBytes = imageReader.GetSnapshotJpeg(JPEG_QUALITY);
            if (jpegBytes == null || jpegBytes.Length == 0)
            {
                DiagStatus = $"running:jpeg_null(ok={uploadCount},fail={failCount})";
                yield break;
            }

            isUploading = true;

            var url = $"{API_BASE}/api/v1/relay/devices/{deviceId}/snapshot";
            var request = new UnityWebRequest(url, "PUT");
            request.SetRequestHeader("Content-Type", "image/jpeg");
            request.SetRequestHeader("X-API-Key", API_KEY);
            request.uploadHandler = new UploadHandlerRaw(jpegBytes);
            request.downloadHandler = new DownloadHandlerBuffer();
            request.timeout = 5;

            yield return request.SendWebRequest();

            if (request.result != UnityWebRequest.Result.Success)
            {
                failCount++;
                lastError = request.error ?? "unknown";
                DiagStatus = $"running:upload_err={lastError}(ok={uploadCount},fail={failCount})";
                Debug.LogWarning($"[{Constants.LOG_TAG}] SnapshotUpload: Failed: {request.error}");
            }
            else
            {
                uploadCount++;
                DiagStatus = $"running:ok(ok={uploadCount},fail={failCount},size={jpegBytes.Length})";
            }

            request.Dispose();
            isUploading = false;
        }
    }
}
