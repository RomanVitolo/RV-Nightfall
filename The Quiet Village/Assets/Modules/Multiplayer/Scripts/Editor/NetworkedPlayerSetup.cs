using System.IO;
using System.Text;
using Modules.Multiplayer.Scripts.Runtime.Cameras;
using Modules.Multiplayer.Scripts.Runtime.Player;
using Modules.Multiplayer.Scripts.Runtime.Sessions;
using Modules.Multiplayer.Scripts.Runtime.UI;
using Unity.Cinemachine;
using Unity.Netcode;
using Unity.Netcode.Components;
using Unity.Netcode.Transports.UTP;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using StarterAssetsFirstPersonController =
    Starter_Assets.Runtime.FirstPersonController.Scripts.FirstPersonController;

namespace Modules.Multiplayer.Scripts.Editor
{
    /// <summary>
    /// Performs the prefab and scene authoring for the networked first-person player.
    /// </summary>
    /// <remarks>
    /// The authoring runs through Unity's own APIs rather than being written into the .prefab and
    /// .unity files directly, so GlobalObjectIdHash, fileIDs and GUIDs are all Editor-generated —
    /// the only way they come out valid. It also makes the setup repeatable instead of a
    /// twenty-step manual checklist.
    /// </remarks>
    public static class NetworkedPlayerSetup
    {
        private const string SourcePrefabPath =
            "Assets/Starter Assets/Runtime/FirstPersonController/Prefabs/PlayerCapsule.prefab";

        private const string VariantPath = "Assets/_NetworkPrefabs/NetworkedPlayer.prefab";

        private const string ScenePath =
            "Assets/Starter Assets/Sample/FirstPersonController/Playground.unity";

        private const string NetworkPrefabsListPath = "Assets/DefaultNetworkPrefabs.asset";
        private const string CameraRootName = "PlayerCameraRoot";

        [MenuItem("Tools/Multiplayer/Set Up Networked Player")]
        private static void RunFromMenu()
        {
            // NetworkObject.OnValidate refuses to generate GlobalObjectIdHash while the Editor is in
            // or entering play mode, so the prefab would be saved with a zero hash.
            if (EditorApplication.isPlaying || EditorApplication.isPlayingOrWillChangePlaymode)
            {
                EditorUtility.DisplayDialog("Networked Player Setup",
                    "Exit Play Mode before running this — network prefab hashes cannot be generated while playing.",
                    "OK");
                return;
            }

            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;

            Execute();
        }

        /// <summary>
        /// Entry point for <c>-executeMethod</c>, so the same authoring can run headlessly.
        /// </summary>
        /// <remarks>
        /// Exits with a non-zero code on failure — in batch mode a logged error alone would still
        /// report success to the calling process.
        /// </remarks>
        public static void RunFromCommandLine()
        {
            var succeeded = Execute();

            if (Application.isBatchMode) EditorApplication.Exit(succeeded ? 0 : 1);
        }

        private static bool Execute()
        {
            var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            if (!scene.IsValid())
            {
                Debug.LogError($"Networked Player Setup: could not open {ScenePath}.");
                return false;
            }

            var report = new StringBuilder();

            var playerPrefab = CreateOrUpdatePlayerPrefab(report);
            if (playerPrefab == null)
            {
                Debug.LogError($"Networked Player Setup aborted.\n{report}");
                return false;
            }

            RegisterNetworkPrefab(playerPrefab, report);
            ConfigureScene(playerPrefab, report);

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            AssetDatabase.SaveAssets();

            Debug.Log($"Networked Player Setup complete.\n{report}");
            return true;
        }

        private static GameObject CreateOrUpdatePlayerPrefab(StringBuilder report)
        {
            var source = AssetDatabase.LoadAssetAtPath<GameObject>(SourcePrefabPath);
            if (source == null)
            {
                report.AppendLine($"FAILED: source prefab not found at {SourcePrefabPath}");
                return null;
            }

            if (AssetDatabase.LoadAssetAtPath<GameObject>(VariantPath) == null)
            {
                var directory = Path.GetDirectoryName(VariantPath);
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                    AssetDatabase.Refresh();
                }

                // Saving a prefab *instance* as an asset produces a variant, so Starter Assets updates
                // keep flowing into our player instead of being forked away.
                var instance = (GameObject)PrefabUtility.InstantiatePrefab(source);
                try
                {
                    PrefabUtility.SaveAsPrefabAsset(instance, VariantPath);
                    report.AppendLine($"Created prefab variant {VariantPath}");
                }
                finally
                {
                    Object.DestroyImmediate(instance);
                }
            }
            else
            {
                report.AppendLine($"Reusing existing {VariantPath}");
            }

            var root = PrefabUtility.LoadPrefabContents(VariantPath);
            try
            {
                GetOrAddComponent<NetworkObject>(root);

                ConfigureBodyTransform(GetOrAddComponent<NetworkTransform>(root));

                var cameraRoot = FindDeepChild(root.transform, CameraRootName);
                if (cameraRoot == null)
                {
                    report.AppendLine($"FAILED: no '{CameraRootName}' child under {root.name}");
                    return null;
                }

                ConfigureHeadTransform(GetOrAddComponent<NetworkTransform>(cameraRoot.gameObject));

                var setup = GetOrAddComponent<PlayerNetworkSetup>(root);
                AssignSerializedReference(setup, "m_cameraRoot", cameraRoot);

                PrefabUtility.SaveAsPrefabAsset(root, VariantPath);
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }

            // The prefab contents above live in a preview scene, where GlobalObjectId resolves to the
            // null type and NetworkObject.OnValidate bails out. Reimporting runs OnValidate against
            // the persisted asset, which is what actually mints the hash.
            AssetDatabase.ImportAsset(VariantPath, ImportAssetOptions.ForceUpdate);

            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(VariantPath);
            if (prefab == null)
            {
                report.AppendLine($"FAILED: could not reload {VariantPath} after import");
                return null;
            }

            if (!prefab.TryGetComponent<NetworkObject>(out var networkObject))
            {
                report.AppendLine("FAILED: NetworkObject missing from the saved prefab.");
                return null;
            }

            // OnValidate computes the hash during import and only flags the asset dirty, which
            // AssetDatabase.SaveAssets alone does not reliably flush back to the .prefab file — the
            // asset then ships with a zero hash and spawning silently fails. Force the write, then
            // read it back from a fresh import instead of trusting the in-memory value.
            EditorUtility.SetDirty(networkObject);
            PrefabUtility.SavePrefabAsset(prefab);
            AssetDatabase.SaveAssets();
            AssetDatabase.ImportAsset(VariantPath, ImportAssetOptions.ForceUpdate);

            prefab = AssetDatabase.LoadAssetAtPath<GameObject>(VariantPath);
            if (prefab == null || !prefab.TryGetComponent(out networkObject))
            {
                report.AppendLine($"FAILED: could not reload {VariantPath} after persisting the hash.");
                return null;
            }

            if (networkObject.PrefabIdHash == 0)
            {
                report.AppendLine(
                    "FAILED: GlobalObjectIdHash is still 0 after saving. The prefab would not spawn.");
                return null;
            }

            report.AppendLine($"NetworkObject hash: {networkObject.PrefabIdHash}");
            return prefab;
        }

        /// <summary>Body: position plus yaw only — pitch belongs to the head.</summary>
        private static void ConfigureBodyTransform(NetworkTransform networkTransform)
        {
            networkTransform.AuthorityMode = NetworkTransform.AuthorityModes.Owner;

            networkTransform.SyncPositionX = true;
            networkTransform.SyncPositionY = true;
            networkTransform.SyncPositionZ = true;

            networkTransform.SyncRotAngleX = false;
            networkTransform.SyncRotAngleY = true;
            networkTransform.SyncRotAngleZ = false;

            networkTransform.SyncScaleX = false;
            networkTransform.SyncScaleY = false;
            networkTransform.SyncScaleZ = false;

            networkTransform.InLocalSpace = false;
            networkTransform.Interpolate = true;
        }

        /// <summary>Head: local pitch only, which is what makes remote players look up and down.</summary>
        private static void ConfigureHeadTransform(NetworkTransform networkTransform)
        {
            networkTransform.AuthorityMode = NetworkTransform.AuthorityModes.Owner;

            networkTransform.SyncPositionX = false;
            networkTransform.SyncPositionY = false;
            networkTransform.SyncPositionZ = false;

            networkTransform.SyncRotAngleX = true;
            networkTransform.SyncRotAngleY = false;
            networkTransform.SyncRotAngleZ = false;

            networkTransform.SyncScaleX = false;
            networkTransform.SyncScaleY = false;
            networkTransform.SyncScaleZ = false;

            // Pitch is authored relative to the body, which is itself rotating.
            networkTransform.InLocalSpace = true;
            networkTransform.Interpolate = true;
        }

        private static void RegisterNetworkPrefab(GameObject playerPrefab, StringBuilder report)
        {
            var list = AssetDatabase.LoadAssetAtPath<NetworkPrefabsList>(NetworkPrefabsListPath);
            if (list == null)
            {
                report.AppendLine($"WARNING: {NetworkPrefabsListPath} not found; skipped prefab registration.");
                return;
            }

            if (list.Contains(playerPrefab))
            {
                report.AppendLine("Prefab already registered in DefaultNetworkPrefabs.");
                return;
            }

            list.Add(new NetworkPrefab { Prefab = playerPrefab });
            EditorUtility.SetDirty(list);
            report.AppendLine("Registered prefab in DefaultNetworkPrefabs.");
        }

        private static void ConfigureScene(GameObject playerPrefab, StringBuilder report)
        {
            RemoveStarterAssetsPlayers(report);
            ConfigureNetworkManager(playerPrefab, report);
            ConfigureSessionRoot(report);
            ConfigureCameraRig(report);
        }

        private static void RemoveStarterAssetsPlayers(StringBuilder report)
        {
            var controllers = Object.FindObjectsByType<StarterAssetsFirstPersonController>(
                FindObjectsInactive.Include, FindObjectsSortMode.None);

            foreach (var controller in controllers)
            {
                if (controller == null) continue;

                // Netcode spawns the player now; a scene copy would be an unowned duplicate.
                report.AppendLine($"Removed in-scene player '{controller.gameObject.name}'.");
                Undo.DestroyObjectImmediate(controller.gameObject);
            }
        }

        private static void ConfigureNetworkManager(GameObject playerPrefab, StringBuilder report)
        {
            var networkManager = Object.FindFirstObjectByType<NetworkManager>(FindObjectsInactive.Include);
            if (networkManager == null)
            {
                var host = new GameObject("NetworkManager");
                Undo.RegisterCreatedObjectUndo(host, "Create NetworkManager");
                networkManager = host.AddComponent<NetworkManager>();
                report.AppendLine("Created NetworkManager.");
            }

            if (networkManager.NetworkConfig == null) networkManager.NetworkConfig = new NetworkConfig();

            var transport = networkManager.GetComponent<UnityTransport>();
            if (transport == null) transport = networkManager.gameObject.AddComponent<UnityTransport>();

            // Relay rewrites the transport endpoint when a session starts; these are only the resting
            // defaults so the NetworkManager is valid before anyone connects.
            networkManager.NetworkConfig.NetworkTransport = transport;
            networkManager.NetworkConfig.PlayerPrefab = playerPrefab;

            EditorUtility.SetDirty(networkManager);
            report.AppendLine("NetworkManager configured with player prefab and UnityTransport.");
        }

        private static void ConfigureSessionRoot(StringBuilder report)
        {
            var sessionService = Object.FindFirstObjectByType<SessionService>(FindObjectsInactive.Include);
            if (sessionService == null)
            {
                var host = new GameObject("SessionRoot");
                Undo.RegisterCreatedObjectUndo(host, "Create SessionRoot");
                sessionService = host.AddComponent<SessionService>();
                report.AppendLine("Created SessionRoot with SessionService.");
            }

            var debugUI = sessionService.GetComponent<SessionDebugUI>();
            if (debugUI == null) debugUI = sessionService.gameObject.AddComponent<SessionDebugUI>();

            AssignSerializedReference(debugUI, "m_sessionService", sessionService);
            EditorUtility.SetDirty(debugUI);
            report.AppendLine("SessionDebugUI wired to SessionService.");
        }

        private static void ConfigureCameraRig(StringBuilder report)
        {
            var cinemachineCamera = Object.FindFirstObjectByType<CinemachineCamera>(FindObjectsInactive.Include);
            if (cinemachineCamera == null)
            {
                report.AppendLine("WARNING: no CinemachineCamera in the scene; the local player will have no camera.");
                return;
            }

            // The old target was the in-scene player we just deleted; the owning player rebinds at spawn.
            cinemachineCamera.Follow = null;
            cinemachineCamera.LookAt = null;

            if (cinemachineCamera.GetComponent<PlayerCameraRig>() == null)
            {
                cinemachineCamera.gameObject.AddComponent<PlayerCameraRig>();
            }

            EditorUtility.SetDirty(cinemachineCamera);
            report.AppendLine($"PlayerCameraRig added to '{cinemachineCamera.gameObject.name}'.");
        }

        private static T GetOrAddComponent<T>(GameObject target) where T : Component
        {
            var existing = target.GetComponent<T>();
            return existing != null ? existing : target.AddComponent<T>();
        }

        private static Transform FindDeepChild(Transform parent, string childName)
        {
            foreach (var candidate in parent.GetComponentsInChildren<Transform>(true))
            {
                if (candidate != null && candidate.name == childName) return candidate;
            }

            return null;
        }

        /// <summary>
        /// Writes a [SerializeField] private reference through Unity's serialization API.
        /// </summary>
        /// <remarks>
        /// SerializedObject is the supported route for this — it is how every custom inspector writes
        /// private serialized fields, and it avoids reflecting into the type.
        /// </remarks>
        private static void AssignSerializedReference(Object target, string propertyPath, Object value)
        {
            var serializedObject = new SerializedObject(target);
            var property = serializedObject.FindProperty(propertyPath);

            if (property == null)
            {
                Debug.LogWarning(
                    $"Networked Player Setup: '{propertyPath}' not found on {target.GetType().Name}.", target);
                return;
            }

            property.objectReferenceValue = value;
            serializedObject.ApplyModifiedPropertiesWithoutUndo();
        }
    }
}
