using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using QuietVillage.Multiplayer.Characters;
using QuietVillage.Multiplayer.Flow;
using QuietVillage.Multiplayer.Sessions;
using Unity.Netcode;
using UHFPS.Runtime;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Object = UnityEngine.Object;

namespace QuietVillage.Multiplayer.Bridge.EditorTools
{
    /// <summary>
    /// Builds the character catalog and connects it to everything that uses it: the player prefab, the multiplayer
    /// root, and the lobby's preview stage.
    /// </summary>
    /// <remarks>
    /// Seeds a starting roster from the Sci-Fi pack only when the catalog is empty. After that the catalog is design
    /// data, like the level list: characters, roles and perk numbers are edited in its Inspector and re-running this
    /// never overwrites them. It only fills what is missing (a character's Humanoid Avatar, an item GUID from its
    /// title, the shared body controller) and reports what cannot work.
    ///
    /// Beside <see cref="HeroPlayerSetup"/> because it needs UHFPS types (the inventory database, the player's collider)
    /// and edits the same prefabs. Idempotent.
    /// </remarks>
    public static class CharacterSetup
    {
        private const string CatalogPath = ProjectPaths.CharacterCatalog;
        private const string StageObjectName = "CharacterPreviewStage";

        // Far below the lobby camera's view; see CharacterPreviewStage.
        private static readonly Vector3 StagePosition = new(0f, -500f, 0f);

        // Beyond this, a body visibly towers over or shrinks inside the collider it stands in.
        private const float MaxBodyHeightMismatch = 0.3f;

        private const string Pack = "Assets/Sci_Fi_Super_Pack";

        private sealed class Seed
        {
            public string Id;
            public string DisplayName;
            public string Role;
            public string Description;
            public (string Name, string Path)[] Variants;
            public Action<CharacterCatalog.Perks> Perks;
            public (string Title, int Quantity)[] Items = Array.Empty<(string, int)>();
        }

        // The starting roster. Every body shares the Worker's UE-Mannequin skeleton and a Humanoid rig, so all of them
        // play the Worker's animations. Swap bodies and tune numbers in the catalog afterwards.
        private static readonly Seed[] Roster =
        {
            new()
            {
                Id = "builder", DisplayName = "Sci-Fi Worker", Role = "Builder",
                Description = "Keeps the shelter standing. Every piece of scrap goes further in a Builder's hands.",
                Variants = new[]
                {
                    ("Standard", Pack + "/Character_Worker/Prefabs/Sci_Fi_Worker 01.prefab"),
                    ("Armoured", Pack + "/Character_Worker/Prefabs/Sci_Fi_Worker_02.prefab")
                },
                Perks = perks => perks.BarricadeRepair = 1.5f,
                Items = new[] { ("Scrap", 2) }
            },
            new()
            {
                Id = "medic", DisplayName = "Field Medic", Role = "Medic",
                Description = "Patches the team up between nights. Medical supplies heal far more when a Medic uses them.",
                Variants = new[]
                {
                    ("Style 1", Pack + "/Sci_Fi_Character_05/Prefabs/Sci_Fi_Character_05_01.prefab"),
                    ("Style 2", Pack + "/Sci_Fi_Character_05/Prefabs/Sci_Fi_Character_05_02.prefab"),
                    ("Style 3", Pack + "/Sci_Fi_Character_05/Prefabs/Sci_Fi_Character_05_03.prefab")
                },
                Perks = perks => perks.Healing = 1.5f,
                Items = new[] { ("First AID KIT", 1) }
            },
            new()
            {
                Id = "scout", DisplayName = "Head Hunter", Role = "Scout",
                Description = "First out and first back. Runs faster than anyone, and carries a lockpick for what is locked.",
                Variants = new[]
                {
                    ("Style 1", Pack + "/Character_Head_Hunter/Prefabs/Head_Hunter_01.prefab"),
                    ("Style 2", Pack + "/Character_Head_Hunter/Prefabs/Head_Hunter_02.prefab")
                },
                Perks = perks => perks.RunSpeed = 1.15f,
                Items = new[] { ("Lockpick", 1) }
            },
            new()
            {
                Id = "scavenger", DisplayName = "Seller", Role = "Scavenger",
                Description = "Brings back what the others leave behind. Starts with an extra row of inventory space.",
                Variants = new[]
                {
                    ("Backpack", Pack + "/Character_Seller/Prefabs/Seller_With_Backpack.prefab"),
                    ("Style 1", Pack + "/Character_Seller/Prefabs/Seller_01.prefab"),
                    ("Style 2", Pack + "/Character_Seller/Prefabs/Seller_02.prefab"),
                    ("Style 3", Pack + "/Character_Seller/Prefabs/Seller_03.prefab")
                },
                Perks = perks => perks.ExtraInventorySlots = 9
            }
        };

        [MenuItem("Tools/Quiet Village/Characters/Set Up Characters")]
        private static void RunFromMenu()
        {
            if (EditorApplication.isPlaying || EditorApplication.isPlayingOrWillChangePlaymode)
            {
                EditorUtility.DisplayDialog("Set Up Characters", "Exit Play Mode first.", "OK");
                return;
            }

            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;

            Execute();
        }

        [MenuItem("Tools/Quiet Village/Characters/Select Character Catalog")]
        private static void SelectCatalog()
        {
            var catalog = AssetDatabase.LoadAssetAtPath<CharacterCatalog>(CatalogPath);
            if (catalog == null)
            {
                EditorUtility.DisplayDialog("Character Catalog",
                    "No catalog yet. Run Tools > Quiet Village > Characters > Set Up Characters.", "OK");
                return;
            }

            Selection.activeObject = catalog;
            EditorGUIUtility.PingObject(catalog);
        }

        /// <summary>Entry point for <c>-executeMethod</c>, so this can run headlessly.</summary>
        public static void RunFromCommandLine()
        {
            var succeeded = Execute();

            if (Application.isBatchMode) EditorApplication.Exit(succeeded ? 0 : 1);
        }

        private static bool Execute()
        {
            var report = new StringBuilder();

            var catalog = EnsureCatalog(report);
            if (catalog == null)
            {
                Debug.LogError($"Set Up Characters aborted.\n{report}");
                return false;
            }

            var usable = CompleteCatalog(catalog, report);
            EditorUtility.SetDirty(catalog);
            AssetDatabase.SaveAssets();

            if (usable == 0)
            {
                Debug.LogError($"Set Up Characters: no character in {CatalogPath} can be played.\n{report}");
                return false;
            }

            if (!ConfigurePlayerPrefab(catalog, report) || !ConfigureRoot(catalog, report))
            {
                Debug.LogError($"Set Up Characters aborted.\n{report}");
                return false;
            }

            // Last: opening a scene unloads assets nothing references, and every asset edit above is saved by now.
            if (!ConfigureLobbyScene(report))
            {
                Debug.LogError($"Set Up Characters aborted.\n{report}");
                return false;
            }

            AssetDatabase.SaveAssets();
            Debug.Log($"Set Up Characters complete: {usable} playable character(s).\n{report}");
            return true;
        }

        // ---- Catalog ---------------------------------------------------------------------------------

        private static CharacterCatalog EnsureCatalog(StringBuilder report)
        {
            var catalog = AssetDatabase.LoadAssetAtPath<CharacterCatalog>(CatalogPath);
            if (catalog != null) return catalog;

            var folder = Path.GetDirectoryName(CatalogPath)?.Replace('\\', '/');
            if (!string.IsNullOrEmpty(folder) && !AssetDatabase.IsValidFolder(folder))
            {
                report.AppendLine($"FAILED: {folder} does not exist.");
                return null;
            }

            catalog = ScriptableObject.CreateInstance<CharacterCatalog>();
            AssetDatabase.CreateAsset(catalog, CatalogPath);
            report.AppendLine($"Created {CatalogPath}.");
            return catalog;
        }

        /// <summary>Seeds an empty catalog, fills in what each entry is missing, and checks every one.</summary>
        /// <returns>How many characters can be played.</returns>
        private static int CompleteCatalog(CharacterCatalog catalog, StringBuilder report)
        {
            var characters = catalog.EditableCharacters;
            if (characters.Count == 0) SeedRoster(characters, report);

            if (catalog.EditableBodyAnimator == null)
            {
                catalog.EditableBodyAnimator =
                    AssetDatabase.LoadAssetAtPath<RuntimeAnimatorController>(AvatarAnimatorAssets.ControllerPath);

                report.AppendLine(catalog.EditableBodyAnimator != null
                    ? $"Body controller: {AvatarAnimatorAssets.ControllerPath}."
                    : $"WARNING: {AvatarAnimatorAssets.ControllerPath} not found; run Set Up Networked HEROPLAYER first, " +
                      "or bodies will keep their prefab's controller.");
            }

            var items = ItemsByTitle();
            var colliderHeight = PlayerColliderHeight();
            var ids = new HashSet<string>();
            var usable = 0;

            foreach (var character in characters)
            {
                if (character == null) continue;

                var label = string.IsNullOrEmpty(character.Role) ? character.Id : character.Role;

                if (string.IsNullOrEmpty(character.Id) || !ids.Add(character.Id))
                {
                    report.AppendLine($"FAILED '{label}': its id is empty or used twice, so it cannot be chosen.");
                    continue;
                }

                var body = FirstBody(character);
                if (body == null)
                {
                    report.AppendLine($"FAILED '{label}': no variant has a body prefab.");
                    continue;
                }

                if (character.HumanoidAvatar == null) character.HumanoidAvatar = FindHumanoidAvatar(body);

                if (character.HumanoidAvatar == null || !character.HumanoidAvatar.isHuman || !character.HumanoidAvatar.isValid)
                {
                    report.AppendLine($"WARNING '{label}': no valid Humanoid Avatar for {body.name}. Set its model's Rig to " +
                                      "Humanoid, or assign the Avatar in the catalog; until then it cannot animate.");
                }

                CompleteItems(character, items, label, report);
                ReportBodyFit(character, body, colliderHeight, label, report);
                usable++;
            }

            return usable;
        }

        private static void SeedRoster(List<CharacterCatalog.Character> characters, StringBuilder report)
        {
            foreach (var seed in Roster)
            {
                var character = new CharacterCatalog.Character
                {
                    Id = seed.Id,
                    DisplayName = seed.DisplayName,
                    Role = seed.Role,
                    Description = seed.Description
                };

                foreach (var (name, path) in seed.Variants)
                {
                    var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                    if (prefab == null)
                    {
                        report.AppendLine($"WARNING: {seed.Role}'s look '{name}' not found at {path}; left out.");
                        continue;
                    }

                    character.Variants.Add(new CharacterCatalog.Variant { DisplayName = name, Prefab = prefab });
                }

                seed.Perks?.Invoke(character.Perks);

                foreach (var (title, quantity) in seed.Items)
                    character.StartingItems.Add(new CharacterCatalog.StartingItem { Title = title, Quantity = quantity });

                characters.Add(character);
            }

            report.AppendLine($"Seeded {Roster.Length} characters: Builder, Medic, Scout, Scavenger.");
        }

        private static GameObject FirstBody(CharacterCatalog.Character character)
        {
            if (character.Variants == null) return null;

            foreach (var variant in character.Variants)
            {
                if (variant != null && variant.Prefab != null) return variant.Prefab;
            }

            return null;
        }

        /// <summary>
        /// The Humanoid Avatar of the model a body prefab's skinned mesh comes from.
        /// </summary>
        /// <remarks>
        /// Read from the model rather than the prefab's Animator: several pack prefabs have no Animator at all, or one
        /// whose Avatar is empty, while their model is imported as Humanoid.
        /// </remarks>
        private static Avatar FindHumanoidAvatar(GameObject prefab)
        {
            var animator = prefab.GetComponent<Animator>();
            if (animator != null && animator.avatar != null && animator.avatar.isHuman) return animator.avatar;

            foreach (var skinned in prefab.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (skinned == null || skinned.sharedMesh == null) continue;

                var modelPath = AssetDatabase.GetAssetPath(skinned.sharedMesh);
                foreach (var asset in AssetDatabase.LoadAllAssetRepresentationsAtPath(modelPath))
                {
                    if (asset is Avatar avatar && avatar.isHuman) return avatar;
                }
            }

            return null;
        }

        private static void CompleteItems(CharacterCatalog.Character character, Dictionary<string, string> items,
            string label, StringBuilder report)
        {
            if (character.StartingItems == null) return;

            foreach (var item in character.StartingItems)
            {
                if (item == null || !string.IsNullOrEmpty(item.ItemGuid)) continue;

                if (!string.IsNullOrWhiteSpace(item.Title) && items.TryGetValue(item.Title.Trim(), out var guid))
                {
                    item.ItemGuid = guid;
                    continue;
                }

                report.AppendLine($"WARNING '{label}': no inventory item titled '{item.Title}'; it will not be given.");
            }
        }

        /// <summary>Every item in the player's inventory database, by title, ignoring case.</summary>
        private static Dictionary<string, string> ItemsByTitle()
        {
            var items = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            var inventory = PlayerAssetLookup.FindInventory();
            if (inventory == null || inventory.inventoryDatabase == null) return items;

            foreach (var section in inventory.inventoryDatabase.Sections)
            {
                if (section?.Items == null) continue;

                foreach (var item in section.Items)
                {
                    if (item != null && !string.IsNullOrWhiteSpace(item.Title)) items.TryAdd(item.Title.Trim(), item.GUID);
                }
            }

            return items;
        }

        private static float PlayerColliderHeight()
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PlayerAssetLookup.PlayerPrefabPath);
            var collider = prefab != null ? prefab.GetComponent<CharacterController>() : null;
            return collider != null ? collider.height : 0f;
        }

        /// <summary>Compares a body's height with the player's collider, and suggests a scale when they disagree.</summary>
        /// <remarks>
        /// Reports rather than corrects, as the HEROPLAYER setup does: the owner never sees their own body, so a towering
        /// or shrunken teammate is easy to miss, and the right fix may be the model rather than a scale.
        /// </remarks>
        private static void ReportBodyFit(CharacterCatalog.Character character, GameObject body, float colliderHeight,
            string label, StringBuilder report)
        {
            if (colliderHeight <= 0f) return;

            var scene = EditorSceneManager.NewPreviewScene();
            try
            {
                var instance = (GameObject)PrefabUtility.InstantiatePrefab(body, scene);
                instance.transform.localScale = Vector3.one * Mathf.Max(0.1f, character.BodyScale);

                var renderers = instance.GetComponentsInChildren<Renderer>(true);
                if (renderers.Length == 0)
                {
                    report.AppendLine($"WARNING '{label}': {body.name} has no renderers, so it would be invisible.");
                    return;
                }

                var bounds = renderers[0].bounds;
                for (var i = 1; i < renderers.Length; i++) bounds.Encapsulate(renderers[i].bounds);

                var height = bounds.size.y;
                if (Mathf.Abs(height - colliderHeight) > MaxBodyHeightMismatch && height > 0.01f)
                {
                    var suggested = character.BodyScale * colliderHeight / height;
                    report.AppendLine($"WARNING '{label}': body is {height:0.00} m tall against a {colliderHeight:0.00} m " +
                                      $"collider. Try Body Scale {suggested:0.00} in the catalog.");
                }
                else
                {
                    report.AppendLine($"'{label}': {body.name}, {height:0.00} m tall, fits the collider.");
                }
            }
            finally
            {
                EditorSceneManager.ClosePreviewScene(scene);
            }
        }

        // ---- Prefabs ---------------------------------------------------------------------------------

        private static bool ConfigurePlayerPrefab(CharacterCatalog catalog, StringBuilder report)
        {
            var path = PlayerAssetLookup.PlayerPrefabPath;
            if (AssetDatabase.LoadAssetAtPath<GameObject>(path) == null)
            {
                report.AppendLine($"FAILED: no player prefab at {path}. Run Set Up Networked HEROPLAYER first.");
                return false;
            }

            var root = PrefabUtility.LoadPrefabContents(path);
            try
            {
                if (root.GetComponent<NetworkObject>() == null || root.GetComponent<RemoteAvatarBinder>() == null)
                {
                    report.AppendLine("FAILED: the player prefab has no NetworkObject or RemoteAvatarBinder. " +
                                      "Run Set Up Networked HEROPLAYER first.");
                    return false;
                }

                var character = HeroPlayerSetup.GetOrAddComponent<PlayerCharacter>(root);
                HeroPlayerSetup.AssignSerializedReference(character, "m_catalog", catalog);

                PrefabUtility.SaveAsPrefabAsset(root, path);
                report.AppendLine("Player prefab: PlayerCharacter wired to the catalog.");
                return true;
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }

        private static bool ConfigureRoot(CharacterCatalog catalog, StringBuilder report)
        {
            var path = LobbySetup.RootPrefabPath;
            if (AssetDatabase.LoadAssetAtPath<GameObject>(path) == null)
            {
                report.AppendLine($"FAILED: no {path}. Run Tools > Quiet Village > Multiplayer > Set Up Lobby first.");
                return false;
            }

            var root = PrefabUtility.LoadPrefabContents(path);
            try
            {
                var selections = HeroPlayerSetup.GetOrAddComponent<CharacterSelections>(root);
                HeroPlayerSetup.AssignSerializedReference(selections, "m_catalog", catalog);
                HeroPlayerSetup.AssignSerializedReference(selections, "m_networkManager", root.GetComponent<NetworkManager>());
                HeroPlayerSetup.AssignSerializedReference(selections, "m_sessions", root.GetComponent<SessionService>());

                var spawner = root.GetComponent<NetworkPlayerSpawner>();
                if (spawner != null) HeroPlayerSetup.AssignSerializedReference(spawner, "m_characters", selections);
                else report.AppendLine("WARNING: MultiplayerRoot has no NetworkPlayerSpawner; re-run Set Up Lobby.");

                PrefabUtility.SaveAsPrefabAsset(root, path);
                report.AppendLine("MultiplayerRoot: CharacterSelections wired to the catalog and the spawner.");
                return true;
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }

        // ---- Lobby scene -----------------------------------------------------------------------------

        private static bool ConfigureLobbyScene(StringBuilder report)
        {
            var scenePath = LobbySetup.LobbyScenePath;
            if (!File.Exists(scenePath))
            {
                report.AppendLine($"FAILED: no {scenePath}. Run Set Up Lobby first.");
                return false;
            }

            var scene = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Single);
            if (!scene.IsValid())
            {
                report.AppendLine($"FAILED: could not open {scenePath}.");
                return false;
            }

            var stage = Object.FindAnyObjectByType<CharacterPreviewStage>(FindObjectsInactive.Include);
            if (stage == null)
            {
                var stageObject = new GameObject(StageObjectName);
                stageObject.transform.position = StagePosition;
                stage = stageObject.AddComponent<CharacterPreviewStage>();
                report.AppendLine($"LobbyScene: added {StageObjectName} at {StagePosition}.");
            }

            var bootstrap = Object.FindAnyObjectByType<MultiplayerBootstrap>(FindObjectsInactive.Include);
            if (bootstrap == null)
            {
                report.AppendLine("FAILED: LobbyScene has no MultiplayerBootstrap. Run Set Up Lobby first.");
                return false;
            }

            HeroPlayerSetup.AssignSerializedReference(bootstrap, "m_previewStage", stage);

            EditorSceneManager.MarkSceneDirty(scene);
            if (!EditorSceneManager.SaveScene(scene))
            {
                report.AppendLine($"FAILED: could not save {scenePath}.");
                return false;
            }

            report.AppendLine("LobbyScene: preview stage wired to the bootstrap.");
            return true;
        }
    }
}
