# nullable enable

using System;
using System.IO;
using System.IO.Compression;
using System.Threading;
using System.Threading.Tasks;

namespace RealityLog.FileOperations
{
    /// <summary>
    /// Static utility for compressing directories into ZIP archives on a background thread.
    /// </summary>
    public static class ZipHelper
    {
        public class CompressionProgress
        {
            public int ProcessedFiles;
            public int TotalFiles;
            public bool IsDone;
            public bool IsCancelled;
            public Exception? Exception;
        }

        /// <summary>
        /// Compresses a directory into a ZIP file asynchronously using Task.Run.
        /// Returns a (Task, CompressionProgress) tuple for coroutine-based polling.
        /// </summary>
        public static (Task, CompressionProgress) CompressDirectoryAsync(string sourcePath, string zipPath)
        {
            string[] files = Directory.GetFiles(sourcePath, "*", SearchOption.AllDirectories);

            var progress = new CompressionProgress
            {
                TotalFiles = files.Length
            };

            var task = Task.Run(() =>
            {
                try
                {
                    // Delete existing zip if present
                    if (File.Exists(zipPath))
                        File.Delete(zipPath);

                    using var zipStream = new FileStream(zipPath, FileMode.Create);
                    using var archive = new ZipArchive(zipStream, ZipArchiveMode.Create);

                    foreach (var file in files)
                    {
                        if (progress.IsCancelled) break;

                        string entryName = Path.GetRelativePath(sourcePath, file);
                        archive.CreateEntryFromFile(file, entryName, CompressionLevel.Fastest);

                        Interlocked.Increment(ref progress.ProcessedFiles);
                    }
                }
                catch (Exception e)
                {
                    progress.Exception = e;
                }
                finally
                {
                    progress.IsDone = true;
                }
            });

            return (task, progress);
        }
    }
}
