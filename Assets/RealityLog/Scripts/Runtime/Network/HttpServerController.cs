# nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using UnityEngine;
using RealityLog.Common;
using RealityLog.FileOperations;
using RealityLog.UI;

namespace RealityLog.Network
{
    /// <summary>
    /// MonoBehaviour that starts the embedded HTTP server and routes requests
    /// to RecordingManager, RecordingListManager, and RecordingOperations.
    /// Implements the Quest HTTP API contract for FieldData Pro phone control.
    /// Auto-bootstraps at runtime — no need to manually add to the scene.
    /// </summary>
    public class HttpServerController : MonoBehaviour
    {
        [Header("Server Settings")]
        [SerializeField] private int port = 8080;

        [Header("Dependencies")]
        [SerializeField] private RecordingManager? recordingManager;
        [SerializeField] private RecordingListManager? recordingListManager;
        [SerializeField] private RecordingOperations? recordingOperations;



        [Header("Storage")]
        [Tooltip("Minimum free bytes required to start recording (500 MB)")]
        [SerializeField] private long minFreeStorageBytes = 524_288_000;

        private EmbeddedHttpServer? server;
        private float appStartTime;
        private long appStartUnixMs; // For background-thread-safe uptime calc
        private string cachedDataPath = ""; // Cache for background thread access

        // State tracked across start/stop for response metadata
        private string? currentRecordingFile;
        private string? currentRecordingStartedAt;
        private long currentRecordingStartMs;

        // Sidecar metadata for recordings (sessionId, employeeId, etc.)
        private readonly Dictionary<string, string> recordingMetadata = new();

        private static readonly Regex RecordingDirPattern = new(@"^\d{8}_\d{6}$", RegexOptions.Compiled);

        // Cached status values (refreshed every Update, read lock-free by HTTP thread)
        private volatile int cachedBattery = 100;
        private long cachedStorageFree = long.MaxValue;
        private long cachedStorageTotal = 0;
        private volatile bool cachedIsRecording = false;
        private float cachedDuration = 0f;
        private string cachedAppVersion = "1.2.0";
        private float lastStorageRefresh = 0f;

        private void Awake()
        {
            appStartTime = Time.realtimeSinceStartup;

            // Auto-discover dependencies if not assigned
            recordingManager ??= FindFirstObjectByType<RecordingManager>();
            recordingListManager ??= FindFirstObjectByType<RecordingListManager>();
            recordingOperations ??= FindFirstObjectByType<RecordingOperations>();

            if (recordingManager == null)
                Debug.LogError($"[{Constants.LOG_TAG}] HttpServerController: RecordingManager not found!");
        }

        private void Start()
        {
            cachedDataPath = Application.persistentDataPath; // Cache on main thread
            appStartUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            server = new EmbeddedHttpServer(port);
            RegisterRoutes();
            server.Start();
        }

        private void Update()
        {
            server?.ProcessMainThreadQueue();

            // Refresh cached values on main thread (safe for JNI)
            cachedIsRecording = recordingManager != null && recordingManager.IsRecording;
            cachedDuration = recordingManager != null ? recordingManager.RecordingDuration : 0f;
            cachedBattery = GetBatteryPercent();

            // Refresh storage every 30 seconds (expensive JNI call)
            if (Time.realtimeSinceStartup - lastStorageRefresh > 30f)
            {
                cachedStorageFree = GetStorageFreeBytes();
                cachedStorageTotal = GetStorageTotalBytes();
                cachedAppVersion = Application.version;
                lastStorageRefresh = Time.realtimeSinceStartup;
            }
        }

        private void OnDestroy()
        {
            server?.Stop();
        }

        private void OnApplicationPause(bool paused)
        {
            // Keep server alive through pause/resume — it's low-overhead when idle
        }

        // ── Route registration ──

        private void RegisterRoutes()
        {
            if (server == null) return;

            server.Get("/api/status", HandleStatus);
            server.Post("/api/start-recording", HandleStartRecording);
            server.Post("/api/stop-recording", HandleStopRecording);
            server.Get("/api/recordings", HandleListRecordings);
            server.WildcardGet("/api/recordings/", HandleStreamRecording);
            server.WildcardDelete("/api/recordings/", HandleDeleteRecording);
        }

        // ── GET /api/status ──

        private HttpResponse HandleStatus(HttpRequest request)
        {
            // Read cached values — no main thread dispatch needed, no blocking
            bool isRecording = cachedIsRecording;
            string? currentFile = isRecording ? currentRecordingFile : null;
            int durationMs = (int)(cachedDuration * 1000);
            long uptimeMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - appStartUnixMs;

            long fileSizeBytes = 0;
            if (isRecording && currentFile != null)
            {
                var videoPath = FindVideoFile(currentFile);
                if (videoPath != null && File.Exists(videoPath))
                {
                    try { fileSizeBytes = new FileInfo(videoPath).Length; } catch { }
                }
            }

            var json = "{\n" +
                $"  \"device\": \"MetaQuest3\",\n" +
                $"  \"batteryPercent\": {cachedBattery},\n" +
                $"  \"storageFreeBytes\": {cachedStorageFree},\n" +
                $"  \"storageTotalBytes\": {cachedStorageTotal},\n" +
                $"  \"recording\": {{\n" +
                $"    \"active\": {(isRecording ? "true" : "false")},\n" +
                $"    \"currentFile\": {(currentFile != null ? $"\"{EscapeJson(currentFile)}\"" : "null")},\n" +
                $"    \"durationMs\": {durationMs},\n" +
                $"    \"fileSizeBytes\": {fileSizeBytes}\n" +
                $"  }},\n" +
                $"  \"apkVersion\": \"{EscapeJson(cachedAppVersion)}\",\n" +
                $"  \"uptimeMs\": {uptimeMs}\n" +
                "}";

            return HttpResponse.Ok(json);
        }

        // ── POST /api/start-recording ──

        private HttpResponse HandleStartRecording(HttpRequest request)
        {
            bool alreadyRecording = false;
            bool success = false;
            bool storageFull = false;
            long freeBytes = 0;
            string? errorMessage = null;

            var signal = server!.EnqueueOnMainThread(() =>
            {
                try
                {
                    // Storage check on main thread (JNI-safe)
                    freeBytes = GetStorageFreeBytes();
                    if (freeBytes > 0 && freeBytes < minFreeStorageBytes)
                    {
                        storageFull = true;
                        return;
                    }

                    if (recordingManager == null)
                    {
                        errorMessage = "RecordingManager not available";
                        return;
                    }

                    if (recordingManager.IsRecording)
                    {
                        alreadyRecording = true;
                        return;
                    }

                    recordingManager.StartRecording();

                    // Capture metadata
                    currentRecordingFile = recordingManager.CurrentSessionDirectory;
                    currentRecordingStartMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    currentRecordingStartedAt = DateTimeOffset.UtcNow.ToString("O");

                    // Save sidecar metadata from request body
                    if (!string.IsNullOrEmpty(request.Body) && currentRecordingFile != null)
                    {
                        SaveSidecarMetadata(currentRecordingFile, request.Body);
                    }

                    success = true;
                }
                catch (Exception ex)
                {
                    errorMessage = ex.Message;
                    Debug.LogError($"[{Constants.LOG_TAG}] HttpServerController: StartRecording error: {ex}");
                }
            });
            signal.Wait(5000);

            if (storageFull)
            {
                return HttpResponse.StorageFull(freeBytes, minFreeStorageBytes);
            }

            if (alreadyRecording)
            {
                return HttpResponse.Json(409,
                    "{\n" +
                    $"  \"error\": \"already_recording\",\n" +
                    $"  \"message\": \"Recording is already in progress\",\n" +
                    $"  \"currentFile\": \"{EscapeJson(currentRecordingFile ?? "unknown")}\"\n" +
                    "}");
            }

            if (!success)
            {
                return HttpResponse.InternalError(errorMessage ?? "Failed to start recording");
            }

            var json = "{\n" +
                $"  \"status\": \"recording\",\n" +
                $"  \"file\": \"{EscapeJson(currentRecordingFile ?? "")}\",\n" +
                $"  \"startedAt\": \"{EscapeJson(currentRecordingStartedAt ?? "")}\"\n" +
                "}";

            return HttpResponse.Ok(json);
        }

        // ── POST /api/stop-recording ──

        private HttpResponse HandleStopRecording(HttpRequest request)
        {
            bool notRecording = false;
            bool success = false;
            string? errorMessage = null;
            string? stoppedFile = null;
            long durationMs = 0;
            string? startedAt = null;

            var signal = server!.EnqueueOnMainThread(() =>
            {
                try
                {
                    if (recordingManager == null)
                    {
                        errorMessage = "RecordingManager not available";
                        return;
                    }

                    if (!recordingManager.IsRecording)
                    {
                        notRecording = true;
                        return;
                    }

                    durationMs = (long)(recordingManager.RecordingDuration * 1000);
                    stoppedFile = currentRecordingFile;
                    startedAt = currentRecordingStartedAt;

                    recordingManager.StopRecording();

                    // Clear recording state
                    currentRecordingFile = null;
                    currentRecordingStartedAt = null;
                    currentRecordingStartMs = 0;

                    success = true;
                }
                catch (Exception ex)
                {
                    errorMessage = ex.Message;
                    Debug.LogError($"[{Constants.LOG_TAG}] HttpServerController: StopRecording error: {ex}");
                }
            });
            signal.Wait(5000);

            if (notRecording)
            {
                return HttpResponse.Conflict("not_recording", "No recording in progress");
            }

            if (!success)
            {
                return HttpResponse.InternalError(errorMessage ?? "Failed to stop recording");
            }

            // Get file size
            long fileSizeBytes = 0;
            if (stoppedFile != null)
            {
                var videoPath = FindVideoFile(stoppedFile);
                if (videoPath != null)
                {
                    // Wait briefly for finalization
                    for (int i = 0; i < 20; i++)
                    {
                        try
                        {
                            if (File.Exists(videoPath))
                            {
                                var size = new FileInfo(videoPath).Length;
                                if (size > 0) { fileSizeBytes = size; break; }
                            }
                        }
                        catch { }
                        Thread.Sleep(100);
                    }
                }
            }

            var stoppedAt = DateTimeOffset.UtcNow.ToString("O");
            var json = "{\n" +
                $"  \"status\": \"stopped\",\n" +
                $"  \"file\": \"{EscapeJson(stoppedFile ?? "")}\",\n" +
                $"  \"durationMs\": {durationMs},\n" +
                $"  \"fileSizeBytes\": {fileSizeBytes},\n" +
                $"  \"startedAt\": \"{EscapeJson(startedAt ?? "")}\",\n" +
                $"  \"stoppedAt\": \"{EscapeJson(stoppedAt)}\"\n" +
                "}";

            return HttpResponse.Ok(json);
        }

        // ── GET /api/recordings ──

        private HttpResponse HandleListRecordings(HttpRequest request)
        {
            int limit = 20;
            if (request.QueryParams.TryGetValue("limit", out var limitStr) && int.TryParse(limitStr, out var l))
                limit = Math.Max(1, Math.Min(l, 100));

            string? afterFilter = null;
            if (request.QueryParams.TryGetValue("after", out var after) && !string.IsNullOrEmpty(after))
                afterFilter = after;

            var recordings = ScanRecordings(limit, afterFilter);

            var items = new List<string>();
            foreach (var rec in recordings)
            {
                var metaJson = "null";
                if (recordingMetadata.TryGetValue(rec.DirectoryName, out var meta))
                    metaJson = meta;
                else
                {
                    var sidecarMeta = LoadSidecarMetadata(rec.DirectoryName);
                    if (sidecarMeta != null) metaJson = sidecarMeta;
                }

                items.Add(
                    "    {\n" +
                    $"      \"file\": \"{EscapeJson(rec.DirectoryName)}\",\n" +
                    $"      \"durationMs\": {(int)(rec.DurationSeconds * 1000)},\n" +
                    $"      \"fileSizeBytes\": {rec.SizeBytes},\n" +
                    $"      \"createdAt\": \"{rec.CreationTime:O}\",\n" +
                    $"      \"metadata\": {metaJson}\n" +
                    "    }");
            }

            int total = CountAllRecordings();

            var json = "{\n" +
                $"  \"recordings\": [\n{string.Join(",\n", items)}\n  ],\n" +
                $"  \"total\": {total}\n" +
                "}";

            return HttpResponse.Ok(json);
        }

        // ── GET /api/recordings/:filename ──

        private HttpResponse HandleStreamRecording(HttpRequest request)
        {
            var filename = request.PathParam;
            var videoPath = FindVideoFile(filename);

            if (videoPath == null || !File.Exists(videoPath))
            {
                return HttpResponse.NotFound($"Recording not found: {filename}");
            }

            return HttpResponse.File(videoPath, "video/mp4");
        }

        // ── DELETE /api/recordings/:filename ──

        private HttpResponse HandleDeleteRecording(HttpRequest request)
        {
            var filename = request.PathParam;

            // Check if this recording is currently being recorded
            bool isCurrentRecording = false;
            var signal = server!.EnqueueOnMainThread(() =>
            {
                if (recordingManager != null && recordingManager.IsRecording
                    && currentRecordingFile == filename)
                {
                    isCurrentRecording = true;
                }
            });
            signal.Wait(3000);

            if (isCurrentRecording)
            {
                return HttpResponse.Conflict("recording_in_progress", "Cannot delete file that is currently being recorded");
            }

            var dirPath = Path.Combine(Application.persistentDataPath, filename);
            if (!Directory.Exists(dirPath))
            {
                return HttpResponse.NotFound("Recording not found");
            }

            long freedBytes = 0;
            try
            {
                // Calculate size before deletion
                var files = Directory.GetFiles(dirPath, "*", SearchOption.AllDirectories);
                foreach (var f in files)
                {
                    try { freedBytes += new FileInfo(f).Length; } catch { }
                }

                Directory.Delete(dirPath, true);
                recordingMetadata.Remove(filename);

                Debug.Log($"[{Constants.LOG_TAG}] HttpServerController: Deleted recording {filename}");
            }
            catch (Exception ex)
            {
                return HttpResponse.InternalError($"Failed to delete: {ex.Message}");
            }

            var json = "{\n" +
                $"  \"deleted\": \"{EscapeJson(filename)}\",\n" +
                $"  \"freedBytes\": {freedBytes}\n" +
                "}";

            return HttpResponse.Ok(json);
        }

        // ── Helpers ──

        private string? FindVideoFile(string dirName)
        {
            var dirPath = Path.Combine(Application.persistentDataPath, dirName);
            if (!Directory.Exists(dirPath)) return null;

            var videoPath = Path.Combine(dirPath, "center_camera.mp4");
            return File.Exists(videoPath) ? videoPath : null;
        }

        private List<RecordingEntry> ScanRecordings(int limit, string? afterIso)
        {
            var results = new List<RecordingEntry>();
            var dataPath = Application.persistentDataPath;

            if (!Directory.Exists(dataPath)) return results;

            DateTimeOffset? afterTime = null;
            if (afterIso != null && DateTimeOffset.TryParse(afterIso, out var parsed))
                afterTime = parsed;

            var dirs = Directory.GetDirectories(dataPath)
                .Select(d => Path.GetFileName(d))
                .Where(d => RecordingDirPattern.IsMatch(d))
                .OrderByDescending(d => d)
                .ToList();

            foreach (var dirName in dirs)
            {
                if (results.Count >= limit) break;

                var fullPath = Path.Combine(dataPath, dirName);
                var creationTime = Directory.GetCreationTime(fullPath);

                if (afterTime.HasValue && creationTime <= afterTime.Value.LocalDateTime)
                    continue;

                long sizeBytes = 0;
                double durationSec = -1;

                try
                {
                    var files = Directory.GetFiles(fullPath, "*", SearchOption.AllDirectories);
                    foreach (var f in files)
                    {
                        try { sizeBytes += new FileInfo(f).Length; } catch { }
                    }

                    // Parse duration from video_metadata.json
                    var metadataPath = Path.Combine(fullPath, "video_metadata.json");
                    if (File.Exists(metadataPath))
                    {
                        var json = File.ReadAllText(metadataPath);
                        // Simple parse — avoid dependency on JsonUtility from background thread
                        var startMatch = Regex.Match(json, "\"recording_start_unix_ms\"\\s*:\\s*(\\d+)");
                        var stopMatch = Regex.Match(json, "\"recording_stop_unix_ms\"\\s*:\\s*(\\d+)");
                        if (startMatch.Success && stopMatch.Success)
                        {
                            long startMs = long.Parse(startMatch.Groups[1].Value);
                            long stopMs = long.Parse(stopMatch.Groups[1].Value);
                            if (stopMs > startMs)
                                durationSec = (stopMs - startMs) / 1000.0;
                        }
                    }
                }
                catch { }

                results.Add(new RecordingEntry
                {
                    DirectoryName = dirName,
                    SizeBytes = sizeBytes,
                    DurationSeconds = durationSec >= 0 ? durationSec : 0,
                    CreationTime = creationTime
                });
            }

            return results;
        }

        private int CountAllRecordings()
        {
            try
            {
                return Directory.GetDirectories(Application.persistentDataPath)
                    .Count(d => RecordingDirPattern.IsMatch(Path.GetFileName(d)));
            }
            catch { return 0; }
        }

        private void SaveSidecarMetadata(string dirName, string requestBody)
        {
            try
            {
                recordingMetadata[dirName] = requestBody;

                // Also write to disk as sidecar JSON
                var dirPath = Path.Combine(Application.persistentDataPath, dirName);
                Directory.CreateDirectory(dirPath);
                var metaPath = Path.Combine(dirPath, "fielddata_metadata.json");
                File.WriteAllText(metaPath, requestBody);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[{Constants.LOG_TAG}] HttpServerController: Failed to save sidecar metadata: {ex.Message}");
            }
        }

        private string? LoadSidecarMetadata(string dirName)
        {
            try
            {
                var metaPath = Path.Combine(Application.persistentDataPath, dirName, "fielddata_metadata.json");
                if (File.Exists(metaPath))
                    return File.ReadAllText(metaPath);
            }
            catch { }
            return null;
        }

        // ── Platform helpers ──

        private static int GetBatteryPercent()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                return (int)(SystemInfo.batteryLevel * 100);
            }
            catch { }
#endif
            return (int)(SystemInfo.batteryLevel >= 0 ? SystemInfo.batteryLevel * 100 : 100);
        }

        private long GetStorageFreeBytes()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            // Try multiple paths — persistentDataPath may fail from background thread
            string[] paths = { cachedDataPath, "/storage/emulated/0", "/data" };
            foreach (var path in paths)
            {
                if (string.IsNullOrEmpty(path)) continue;
                try
                {
                    using var statFs = new AndroidJavaObject("android.os.StatFs", path);
                    long blockSize = statFs.Call<long>("getBlockSizeLong");
                    long availBlocks = statFs.Call<long>("getAvailableBlocksLong");
                    long result = blockSize * availBlocks;
                    if (result > 0) return result;
                }
                catch { }
            }
#endif
            return long.MaxValue; // If we can't determine, assume plenty of space
        }

        private long GetStorageTotalBytes()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            string[] paths = { cachedDataPath, "/storage/emulated/0", "/data" };
            foreach (var path in paths)
            {
                if (string.IsNullOrEmpty(path)) continue;
                try
                {
                    using var statFs = new AndroidJavaObject("android.os.StatFs", path);
                    long blockSize = statFs.Call<long>("getBlockSizeLong");
                    long totalBlocks = statFs.Call<long>("getBlockCountLong");
                    long result = blockSize * totalBlocks;
                    if (result > 0) return result;
                }
                catch { }
            }
#endif
            return 0;
        }

        private static string EscapeJson(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r");
        }

        private class RecordingEntry
        {
            public string DirectoryName { get; set; } = "";
            public long SizeBytes { get; set; }
            public double DurationSeconds { get; set; }
            public DateTime CreationTime { get; set; }
        }
    }
}
