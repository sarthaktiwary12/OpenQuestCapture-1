#nullable enable

using System.IO;
using NUnit.Framework;
using RealityLog.Common;

namespace RealityLog.Tests
{
    public class AuthTokenManagerTests
    {
        [Test]
        public void GenerateBearerToken_returns_url_safe_base64_of_sufficient_entropy()
        {
            var t = AuthTokenManager.GenerateBearerToken();
            Assert.IsNotNull(t);
            Assert.GreaterOrEqual(t.Length, 20, "token should encode at least 128 bits");
            StringAssert.DoesNotContain("+", t);
            StringAssert.DoesNotContain("/", t);
            StringAssert.DoesNotContain("=", t);
        }

        [Test]
        public void GenerateBearerToken_is_unique_across_calls()
        {
            var a = AuthTokenManager.GenerateBearerToken();
            var b = AuthTokenManager.GenerateBearerToken();
            Assert.AreNotEqual(a, b);
        }

        [Test]
        public void IsAuthorized_accepts_matching_bearer()
        {
            var token = "abc123XYZ";
            Assert.IsTrue(AuthTokenManager.IsAuthorized("Bearer abc123XYZ", token));
        }

        [Test]
        public void IsAuthorized_rejects_missing_prefix()
        {
            var token = "abc123XYZ";
            Assert.IsFalse(AuthTokenManager.IsAuthorized("abc123XYZ", token));
            Assert.IsFalse(AuthTokenManager.IsAuthorized("Token abc123XYZ", token));
        }

        [Test]
        public void IsAuthorized_rejects_wrong_token()
        {
            Assert.IsFalse(AuthTokenManager.IsAuthorized("Bearer wrong", "right"));
        }

        [Test]
        public void IsAuthorized_rejects_null_or_empty_inputs()
        {
            Assert.IsFalse(AuthTokenManager.IsAuthorized(null, "x"));
            Assert.IsFalse(AuthTokenManager.IsAuthorized("", "x"));
            Assert.IsFalse(AuthTokenManager.IsAuthorized("Bearer x", ""));
        }

        [Test]
        public void IsAuthorized_rejects_length_prefix_attacks()
        {
            // Attacker presents a prefix of the real token; constant-time
            // compare must still reject because lengths differ.
            var token = "abcdefghij";
            Assert.IsFalse(AuthTokenManager.IsAuthorized("Bearer abcdef", token));
            Assert.IsFalse(AuthTokenManager.IsAuthorized("Bearer abcdefghijk", token));
        }

        [Test]
        public void LoadRelayKey_returns_null_when_file_missing()
        {
            var tempPath = Path.Combine(Path.GetTempPath(), "ws-e-missing-relay-key-" + System.Guid.NewGuid() + ".txt");
            Assert.IsNull(AuthTokenManager.LoadRelayKey(tempPath));
        }

        [Test]
        public void LoadRelayKey_trims_whitespace_and_newline()
        {
            var tempPath = Path.GetTempFileName();
            try
            {
                File.WriteAllText(tempPath, "  rotated-key-07  \n");
                var k = AuthTokenManager.LoadRelayKey(tempPath);
                Assert.AreEqual("rotated-key-07", k);
            }
            finally { File.Delete(tempPath); }
        }

        [Test]
        public void LoadRelayKey_returns_null_for_empty_file()
        {
            var tempPath = Path.GetTempFileName();
            try
            {
                File.WriteAllText(tempPath, "   \n");
                Assert.IsNull(AuthTokenManager.LoadRelayKey(tempPath));
            }
            finally { File.Delete(tempPath); }
        }

        [Test]
        public void WriteTokenFile_and_read_roundtrips()
        {
            var tempPath = Path.Combine(Path.GetTempPath(), "ws-e-token-" + System.Guid.NewGuid() + ".txt");
            try
            {
                AuthTokenManager.WriteTokenFile(tempPath, "my-token");
                Assert.AreEqual("my-token", File.ReadAllText(tempPath));
            }
            finally { if (File.Exists(tempPath)) File.Delete(tempPath); }
        }
    }
}
