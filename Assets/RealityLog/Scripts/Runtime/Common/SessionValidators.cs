#nullable enable

using System;
using System.IO;
using System.Text;

namespace RealityLog.Common
{
    /// <summary>
    /// Pure validators for on-disk session artefacts. Separate from
    /// RecordingManager so they can be covered by NUnit tests without Unity.
    /// </summary>
    public static class SessionValidators
    {
        // MP4 ─────────────────────────────────────────────────────────────

        public enum Mp4Status
        {
            Valid,
            MissingFile,
            Truncated,
            MissingFtyp,
            MissingMoov,
            MissingMdat,
        }

        /// <summary>
        /// Minimal MP4 atom scan. Validates that ftyp, moov and mdat atoms
        /// are present. Does NOT attempt full box parsing — sufficient to
        /// catch the common corruption modes (zero-byte, truncated, never
        /// finalized). Atom parser follows ISO/IEC 14496-12 §4.2.
        /// </summary>
        public static Mp4Status ValidateMp4(string path)
        {
            if (!File.Exists(path)) return Mp4Status.MissingFile;

            using var fs = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return ValidateMp4Stream(fs);
        }

        public static Mp4Status ValidateMp4Stream(Stream stream)
        {
            bool sawFtyp = false, sawMoov = false, sawMdat = false;
            long length = stream.Length;
            if (length < 8) return Mp4Status.Truncated;

            long offset = 0;
            var header = new byte[8];
            while (offset + 8 <= length)
            {
                stream.Seek(offset, SeekOrigin.Begin);
                int read = ReadFully(stream, header, 0, 8);
                if (read < 8) return Mp4Status.Truncated;

                long size = ((long)header[0] << 24) | ((long)header[1] << 16) |
                            ((long)header[2] << 8)  | header[3];
                string type = Encoding.ASCII.GetString(header, 4, 4);
                long headerSize = 8;

                if (size == 1)
                {
                    // 64-bit largesize
                    var large = new byte[8];
                    if (ReadFully(stream, large, 0, 8) < 8) return Mp4Status.Truncated;
                    size = ((long)large[0] << 56) | ((long)large[1] << 48) |
                           ((long)large[2] << 40) | ((long)large[3] << 32) |
                           ((long)large[4] << 24) | ((long)large[5] << 16) |
                           ((long)large[6] << 8)  | large[7];
                    headerSize = 16;
                }
                else if (size == 0)
                {
                    // "rest of file" — only legal as the final atom.
                    size = length - offset;
                }

                if (size < headerSize || offset + size > length)
                    return Mp4Status.Truncated;

                switch (type)
                {
                    case "ftyp": sawFtyp = true; break;
                    case "moov": sawMoov = true; break;
                    case "mdat": sawMdat = true; break;
                }

                offset += size;
            }

            if (!sawFtyp) return Mp4Status.MissingFtyp;
            if (!sawMoov) return Mp4Status.MissingMoov;
            if (!sawMdat) return Mp4Status.MissingMdat;
            return Mp4Status.Valid;
        }

        // MCAP ────────────────────────────────────────────────────────────

        public enum McapStatus
        {
            Valid,
            MissingFile,
            Truncated,
            BadMagic,
            MissingFooter,
        }

        /// <summary>
        /// MCAP magic bytes per spec v0: 0x89, 'M', 'C', 'A', 'P', 0x30, '\r', '\n'.
        /// </summary>
        public static readonly byte[] McapMagic = { 0x89, 0x4D, 0x43, 0x41, 0x50, 0x30, 0x0D, 0x0A };

        /// <summary>
        /// Validates that the file starts and ends with the MCAP magic bytes.
        /// A well-formed MCAP file has magic at BOTH offset 0 and at (length - 8).
        /// The "footer offset" check ensures the writer actually flushed the
        /// final record — a truncated recording will be missing the trailing
        /// magic even if the head looks valid.
        /// </summary>
        public static McapStatus ValidateMcap(string path)
        {
            if (!File.Exists(path)) return McapStatus.MissingFile;
            using var fs = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return ValidateMcapStream(fs);
        }

        public static McapStatus ValidateMcapStream(Stream stream)
        {
            long length = stream.Length;
            if (length < McapMagic.Length * 2) return McapStatus.Truncated;

            var head = new byte[McapMagic.Length];
            stream.Seek(0, SeekOrigin.Begin);
            if (ReadFully(stream, head, 0, head.Length) < head.Length) return McapStatus.Truncated;
            if (!BytesEqual(head, McapMagic)) return McapStatus.BadMagic;

            var tail = new byte[McapMagic.Length];
            stream.Seek(length - McapMagic.Length, SeekOrigin.Begin);
            if (ReadFully(stream, tail, 0, tail.Length) < tail.Length) return McapStatus.Truncated;
            if (!BytesEqual(tail, McapMagic)) return McapStatus.MissingFooter;

            return McapStatus.Valid;
        }

        // ── helpers ────────────────────────────────────────────────────

        private static int ReadFully(Stream s, byte[] buffer, int offset, int count)
        {
            int total = 0;
            while (total < count)
            {
                int read = s.Read(buffer, offset + total, count - total);
                if (read == 0) break;
                total += read;
            }
            return total;
        }

        private static bool BytesEqual(byte[] a, byte[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }
    }
}
