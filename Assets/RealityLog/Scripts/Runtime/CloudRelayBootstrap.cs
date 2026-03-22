# nullable enable

using System;
using System.Collections;
using UnityEngine;
using RealityLog.Common;

namespace RealityLog
{
    /// <summary>
    /// Bootstraps the CloudRelayService after the HTTP server is up.
    /// Creates a persistent GameObject with the service attached.
    /// </summary>
    public class CloudRelayBootstrap : MonoBehaviour
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            var go = new GameObject("CloudRelayBootstrap");
            go.AddComponent<CloudRelayBootstrap>();
            DontDestroyOnLoad(go);
        }

        private IEnumerator Start()
        {
            // Wait for HTTP server and other systems to initialize (6 seconds)
            yield return new WaitForSeconds(6f);

            try
            {
                var controllerType = Type.GetType("RealityLog.Network.CloudRelayService, RealityLog.Network");
                if (controllerType == null)
                {
                    Debug.LogError($"[{Constants.LOG_TAG}] CloudRelayBootstrap: Could not find CloudRelayService type");
                    yield break;
                }

                if (FindFirstObjectByType(controllerType) != null)
                {
                    Debug.Log($"[{Constants.LOG_TAG}] CloudRelayBootstrap: CloudRelayService already exists");
                    yield break;
                }

                var relayGo = new GameObject("CloudRelayService");
                relayGo.AddComponent(controllerType);
                DontDestroyOnLoad(relayGo);
                Debug.Log($"[{Constants.LOG_TAG}] CloudRelayBootstrap: Created CloudRelayService");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[{Constants.LOG_TAG}] CloudRelayBootstrap: Failed: {ex.Message}");
            }

            // Clean up bootstrap object
            Destroy(gameObject);
        }
    }
}
