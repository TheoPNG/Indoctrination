using Indoctrination.Net;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Indoctrination.EditorTools
{
    /// <summary>
    /// Builds the three objects the multiplayer loop needs into the open scene.
    /// Doing it from a menu item rather than by hand keeps the setup repeatable
    /// and reviewable - and means the scene can be rebuilt from scratch if it is
    /// ever broken or lost.
    /// </summary>
    public static class MultiplayerSceneSetup
    {
        [MenuItem("Indoctrination/Set Up Multiplayer Scene")]
        public static void SetUp()
        {
            var scene = SceneManager.GetActiveScene();

            var manager = EnsureNetworkManager();
            EnsureGameManager();
            EnsureUI();

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);

            Selection.activeGameObject = manager.gameObject;
            Debug.Log("Multiplayer scene is set up. Press Play, then use the Host / Join buttons.");
        }

        private static NetworkManager EnsureNetworkManager()
        {
            var existing = Object.FindAnyObjectByType<NetworkManager>();
            var go = existing != null ? existing.gameObject : new GameObject("NetworkManager");
            var manager = existing != null ? existing : go.AddComponent<NetworkManager>();

            var transport = go.GetComponent<UnityTransport>() ?? go.AddComponent<UnityTransport>();
            transport.SetConnectionData("127.0.0.1", 7777, "0.0.0.0");

            manager.NetworkConfig ??= new NetworkConfig();
            manager.NetworkConfig.NetworkTransport = transport;

            // Without this an editor instance that loses focus stops ticking, which
            // looks exactly like a dropped connection when testing two on one machine.
            manager.RunInBackground = true;

            EditorUtility.SetDirty(manager);
            return manager;
        }

        private static void EnsureGameManager()
        {
            if (Object.FindAnyObjectByType<NetworkGameManager>() != null)
            {
                return;
            }

            // Kept off the NetworkManager object: Netcode refuses to let the
            // NetworkManager itself carry a NetworkObject.
            var go = new GameObject("Game Manager");
            go.AddComponent<NetworkObject>();
            go.AddComponent<NetworkGameManager>();
        }

        private static void EnsureUI()
        {
            if (Object.FindAnyObjectByType<BoardUI>() != null)
            {
                return;
            }

            new GameObject("Game UI").AddComponent<BoardUI>();
        }
    }
}
