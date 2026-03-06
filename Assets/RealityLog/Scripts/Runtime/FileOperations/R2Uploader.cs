# nullable enable

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.Networking;
using RealityLog.Common;

namespace RealityLog.FileOperations
{
    /// <summary>
    /// Uploads recording directories to Cloudflare R2 via presigned URLs.
    /// Compresses to ZIP, fetches a presigned PUT URL from a Worker, then streams the upload.
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

        [Tooltip("Maximum number of retry attempts per upload")]
        [SerializeField] private int maxRetries = 3;

        [Tooltip("Initial delay in seconds before first retry (doubles each attempt)")]
        [SerializeField] private float initialRetryDelaySec = 2f;

        [Tooltip("Re-queue failed/interrupted uploads on startup")]
        [SerializeField] private bool retryFailedOnStartup = true;

        [Header("Events")]
        public UnityEvent<string, float> OnUploadProgress = default!;
        public UnityEvent<string, bool, string> OnUploadComplete = default!;

        private readonly Queue<string> uploadQueue = new();
        private bool isProcessing;

        [Serializable]
        private class PresignResponse
        {
            public string upload_url = "";
            public string key = "";
            public string error = "";
        }

        private void Start()
        {
            if (retryFailedOnStartup)
                StartCoroutine(RetryInterruptedUploads());
        }

        /// <summary>
        /// Called by RecordingManager.onRecordingSaved UnityEvent (dynamic string).
        /// </summary>
        public void OnRecordingSaved(string directoryName)
        {
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

            while (uploadQueue.Count > 0)
            {
                string dirName = uploadQueue.Dequeue();
                yield return StartCoroutine(UploadRecording(dirName));
            }

            Application.runInBackground = originalRunInBackground;
            isProcessing = false;
        }

        private IEnumerator UploadRecording(string directoryName)
        {
            string sourcePath = Path.Combine(Application.persistentDataPath, directoryName);
            string zipPath = Path.Combine(Application.persistentDataPath, $"{directoryName}.zip");
            string zipFileName = $"{directoryName}.zip";

            // --- Phase 1: Compress (progress 0 → 0.5) ---
            var status = UploadStatus.Read(sourcePath) ?? new UploadStatus();

            // Skip compression if ZIP already exists (crash recovery)
            bool needsCompression = !File.Exists(zipPath);
            if (needsCompression)
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

                if (Application.internetReachability == NetworkReachability.NotReachable)
                {
                    Debug.LogWarning($"[{Constants.LOG_TAG}] R2Uploader: No internet, waiting...");
                    yield return new WaitForSeconds(initialRetryDelaySec);
                    attempt++;
                    continue;
                }

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

                // Upload via PUT with UploadHandlerFile (streams from disk)
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
            status.errorMessage = message;
            UploadStatus.Write(sourcePath, status);

            OnUploadComplete?.Invoke(directoryName, false, message);
            Debug.LogError($"[{Constants.LOG_TAG}] R2Uploader: {message} for '{directoryName}'");
        }

        private IEnumerator RetryInterruptedUploads()
        {
            // Wait a frame for everything else to initialize
            yield return null;

            string basePath = Application.persistentDataPath;
            if (!Directory.Exists(basePath)) yield break;

            foreach (var dir in Directory.GetDirectories(basePath))
            {
                var status = UploadStatus.Read(dir);
                if (status == null) continue;

                if (status.status == "compressing" || status.status == "uploading" || status.status == "failed")
                {
                    string dirName = Path.GetFileName(dir);
                    Debug.Log($"[{Constants.LOG_TAG}] R2Uploader: Re-queuing interrupted upload: '{dirName}' (was: {status.status})");
                    Enqueue(dirName);
                }
            }
        }
    }
}
