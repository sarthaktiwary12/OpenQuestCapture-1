#nullable enable

using System;
using System.IO;
using System.Security.Cryptography;

namespace RealityLog.Common
{
    /// <summary>
    /// Manages the bearer token used to authenticate requests against the embedded
    /// HTTP server and the externalised API key used by the cloud relay.
    ///
    /// Design:
    ///   - Bearer token is generated fresh on every boot (128 bits, base64url).
    ///   - The cloud relay key lives in a signed file on /sdcard so it can be
    ///     rotated without rebuilding the APK. If the file is missing, the
    ///     service MUST refuse to talk to the relay (fail closed).
    ///   - The class is deliberately free of UnityEngine references so it can be
    ///     unit-tested from NUnit/.NET without a Quest.
    /// </summary>
    public static class AuthTokenManager
    {
        /// <summary>Header name the phone must send with each request.</summary>
        public const string AuthHeader = "Authorization";

        /// <summary>Scheme prefix, e.g. "Bearer abcdef".</summary>
        public const string BearerPrefix = "Bearer ";

        /// <summary>Path on Quest where operators drop the rotatable relay key.</summary>
        public const string DefaultRelayKeyPath = "/sdcard/fielddata/relay_api_key.txt";

        // 16 bytes == 128 bits of entropy, encoded as 22 chars of base64url.
        private const int TokenEntropyBytes = 16;

        /// <summary>
        /// Generate a fresh bearer token using a cryptographic RNG. Deterministic
        /// only in the sense that the caller supplies the RNG (for tests).
        /// </summary>
        public static string GenerateBearerToken(RandomNumberGenerator? rng = null)
        {
            rng ??= RandomNumberGenerator.Create();
            var buffer = new byte[TokenEntropyBytes];
            rng.GetBytes(buffer);
            return ToBase64Url(buffer);
        }

        /// <summary>
        /// Constant-time compare. Returns true when the supplied Authorization
        /// header value matches the expected bearer token exactly.
        /// </summary>
        public static bool IsAuthorized(string? headerValue, string expectedToken)
        {
            if (string.IsNullOrEmpty(expectedToken)) return false;
            if (string.IsNullOrEmpty(headerValue)) return false;

            if (headerValue!.Length < BearerPrefix.Length) return false;
            if (!headerValue.StartsWith(BearerPrefix, StringComparison.Ordinal)) return false;

            var presented = headerValue.Substring(BearerPrefix.Length).Trim();
            return ConstantTimeEquals(presented, expectedToken);
        }

        /// <summary>
        /// Loads the externalised cloud relay API key. Returns null when the
        /// file is missing or empty — callers should treat this as fail-closed.
        /// A key file looks like:
        ///     fielddata-pro-2024-rotation-05
        /// with optional leading/trailing whitespace and a trailing newline.
        /// </summary>
        public static string? LoadRelayKey(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                var raw = File.ReadAllText(path);
                var trimmed = raw?.Trim();
                if (string.IsNullOrEmpty(trimmed)) return null;
                return trimmed;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Persist the bearer token to a USB-readable localhost file so the
        /// pairing endpoint (served from the same process) can hand it to the
        /// phone without exposing it over Wi-Fi.
        /// </summary>
        public static void WriteTokenFile(string path, string token)
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir!);
            File.WriteAllText(path, token);
        }

        // ── helpers ─────────────────────────────────────────────────────

        private static string ToBase64Url(byte[] bytes)
        {
            var s = Convert.ToBase64String(bytes);
            return s.Replace('+', '-').Replace('/', '_').TrimEnd('=');
        }

        private static bool ConstantTimeEquals(string a, string b)
        {
            if (a.Length != b.Length) return false;
            int diff = 0;
            for (int i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
            return diff == 0;
        }
    }
}
