using System.Collections.Generic;
using System.IO;
using System.Text;
using Modules.Multiplayer.Bridge;
using Modules.Multiplayer.Scripts.Runtime.Player;
using Modules.Multiplayer.Scripts.Runtime.Sessions;
using Modules.Multiplayer.Scripts.Runtime.UI;
using Unity.Netcode;
using Unity.Netcode.Components;
using Unity.Netcode.Transports.UTP;
using UHFPS.Runtime;
using Unity.AI.Navigation;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEditorInternal;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.Rendering;

namespace Modules.Multiplayer.Bridge.EditorTools
{
    /// <summary>
    /// Authors the networked HEROPLAYER prefab and wires GameplayScene for multiplayer.
    /// </summary>
    /// <remarks>
    /// Lives in a plain <c>Editor</c> folder rather than an asmdef because it needs UHFPS types, which
    /// compile into the predefined Assembly-CSharp — and Unity forbids an asmdef from referencing that.
    /// The Editor folder still puts it in Assembly-CSharp-Editor, so no runtime assembly sees UnityEditor.
    ///
    /// Everything here runs through Unity's own APIs so GUIDs, fileIDs and GlobalObjectIdHash are
    /// Editor-generated. Hand-written YAML for these is silently invalid.
    /// </remarks>
    public static class HeroPlayerSetup
    {
        private const string SourcePrefabPath =
            "Assets/ThunderWire Studio/UHFPS/Content/Resources/Setup/HEROPLAYER.prefab";

        private const string VariantPath = "Assets/_NetworkPrefabs/NetworkedHeroPlayer.prefab";

        private const string ScenePath =
            "Assets/ThunderWire Studio/UHFPS/_Demo/Scenes/GameplayScene.unity";

        private const string AvatarPrefabPath =
            "Assets/Synty/PolygonHorrorMansion/Prefabs/Characters/Characters/SM_Chr_Jock_01.prefab";

        private const string AvatarModelPath =
            "Assets/Synty/PolygonHorrorMansion/Models/Characters.fbx";

        private const string AvatarControllerPath =
            "Assets/Starter Assets/Runtime/ThirdPersonController/Character/Animations/StarterAssetsThirdPerson.controller";

        private const string NetworkPrefabsListPath = "Assets/DefaultNetworkPrefabs.asset";
        private const string AvatarChildName = "RemoteAvatar";

        // Matches SessionService.MaxPlayers so every connecting client has its own point.
        private const int SpawnPointCount = 4;
        private const float SpawnHeightOffset = 0.2f;

        // Far enough apart that four CharacterControllers do not start inside one another, close
        // enough that players see each other immediately.
        private const float SpawnRingRadius = 1.25f;

        [MenuItem("Tools/Multiplayer/Set Up Networked HEROPLAYER")]
        private static void RunFromMenu()
        {
            if (EditorApplication.isPlaying || EditorApplication.isPlayingOrWillChangePlaymode)
            {
                EditorUtility.DisplayDialog("Networked HEROPLAYER Setup",
                    "Exit Play Mode first — network prefab hashes cannot be generated while playing.", "OK");
                return;
            }

            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;

            Execute();
        }

        /// <summary>Entry point for <c>-executeMethod</c>, so this can run headlessly.</summary>
        public static void RunFromCommandLine()
        {
            var succeeded = Execute();

            if (Application.isBatchMode) EditorApplication.Exit(succeeded ? 0 : 1);
        }

        /// <summary>
        /// Discards the existing spawn points and re-derives them from the NavMesh.
        /// </summary>
        /// <remarks>
        /// The normal setup deliberately leaves existing spawn points alone, since where players start
        /// is level design rather than something a tool should overwrite. This is the explicit opt-in
        /// for when the generated defaults were wrong.
        /// </remarks>
        [MenuItem("Tools/Multiplayer/Reset Player Spawn Points")]
        private static void ResetSpawnPointsFromMenu()
        {
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;

            ResetSpawnPoints();
        }

        /// <summary>Command-line form of <see cref="ResetSpawnPointsFromMenu"/>.</summary>
        public static void ResetSpawnPointsFromCommandLine()
        {
            var succeeded = ResetSpawnPoints();

            if (Application.isBatchMode) EditorApplication.Exit(succeeded ? 0 : 1);
        }

        /// <summary>
        /// Moves the per-player managers and the HUD off the scene and onto the player prefab.
        /// </summary>
        /// <remarks>
        /// One-way migration, run once. The HUD travels with the managers so their ~37 serialized
        /// references stay intact — copying those across at runtime instead would mean forty-odd
        /// hand-written assignments that break silently whenever UHFPS adds a UI field.
        /// </remarks>
        [MenuItem("Tools/Multiplayer/Move Managers And HUD Into Player")]
        private static void MoveManagersFromMenu()
        {
            if (EditorApplication.isPlaying || EditorApplication.isPlayingOrWillChangePlaymode)
            {
                EditorUtility.DisplayDialog("Move Managers", "Exit Play Mode first.", "OK");
                return;
            }

            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;

            MoveManagersIntoPlayer();
        }

        /// <summary>Command-line form of <see cref="MoveManagersFromMenu"/>.</summary>
        public static void MoveManagersIntoPlayerFromCommandLine()
        {
            var succeeded = MoveManagersIntoPlayer();

            if (Application.isBatchMode) EditorApplication.Exit(succeeded ? 0 : 1);
        }

        private static bool MoveManagersIntoPlayer()
        {
            var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            if (!scene.IsValid())
            {
                Debug.LogError($"Move Managers: could not open {ScenePath}.");
                return false;
            }

            var report = new StringBuilder();

            var sceneGameManager = Object.FindFirstObjectByType<GameManager>(FindObjectsInactive.Include);
            if (sceneGameManager == null)
            {
                report.AppendLine("No scene GameManager found — already migrated. Nothing to do.");
                Debug.Log($"Move Managers skipped.\n{report}");
                return true;
            }

            var hudRoot = FindHudRoot(sceneGameManager);
            if (hudRoot == null)
            {
                Debug.LogError(
                    $"Move Managers: could not find the HUD canvas from GameManager.GamePanel.\n{report}");
                return false;
            }

            var playerPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(VariantPath);
            if (playerPrefab == null)
            {
                Debug.LogError($"Move Managers: {VariantPath} not found. Run the HEROPLAYER setup first.");
                return false;
            }

            // Everything that holds a reference into the HUD has to travel with it. Leaving one behind
            // strands its references: OptionsManager alone lost all 28 of its section/option links,
            // stored in a nested list that cannot be rebound by name.
            var managers = CollectManagersToMove();

            var instance = (GameObject)PrefabUtility.InstantiatePrefab(playerPrefab);
            try
            {
                // Reparent first: once the HUD lives under the instance, applying the instance remaps
                // the managers' references to prefab-internal ones instead of dangling scene links.
                hudRoot.SetParent(instance.transform, false);
                report.AppendLine($"Moved HUD '{hudRoot.name}' under the player.");

                // CopyComponent/PasteComponentAsNew is the Inspector's own "Copy Component" path, so
                // every serialized value comes across without reflecting over the type.
                foreach (var manager in managers)
                {
                    CopyComponentOnto(manager, instance, report);
                }

                PrefabUtility.ApplyPrefabInstance(instance, InteractionMode.AutomatedAction);
                report.AppendLine("Applied to the player prefab.");
            }
            finally
            {
                Object.DestroyImmediate(instance);
            }

            // Remove the originals only after the apply succeeded, so a failure leaves the scene intact.
            foreach (var manager in managers)
            {
                DestroyComponent(manager, report);
            }

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            AssetDatabase.SaveAssets();

            Debug.Log($"Move Managers complete.\n{report}");
            return true;
        }

        /// <summary>
        /// The managers that must move onto the player prefab with the HUD.
        /// </summary>
        /// <remarks>
        /// The first three are per-player by nature and are coupled to each other by
        /// <c>GetComponent</c>, so they cannot be split up. The rest are here purely because they hold
        /// serialized references into the game UI — dialogue panel, objectives list, jumpscare overlay,
        /// saving icon, and the options menu's section/option links. Left in the scene, all of those
        /// references dangle the moment the UI moves.
        ///
        /// <c>GameLocalization</c> and <c>InputManager</c> are deliberately absent: they reference no
        /// UI and stay scene-global.
        /// </remarks>
        private static List<Component> CollectManagersToMove()
        {
            var managers = new List<Component>();

            AddIfPresent<PlayerPresenceManager>(managers);
            AddIfPresent<Inventory>(managers);
            AddIfPresent<GameManager>(managers);

            AddIfPresent<DialogueSystem>(managers);
            AddIfPresent<ObjectiveManager>(managers);
            AddIfPresent<JumpscareManager>(managers);
            AddIfPresent<SaveGameManager>(managers);
            AddIfPresent<OptionsManager>(managers);

            return managers;
        }

        private static void AddIfPresent<T>(List<Component> managers) where T : Component
        {
            var manager = Object.FindFirstObjectByType<T>(FindObjectsInactive.Include);
            if (manager != null) managers.Add(manager);
        }

        /// <summary>Walks up from a HUD panel to the outermost Canvas that contains it.</summary>
        private static Transform FindHudRoot(GameManager gameManager)
        {
            if (gameManager.GamePanel == null) return null;

            Transform outermostCanvas = null;
            for (var current = gameManager.GamePanel.transform; current != null; current = current.parent)
            {
                if (current.GetComponent<Canvas>() != null) outermostCanvas = current;
            }

            return outermostCanvas;
        }

        private static void CopyComponentOnto(Component source, GameObject target, StringBuilder report)
        {
            if (source == null)
            {
                report.AppendLine("WARNING: a manager was missing from the scene and was not moved.");
                return;
            }

            if (!ComponentUtility.CopyComponent(source) || !ComponentUtility.PasteComponentAsNew(target))
            {
                report.AppendLine($"WARNING: failed to copy {source.GetType().Name} onto the player.");
                return;
            }

            report.AppendLine($"Copied {source.GetType().Name} onto the player.");
        }

        private static void DestroyComponent(Component component, StringBuilder report)
        {
            if (component == null) return;

            report.AppendLine($"Removed {component.GetType().Name} from '{component.gameObject.name}'.");
            Undo.DestroyObjectImmediate(component);
        }

        private static bool ResetSpawnPoints()
        {
            var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            if (!scene.IsValid())
            {
                Debug.LogError($"Reset Spawn Points: could not open {ScenePath}.");
                return false;
            }

            var report = new StringBuilder();

            foreach (var existing in Object.FindObjectsByType<PlayerSpawnPoints>(
                         FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (existing == null) continue;

                report.AppendLine($"Removed existing '{existing.gameObject.name}'.");
                Undo.DestroyObjectImmediate(existing.gameObject);
            }

            ConfigureSpawnPoints(report);

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            AssetDatabase.SaveAssets();

            Debug.Log($"Reset Spawn Points complete.\n{report}");
            return true;
        }

        private static bool Execute()
        {
            var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            if (!scene.IsValid())
            {
                Debug.LogError($"Networked HEROPLAYER Setup: could not open {ScenePath}.");
                return false;
            }

            var report = new StringBuilder();

            var playerPrefab = BuildPlayerPrefab(report);
            if (playerPrefab == null)
            {
                Debug.LogError($"Networked HEROPLAYER Setup aborted.\n{report}");
                return false;
            }

            RegisterNetworkPrefab(playerPrefab, report);
            ConfigureScene(playerPrefab, report);

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene);
            AssetDatabase.SaveAssets();

            Debug.Log($"Networked HEROPLAYER Setup complete.\n{report}");
            return true;
        }

        private static GameObject BuildPlayerPrefab(StringBuilder report)
        {
            var source = AssetDatabase.LoadAssetAtPath<GameObject>(SourcePrefabPath);
            if (source == null)
            {
                report.AppendLine($"FAILED: HEROPLAYER prefab not found at {SourcePrefabPath}");
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

                // Saving a prefab instance produces a variant, so UHFPS updates to HEROPLAYER keep
                // flowing into the networked player instead of being forked away.
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

                if (!ConfigureHeadTransform(root, report)) return null;

                var avatar = ConfigureAvatar(root, report);
                if (avatar == null) return null;

                ConfigureBridgeComponents(root, avatar, report);

                PrefabUtility.SaveAsPrefabAsset(root, VariantPath);
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }

            return PersistAndVerifyHash(report);
        }

        /// <summary>
        /// Body: position only.
        /// </summary>
        /// <remarks>
        /// UHFPS runs its look controller in <c>LookForward</c> mode (<c>PlayerForward: 1</c>), which
        /// writes the entire look rotation onto the camera holder and leaves the player root
        /// unrotated. Syncing rotation here would spend bandwidth on a value that never changes.
        /// </remarks>
        private static void ConfigureBodyTransform(NetworkTransform networkTransform)
        {
            networkTransform.AuthorityMode = NetworkTransform.AuthorityModes.Owner;

            networkTransform.SyncPositionX = true;
            networkTransform.SyncPositionY = true;
            networkTransform.SyncPositionZ = true;

            networkTransform.SyncRotAngleX = false;
            networkTransform.SyncRotAngleY = false;
            networkTransform.SyncRotAngleZ = false;

            networkTransform.SyncScaleX = false;
            networkTransform.SyncScaleY = false;
            networkTransform.SyncScaleZ = false;

            networkTransform.InLocalSpace = false;
            networkTransform.Interpolate = true;
        }

        /// <summary>
        /// The whole look rotation, taken from UHFPS's own <c>PlayerManager.CameraHolder</c> rather
        /// than a guessed child name, so it survives HEROPLAYER being restructured.
        /// </summary>
        /// <remarks>
        /// Both yaw and pitch are synced here. <c>LookController</c> combines them into this one
        /// transform's local rotation, so syncing pitch alone would leave remote players permanently
        /// facing whichever way they spawned.
        /// </remarks>
        private static bool ConfigureHeadTransform(GameObject root, StringBuilder report)
        {
            var playerManager = root.GetComponent<PlayerManager>();
            if (playerManager == null)
            {
                report.AppendLine("FAILED: HEROPLAYER has no PlayerManager, cannot locate the camera holder.");
                return false;
            }

            var cameraHolder = playerManager.CameraHolder;
            if (cameraHolder == null)
            {
                report.AppendLine("FAILED: PlayerManager.CameraHolder is unassigned on the prefab.");
                return false;
            }

            var head = GetOrAddComponent<NetworkTransform>(cameraHolder.gameObject);
            head.AuthorityMode = NetworkTransform.AuthorityModes.Owner;

            head.SyncPositionX = false;
            head.SyncPositionY = false;
            head.SyncPositionZ = false;

            head.SyncRotAngleX = true;
            head.SyncRotAngleY = true;
            head.SyncRotAngleZ = false;

            head.SyncScaleX = false;
            head.SyncScaleY = false;
            head.SyncScaleZ = false;

            // Pitch is authored relative to the body, which is itself yawing.
            head.InLocalSpace = true;
            head.Interpolate = true;

            report.AppendLine($"Head NetworkTransform on '{cameraHolder.name}'.");
            return true;
        }

        /// <summary>Attaches the third-person body other clients will see.</summary>
        private static RemoteAvatarBinder ConfigureAvatar(GameObject root, StringBuilder report)
        {
            var existing = FindDirectChild(root.transform, AvatarChildName);
            GameObject avatarRoot;

            if (existing != null)
            {
                avatarRoot = existing.gameObject;
                report.AppendLine("Reusing existing RemoteAvatar child.");
            }
            else
            {
                var avatarSource = AssetDatabase.LoadAssetAtPath<GameObject>(AvatarPrefabPath);
                if (avatarSource == null)
                {
                    report.AppendLine($"FAILED: avatar prefab not found at {AvatarPrefabPath}");
                    return null;
                }

                avatarRoot = (GameObject)PrefabUtility.InstantiatePrefab(avatarSource, root.transform);
                avatarRoot.name = AvatarChildName;

                // HEROPLAYER's origin sits at the feet, as does the Synty rig, so no offset is needed.
                avatarRoot.transform.localPosition = Vector3.zero;
                avatarRoot.transform.localRotation = Quaternion.identity;

                report.AppendLine($"Added avatar '{avatarSource.name}'.");
            }

            var animator = GetOrAddComponent<Animator>(avatarRoot);

            var controller = AssetDatabase.LoadAssetAtPath<RuntimeAnimatorController>(AvatarControllerPath);
            if (controller == null)
            {
                report.AppendLine($"FAILED: animator controller not found at {AvatarControllerPath}");
                return null;
            }

            animator.runtimeAnimatorController = controller;

            // Both the Synty rig and the Starter Assets clips are Humanoid, so the clips retarget onto
            // this skeleton with no authoring. Without the Avatar assigned they would not play at all.
            var humanoidAvatar = LoadHumanoidAvatar(AvatarModelPath);
            if (humanoidAvatar == null)
            {
                report.AppendLine(
                    $"FAILED: no Humanoid Avatar in {AvatarModelPath}. Set its Rig to Humanoid and re-run.");
                return null;
            }

            animator.avatar = humanoidAvatar;
            animator.applyRootMotion = false;

            var binder = GetOrAddComponent<RemoteAvatarBinder>(root);
            AssignSerializedReference(binder, "m_avatarRoot", avatarRoot);
            AssignSerializedReference(binder, "m_animator", animator);

            report.AppendLine($"Avatar animator bound to {controller.name} with avatar {humanoidAvatar.name}.");
            return binder;
        }

        private static void ConfigureBridgeComponents(GameObject root, RemoteAvatarBinder avatar, StringBuilder report)
        {
            var setup = GetOrAddComponent<HeroPlayerNetworkSetup>(root);
            AssignSerializedReference(setup, "m_avatar", avatar);

            var locomotion = GetOrAddComponent<PlayerLocomotionSync>(root);
            AssignSerializedReference(locomotion, "m_avatar", avatar);
            AssignSerializedReference(locomotion, "m_stateMachine", root.GetComponent<PlayerStateMachine>());

            // The avatar takes its facing from the camera holder, since the root never rotates.
            var playerManager = root.GetComponent<PlayerManager>();
            if (playerManager != null)
            {
                AssignSerializedReference(locomotion, "m_lookTransform", playerManager.CameraHolder);
            }

            var health = GetOrAddComponent<PlayerHealthSync>(root);
            AssignSerializedReference(health, "m_playerHealth", root.GetComponent<PlayerHealth>());

            report.AppendLine("Bridge components attached and wired.");
        }

        private static GameObject PersistAndVerifyHash(StringBuilder report)
        {
            // OnValidate computes the hash during import and only flags the asset dirty, which
            // SaveAssets alone does not reliably flush to the .prefab. Force the write, then read it
            // back from a fresh import rather than trusting the in-memory value.
            AssetDatabase.ImportAsset(VariantPath, ImportAssetOptions.ForceUpdate);

            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(VariantPath);
            if (prefab == null || !prefab.TryGetComponent<NetworkObject>(out var networkObject))
            {
                report.AppendLine($"FAILED: could not reload {VariantPath}.");
                return null;
            }

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
                report.AppendLine("FAILED: GlobalObjectIdHash is still 0. The prefab would not spawn.");
                return null;
            }

            report.AppendLine($"NetworkObject hash: {networkObject.PrefabIdHash}");
            return prefab;
        }

        private static void RegisterNetworkPrefab(GameObject playerPrefab, StringBuilder report)
        {
            var list = AssetDatabase.LoadAssetAtPath<NetworkPrefabsList>(NetworkPrefabsListPath);
            if (list == null)
            {
                report.AppendLine($"WARNING: {NetworkPrefabsListPath} not found; skipped registration.");
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
            RemoveScenePlayer(report);
            ConfigureNetworkManager(playerPrefab, report);
            ConfigureSessionRoot(report);
            ConfigureSpawnPoints(report);
            ConfigureSceneReferences(report);
        }

        /// <summary>
        /// Wires the post-processing volumes the HUD move left behind.
        /// </summary>
        /// <remarks>
        /// Matched by name once, at author time. If the scene renames them, re-point the two fields in
        /// the Inspector — the runtime only reads the serialized references, never the names.
        /// </remarks>
        private static void ConfigureSceneReferences(StringBuilder report)
        {
            if (Object.FindFirstObjectByType<SceneGameReferences>(FindObjectsInactive.Include) != null)
            {
                report.AppendLine("SceneGameReferences already present; left as authored.");
                return;
            }

            var host = new GameObject("SceneGameReferences");
            Undo.RegisterCreatedObjectUndo(host, "Create SceneGameReferences");
            var references = host.AddComponent<SceneGameReferences>();

            Volume global = null;
            Volume health = null;

            foreach (var volume in Object.FindObjectsByType<Volume>(
                         FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (volume == null) continue;

                if (volume.gameObject.name == "GlobalVolume") global = volume;
                else if (volume.gameObject.name == "HealthVolume") health = volume;
            }

            AssignSerializedReference(references, "m_globalPostProcessing", global);
            AssignSerializedReference(references, "m_healthPostProcessing", health);

            if (global == null || health == null)
            {
                report.AppendLine(
                    "WARNING: could not find GlobalVolume and/or HealthVolume by name. " +
                    "Assign them on SceneGameReferences by hand, or post-processing will be missing.");
                return;
            }

            report.AppendLine("SceneGameReferences wired to GlobalVolume and HealthVolume.");
        }

        /// <summary>
        /// Creates spawn points on walkable ground, since the prefab's own origin is empty space.
        /// </summary>
        private static void ConfigureSpawnPoints(StringBuilder report)
        {
            if (Object.FindFirstObjectByType<PlayerSpawnPoints>(FindObjectsInactive.Include) != null)
            {
                report.AppendLine("PlayerSpawnPoints already in the scene; left as authored.");
                return;
            }

            var positions = SampleWalkablePositions(SpawnPointCount);
            if (positions.Count == 0)
            {
                report.AppendLine(
                    "WARNING: could not find walkable ground for spawn points. Add a PlayerSpawnPoints " +
                    "component and place its points by hand, or players will spawn at the origin.");
                return;
            }

            var root = new GameObject("PlayerSpawnPoints");
            Undo.RegisterCreatedObjectUndo(root, "Create PlayerSpawnPoints");
            var spawnPoints = root.AddComponent<PlayerSpawnPoints>();

            var points = new List<Transform>();
            for (var i = 0; i < positions.Count; i++)
            {
                var point = new GameObject($"Spawn {i}").transform;
                point.SetParent(root.transform);
                point.position = positions[i];
                points.Add(point);
            }

            var serializedObject = new SerializedObject(spawnPoints);
            var listProperty = serializedObject.FindProperty("m_spawnPoints");
            listProperty.arraySize = points.Count;
            for (var i = 0; i < points.Count; i++)
            {
                listProperty.GetArrayElementAtIndex(i).objectReferenceValue = points[i];
            }

            serializedObject.ApplyModifiedPropertiesWithoutUndo();
            report.AppendLine($"Created {points.Count} spawn points on walkable ground.");
        }

        /// <summary>
        /// Finds a tight cluster of standing positions on walkable ground.
        /// </summary>
        /// <remarks>
        /// Prefers the baked NavMesh, because every point on it is walkable by construction. Players
        /// are placed in a small ring rather than spread across the level: this is co-op, and four
        /// players who cannot see each other make the whole thing untestable.
        /// </remarks>
        private static List<Vector3> SampleWalkablePositions(int count)
        {
            var results = new List<Vector3>();

            if (TryGetNavMeshSeed(out var seed))
            {
                for (var i = 0; i < count; i++)
                {
                    var angle = i * Mathf.PI * 2f / count;
                    var probe = seed + new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * SpawnRingRadius;

                    // Snap back onto the NavMesh so a ring point that lands in a wall is corrected.
                    results.Add(NavMesh.SamplePosition(probe, out var hit, SpawnRingRadius * 2f, NavMesh.AllAreas)
                        ? hit.position
                        : seed);
                }
            }
            else
            {
                // No NavMesh loaded — fall back to whatever floor a downward ray finds.
                var candidates = RaycastFloorCandidates();
                if (candidates.Count == 0) return results;

                var centre = candidates[candidates.Count / 2];
                for (var i = 0; i < count; i++)
                {
                    var angle = i * Mathf.PI * 2f / count;
                    results.Add(centre + new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * SpawnRingRadius);
                }
            }

            // Lift clear of the floor so the CharacterController does not start embedded in it.
            for (var i = 0; i < results.Count; i++)
            {
                results[i] += Vector3.up * SpawnHeightOffset;
            }

            return results;
        }

        /// <summary>
        /// Picks a central point on the baked NavMesh to cluster spawns around.
        /// </summary>
        /// <remarks>
        /// NavMeshSurface only registers its data at runtime, so in the Editor the triangulation is
        /// empty until <c>AddData</c> is called explicitly.
        /// </remarks>
        private static bool TryGetNavMeshSeed(out Vector3 seed)
        {
            seed = Vector3.zero;

            foreach (var surface in Object.FindObjectsByType<NavMeshSurface>(
                         FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (surface != null && surface.navMeshData != null) surface.AddData();
            }

            var triangulation = NavMesh.CalculateTriangulation();
            if (triangulation.vertices == null || triangulation.vertices.Length == 0) return false;

            var centroid = Vector3.zero;
            foreach (var vertex in triangulation.vertices)
            {
                centroid += vertex;
            }

            centroid /= triangulation.vertices.Length;

            // The centroid itself can sit over a hole, so use the nearest actual walkable vertex.
            var nearestDistance = float.MaxValue;
            foreach (var vertex in triangulation.vertices)
            {
                var distance = Vector3.SqrMagnitude(vertex - centroid);
                if (distance >= nearestDistance) continue;

                nearestDistance = distance;
                seed = vertex;
            }

            return true;
        }

        private static List<Vector3> RaycastFloorCandidates()
        {
            var hits = new List<Vector3>();

            var bounds = new Bounds();
            var hasBounds = false;

            foreach (var renderer in Object.FindObjectsByType<MeshRenderer>(
                         FindObjectsInactive.Exclude, FindObjectsSortMode.None))
            {
                if (renderer == null) continue;

                if (!hasBounds)
                {
                    bounds = renderer.bounds;
                    hasBounds = true;
                }
                else
                {
                    bounds.Encapsulate(renderer.bounds);
                }
            }

            if (!hasBounds) return hits;

            // Probe a coarse grid from above the level and keep whatever floor we land on.
            const int steps = 8;
            for (var x = 0; x < steps; x++)
            {
                for (var z = 0; z < steps; z++)
                {
                    var origin = new Vector3(
                        Mathf.Lerp(bounds.min.x, bounds.max.x, (x + 0.5f) / steps),
                        bounds.max.y + 10f,
                        Mathf.Lerp(bounds.min.z, bounds.max.z, (z + 0.5f) / steps));

                    if (Physics.Raycast(origin, Vector3.down, out var hit, bounds.size.y + 50f))
                    {
                        hits.Add(hit.point);
                    }
                }
            }

            return hits;
        }

        /// <summary>
        /// Removes the unpacked HEROPLAYER copy and clears the now-dangling presence reference.
        /// </summary>
        private static void RemoveScenePlayer(StringBuilder report)
        {
            foreach (var playerManager in Object.FindObjectsByType<PlayerManager>(
                         FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                if (playerManager == null) continue;

                report.AppendLine($"Removed in-scene player '{playerManager.gameObject.name}'.");
                Undo.DestroyObjectImmediate(playerManager.gameObject);
            }

            var presence = Object.FindFirstObjectByType<PlayerPresenceManager>(FindObjectsInactive.Include);
            if (presence == null)
            {
                // Expected once the managers have moved onto the prefab: the only PlayerPresenceManager
                // now lives there, and it points at its own GameObject.
                report.AppendLine("No scene PlayerPresenceManager — it lives on the player prefab now.");
                return;
            }

            // Awake early-returns on null and HeroPlayerNetworkSetup calls BindPlayer once the local
            // player spawns. Leaving a stale reference here would point AI and doors at a dead object.
            presence.Player = null;
            EditorUtility.SetDirty(presence);
            report.AppendLine("Cleared PlayerPresenceManager.Player; it is bound at runtime on spawn.");
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

            // Relay rewrites the endpoint when a session starts; these are only resting defaults.
            networkManager.NetworkConfig.NetworkTransport = transport;
            networkManager.NetworkConfig.PlayerPrefab = playerPrefab;

            EditorUtility.SetDirty(networkManager);
            report.AppendLine("NetworkManager configured with the networked HEROPLAYER.");
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

        private static Avatar LoadHumanoidAvatar(string modelPath)
        {
            foreach (var asset in AssetDatabase.LoadAllAssetRepresentationsAtPath(modelPath))
            {
                if (asset is Avatar avatar && avatar.isHuman) return avatar;
            }

            return null;
        }

        private static Transform FindDirectChild(Transform parent, string childName)
        {
            for (var i = 0; i < parent.childCount; i++)
            {
                var child = parent.GetChild(i);
                if (child != null && child.name == childName) return child;
            }

            return null;
        }

        private static T GetOrAddComponent<T>(GameObject target) where T : Component
        {
            var existing = target.GetComponent<T>();
            return existing != null ? existing : target.AddComponent<T>();
        }

        /// <summary>
        /// Writes a [SerializeField] private reference through Unity's serialization API, which is the
        /// supported route and avoids reflecting into the type.
        /// </summary>
        private static void AssignSerializedReference(Object target, string propertyPath, Object value)
        {
            if (target == null) return;

            var serializedObject = new SerializedObject(target);
            var property = serializedObject.FindProperty(propertyPath);

            if (property == null)
            {
                Debug.LogWarning(
                    $"HeroPlayerSetup: '{propertyPath}' not found on {target.GetType().Name}.", target);
                return;
            }

            property.objectReferenceValue = value;
            serializedObject.ApplyModifiedPropertiesWithoutUndo();
        }
    }
}
