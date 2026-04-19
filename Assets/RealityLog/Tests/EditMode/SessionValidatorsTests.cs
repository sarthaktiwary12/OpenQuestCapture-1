#nullable enable

using System;
using System.IO;
using NUnit.Framework;
using RealityLog.Common;

namespace RealityLog.Tests
{
    public class SessionValidatorsTests
    {
        // ── MP4 ─────────────────────────────────────────────────────────

        [Test]
        public void Mp4_missing_file_reports_MissingFile()
        {
            var path = Path.Combine(Path.GetTempPath(), "ws-e-missing-" + Guid.NewGuid() + ".mp4");
            Assert.AreEqual(SessionValidators.Mp4Status.MissingFile,
                SessionValidators.ValidateMp4(path));
        }

        [Test]
        public void Mp4_zero_bytes_reports_Truncated()
        {
            var path = Path.GetTempFileName();
            try
            {
                File.WriteAllBytes(path, new byte[0]);
                Assert.AreEqual(SessionValidators.Mp4Status.Truncated,
                    SessionValidators.ValidateMp4(path));
            }
            finally { File.Delete(path); }
        }

        [Test]
        public void Mp4_valid_ftyp_moov_mdat_accepted()
        {
            using var ms = new MemoryStream();
            WriteAtom(ms, "ftyp", new byte[] { 0x69, 0x73, 0x6F, 0x6D, 0, 0, 0, 0 });
            WriteAtom(ms, "moov", new byte[32]);
            WriteAtom(ms, "mdat", new byte[64]);
            ms.Position = 0;
            Assert.AreEqual(SessionValidators.Mp4Status.Valid,
                SessionValidators.ValidateMp4Stream(ms));
        }

        [Test]
        public void Mp4_missing_moov_reports_MissingMoov()
        {
            using var ms = new MemoryStream();
            WriteAtom(ms, "ftyp", new byte[] { 0x69, 0x73, 0x6F, 0x6D, 0, 0, 0, 0 });
            WriteAtom(ms, "mdat", new byte[64]);
            ms.Position = 0;
            Assert.AreEqual(SessionValidators.Mp4Status.MissingMoov,
                SessionValidators.ValidateMp4Stream(ms));
        }

        [Test]
        public void Mp4_missing_ftyp_reports_MissingFtyp()
        {
            using var ms = new MemoryStream();
            WriteAtom(ms, "moov", new byte[32]);
            WriteAtom(ms, "mdat", new byte[64]);
            ms.Position = 0;
            Assert.AreEqual(SessionValidators.Mp4Status.MissingFtyp,
                SessionValidators.ValidateMp4Stream(ms));
        }

        [Test]
        public void Mp4_missing_mdat_reports_MissingMdat()
        {
            using var ms = new MemoryStream();
            WriteAtom(ms, "ftyp", new byte[] { 0x69, 0x73, 0x6F, 0x6D, 0, 0, 0, 0 });
            WriteAtom(ms, "moov", new byte[32]);
            ms.Position = 0;
            Assert.AreEqual(SessionValidators.Mp4Status.MissingMdat,
                SessionValidators.ValidateMp4Stream(ms));
        }

        [Test]
        public void Mp4_truncated_atom_body_reports_Truncated()
        {
            // Header claims 64-byte atom but only 16 bytes of stream follow.
            using var ms = new MemoryStream();
            WriteAtomHeader(ms, "mdat", 64);
            ms.Write(new byte[8], 0, 8); // only 16 bytes total (8 header + 8 body)
            ms.Position = 0;
            Assert.AreEqual(SessionValidators.Mp4Status.Truncated,
                SessionValidators.ValidateMp4Stream(ms));
        }

        // ── MCAP ────────────────────────────────────────────────────────

        [Test]
        public void Mcap_missing_file_reports_MissingFile()
        {
            var path = Path.Combine(Path.GetTempPath(), "ws-e-missing-" + Guid.NewGuid() + ".mcap");
            Assert.AreEqual(SessionValidators.McapStatus.MissingFile,
                SessionValidators.ValidateMcap(path));
        }

        [Test]
        public void Mcap_too_short_reports_Truncated()
        {
            using var ms = new MemoryStream(new byte[] { 0x89, 0x4D, 0x43 });
            Assert.AreEqual(SessionValidators.McapStatus.Truncated,
                SessionValidators.ValidateMcapStream(ms));
        }

        [Test]
        public void Mcap_valid_head_and_tail_magic_accepted()
        {
            using var ms = new MemoryStream();
            ms.Write(SessionValidators.McapMagic, 0, SessionValidators.McapMagic.Length);
            ms.Write(new byte[128], 0, 128); // fake records
            ms.Write(SessionValidators.McapMagic, 0, SessionValidators.McapMagic.Length);
            ms.Position = 0;
            Assert.AreEqual(SessionValidators.McapStatus.Valid,
                SessionValidators.ValidateMcapStream(ms));
        }

        [Test]
        public void Mcap_head_magic_wrong_reports_BadMagic()
        {
            using var ms = new MemoryStream();
            ms.Write(new byte[] { 0x00, 0x4D, 0x43, 0x41, 0x50, 0x30, 0x0D, 0x0A }, 0, 8);
            ms.Write(new byte[16], 0, 16);
            ms.Write(SessionValidators.McapMagic, 0, SessionValidators.McapMagic.Length);
            ms.Position = 0;
            Assert.AreEqual(SessionValidators.McapStatus.BadMagic,
                SessionValidators.ValidateMcapStream(ms));
        }

        [Test]
        public void Mcap_footer_missing_reports_MissingFooter()
        {
            using var ms = new MemoryStream();
            ms.Write(SessionValidators.McapMagic, 0, SessionValidators.McapMagic.Length);
            ms.Write(new byte[16], 0, 16); // garbage where footer should be
            ms.Write(new byte[] { 0x89, 0x4D, 0x43, 0x41, 0x50, 0x30, 0x0D, 0x00 }, 0, 8);
            ms.Position = 0;
            Assert.AreEqual(SessionValidators.McapStatus.MissingFooter,
                SessionValidators.ValidateMcapStream(ms));
        }

        // ── helpers ─────────────────────────────────────────────────────

        private static void WriteAtomHeader(Stream s, string fourcc, int totalSize)
        {
            byte[] header = new byte[8];
            header[0] = (byte)((totalSize >> 24) & 0xFF);
            header[1] = (byte)((totalSize >> 16) & 0xFF);
            header[2] = (byte)((totalSize >> 8) & 0xFF);
            header[3] = (byte)(totalSize & 0xFF);
            for (int i = 0; i < 4; i++) header[4 + i] = (byte)fourcc[i];
            s.Write(header, 0, 8);
        }

        private static void WriteAtom(Stream s, string fourcc, byte[] body)
        {
            int totalSize = 8 + body.Length;
            WriteAtomHeader(s, fourcc, totalSize);
            s.Write(body, 0, body.Length);
        }
    }
}
