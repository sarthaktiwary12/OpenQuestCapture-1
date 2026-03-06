# nullable enable

using System;
using System.IO;
using UnityEngine;

namespace RealityLog.FileOperations
{
    /// <summary>
    /// Serializable model for the .upload_status JSON file written per recording directory.
    /// Tracks the upload lifecycle: pending → compressing → uploading → uploaded | failed.
    /// </summary>
    [Serializable]
    public class UploadStatus
    {
        public const string FileName = ".upload_status";

        public string status = "pending";
        public string r2Key = "";
        public string uploadedAt = "";
        public long sizeBytes;
        public string errorMessage = "";
        public int retryCount;

        public static UploadStatus? Read(string recordingDir)
        {
            string path = Path.Combine(recordingDir, FileName);
            if (!File.Exists(path))
                return null;

            try
            {
                string json = File.ReadAllText(path);
                return JsonUtility.FromJson<UploadStatus>(json);
            }
            catch
            {
                return null;
            }
        }

        public static void Write(string recordingDir, UploadStatus status)
        {
            string path = Path.Combine(recordingDir, FileName);
            string json = JsonUtility.ToJson(status, true);
            File.WriteAllText(path, json);
        }
    }
}
