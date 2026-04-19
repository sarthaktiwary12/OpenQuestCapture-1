# nullable enable

// Sentry SDK not yet installed — this file is a placeholder.
// To enable: add io.sentry.unity via Unity Package Manager, then remove the #if guard.

#if HAS_SENTRY
using System;
using Sentry;
using Sentry.Unity;
using UnityEngine;
using RealityLog.Common;

namespace RealityLog
{
    public static class SentryBootstrap
    {
        private const string SentryDsn = "https://0b2c15ec06c05cc3eec8b20ba5187359@o4511111680294913.ingest.us.sentry.io/4511121459183616";

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Bootstrap()
        {
            if (SentryDsn == "YOUR_SENTRY_DSN")
            {
                Debug.LogWarning($"[{Constants.LOG_TAG}] SentryBootstrap: DSN not configured — skipping Sentry init");
                return;
            }

            try
            {
                SentrySdk.Init(options =>
                {
                    options.Dsn = SentryDsn;
                    options.Release = Application.version;
                    options.Environment = Debug.isDebugBuild ? "development" : "production";
                    options.AutoSessionTracking = true;
                });

                SentrySdk.ConfigureScope(scope =>
                {
                    scope.SetTag("device.name", SystemInfo.deviceName);
                    scope.SetTag("device.platform", Application.platform.ToString());
                });

                Application.logMessageReceived += OnLogMessageReceived;

                Debug.Log($"[{Constants.LOG_TAG}] SentryBootstrap: Sentry initialized (env={( Debug.isDebugBuild ? "development" : "production" )})");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[{Constants.LOG_TAG}] SentryBootstrap: Failed to initialize Sentry: {ex.Message}");
            }
        }

        private static void OnLogMessageReceived(string logMessage, string stackTrace, LogType type)
        {
            if (!SentrySdk.IsEnabled)
                return;

            switch (type)
            {
                case LogType.Error:
                    SentrySdk.CaptureMessage(logMessage, SentryLevel.Error);
                    break;

                case LogType.Exception:
                    SentrySdk.CaptureEvent(new SentryEvent
                    {
                        Message = new SentryMessage { Formatted = logMessage },
                        Level = SentryLevel.Fatal,
                    });
                    break;
            }
        }
    }
}
#endif
