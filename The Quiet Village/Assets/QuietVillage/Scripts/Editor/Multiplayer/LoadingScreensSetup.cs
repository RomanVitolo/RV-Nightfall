using System.IO;
using System.Linq;
using System.Text;
using QuietVillage.Multiplayer.Flow;
using QuietVillage.Multiplayer.Loading;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;

namespace QuietVillage.Multiplayer.Bridge.EditorTools
{
    /// <summary>
    /// Sets up the startup splash and the loading screen: their panel settings and content, and the Boot scene that opens
    /// the game.
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item><c>Resources/Loading/LoadingPanelSettings</c>: a copy of the lobby's panel settings (same scaling and theme,
    /// so the same fonts), sorted above every other panel.</item>
    /// <item><c>Resources/Loading/LoadingScreenContent</c>: the tagline and tips, created with defaults only when missing.</item>
    /// <item><c>Scenes/BootScene</c>: a black camera and a <see cref="BootLoader"/>, first in Build Settings, so a built game
    /// opens on the splash and then the main menu.</item>
    /// </list>
    /// Idempotent: existing assets are kept (the content may have been edited), the scene is rebuilt, and BootScene is
    /// moved back to the front of Build Settings if something else took its place.
    /// </remarks>
    public static class LoadingScreensSetup
    {
        private const string ResourcesFolder = ProjectPaths.Root + "/UI/Resources/Loading";
        private const string PanelSettingsPath = ResourcesFolder + "/LoadingPanelSettings.asset";
        private const string ContentPath = ResourcesFolder + "/LoadingScreenContent.asset";
        private const string LobbyPanelSettingsPath = ProjectPaths.MultiplayerUI + "/LobbyPanelSettings.asset";
        public const string BootScenePath = ProjectPaths.Scenes + "/BootScene.unity";

        [MenuItem("Tools/Quiet Village/UI/Set Up Boot And Loading Screens")]
        public static void Run()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                Debug.LogWarning($"{nameof(LoadingScreensSetup)}: leave Play mode first.");
                return;
            }

            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;

            var report = new StringBuilder($"{nameof(LoadingScreensSetup)}:\n");
            EnsureFolder(ResourcesFolder);

            EnsurePanelSettings(report);
            EnsureContent(report);
            AssetDatabase.SaveAssets();

            BuildBootScene(report);
            PutBootSceneFirst(report);

            Debug.Log(report.ToString());
        }

        private static void EnsurePanelSettings(StringBuilder report)
        {
            if (AssetDatabase.LoadAssetAtPath<PanelSettings>(PanelSettingsPath) != null)
            {
                report.AppendLine("  LoadingPanelSettings kept.");
                return;
            }

            var lobby = AssetDatabase.LoadAssetAtPath<PanelSettings>(LobbyPanelSettingsPath);
            var settings = lobby != null ? Object.Instantiate(lobby) : ScriptableObject.CreateInstance<PanelSettings>();
            if (lobby == null)
            {
                settings.scaleMode = PanelScaleMode.ScaleWithScreenSize;
                settings.referenceResolution = new Vector2Int(1920, 1080);
                settings.match = 0.5f;
                report.AppendLine("  WARNING: LobbyPanelSettings not found; the loading screen has no theme (default fonts).");
            }

            // Above the lobby (0) and the in-game room panel (100).
            settings.sortingOrder = 1000;
            AssetDatabase.CreateAsset(settings, PanelSettingsPath);
            report.AppendLine("  Created LoadingPanelSettings.");
        }

        private static void EnsureContent(StringBuilder report)
        {
            if (AssetDatabase.LoadAssetAtPath<LoadingScreenContent>(ContentPath) != null)
            {
                report.AppendLine("  LoadingScreenContent kept.");
                return;
            }

            AssetDatabase.CreateAsset(ScriptableObject.CreateInstance<LoadingScreenContent>(), ContentPath);
            report.AppendLine($"  Created LoadingScreenContent with {LoadingScreenContent.DefaultTips.Length} tips.");
        }

        private static void BuildBootScene(StringBuilder report)
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            // UI Toolkit draws without a camera, but a scene with none shows "No cameras rendering" in the editor and
            // undefined pixels in a build behind the splash's fade-in.
            var cameraObject = new GameObject("Main Camera");
            var camera = cameraObject.AddComponent<Camera>();
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = new Color(0.024f, 0.027f, 0.035f);
            camera.cullingMask = 0;

            new GameObject("Boot").AddComponent<BootLoader>();

            EditorSceneManager.SaveScene(scene, BootScenePath);
            report.AppendLine($"  Built {BootScenePath}.");
        }

        private static void PutBootSceneFirst(StringBuilder report)
        {
            var scenes = EditorBuildSettings.scenes.Where(entry => entry.path != BootScenePath).ToList();
            scenes.Insert(0, new EditorBuildSettingsScene(BootScenePath, true));
            EditorBuildSettings.scenes = scenes.ToArray();

            report.AppendLine("  BootScene is first in Build Settings: " +
                              string.Join(", ", EditorBuildSettings.scenes.Select(entry => Path.GetFileNameWithoutExtension(entry.path))));
        }

        private static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path)) return;

            var parent = Path.GetDirectoryName(path)?.Replace('\\', '/');
            EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, Path.GetFileName(path));
        }
    }
}
