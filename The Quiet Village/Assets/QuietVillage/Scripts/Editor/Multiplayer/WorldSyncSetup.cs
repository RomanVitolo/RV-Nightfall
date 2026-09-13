using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using QuietVillage.Multiplayer.Bridge.World;
using UHFPS.Runtime;
using Unity.Netcode;
using Unity.Netcode.Components;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Object = UnityEngine.Object;

namespace QuietVillage.Multiplayer.Bridge.EditorTools
{
    /// <summary>
    /// Makes a level's interactable objects replicate: adds the level's <see cref="WorldSync"/> and a
    /// sync entity to every object that needs one, each with a stable id.
    /// </summary>
    /// <remarks>
    /// Chooses the entity per object: pickups get <see cref="SyncedPickup"/>, doors and the like
    /// <see cref="SyncedDynamicObject"/>, physics props <see cref="SyncedRigidbody"/>, and every other shared
    /// saveable a <see cref="SyncedSaveable"/>. Idempotent — re-run it after adding objects to the level. An
    /// entity a designer has disabled is left disabled, which is how a single object is opted out.
    ///
    /// From the menu it works on the open level. Ids are GlobalObjectIds, which include the scene's own GUID, so
    /// each level's ids are its own and a save from one level never matches objects in another.
    /// </remarks>
    public static class WorldSyncSetup
    {
        private const string WorldSyncObjectName = "WorldSync";

        // Never replicated. Per-player experiences: each player gets their own cutscene, dialogue, objective,
        // jumpscare and instrument reading, rather than having them marked done because someone else triggered
        // them. And AI, which runs independently on every client — replicating its state would fight that, and
        // networking AI properly is separate work.
        private static readonly Type[] ExcludedTypes =
        {
            typeof(CutsceneTrigger), typeof(DialogueTrigger), typeof(ObjectiveTrigger), typeof(JumpscareTrigger),
            typeof(LookAtTrigger), typeof(ThermometerTemp), typeof(UVFlashlightReveal), typeof(NPCHealth)
        };

        // Replicated by a dedicated entity, so never by the generic save-state one as well.
        private static readonly Type[] DedicatedTypes =
        {
            typeof(DynamicObject), typeof(InteractableItem), typeof(DraggableItem)
        };


        [MenuItem("Tools/Quiet Village/Multiplayer/Set Up World Sync")]
        private static void RunFromMenu()
        {
            if (EditorApplication.isPlaying || EditorApplication.isPlayingOrWillChangePlaymode)
            {
                EditorUtility.DisplayDialog("World Sync Setup", "Exit Play Mode first.", "OK");
                return;
            }

            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;

            var scenePath = LevelSetup.OpenLevelScenePath("World Sync Setup");
            if (scenePath == null) return;

            Execute(scenePath);
        }

        /// <summary>Entry point for <c>-executeMethod</c>, so this can run headlessly.</summary>
        /// <remarks>Sets up the scene named by <c>-levelScene &lt;path&gt;</c>, or GameplayScene without one.</remarks>
        public static void RunFromCommandLine()
        {
            var succeeded = Execute(LevelSetup.CommandLineScenePath());

            if (Application.isBatchMode) EditorApplication.Exit(succeeded ? 0 : 1);
        }

        /// <summary>Sets up World Sync in one level scene, which is opened, saved and left open.</summary>
        internal static bool Execute(string scenePath)
        {
            var scene = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Single);
            if (!scene.IsValid())
            {
                Debug.LogError($"World Sync Setup: could not open {scenePath}.");
                return false;
            }

            var report = new StringBuilder($"{scene.name}:\n");
            EnsureWorldSyncObject(scene.name, report);

            var added = new Dictionary<string, int>();
            AddPickups(added);
            AddDynamicObjects(added, report);
            AddRigidbodies(added);
            AddExaminables(added);
            AddHidingPlaces(added);
            AddSaveables(added, report);
            AddNetworkedNpcs(added, report);

            foreach (var entry in added.OrderBy(pair => pair.Key))
                report.AppendLine($"  {entry.Key}: {entry.Value} added");

            if (!AssignIds(report))
            {
                Debug.LogError($"World Sync Setup aborted.\n{report}");
                return false;
            }

            EditorSceneManager.MarkSceneDirty(scene);
            if (!EditorSceneManager.SaveScene(scene))
            {
                Debug.LogError($"World Sync Setup: could not save {scenePath}.\n{report}");
                return false;
            }

            if (!VerifyNetworkObjectHashes(scenePath, report))
            {
                Debug.LogError($"World Sync Setup aborted.\n{report}");
                return false;
            }

            Debug.Log($"World Sync Setup complete.\n{report}");
            return true;
        }

        // ---- WorldSync -------------------------------------------------------------------------------

        private static void EnsureWorldSyncObject(string sceneName, StringBuilder report)
        {
            var worldSync = Object.FindAnyObjectByType<WorldSync>(FindObjectsInactive.Include);
            if (worldSync == null)
            {
                var host = new GameObject(WorldSyncObjectName);
                host.AddComponent<NetworkObject>();
                worldSync = host.AddComponent<WorldSync>();
                report.AppendLine($"Created WorldSync in {sceneName}.");
            }

            var networkObject = worldSync.GetComponent<NetworkObject>();

            // It never moves and has no parent to follow; syncing either would only cost bandwidth.
            networkObject.SynchronizeTransform = false;
            networkObject.AutoObjectParentSync = false;
            EditorUtility.SetDirty(networkObject);
        }

        /// <summary>
        /// Confirms every in-scene NetworkObject's hash reached the scene file, re-saving once if not.
        /// </summary>
        /// <remarks>
        /// The same trap as the player prefab: the hash can look right in memory while the file still holds 0,
        /// and Netcode then cannot match the object between host and clients. The only proof is reading it back
        /// from a fresh load of the file. Covers WorldSync and every networked NPC.
        /// </remarks>
        private static bool VerifyNetworkObjectHashes(string scenePath, StringBuilder report)
        {
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var scene = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Single);
                var networkObjects = Object.FindObjectsByType<NetworkObject>(FindObjectsInactive.Include);

                if (Object.FindAnyObjectByType<WorldSync>(FindObjectsInactive.Include) == null)
                {
                    report.AppendLine("FAILED: WorldSync is missing after reload.");
                    return false;
                }

                var unhashed = networkObjects.Where(n => n.PrefabIdHash == 0).ToList();
                if (unhashed.Count == 0)
                {
                    report.AppendLine($"In-scene NetworkObjects with a hash on disk: {networkObjects.Length} " +
                                      $"({string.Join(", ", networkObjects.Select(n => $"{n.name} {n.PrefabIdHash}"))})");
                    return true;
                }

                // A freshly added object only gets a stable file id when first saved, and the hash is derived
                // from it — so the second save is the one that records it.
                foreach (var networkObject in unhashed) EditorUtility.SetDirty(networkObject);
                EditorSceneManager.MarkSceneDirty(scene);
                EditorSceneManager.SaveScene(scene);
            }

            report.AppendLine("FAILED: some in-scene NetworkObjects still have hash 0 on disk; clients could not match them.");
            return false;
        }

        // ---- Entities --------------------------------------------------------------------------------

        private static void AddPickups(Dictionary<string, int> added)
        {
            foreach (var item in Object.FindObjectsByType<InteractableItem>(FindObjectsInactive.Include))
            {
                // Items that are only examined are never consumed, so there is nothing to replicate.
                if (item == null || item.DisableType == InteractableItem.DisableTypeEnum.None) continue;

                var entity = GetOrAdd<SyncedPickup>(item.gameObject, added);
                HeroPlayerSetup.AssignSerializedReference(entity, "m_item", item);
            }
        }

        private static void AddDynamicObjects(Dictionary<string, int> added, StringBuilder report)
        {
            foreach (var dynamicObject in Object.FindObjectsByType<DynamicObject>(FindObjectsInactive.Include))
            {
                if (dynamicObject == null) continue;

                if (dynamicObject.target == null)
                {
                    report.AppendLine($"  skipped DynamicObject '{dynamicObject.name}': no target transform to replicate.");
                    continue;
                }

                var entity = GetOrAdd<SyncedDynamicObject>(dynamicObject.gameObject, added);
                HeroPlayerSetup.AssignSerializedReference(entity, "m_dynamicObject", dynamicObject);
            }
        }

        private static void AddRigidbodies(Dictionary<string, int> added)
        {
            var bodies = new HashSet<Rigidbody>();

            // Props made to be carried, and pickups that can be knocked about before someone takes them.
            foreach (var draggable in Object.FindObjectsByType<DraggableItem>(FindObjectsInactive.Include))
                if (draggable != null && draggable.TryGetComponent<Rigidbody>(out var body)) bodies.Add(body);

            foreach (var item in Object.FindObjectsByType<InteractableItem>(FindObjectsInactive.Include))
                if (item != null && item.TryGetComponent<Rigidbody>(out var body)) bodies.Add(body);

            foreach (var body in bodies)
            {
                var entity = GetOrAdd<SyncedRigidbody>(body.gameObject, added);
                HeroPlayerSetup.AssignSerializedReference(entity, "m_body", body);
            }
        }

        /// <summary>
        /// Every object a player can examine gets the examine lock, and — unless physics already streams it —
        /// a transform stream, so others see it lifted into the examiner's view.
        /// </summary>
        private static void AddExaminables(Dictionary<string, int> added)
        {
            foreach (var item in Object.FindObjectsByType<InteractableItem>(FindObjectsInactive.Include))
            {
                if (item == null || item.ExamineType == InteractableItem.ExamineTypeEnum.None) continue;

                var examineLock = GetOrAdd<SyncedExamineLock>(item.gameObject, added);
                HeroPlayerSetup.AssignSerializedReference(examineLock, "m_item", item);

                // One motion stream per transform: a physics prop's SyncedRigidbody already carries it.
                if (item.GetComponent<SyncedMotionEntity>() == null) GetOrAdd<SyncedTransform>(item.gameObject, added);
            }
        }

        private static void AddHidingPlaces(Dictionary<string, int> added)
        {
            foreach (var place in Object.FindObjectsByType<HideInteract>(FindObjectsInactive.Include))
            {
                if (place == null) continue;

                var entity = GetOrAdd<SyncedHidingPlace>(place.gameObject, added);
                HeroPlayerSetup.AssignSerializedReference(entity, "m_hideInteract", place);
            }
        }

        /// <summary>
        /// Makes each NPC a server-simulated network object: NetworkObject, server-authority NetworkTransform
        /// and NetworkAnimator, and <see cref="NetworkedNpc"/> for targeting and health.
        /// </summary>
        /// <remarks>
        /// Added to the scene instances rather than to UHFPS's Zombie prefab, so the vendor prefab is untouched
        /// and the override lives with the level that needs it.
        /// </remarks>
        private static void AddNetworkedNpcs(Dictionary<string, int> added, StringBuilder report)
        {
            foreach (var machine in Object.FindObjectsByType<NPCStateMachine>(FindObjectsInactive.Include))
            {
                if (machine == null) continue;

                var root = machine.gameObject;

                var networkObject = GetOrAdd<NetworkObject>(root, added);

                // NPCs sit under ordinary level folders; there is no networked parent to follow.
                networkObject.AutoObjectParentSync = false;
                EditorUtility.SetDirty(networkObject);

                ConfigureNpcTransform(GetOrAdd<NetworkTransform>(root, added));

                var animator = machine.Animator != null ? machine.Animator : root.GetComponentInChildren<Animator>(true);
                if (animator != null)
                {
                    var networkAnimator = GetOrAdd<NetworkAnimator>(root, added);
                    networkAnimator.Animator = animator;
                    EditorUtility.SetDirty(networkAnimator);
                }
                else
                {
                    report.AppendLine($"  WARNING: NPC '{root.name}' has no Animator; its animation will not replicate.");
                }

                var npc = GetOrAdd<NetworkedNpc>(root, added);
                HeroPlayerSetup.AssignSerializedReference(npc, "m_machine", machine);
                HeroPlayerSetup.AssignSerializedReference(npc, "m_health", root.GetComponentInChildren<NPCHealth>(true));
                HeroPlayerSetup.AssignSerializedReference(npc, "m_agent", root.GetComponent<UnityEngine.AI.NavMeshAgent>());
            }
        }

        /// <summary>Server-authoritative: only the host moves NPCs. Position plus facing; NPCs only turn about Y.</summary>
        private static void ConfigureNpcTransform(NetworkTransform networkTransform)
        {
            networkTransform.AuthorityMode = NetworkTransform.AuthorityModes.Server;

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
            EditorUtility.SetDirty(networkTransform);
        }

        /// <summary>Every saveable in the level except the excluded per-player and AI types.</summary>
        /// <remarks>
        /// Deliberately includes saveables nothing interacts with directly. Switches, puzzles and generators change
        /// other objects — lights, radios — through events, and <c>OnLoad</c> does not replay events on other
        /// clients. Replicating those objects' own state is what carries the effect across instead. One that
        /// changes by itself on every client is caught at runtime by SyncedSaveable's churn guard.
        /// </remarks>
        private static void AddSaveables(Dictionary<string, int> added, StringBuilder report)
        {
            var skipped = new Dictionary<string, int>();

            foreach (var behaviour in Object.FindObjectsByType<MonoBehaviour>(FindObjectsInactive.Include))
            {
                if (behaviour == null || !(behaviour is ISaveable)) continue;

                var type = behaviour.GetType();
                if (DedicatedTypes.Any(t => t.IsAssignableFrom(type))) continue;

                if (ExcludedTypes.Any(t => t.IsAssignableFrom(type)))
                {
                    skipped[type.Name] = skipped.TryGetValue(type.Name, out var count) ? count + 1 : 1;
                    continue;
                }

                var existing = behaviour.GetComponents<SyncedSaveable>().FirstOrDefault(s => s.Target == behaviour);
                if (existing != null) continue;

                var entity = behaviour.gameObject.AddComponent<SyncedSaveable>();
                HeroPlayerSetup.AssignSerializedReference(entity, "m_saveable", behaviour);
                Increment(added, nameof(SyncedSaveable));
            }

            if (skipped.Count > 0)
                report.AppendLine("  not replicated (per-player or AI): " +
                                  string.Join(", ", skipped.Select(pair => $"{pair.Key} x{pair.Value}")));
        }


        private static T GetOrAdd<T>(GameObject target, Dictionary<string, int> added) where T : Component
        {
            var existing = target.GetComponent<T>();
            if (existing != null) return existing;

            Increment(added, typeof(T).Name);
            return target.AddComponent<T>();
        }

        private static void Increment(Dictionary<string, int> added, string key) =>
            added[key] = added.TryGetValue(key, out var count) ? count + 1 : 1;

        // ---- Ids -------------------------------------------------------------------------------------

        /// <summary>
        /// Gives every entity the GlobalObjectId of the component it replicates, plus its kind.
        /// </summary>
        /// <remarks>
        /// Taken from the UHFPS component rather than the entity: the UHFPS component is already saved in the
        /// scene with a stable file id, whereas an entity added moments ago has none until the scene is saved.
        /// </remarks>
        private static bool AssignIds(StringBuilder report)
        {
            var seen = new Dictionary<string, WorldSyncEntity>();
            var total = 0;

            foreach (var entity in Object.FindObjectsByType<WorldSyncEntity>(FindObjectsInactive.Include))
            {
                if (entity == null) continue;

                var (target, kind) = TargetOf(entity);
                if (target == null)
                {
                    report.AppendLine($"FAILED: '{entity.name}' has a {entity.GetType().Name} with nothing to replicate.");
                    return false;
                }

                var globalId = GlobalObjectId.GetGlobalObjectIdSlow(target);
                if (globalId.identifierType == 0)
                {
                    report.AppendLine($"FAILED: '{entity.name}' has no GlobalObjectId yet; save the scene and re-run.");
                    return false;
                }

                var id = $"{globalId}#{kind}";
                if (seen.TryGetValue(id, out var other))
                {
                    report.AppendLine($"FAILED: '{entity.name}' and '{other.name}' resolved to the same id {id}.");
                    return false;
                }

                seen.Add(id, entity);
                HeroPlayerSetup.AssignSerializedValue(entity, "m_id", property => property.stringValue = id);
                total++;
            }

            var keys = seen.Keys.GroupBy(WorldSyncEntity.KeyFor).Where(group => group.Count() > 1).ToList();
            if (keys.Count > 0)
            {
                report.AppendLine($"FAILED: {keys.Count} id hash collisions; the wire keys would be ambiguous.");
                return false;
            }

            report.AppendLine($"{total} sync entities, all with unique ids.");
            return true;
        }

        private static (Object target, string kind) TargetOf(WorldSyncEntity entity)
        {
            var serialized = new SerializedObject(entity);

            return entity switch
            {
                SyncedPickup => (serialized.FindProperty("m_item")?.objectReferenceValue, "pickup"),
                SyncedDynamicObject => (serialized.FindProperty("m_dynamicObject")?.objectReferenceValue, "dynamic"),
                SyncedRigidbody => (serialized.FindProperty("m_body")?.objectReferenceValue, "body"),
                SyncedSaveable => (serialized.FindProperty("m_saveable")?.objectReferenceValue, "state"),
                SyncedExamineLock => (serialized.FindProperty("m_item")?.objectReferenceValue, "examine"),
                SyncedHidingPlace => (serialized.FindProperty("m_hideInteract")?.objectReferenceValue, "hide"),

                // Nothing to point at but the object itself; its Transform is saved in the scene with a stable id.
                SyncedTransform => (entity.transform, "transform"),
                _ => (null, null)
            };
        }
    }
}
