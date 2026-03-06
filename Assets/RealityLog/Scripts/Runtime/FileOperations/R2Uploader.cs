# nullable enable

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.Networking;
using RealityLog.Common;

namespace RealityLog.FileOperations
{
    /// <summary>
    /// Uploads recording directories to Cloudflare R2 via presigned URLs.
    /// Offline-first: persists upload state per recording, syncs a manifest of all
    /// pending recordings to the server before uploading, and waits for WiFi gracefully.
    /// Wire RecordingManager.onRecordingSaved → OnRecordingSaved in the Inspector.
    /// </summary>
    public class R2Uploader : MonoBehaviour
    {
        [Header("R2 Configuration")]
        [Tooltip("URL of the Cloudflare Worker presign endpoint")]
        [SerializeField] private string presignEndpoint = "";

        [Tooltip("Optional Bearer token for presign endpoint auth")]
        [SerializeField] private string authToken = "";

        [Header("Behavior")]
        [Tooltip("Automatically upload when a recording is saved")]
        [SerializeField] private bool autoUploadOnSave = true;

        [Tooltip("Delete the ZIP file after successful upload")]
        [SerializeField] private bool deleteZipAfterUpload = true;

        [Tooltip("Maximum number of retry attempts per upload (network errors only, not offline waits)")]
        [SerializeField] private int maxRetries = 3;

        [Tooltip("Initial delay in seconds before first retry (doubles each attempt)")]
        [SerializeField] private float initialRetryDelaySec = 2f;

        [Tooltip("Re-queue failed/interrupted uploads on startup")]
        [SerializeField] private bool retryFailedOnStartup = true;

        [Tooltip("How often to check for WiFi when offline (seconds)")]
        [SerializeField] private float wifiPollIntervalSec = 15f;

        [Header("Events")]
        public UnityEvent<string, float> OnUploadProgress = default!;
        public UnityEvent<string, bool, string> OnUploadComplete = default!;

        private readonly Queue<string> uploadQueue = new();
        private bool isProcessing;
        private string deviceId = "";

        [Serializable]
        private class PresignResponse
        {
            public string upload_url = "";
            public string key = "";
            public string error = "";
        }

        [Serializable]
        private class ManifestEntry
        {
            public string directoryName = "";
            public string status = "";
            public long sizeBytes;
            public int fileCount;
            public string errorMessage = "";
        }

        [Serializable]
        private class DeviceManifest
        {
            public string deviceId = "";
            public string deviceModel = "";
            public string timestamp = "";
            public List<ManifestEntry> recordings = new();
        }

        private void Start()
        {
            deviceId = SystemInfo.deviceUniqueIdentifier;
            Debug.Log($"[{Constants.LOG_TAG}] R2Uploader: Device ID = {deviceId}");

            if (retryFailedOnStartup)
                StartCoroutine(StartupSequence());
        }

        private IEnumerator StartupSequence()
        {
            // Wait a frame for everything else to initialize
            yield return null;

            // Scan for interrupted uploads and re-queue them
            string basePath = Application.persistentDataPath;
            if (!Directory.Exists(basePath)) yield break;

            int requeued = 0;
            foreach (var dir in Directory.GetDirectories(basePath))
            {
                var status = UploadStatus.Read(dir);
                if (status == null) continue;

                if (status.status is "compressing" or "uploading" or "pending" or "failed")
                {
                    string dirName = Path.GetFileName(dir);
                    Debug.Log($"[{Constants.LOG_TAG}] R2Uploader: Re-queuing '{dirName}' (was: {status.status})");
                    uploadQueue.Enqueue(dirName);
                    requeued++;
                }
            }

            if (requeued > 0)
            {
                Debug.Log($"[{Constants.LOG_TAG}] R2Uploader: {requeued} recording(s) queued for upload");
                if (!isProcessing)
                    StartCoroutine(ProcessQueue());
            }
        }

        /// <summary>
        /// Called by RecordingManager.onRecordingSaved UnityEvent (dynamic string).
        /// </summary>
        public void OnRecordingSaved(string directoryName)
        {
            // Always write a pending status so the recording is tracked locally
            // even if we can't upload right now
            string fullPath = Path.Combine(Application.persistentDataPath, directoryName);
            if (Directory.Exists(fullPath))
            {
                var existing = UploadStatus.Read(fullPath);
                if (existing == null)
                {
                    var fresh = new UploadStatus { status = "pending" };
                    UploadStatus.Write(fullPath, fresh);
                }
            }

            if (!autoUploadOnSave) return;
            Enqueue(directoryName);
        }

        /// <summary>
        /// Manually enqueue a recording directory for upload.
        /// </summary>
        public void Enqueue(string directoryName)
        {
            string fullPath = Path.Combine(Application.persistentDataPath, directoryName);
            if (!Directory.Exists(fullPath))
            {
                Debug.LogWarning($"[{Constants.LOG_TAG}] R2Uploader: Directory not found: {directoryName}");
                OnUploadComplete?.Invoke(directoryName, false, "Directory not found");
                return;
            }

            Debug.Log($"[{Constants.LOG_TAG}] R2Uploader: Enqueued '{directoryName}' for upload");
            uploadQueue.Enqueue(directoryName);

            if (!isProcessing)
                StartCoroutine(ProcessQueue());
        }

        private IEnumerator ProcessQueue()
        {
            isProcessing = true;
            bool originalRunInBackground = Application.runInBackground;
            Application.runInBackground = true;

            // Sync manifest before starting uploads — tells the server what this device has
            yield return StartCoroutine(SyncManifest());

            while (uploadQueue.Count > 0)
            {
                // Wait for WiFi before attempting each upload
                yield return StartCoroutine(WaitForConnectivity());

                string dirName = uploadQueue.Dequeue();
                yield return StartCoroutine(UploadRecording(dirName));
            }

            // Final manifest sync to update server with completed states
            yield return StartCoroutine(SyncManifest());

            Application.runInBackground = originalRunInBackground;
            isProcessing = false;
        }

        /// <summary>
        /// Polls until the device has network connectivity. Does not consume retry budget.
        /// </summary>
        private IEnumerator WaitForConnectivity()
        {
            while (Application.internetReachability == NetworkReachability.NotReachable)
            {
                Debug.Log($"[{Constants.LOG_TAG}] R2Uploader: No network, checking again in {wifiPollIntervalSec}s...");
                yield return new WaitForSeconds(wifiPollIntervalSec);
            }
        }

        /// <summary>
        /// Collects status of all recordings on device and POSTs a manifest to the server.
        /// This lets the server know what data is stuck on this device.
        /// </summary>
        private IEnumerator SyncManifest()
        {
            if (string.IsNullOrEmpty(presignEndpoint)) yield break;
            if (Application.internetReachability == NetworkReachability.NotReachable) yield break;

            var manifest = new DeviceManifest
            {
                deviceId = deviceId,
                deviceModel = SystemInfo.deviceModel,
                timestamp = DateTime.UtcNow.ToString("o")
            };

            string basePath = Application.persistentDataPath;
            foreach (var dir in Directory.GetDirectories(basePath))
            {
                var status = UploadStatus.Read(dir);
                if (status == null) continue;

                // Skip already-uploaded recordings from the manifest
                if (status.status == "uploaded") continue;

                int fileCount = 0;
                try { fileCount = Directory.GetFiles(dir, "*", SearchOption.AllDirectories).Length; }
                catch { /* ignore */ }

                manifest.recordings.Add(new ManifestEntry
                {
                    directoryName = Path.GetFileName(dir),
                    status = status.status,
                    sizeBytes = status.sizeBytes,
                    fileCount = fileCount,
                    errorMessage = status.errorMessage
                });
            }

            string json = JsonUtility.ToJson(manifest);
            string manifestUrl = presignEndpoint.TrimEnd('/') + "/manifest";

            using var req = new UnityWebRequest(manifestUrl, UnityWebRequest.kHttpVerbPOST);
            req.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(json));
            req.downloadHandler = new DownloadHandlerBuffer();
            req.SetRequestHeader("Content-Type", "application/json");
            if (!string.IsNullOrEmpty(authToken))
                req.SetRequestHeader("Authorization", $"Bearer {authToken}");

            yield return req.SendWebRequest();

            if (req.result == UnityWebRequest.Result.Success)
            {
                Debug.Log($"[{Constants.LOG_TAG}] R2Uploader: Manifest synced ({manifest.recordings.Count} pending recordings)");
            }
            else
            {
                // Non-fatal — upload can proceed without manifest sync
                Debug.LogWarning($"[{Constants.LOG_TAG}] R2Uploader: Manifest sync failed: {req.error}");
            }
        }

        private IEnumerator UploadRecording(string directoryName)
        {
            string sourcePath = Path.Combine(Application.persistentDataPath, directoryName);
            string zipPath = Path.Combine(Application.persistentDataPath, $"{directoryName}.zip");
            string zipFileName = $"{directoryName}.zip";

            if (!Directory.Exists(sourcePath))
            {
                Debug.LogWarning($"[{Constants.LOG_TAG}] R2Uploader: Directory gone: {directoryName}");
                yield break;
            }

            // --- Phase 1: Compress (progress 0 → 0.5) ---
            var status = UploadStatus.Read(sourcePath) ?? new UploadStatus();

            // Skip compression if ZIP already exists (crash recovery)
            if (!File.Exists(zipPath))
            {
                status.status = "compressing";
                UploadStatus.Write(sourcePath, status);

                OnUploadProgress?.Invoke(directoryName, 0f);

                ZipHelper.CompressionProgress compProgress;
                System.Threading.Tasks.Task compTask;
                try
                {
                    (compTask, compProgress) = ZipHelper.CompressDirectoryAsync(sourcePath, zipPath);
                }
                catch (Exception e)
                {
                    FailUpload(sourcePath, directoryName, status, $"Compression setup error: {e.Message}");
                    yield break;
                }

                while (!compProgress.IsDone)
                {
                    float p = compProgress.TotalFiles > 0
                        ? (float)compProgress.ProcessedFiles / compProgress.TotalFiles * 0.5f
                        : 0f;
                    OnUploadProgress?.Invoke(directoryName, p);
                    yield return null;
                }

                if (compProgress.Exception != null)
                {
                    FailUpload(sourcePath, directoryName, status, $"Compression failed: {compProgress.Exception.Message}");
                    yield break;
                }
            }

            OnUploadProgress?.Invoke(directoryName, 0.5f);

            // --- Phase 2: Presign + Upload (progress 0.5 → 1.0) ---
            status.status = "uploading";
            status.sizeBytes = new FileInfo(zipPath).Length;
            UploadStatus.Write(sourcePath, status);

            int attempt = 0;
            bool uploaded = false;

            while (attempt <= maxRetries && !uploaded)
            {
                if (attempt > 0)
                {
                    float delay = initialRetryDelaySec * Mathf.Pow(2, attempt - 1);
                    Debug.Log($"[{Constants.LOG_TAG}] R2Uploader: Retry {attempt}/{maxRetries} for '{directoryName}' in {delay}s");
                    yield return new WaitForSeconds(delay);
                }

                // Wait for connectivity — doesn't consume retry budget
                yield return StartCoroutine(WaitForConnectivity());

                // Fetch presigned URL
                string presignUrl = $"{presignEndpoint}?filename={UnityWebRequest.EscapeURL(zipFileName)}";
                using var presignReq = UnityWebRequest.Get(presignUrl);
                if (!string.IsNullOrEmpty(authToken))
                    presignReq.SetRequestHeader("Authorization", $"Bearer {authToken}");

                yield return presignReq.SendWebRequest();

                if (presignReq.result != UnityWebRequest.Result.Success)
                {
                    Debug.LogWarning($"[{Constants.LOG_TAG}] R2Uploader: Presign failed: {presignReq.error}");
                    attempt++;
                    continue;
                }

                PresignResponse? presignResp;
                try
                {
                    presignResp = JsonUtility.FromJson<PresignResponse>(presignReq.downloadHandler.text);
                }
                catch
                {
                    Debug.LogWarning($"[{Constants.LOG_TAG}] R2Uploader: Invalid presign JSON");
                    attempt++;
                    continue;
                }

                if (presignResp == null || string.IsNullOrEmpty(presignResp.upload_url))
                {
                    string err = presignResp?.error ?? "empty upload_url";
                    Debug.LogWarning($"[{Constants.LOG_TAG}] R2Uploader: Presign error: {err}");
                    attempt++;
                    continue;
                }

                status.r2Key = presignResp.key;
                UploadStatus.Write(sourcePath, status);

                // Upload via PUT with UploadHandlerFile (streams from disk, no RAM copy)
                using var uploadReq = new UnityWebRequest(presignResp.upload_url, UnityWebRequest.kHttpVerbPUT);
                uploadReq.uploadHandler = new UploadHandlerFile(zipPath);
                uploadReq.SetRequestHeader("Content-Type", "application/zip");

                var op = uploadReq.SendWebRequest();

                while (!op.isDone)
                {
                    float uploadFraction = uploadReq.uploadProgress * 0.5f;
                    OnUploadProgress?.Invoke(directoryName, 0.5f + uploadFraction);
                    yield return null;
                }

                if (uploadReq.result == UnityWebRequest.Result.Success)
                {
                    uploaded = true;
                }
                else
                {
                    Debug.LogWarning($"[{Constants.LOG_TAG}] R2Uploader: Upload failed ({uploadReq.responseCode}): {uploadReq.error}");
                    attempt++;
                }
            }

            if (uploaded)
            {
                status.status = "uploaded";
                status.uploadedAt = DateTime.UtcNow.ToString("o");
                UploadStatus.Write(sourcePath, status);

                if (deleteZipAfterUpload && File.Exists(zipPath))
                    File.Delete(zipPath);

                OnUploadProgress?.Invoke(directoryName, 1f);
                OnUploadComplete?.Invoke(directoryName, true, $"Uploaded to {status.r2Key}");
                Debug.Log($"[{Constants.LOG_TAG}] R2Uploader: Successfully uploaded '{directoryName}' → {status.r2Key}");
            }
            else
            {
                FailUpload(sourcePath, directoryName, status, $"Upload failed after {maxRetries} retries");
            }
        }

        private void FailUpload(string sourcePath, string directoryName, UploadStatus status, string message)
        {
            status.status = "failed";
            status.retryCount++;
            status.errorMessage = message;
            UploadStatus.Write(sourcePath, status);

            OnUploadComplete?.Invoke(directoryName, false, message);
            Debug.LogError($"[{Constants.LOG_TAG}] R2Uploader: {message} for '{directoryName}'");
        }
    }
}
