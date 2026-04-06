# nullable enable

using System;
using System.Collections;
using UnityEngine;
using RealityLog.Common;

namespace RealityLog
{
    /// <summary>
    /// Immediately prevents the Quest from sleeping/freezing when the headset is removed.
    /// Runs at app startup with zero delay — before any other bootstrap.
    ///
    /// The Quest's VrPowerManagerService force-sleeps the device ~15s after headset removal,
    /// ignoring standard Android power settings. The watchdog coroutine detects this and
    /// sends KEYCODE_WAKEUP to fight back — same approach as quest-keep-awake.sh but
    /// running inside the app so no ADB connection is needed in the field.
    /// </summary>
    public class KeepAwakeBootstrap : MonoBehaviour
    {
        private const float WATCHDOG_INTERVAL = 5f; // seconds between wake checks

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            // Run immediately — no delay
            var go = new GameObject("KeepAwakeBootstrap");
            go.AddComponent<KeepAwakeBootstrap>();
            DontDestroyOnLoad(go);
        }

        private void Awake()
        {
            // Prevent Unity from pausing when app loses focus (headset removal)
            Application.runInBackground = true;
            Debug.Log($"[{Constants.LOG_TAG}] KeepAwakeBootstrap: runInBackground = true");

            ApplyKeepAwake();
            StartCoroutine(WakeWatchdog());
        }

        private void OnApplicationPause(bool paused)
        {
            if (paused)
            {
                Debug.Log($"[{Constants.LOG_TAG}] KeepAwakeBootstrap: App pause event — re-applying keep-awake");
                // Re-apply on every pause event to ensure we stay awake
                ApplyKeepAwake();
                SendWakeup();
            }
            else
            {
                Debug.Log($"[{Constants.LOG_TAG}] KeepAwakeBootstrap: App resumed");
            }
        }

        /// <summary>
        /// Watchdog coroutine: every 5 seconds, send KEYCODE_WAKEUP and re-apply
        /// proximity disable. This defeats VrPowerManagerService's 15s force-sleep
        /// by continuously waking the device before it can fully sleep.
        /// </summary>
        private IEnumerator WakeWatchdog()
        {
            Debug.Log($"[{Constants.LOG_TAG}] KeepAwakeBootstrap: Wake watchdog started (interval={WATCHDOG_INTERVAL}s)");
            while (true)
            {
                yield return new WaitForSecondsRealtime(WATCHDOG_INTERVAL);
                SendWakeup();
            }
        }

        private void SendWakeup()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                using var runtime = new AndroidJavaClass("java.lang.Runtime")
                    .CallStatic<AndroidJavaObject>("getRuntime");

                // Send KEYCODE_WAKEUP (224) — same as quest-keep-awake.sh watchdog
                using var p = runtime.Call<AndroidJavaObject>("exec",
                    new string[] { "/system/bin/input", "keyevent", "KEYCODE_WAKEUP" });
                p.Call<int>("waitFor");

                // Re-disable proximity sensor (VrPowerManagerService may re-enable it)
                using var p2 = runtime.Call<AndroidJavaObject>("exec",
                    new string[] { "/system/bin/setprop", "debug.oculus.proximityDisabled", "1" });
                p2.Call<int>("waitFor");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[{Constants.LOG_TAG}] KeepAwakeBootstrap: Wakeup failed: {ex.Message}");
            }
#endif
        }

        private void ApplyKeepAwake()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                using var runtime = new AndroidJavaClass("java.lang.Runtime")
                    .CallStatic<AndroidJavaObject>("getRuntime");

                // Disable proximity sensor — THE key fix for headset removal freeze
                using var p1 = runtime.Call<AndroidJavaObject>("exec",
                    new string[] { "/system/bin/setprop", "debug.oculus.proximityDisabled", "1" });
                p1.Call<int>("waitFor");
                Debug.Log($"[{Constants.LOG_TAG}] KeepAwakeBootstrap: Proximity sensor DISABLED");

                // Max screen timeout
                using var p2 = runtime.Call<AndroidJavaObject>("exec",
                    new string[] { "/system/bin/settings", "put", "system", "screen_off_timeout", "2147483647" });
                p2.Call<int>("waitFor");

                // Stay on while plugged in (AC + USB + Wireless = 7)
                using var p3 = runtime.Call<AndroidJavaObject>("exec",
                    new string[] { "/system/bin/settings", "put", "global", "stay_on_while_plugged_in", "7" });
                p3.Call<int>("waitFor");

                // Disable doze / app standby / adaptive battery — prevent OS from throttling us
                using var p4 = runtime.Call<AndroidJavaObject>("exec",
                    new string[] { "/system/bin/dumpsys", "deviceidle", "disable" });
                p4.Call<int>("waitFor");

                using var p5 = runtime.Call<AndroidJavaObject>("exec",
                    new string[] { "/system/bin/settings", "put", "global", "app_standby_enabled", "0" });
                p5.Call<int>("waitFor");

                using var p6 = runtime.Call<AndroidJavaObject>("exec",
                    new string[] { "/system/bin/settings", "put", "global", "adaptive_battery_management_enabled", "0" });
                p6.Call<int>("waitFor");

                // Prevent WiFi from sleeping (policy 2 = never sleep)
                using var p7 = runtime.Call<AndroidJavaObject>("exec",
                    new string[] { "/system/bin/settings", "put", "global", "wifi_sleep_policy", "2" });
                p7.Call<int>("waitFor");

                Debug.Log($"[{Constants.LOG_TAG}] KeepAwakeBootstrap: All keep-awake settings applied");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[{Constants.LOG_TAG}] KeepAwakeBootstrap: Failed: {ex.Message}");
            }

            // Acquire wake lock
            try
            {
                using var unityPlayer = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
                using var activity = unityPlayer.GetStatic<AndroidJavaObject>("currentActivity");

                // FLAG_KEEP_SCREEN_ON = 128
                using var window = activity.Call<AndroidJavaObject>("getWindow");
                window.Call("addFlags", 128);

                // Partial wake lock
                using var powerManager = activity.Call<AndroidJavaObject>("getSystemService", "power");
                var wakeLock = powerManager.Call<AndroidJavaObject>("newWakeLock",
                    1 /* PARTIAL_WAKE_LOCK */, "RealityLog:BootKeepAwake");
                wakeLock.Call("acquire");

                Debug.Log($"[{Constants.LOG_TAG}] KeepAwakeBootstrap: Wake lock acquired + screen on flag set");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[{Constants.LOG_TAG}] KeepAwakeBootstrap: Wake lock failed: {ex.Message}");
            }
#else
            Debug.Log($"[{Constants.LOG_TAG}] KeepAwakeBootstrap: Editor mode — skipping Android keep-awake");
#endif
        }
    }
}
