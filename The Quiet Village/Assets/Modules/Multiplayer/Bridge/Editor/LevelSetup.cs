using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Modules.Multiplayer.Scripts.Runtime.Flow;
using Modules.Multiplayer.Scripts.Runtime.Levels;
using Modules.Multiplayer.Scripts.Runtime.Sessions;
using UHFPS.Runtime;
using Unity.Cinemachine;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace Modules.Multiplayer.Bridge.EditorTools
{
    /// <summary>
    /// Creates level scenes and makes any scene playable as a level: session objects, spawn points, World Sync,
    /// Build Settings and the <see cref="LevelCatalog"/> entry the lobby offers.
    /// </summary>
    /// <remarks>
    /// A level needs two kinds of scene objects. The game systems UHFPS expects in every scene (input, localization,
    /// the camera brain, post-processing) are copied from GameplayScene's <c>GAMEMANAGER</c> when a level is created,
    /// because after the multiplayer migration that object is the only correct copy: the prefab UHFPS ships still
    /// holds the managers and HUD that now live on the player. The networked objects are added by the same steps
    /// GameplayScene was set up with, so every level is set up identically.
    ///
    /// Idempotent. Re-run Set Up Open Scene As Level after adding interactables to a level; it also re-runs World Sync.
    /// </remarks>
    public static class LevelSetup
    {
        /// <summary>First-party game content, apart from the vendor folders.</summary>
        internal const string LevelsFolder = "Assets/Game/Levels";

        internal const string CatalogPath = LevelsFolder + "/LevelCatalog.asset";

        private const string ScenePathArgument = "-levelScene";
        private const string LevelNameArgument = "-levelName";

        // Plane primitives are 10 m across, so this is a 100 m square: room to block out a few houses.
        private const float GroundScale = 10f;

        // ---- Menu ------------------------------------------------------------------------------------

        [MenuItem("Tools/Multiplayer/Levels/Create New Level...", priority = 0)]
        private static void CreateFromMenu()
        {
            if (IsPlaying("Create Level")) return;
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;

            EnsureFolder(LevelsFolder);
            var path = EditorUtility.SaveFilePanelInProject("Create Level", "NewSettlement", "unity",
                "Name the level. The scene name is how the game finds it, so every level needs a different one.",
                LevelsFolder);

            if (string.IsNullOrEmpty(path)) return;

            CreateLevel(path);
        }

        [MenuItem("Tools/Multiplayer/Levels/Set Up Open Scene As Level", priority = 1)]
        private static void SetUpOpenSceneFromMenu()
        {
            if (IsPlaying("Level Setup")) return;
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;

            var scene = SceneManager.GetActiveScene();
            if (string.IsNullOrEmpty(scene.path))
            {
                EditorUtility.DisplayDialog("Level Setup", "Save the scene first, then run this again.", "OK");
                return;
            }

            if (scene.path == LobbySetup.LobbyScenePath)
            {
                EditorUtility.DisplayDialog("Level Setup", "The lobby cannot be a level. Open a level scene.", "OK");
                return;
            }

            SetUpLevel(scene.path);
        }

        [MenuItem("Tools/Multiplayer/Levels/Select Level Catalog", priority = 20)]
        private static void SelectCatalog()
        {
            var catalog = EnsureCatalog(new StringBuilder());
            Selection.activeObject = catalog;
            EditorGUIUtility.PingObject(catalog);
        }

        /// <summary>Entry point for <c>-executeMethod</c>: creates <c>Assets/Game/Levels/&lt;name&gt;/&lt;name&gt;.unity</c>.</summary>
        /// <remarks>The name comes from <c>-levelName &lt;name&gt;</c>.</remarks>
        public static void CreateLevelFromCommandLine()
        {
            var name = ArgumentValue(LevelNameArgument);
            var succeeded = !string.IsNullOrWhiteSpace(name) && CreateLevel($"{LevelsFolder}/{name}/{name}.unity");

            if (string.IsNullOrWhiteSpace(name)) Debug.LogError($"Create Level: pass {LevelNameArgument} <name>.");
            if (Application.isBatchMode) EditorApplication.Exit(succeeded ? 0 : 1);
        }

        /// <summary>Entry point for <c>-executeMethod</c>: sets up the scene named by <c>-levelScene &lt;path&gt;</c>.</summary>
        public static void SetUpLevelFromCommandLine()
        {
            var succeeded = SetUpLevel(CommandLineScenePath());

            if (Application.isBatchMode) EditorApplication.Exit(succeeded ? 0 : 1);
        }

        // ---- Creating --------------------------------------------------------------------------------

        /// <summary>
        /// Creates a level scene with the game systems and a greybox floor, and sets it up as a level.
        /// </summary>
        internal static bool CreateLevel(string scenePath)
        {
            var sceneName = Path.GetFileNameWithoutExtension(scenePath);

            if (File.Exists(scenePath))
            {
                Debug.LogError($"Create Level: {scenePath} already exists. Open it and use Set Up Open Scene As Level.");
                return false;
            }

            var clash = SceneWithSameName(sceneName, scenePath);
            if (clash != null)
            {
                Debug.LogError($"Create Level: {clash} already uses the scene name '{sceneName}'. Netcode loads scenes " +
                               "by name, so pick another.");
                return false;
            }

            var report = new StringBuilder($"Create Level: {sceneName}\n");

            EnsureFolder(Path.GetDirectoryName(scenePath)?.Replace('\\', '/'));
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            if (!CopyGameSystems(scene, report))
            {
                Debug.LogError($"Create Level aborted.\n{report}");
                return false;
            }

            CreateGreybox(scene, report);

            if (!EditorSceneManager.SaveScene(scene, scenePath))
            {
                Debug.LogError($"Create Level: could not save {scenePath}.\n{report}");
                return false;
            }

            Debug.Log(report.ToString());
            return SetUpLevel(scenePath);
        }

        /// <summary>Copies GameplayScene's game systems object into a new scene.</summary>
        /// <remarks>
        /// Found by what it holds (the <see cref="UHFPS.Input.InputManager"/>) rather than by name, so a renamed object is still
        /// found. A copy rather than a prefab instance: see the class remarks for why the prefab is not usable.
        /// </remarks>
        private static bool CopyGameSystems(Scene destination, StringBuilder report)
        {
            if (!File.Exists(HeroPlayerSetup.ScenePath))
            {
                report.AppendLine($"FAILED: {HeroPlayerSetup.ScenePath} is missing, and it is the template for the " +
                                  "game systems a level needs.");
                return false;
            }

            var template = EditorSceneManager.OpenScene(HeroPlayerSetup.ScenePath, OpenSceneMode.Additive);
            try
            {
                var source = template.GetRootGameObjects()
                    .FirstOrDefault(root => root.GetComponentInChildren<UHFPS.Input.InputManager>(true) != null);

                if (source == null)
                {
                    report.AppendLine("FAILED: GameplayScene has no root object with an InputManager to copy.");
                    return false;
                }

                var copy = Object.Instantiate(source);
                copy.name = source.name;
                SceneManager.MoveGameObjectToScene(copy, destination);

                report.AppendLine($"Copied '{source.name}' (input, localization, camera brain, post-processing) " +
                                  "from GameplayScene.");
                return true;
            }
            finally
            {
                EditorSceneManager.CloseScene(template, true);
                SceneManager.SetActiveScene(destination);
            }
        }

        /// <summary>A floor to stand on and a dim light, so a new level is playable before anything is built.</summary>
        private static void CreateGreybox(Scene scene, StringBuilder report)
        {
            var root = new GameObject("Greybox");
            SceneManager.MoveGameObjectToScene(root, scene);

            var ground = GameObject.CreatePrimitive(PrimitiveType.Plane);
            ground.name = "Ground";
            ground.transform.SetParent(root.transform, false);
            ground.transform.localScale = new Vector3(GroundScale, 1f, GroundScale);
            GameObjectUtility.SetStaticEditorFlags(ground, StaticEditorFlags.NavigationStatic);

            var moon = new GameObject("Moonlight");
            moon.transform.SetParent(root.transform, false);
            moon.transform.rotation = Quaternion.Euler(50f, -30f, 0f);

            var light = moon.AddComponent<Light>();
            light.type = LightType.Directional;
            light.color = new Color(0.62f, 0.7f, 0.86f);
            light.intensity = 0.35f;
            light.shadows = LightShadows.Soft;

            report.AppendLine("Added a greybox: a 100 m ground plane and a directional moonlight.");
        }

        // ---- Setting up ------------------------------------------------------------------------------

        /// <summary>
        /// Makes a scene playable as a level, and lists it for the lobby.
        /// </summary>
        /// <returns><c>false</c> if the scene could not be opened or saved, or World Sync failed.</returns>
        internal static bool SetUpLevel(string scenePath)
        {
            var scene = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Single);
            if (!scene.IsValid())
            {
                Debug.LogError($"Level Setup: could not open {scenePath}.");
                return false;
            }

            var sceneName = scene.name;
            var report = new StringBuilder($"Level Setup: {sceneName}\n");

            ConfigureSessionObjects(sceneName, report);
            HeroPlayerSetup.RemoveScenePlayer(report);
            HeroPlayerSetup.ConfigureSceneReferences(report);

            // A floor created a moment ago has no collider in the physics scene until transforms are synced, and spawn
            // points fall back to raycasting for one when the level has no NavMesh yet.
            Physics.SyncTransforms();
            HeroPlayerSetup.ConfigureSpawnPoints(report);

            ReportMissingSystems(report);

            EditorSceneManager.MarkSceneDirty(scene);
            if (!EditorSceneManager.SaveScene(scene))
            {
                Debug.LogError($"Level Setup: could not save {scenePath}.\n{report}");
                return false;
            }

            // Opens and saves the scene again itself, and logs its own report.
            if (!WorldSyncSetup.Execute(scenePath))
            {
                Debug.LogError($"Level Setup: World Sync failed; the level is not ready.\n{report}");
                return false;
            }

            AddToBuildSettings(scenePath, report);
            AddToCatalog(EnsureCatalog(report), sceneName, report);
            ReportLevelsMissingFromBuild(report);

            AssetDatabase.SaveAssets();
            Debug.Log($"{report}Done. Rename it or add a description in the level catalog " +
                      "(Tools > Multiplayer > Levels > Select Level Catalog).");
            return true;
        }

        /// <summary>
        /// Removes a scene's own NetworkManager and room objects, and adds the guard that replaces them.
        /// </summary>
        /// <remarks>
        /// The room arrives already running, on the persistent root. A second NetworkManager loading with a level
        /// would sit beside the running one — Netcode does not remove duplicates — and a level's own SessionService
        /// would offer a second, disconnected room.
        /// </remarks>
        internal static void ConfigureSessionObjects(string sceneName, StringBuilder report)
        {
            foreach (var networkManager in Object.FindObjectsByType<NetworkManager>(FindObjectsInactive.Include))
                RemoveOwnedObject(networkManager.gameObject, sceneName, report, typeof(NetworkManager), typeof(UnityTransport));

            // The retired SessionDebugUI shows up here as a missing script, which is expected.
            foreach (var sessions in Object.FindObjectsByType<SessionService>(FindObjectsInactive.Include))
                RemoveOwnedObject(sessions.gameObject, sceneName, report, typeof(SessionService));

            var guard = Object.FindAnyObjectByType<SessionSceneGuard>(FindObjectsInactive.Include);
            if (guard == null)
            {
                guard = new GameObject(nameof(SessionSceneGuard)).AddComponent<SessionSceneGuard>();
                report.AppendLine($"Added SessionSceneGuard to {sceneName}.");
            }

            HeroPlayerSetup.AssignSerializedValue(guard, "m_lobbySceneName",
                property => property.stringValue = LobbySetup.LobbySceneName);
        }

        /// <summary>Deletes a GameObject this tooling created, or only its known components if anything else lives on it.</summary>
        private static void RemoveOwnedObject(GameObject target, string sceneName, StringBuilder report,
            params Type[] ownedTypes)
        {
            if (target == null) return;

            var foreign = target.transform.childCount > 0;
            var owned = new List<Component>();

            foreach (var component in target.GetComponents<Component>())
            {
                // Null is a missing script: something deleted from the project, not someone's live work.
                if (component == null || component is Transform) continue;

                if (ownedTypes.Any(type => type.IsInstanceOfType(component))) owned.Add(component);
                else foreign = true;
            }

            if (!foreign)
            {
                report.AppendLine($"Removed '{target.name}' from {sceneName}; it lives on MultiplayerRoot.");
                Object.DestroyImmediate(target);
                return;
            }

            // Something else shares the object; take only what this tooling put there.
            GameObjectUtility.RemoveMonoBehavioursWithMissingScript(target);
            foreach (var component in owned) Object.DestroyImmediate(component);
            report.AppendLine($"Removed network components from '{target.name}', which also holds other components.");
        }

        /// <summary>Warns about UHFPS systems a scene built by hand may lack. None can be added safely by a tool.</summary>
        private static void ReportMissingSystems(StringBuilder report)
        {
            var missing = new List<string>();

            if (Object.FindAnyObjectByType<UHFPS.Input.InputManager>(FindObjectsInactive.Include) == null) missing.Add("InputManager");
            if (Object.FindAnyObjectByType<GameLocalization>(FindObjectsInactive.Include) == null) missing.Add("GameLocalization");
            if (Object.FindAnyObjectByType<CinemachineBrain>(FindObjectsInactive.Include) == null) missing.Add("camera with a CinemachineBrain");
            if (Object.FindAnyObjectByType<EventSystem>(FindObjectsInactive.Include) == null) missing.Add("EventSystem");

            if (missing.Count == 0) return;

            report.AppendLine($"WARNING: no {string.Join(", ", missing)}. Players will not be able to play here. Copy the " +
                              "GAMEMANAGER object from GameplayScene, or start from Create New Level.");
        }

        // ---- Build settings and catalog --------------------------------------------------------------

        private static void AddToBuildSettings(string scenePath, StringBuilder report)
        {
            var scenes = EditorBuildSettings.scenes.ToList();
            var index = scenes.FindIndex(entry => entry.path == scenePath);

            if (index >= 0 && scenes[index].enabled) return;

            if (index >= 0) scenes[index] = new EditorBuildSettingsScene(scenePath, true);
            else scenes.Add(new EditorBuildSettingsScene(scenePath, true));

            EditorBuildSettings.scenes = scenes.ToArray();
            report.AppendLine("Added to Build Settings.");
        }

        /// <summary>The level catalog, created on first use.</summary>
        /// <remarks>
        /// A new catalog lists GameplayScene, the level every room played before there was a choice, so creating the
        /// catalog changes nothing until another level is added.
        /// </remarks>
        internal static LevelCatalog EnsureCatalog(StringBuilder report)
        {
            var catalog = AssetDatabase.LoadAssetAtPath<LevelCatalog>(CatalogPath);
            if (catalog != null) return catalog;

            EnsureFolder(LevelsFolder);
            catalog = ScriptableObject.CreateInstance<LevelCatalog>();

            if (File.Exists(HeroPlayerSetup.ScenePath))
            {
                catalog.EditableLevels.Add(new LevelCatalog.Level
                {
                    DisplayName = "UHFPS Demo",
                    SceneName = Path.GetFileNameWithoutExtension(HeroPlayerSetup.ScenePath),
                    Description = "The template's demo level, for testing systems."
                });
            }

            AssetDatabase.CreateAsset(catalog, CatalogPath);
            AssetDatabase.SaveAssets();
            report.AppendLine($"Created {CatalogPath}.");
            return catalog;
        }

        private static void AddToCatalog(LevelCatalog catalog, string sceneName, StringBuilder report)
        {
            if (catalog.Contains(sceneName)) return;

            catalog.EditableLevels.Add(new LevelCatalog.Level
            {
                DisplayName = ObjectNames.NicifyVariableName(sceneName),
                SceneName = sceneName
            });

            EditorUtility.SetDirty(catalog);
            report.AppendLine("Added to the level catalog.");
        }

        /// <summary>Warns about listed levels the host could not load: absent from Build Settings, or ambiguous by name.</summary>
        internal static void ReportLevelsMissingFromBuild(StringBuilder report)
        {
            var catalog = AssetDatabase.LoadAssetAtPath<LevelCatalog>(CatalogPath);
            if (catalog == null) return;

            foreach (var level in catalog.Levels)
            {
                if (level == null || string.IsNullOrEmpty(level.SceneName)) continue;

                var entries = EditorBuildSettings.scenes
                    .Where(entry => entry.enabled && Path.GetFileNameWithoutExtension(entry.path) == level.SceneName)
                    .ToList();

                if (entries.Count == 0)
                    report.AppendLine($"WARNING: level '{level.SceneName}' is not enabled in Build Settings; " +
                                      "the host cannot load it.");
                else if (entries.Count > 1)
                    report.AppendLine($"WARNING: {entries.Count} scenes in Build Settings are named '{level.SceneName}'; " +
                                      "Netcode will load whichever comes first.");
            }
        }

        // ---- Shared by the other scene tools ---------------------------------------------------------

        /// <summary>
        /// The open scene's path if it is a set-up level, for tools that work on one; otherwise explains and returns
        /// <c>null</c>.
        /// </summary>
        internal static string OpenLevelScenePath(string toolName)
        {
            var scene = SceneManager.GetActiveScene();

            if (string.IsNullOrEmpty(scene.path) || scene.path == LobbySetup.LobbyScenePath
                || Object.FindAnyObjectByType<SessionSceneGuard>(FindObjectsInactive.Include) == null)
            {
                EditorUtility.DisplayDialog(toolName,
                    "Open a level scene first. A scene that is not a level yet needs " +
                    "Tools > Multiplayer > Levels > Set Up Open Scene As Level, which runs this too.", "OK");
                return null;
            }

            return scene.path;
        }

        /// <summary>The scene named by <c>-levelScene &lt;path&gt;</c>, or GameplayScene when none is given.</summary>
        internal static string CommandLineScenePath()
        {
            var path = ArgumentValue(ScenePathArgument);
            return string.IsNullOrWhiteSpace(path) ? HeroPlayerSetup.ScenePath : path;
        }

        private static string ArgumentValue(string name)
        {
            var args = Environment.GetCommandLineArgs();
            for (var i = 0; i < args.Length - 1; i++)
            {
                if (args[i] == name) return args[i + 1];
            }

            return null;
        }

        /// <summary>Another scene in the project or Build Settings with this name, or <c>null</c>.</summary>
        private static string SceneWithSameName(string sceneName, string ownPath)
        {
            foreach (var guid in AssetDatabase.FindAssets($"t:Scene {sceneName}"))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (path != ownPath && Path.GetFileNameWithoutExtension(path) == sceneName) return path;
            }

            return null;
        }

        private static bool IsPlaying(string toolName)
        {
            if (!EditorApplication.isPlaying && !EditorApplication.isPlayingOrWillChangePlaymode) return false;

            EditorUtility.DisplayDialog(toolName, "Exit Play Mode first.", "OK");
            return true;
        }

        private static void EnsureFolder(string path)
        {
            if (string.IsNullOrEmpty(path) || AssetDatabase.IsValidFolder(path)) return;

            var parent = Path.GetDirectoryName(path)?.Replace('\\', '/');
            EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, Path.GetFileName(path));
        }
    }
}
