using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using QuietVillage.Multiplayer.Flow;
using QuietVillage.Multiplayer.Levels;
using QuietVillage.Multiplayer.Sessions;
using QuietVillage.Multiplayer.UI;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.UI;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;
using Object = UnityEngine.Object;

namespace QuietVillage.Multiplayer.Bridge.EditorTools
{
    /// <summary>
    /// Builds the lobby: its scene, the persistent multiplayer root prefab, and the UI assets both use —
    /// and moves the network objects out of GameplayScene, which the room now arrives in already running.
    /// </summary>
    /// <remarks>
    /// Beside <see cref="HeroPlayerSetup"/> rather than in the UHFPS-free editor asmdef, because both edit
    /// GameplayScene and share the player prefab's location; one assembly keeps those paths in one place.
    ///
    /// Everything goes through Unity's APIs, so scenes, prefabs and PanelSettings are Editor-generated;
    /// hand-written YAML for them is silently invalid. The UXML, USS and theme are hand-written by design.
    /// Idempotent: existing objects are reused and re-wired, and anything a designer tunes afterwards —
    /// panel scaling, camera colour — is left as authored.
    /// </remarks>
    public static class LobbySetup
    {
        internal const string LobbyScenePath = ProjectPaths.LobbyScene;
        internal const string RootPrefabPath = ProjectPaths.MultiplayerPrefabs + "/MultiplayerRoot.prefab";
        private const string LobbyUiPrefabPath = ProjectPaths.MultiplayerPrefabs + "/LobbyUI.prefab";
        internal const string NetworkPrefabsListPath = ProjectPaths.NetworkPrefabsList;

        private const string UiFolder = ProjectPaths.MultiplayerUI;
        private const string LobbyUxmlPath = UiFolder + "/LobbyScreen.uxml";
        private const string OverlayUxmlPath = UiFolder + "/SessionOverlay.uxml";
        private const string ThemePath = UiFolder + "/MultiplayerRuntimeTheme.tss";
        private const string LobbyPanelSettingsPath = UiFolder + "/LobbyPanelSettings.asset";
        private const string OverlayPanelSettingsPath = UiFolder + "/OverlayPanelSettings.asset";

        // Unity's default runtime theme, verbatim. Without a theme, runtime controls render unstyled.
        private const string ThemeContents = "@import url(\"unity-theme://default\");\n";

        // Above UHFPS's own HUD, so the in-game room panel is never hidden behind it.
        private const int OverlaySortingOrder = 100;

        private static readonly Color LobbyBackground = new(0.043f, 0.047f, 0.059f);

        internal static string LobbySceneName => Path.GetFileNameWithoutExtension(LobbyScenePath);

        [MenuItem("Tools/Quiet Village/Multiplayer/Set Up Lobby")]
        private static void RunFromMenu()
        {
            if (EditorApplication.isPlaying || EditorApplication.isPlayingOrWillChangePlaymode)
            {
                EditorUtility.DisplayDialog("Lobby Setup", "Exit Play Mode first.", "OK");
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
        /// Points the root's spawner at the player prefab. Called by <see cref="HeroPlayerSetup"/> whenever it
        /// rebuilds the player, so the two cannot disagree about which prefab spawns.
        /// </summary>
        internal static void AssignPlayerPrefab(GameObject playerPrefab, StringBuilder report)
        {
            if (playerPrefab == null) return;

            if (AssetDatabase.LoadAssetAtPath<GameObject>(RootPrefabPath) == null)
            {
                report.AppendLine("Lobby not set up yet; run Tools > Quiet Village > Multiplayer > Set Up Lobby so players can spawn.");
                return;
            }

            var root = PrefabUtility.LoadPrefabContents(RootPrefabPath);
            try
            {
                var spawner = root.GetComponent<NetworkPlayerSpawner>();
                if (spawner == null)
                {
                    report.AppendLine("WARNING: MultiplayerRoot has no NetworkPlayerSpawner; re-run Set Up Lobby.");
                    return;
                }

                HeroPlayerSetup.AssignSerializedReference(spawner, "m_playerPrefab", playerPrefab);
                PrefabUtility.SaveAsPrefabAsset(root, RootPrefabPath);
                report.AppendLine($"MultiplayerRoot spawns {playerPrefab.name} once the level loads.");
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }

        private static bool Execute()
        {
            var report = new StringBuilder();

            var playerPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(PlayerAssetLookup.PlayerPrefabPath);
            if (playerPrefab == null)
            {
                Debug.LogError($"Lobby Setup aborted: no player prefab at {PlayerAssetLookup.PlayerPrefabPath}. " +
                               "Run Tools > Quiet Village > Multiplayer > Set Up Networked HEROPLAYER first.");
                return false;
            }

            var theme = EnsureTheme(report);
            var lobbyUxml = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(LobbyUxmlPath);
            var overlayUxml = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(OverlayUxmlPath);

            if (theme == null || lobbyUxml == null || overlayUxml == null)
            {
                Debug.LogError($"Lobby Setup aborted: missing UI assets in {UiFolder}.\n{report}");
                return false;
            }

            EnsurePanelSettings(LobbyPanelSettingsPath, theme, 0, report);
            var overlayPanel = EnsurePanelSettings(OverlayPanelSettingsPath, theme, OverlaySortingOrder, report);
            var levels = LevelSetup.EnsureCatalog(report);

            if (BuildRootPrefab(playerPrefab, overlayPanel, overlayUxml, levels, report) == null)
            {
                Debug.LogError($"Lobby Setup aborted.\n{report}");
                return false;
            }

            if (!BuildLobbyScene(report) || !MigrateGameplayScene(report))
            {
                Debug.LogError($"Lobby Setup aborted.\n{report}");
                return false;
            }

            EnsureBuildSettings(report);
            AssetDatabase.SaveAssets();

            Debug.Log($"Lobby Setup complete.\n{report}");
            return true;
        }

        // ---- UI assets -------------------------------------------------------------------------------

        private static ThemeStyleSheet EnsureTheme(StringBuilder report)
        {
            if (!File.Exists(ThemePath))
            {
                EnsureFolder(UiFolder);
                File.WriteAllText(ThemePath, ThemeContents);
                AssetDatabase.ImportAsset(ThemePath, ImportAssetOptions.ForceUpdate);
                report.AppendLine($"Created {ThemePath}.");
            }

            return AssetDatabase.LoadAssetAtPath<ThemeStyleSheet>(ThemePath);
        }

        private static PanelSettings EnsurePanelSettings(string path, ThemeStyleSheet theme, int sortingOrder, StringBuilder report)
        {
            var settings = AssetDatabase.LoadAssetAtPath<PanelSettings>(path);

            if (settings == null)
            {
                settings = ScriptableObject.CreateInstance<PanelSettings>();

                // Laid out at 1080p and scaled, so the lobby keeps its proportions on any display.
                settings.scaleMode = PanelScaleMode.ScaleWithScreenSize;
                settings.referenceResolution = new Vector2Int(1920, 1080);
                settings.screenMatchMode = PanelScreenMatchMode.MatchWidthOrHeight;
                settings.match = 0.5f;
                settings.sortingOrder = sortingOrder;
                settings.themeStyleSheet = theme;

                AssetDatabase.CreateAsset(settings, path);
                report.AppendLine($"Created {path}.");
                return settings;
            }

            // Only the theme is enforced: without it every control renders unstyled. Scaling is left as tuned.
            if (settings.themeStyleSheet == null)
            {
                settings.themeStyleSheet = theme;
                EditorUtility.SetDirty(settings);
                report.AppendLine($"Assigned the runtime theme to {path}.");
            }

            return settings;
        }

        // ---- Persistent root -------------------------------------------------------------------------

        private static GameObject BuildRootPrefab(GameObject playerPrefab, PanelSettings overlayPanel,
            VisualTreeAsset overlayUxml, LevelCatalog levels, StringBuilder report)
        {
            if (AssetDatabase.LoadAssetAtPath<GameObject>(RootPrefabPath) == null)
            {
                EnsureFolder(Path.GetDirectoryName(RootPrefabPath));

                var seed = new GameObject(Path.GetFileNameWithoutExtension(RootPrefabPath));
                try
                {
                    PrefabUtility.SaveAsPrefabAsset(seed, RootPrefabPath);
                    report.AppendLine($"Created {RootPrefabPath}.");
                }
                finally
                {
                    Object.DestroyImmediate(seed);
                }
            }

            var root = PrefabUtility.LoadPrefabContents(RootPrefabPath);
            try
            {
                if (!ConfigureNetworkManager(root, report)) return null;

                var networkManager = root.GetComponent<NetworkManager>();
                var sessions = HeroPlayerSetup.GetOrAddComponent<SessionService>(root);

                var flow = HeroPlayerSetup.GetOrAddComponent<SessionFlow>(root);
                HeroPlayerSetup.AssignSerializedReference(flow, "m_sessions", sessions);
                AssignString(flow, "m_lobbySceneName", LobbySceneName);
                HeroPlayerSetup.AssignSerializedReference(flow, "m_levels", levels);

                var spawner = HeroPlayerSetup.GetOrAddComponent<NetworkPlayerSpawner>(root);
                HeroPlayerSetup.AssignSerializedReference(spawner, "m_networkManager", networkManager);
                HeroPlayerSetup.AssignSerializedReference(spawner, "m_playerPrefab", playerPrefab);
                HeroPlayerSetup.AssignSerializedReference(spawner, "m_levels", levels);

                var document = HeroPlayerSetup.GetOrAddComponent<UIDocument>(root);
                AssignDocument(document, overlayPanel, overlayUxml);

                var overlay = HeroPlayerSetup.GetOrAddComponent<SessionOverlay>(root);
                HeroPlayerSetup.AssignSerializedReference(overlay, "m_document", document);
                HeroPlayerSetup.AssignSerializedReference(overlay, "m_sessions", sessions);
                HeroPlayerSetup.AssignSerializedReference(overlay, "m_flow", flow);

                PrefabUtility.SaveAsPrefabAsset(root, RootPrefabPath);
                report.AppendLine("MultiplayerRoot: NetworkManager, SessionService, SessionFlow, spawner and room panel wired.");
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }

            return AssetDatabase.LoadAssetAtPath<GameObject>(RootPrefabPath);
        }

        private static bool ConfigureNetworkManager(GameObject root, StringBuilder report)
        {
            var networkManager = HeroPlayerSetup.GetOrAddComponent<NetworkManager>(root);
            var transport = HeroPlayerSetup.GetOrAddComponent<UnityTransport>(root);

            if (networkManager.NetworkConfig == null) networkManager.NetworkConfig = new NetworkConfig();
            var config = networkManager.NetworkConfig;

            // Relay rewrites the transport's endpoint when a room starts; these are only resting defaults.
            config.NetworkTransport = transport;

            // No automatic player: Netcode would spawn one on connect, in the lobby. NetworkPlayerSpawner
            // spawns each player once its client has loaded the level instead.
            config.PlayerPrefab = null;

            // The host moves everyone into the level through Netcode's scene manager.
            config.EnableSceneManagement = true;
            config.ForceSamePrefabs = true;

            var prefabList = AssetDatabase.LoadAssetAtPath<NetworkPrefabsList>(NetworkPrefabsListPath);
            if (prefabList == null)
            {
                report.AppendLine($"FAILED: {NetworkPrefabsListPath} not found; the player prefab could not spawn.");
                return false;
            }

            config.Prefabs ??= new NetworkPrefabs();
            if (!config.Prefabs.NetworkPrefabsLists.Contains(prefabList)) config.Prefabs.NetworkPrefabsLists.Add(prefabList);

            EditorUtility.SetDirty(networkManager);
            return true;
        }

        // ---- Lobby scene -----------------------------------------------------------------------------

        private static bool BuildLobbyScene(StringBuilder report)
        {
            Scene scene;
            if (File.Exists(LobbyScenePath))
            {
                scene = EditorSceneManager.OpenScene(LobbyScenePath, OpenSceneMode.Single);
            }
            else
            {
                EnsureFolder(Path.GetDirectoryName(LobbyScenePath));
                scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                report.AppendLine($"Created {LobbyScenePath}.");
            }

            if (!scene.IsValid())
            {
                report.AppendLine($"FAILED: could not open {LobbyScenePath}.");
                return false;
            }

            // Loaded only now, after the scene opened. Opening or creating a scene in Single mode unloads
            // assets nothing in memory references, so a PanelSettings loaded before that becomes a destroyed
            // wrapper — and assigning it silently writes null. That shipped a lobby with no panel, rendering
            // nothing, while the assignment itself reported success.
            var rootPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(RootPrefabPath);
            var lobbyPanel = AssetDatabase.LoadAssetAtPath<PanelSettings>(LobbyPanelSettingsPath);
            var lobbyUxml = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(LobbyUxmlPath);

            if (rootPrefab == null || lobbyPanel == null || lobbyUxml == null)
            {
                report.AppendLine("FAILED: MultiplayerRoot, LobbyPanelSettings or LobbyScreen.uxml could not be loaded.");
                return false;
            }

            // UI Toolkit needs no camera, but a scene without one shows a "no cameras rendering" warning in
            // the Game view and hears nothing.
            if (Object.FindAnyObjectByType<Camera>(FindObjectsInactive.Include) == null)
            {
                var cameraObject = new GameObject("LobbyCamera");
                var camera = cameraObject.AddComponent<Camera>();
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = LobbyBackground;
                cameraObject.AddComponent<AudioListener>();
            }

            // The project runs the Input System alone; this module is how UI Toolkit receives its input.
            if (Object.FindAnyObjectByType<EventSystem>(FindObjectsInactive.Include) == null)
            {
                var eventSystem = new GameObject("EventSystem");
                eventSystem.AddComponent<EventSystem>();
                eventSystem.AddComponent<InputSystemUIInputModule>();
            }

            var lobbyUiPrefab = BuildLobbyUiPrefab(lobbyPanel, lobbyUxml, report);
            if (lobbyUiPrefab == null) return false;

            var screen = PlaceLobbyUi(lobbyUiPrefab, report);
            if (screen == null)
            {
                report.AppendLine("FAILED: could not place the LobbyUI prefab in LobbyScene.");
                return false;
            }

            var bootstrap = Object.FindAnyObjectByType<MultiplayerBootstrap>(FindObjectsInactive.Include);
            if (bootstrap == null) bootstrap = new GameObject("MultiplayerBootstrap").AddComponent<MultiplayerBootstrap>();

            HeroPlayerSetup.AssignSerializedReference(bootstrap, "m_multiplayerRootPrefab", rootPrefab);
            HeroPlayerSetup.AssignSerializedReference(bootstrap, "m_lobbyScreen", screen);

            EditorSceneManager.MarkSceneDirty(scene);
            if (!EditorSceneManager.SaveScene(scene, LobbyScenePath))
            {
                report.AppendLine($"FAILED: could not save {LobbyScenePath}.");
                return false;
            }

            report.AppendLine("LobbyScene: camera, EventSystem, lobby UI and bootstrap wired.");
            return true;
        }

        /// <summary>The lobby's UIDocument and controller, as a prefab the scene instances.</summary>
        /// <remarks>
        /// Prefab-first: the UI's panel settings and layout live in one asset, and the scene only holds an
        /// instance, so another scene can host the same lobby without re-wiring it.
        /// </remarks>
        private static GameObject BuildLobbyUiPrefab(PanelSettings lobbyPanel, VisualTreeAsset lobbyUxml, StringBuilder report)
        {
            if (AssetDatabase.LoadAssetAtPath<GameObject>(LobbyUiPrefabPath) == null)
            {
                var seed = new GameObject(Path.GetFileNameWithoutExtension(LobbyUiPrefabPath));
                try
                {
                    PrefabUtility.SaveAsPrefabAsset(seed, LobbyUiPrefabPath);
                    report.AppendLine($"Created {LobbyUiPrefabPath}.");
                }
                finally
                {
                    Object.DestroyImmediate(seed);
                }
            }

            var root = PrefabUtility.LoadPrefabContents(LobbyUiPrefabPath);
            try
            {
                var document = HeroPlayerSetup.GetOrAddComponent<UIDocument>(root);
                AssignDocument(document, lobbyPanel, lobbyUxml);

                var screen = HeroPlayerSetup.GetOrAddComponent<LobbyScreen>(root);
                HeroPlayerSetup.AssignSerializedReference(screen, "m_document", document);

                PrefabUtility.SaveAsPrefabAsset(root, LobbyUiPrefabPath);
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }

            return AssetDatabase.LoadAssetAtPath<GameObject>(LobbyUiPrefabPath);
        }

        /// <summary>
        /// Ensures the scene holds exactly one LobbyScreen, and that it is an instance of the LobbyUI prefab.
        /// </summary>
        private static LobbyScreen PlaceLobbyUi(GameObject lobbyUiPrefab, StringBuilder report)
        {
            LobbyScreen instance = null;

            foreach (var screen in Object.FindObjectsByType<LobbyScreen>(FindObjectsInactive.Include))
            {
                var isInstance = PrefabUtility.GetCorrespondingObjectFromSource(screen.gameObject) == lobbyUiPrefab;

                if (isInstance && instance == null)
                {
                    instance = screen;
                    continue;
                }

                // An earlier, hand-built LobbyUI from before the prefab existed, or a duplicate.
                report.AppendLine($"Replaced scene-built '{screen.gameObject.name}' with the LobbyUI prefab.");
                Object.DestroyImmediate(screen.gameObject);
            }

            if (instance != null)
            {
                // Clear any per-scene override so the prefab's panel settings and layout always apply.
                PrefabUtility.RevertObjectOverride(instance.GetComponent<UIDocument>(), InteractionMode.AutomatedAction);
                return instance;
            }

            var placed = (GameObject)PrefabUtility.InstantiatePrefab(lobbyUiPrefab);
            return placed != null ? placed.GetComponent<LobbyScreen>() : null;
        }

        // ---- GameplayScene ---------------------------------------------------------------------------

        /// <summary>
        /// Removes the level's own NetworkManager and room objects and adds the guard that replaces them.
        /// </summary>
        /// <remarks>
        /// The room now arrives already running, on the persistent root. A second NetworkManager loading with
        /// the level would sit beside the running one — Netcode does not remove duplicates — and the level's
        /// SessionRoot would offer a second, disconnected room.
        /// </remarks>
        private static bool MigrateGameplayScene(StringBuilder report)
        {
            var scene = EditorSceneManager.OpenScene(HeroPlayerSetup.ScenePath, OpenSceneMode.Single);
            if (!scene.IsValid())
            {
                report.AppendLine($"FAILED: could not open {HeroPlayerSetup.ScenePath}.");
                return false;
            }

            LevelSetup.ConfigureSessionObjects(scene.name, report);

            EditorSceneManager.MarkSceneDirty(scene);
            if (!EditorSceneManager.SaveScene(scene))
            {
                report.AppendLine($"FAILED: could not save {HeroPlayerSetup.ScenePath}.");
                return false;
            }

            return true;
        }

        // ---- Build settings --------------------------------------------------------------------------

        private static void EnsureBuildSettings(StringBuilder report)
        {
            var scenes = EditorBuildSettings.scenes.ToList();

            // Netcode loads scenes by their Build Settings entry, so the lobby must be listed even though
            // nothing loads it by index.
            var lobbyIndex = scenes.FindIndex(entry => entry.path == LobbyScenePath);
            if (lobbyIndex < 0)
            {
                scenes.Add(new EditorBuildSettingsScene(LobbyScenePath, true));
                report.AppendLine("Added LobbyScene to Build Settings (at the end; the first scene is unchanged).");
            }
            else if (!scenes[lobbyIndex].enabled)
            {
                scenes[lobbyIndex] = new EditorBuildSettingsScene(LobbyScenePath, true);
                report.AppendLine("Enabled LobbyScene in Build Settings.");
            }

            EditorBuildSettings.scenes = scenes.ToArray();

            LevelSetup.ReportLevelsMissingFromBuild(report);
        }

        // ---- Helpers ---------------------------------------------------------------------------------

        /// <summary>Points a UIDocument at its panel settings and layout, through serialization like every other reference.</summary>
        private static void AssignDocument(UIDocument document, PanelSettings panelSettings, VisualTreeAsset layout)
        {
            HeroPlayerSetup.AssignSerializedReference(document, "m_PanelSettings", panelSettings);
            HeroPlayerSetup.AssignSerializedReference(document, "sourceAsset", layout);
        }

        private static void AssignString(Object target, string propertyPath, string value) =>
            HeroPlayerSetup.AssignSerializedValue(target, propertyPath, property => property.stringValue = value);

        private static void EnsureFolder(string path)
        {
            if (string.IsNullOrEmpty(path) || AssetDatabase.IsValidFolder(path)) return;

            var parent = Path.GetDirectoryName(path)?.Replace('\\', '/');
            EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, Path.GetFileName(path));
        }
    }
}
