using System.Collections.Generic;
using QuietVillage.Multiplayer.Characters;
using QuietVillage.Multiplayer.Flow;
using QuietVillage.Multiplayer.Sessions;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;

namespace QuietVillage.Multiplayer.Loading
{
    /// <summary>
    /// The one loading screen: the startup splash, and the screen that covers a level loading until this player is in it.
    /// </summary>
    /// <remarks>
    /// Persistent and created on demand (<see cref="Instance"/>), with its own UI Toolkit panel sorted above everything,
    /// so no scene has to contain it and it outlives the scene changes it covers. Its layout, styles and panel settings
    /// live in <c>Resources/Loading</c>.
    ///
    /// A level's loading screen opens by itself: it follows Netcode's scene events, and when this client starts loading a
    /// catalog level (a new game, or a restart) it shows that level, this player's role and ability, a rotating tip and
    /// the load's progress. It stays up past the load until this client's player object has spawned, since a level with
    /// no player yet is only an empty camera, and at least <see cref="LoadingScreenContent.MinimumLevelSeconds"/>. It gives
    /// up after <see cref="GiveUpSeconds"/>, or at once if the session ends.
    ///
    /// Everything shown comes from the module's own knowledge (the level catalog, the room and the character catalog),
    /// so it needs nothing from UHFPS or the bridge. To add to it later, add a section to LoadingScreen.uxml and fill it
    /// in <see cref="FillLevel"/>.
    /// </remarks>
    public class LoadingScreen : MonoBehaviour
    {
        private const string LayoutPath = "Loading/LoadingScreen";
        private const string PanelSettingsPath = "Loading/LoadingPanelSettings";
        private const float FadeSeconds = 0.45f;
        private const float GiveUpSeconds = 60f;
        private const float SettleAfterSpawnSeconds = 0.5f;

        private static LoadingScreen s_instance;

        /// <summary>The loading screen, created the first time it is needed.</summary>
        public static LoadingScreen Instance
        {
            get
            {
                if (s_instance == null) s_instance = Create();
                return s_instance;
            }
        }

        private enum Mode { Hidden, Splash, Level }

        private UIDocument m_document;
        private LoadingScreenContent m_content;
        private Mode m_mode = Mode.Hidden;

        private VisualElement m_root, m_background, m_splash, m_level, m_role, m_tipBlock, m_progressFill;
        private Label m_splashTagline, m_levelName, m_levelDescription, m_roleName, m_roleCharacter, m_roleAbility, m_rolePerks,
            m_tip, m_status, m_percent;

        // Level mode.
        private AsyncOperation m_sceneLoad;
        private float m_shownAt;
        private float m_spawnedAt = -1f;
        private float m_nextTipAt;
        private readonly List<string> m_tipOrder = new();
        private int m_tipIndex;

        private NetworkSceneManager m_hookedScenes;
        private bool m_hideOnSceneChange;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void CreateOnStart()
        {
            // Created up front, so it is already listening when the first game starts.
            _ = Instance;
        }

        private static LoadingScreen Create()
        {
            var layout = Resources.Load<VisualTreeAsset>(LayoutPath);
            if (layout == null)
            {
                Debug.LogWarning($"{nameof(LoadingScreen)}: no layout at Resources/{LayoutPath}; loading screens are off.");
                return null;
            }

            var holder = new GameObject(nameof(LoadingScreen));
            holder.SetActive(false);
            DontDestroyOnLoad(holder);

            var document = holder.AddComponent<UIDocument>();
            document.panelSettings = Resources.Load<PanelSettings>(PanelSettingsPath);
            if (document.panelSettings == null)
            {
                // Works without the asset, but without the theme's fonts: run Set Up Boot And Loading Screens.
                var fallback = ScriptableObject.CreateInstance<PanelSettings>();
                fallback.scaleMode = PanelScaleMode.ScaleWithScreenSize;
                fallback.referenceResolution = new Vector2Int(1920, 1080);
                fallback.match = 0.5f;
                fallback.sortingOrder = 1000;
                document.panelSettings = fallback;
            }

            document.visualTreeAsset = layout;

            var screen = holder.AddComponent<LoadingScreen>();
            screen.m_document = document;
            holder.SetActive(true);
            return screen;
        }

        private void OnEnable()
        {
            m_content = LoadingScreenContent.LoadOrDefault();
            QueryElements();
            SetVisible(false, instant: true);
            SceneManager.activeSceneChanged += HandleActiveSceneChanged;
        }

        private void OnDisable()
        {
            SceneManager.activeSceneChanged -= HandleActiveSceneChanged;
            HookSceneEvents(null);
        }

        private void QueryElements()
        {
            var root = m_document.rootVisualElement;
            m_root = root.Q("loading-root");
            m_background = root.Q("loading-background");
            m_splash = root.Q("loading-splash");
            m_level = root.Q("loading-level");
            m_role = root.Q("loading-role");
            m_tipBlock = root.Q("loading-tip-block");
            m_progressFill = root.Q("loading-progress-fill");

            m_splashTagline = root.Q<Label>("loading-splash-tagline");
            m_levelName = root.Q<Label>("loading-level-name");
            m_levelDescription = root.Q<Label>("loading-level-description");
            m_roleName = root.Q<Label>("loading-role-name");
            m_roleCharacter = root.Q<Label>("loading-role-character");
            m_roleAbility = root.Q<Label>("loading-role-ability");
            m_rolePerks = root.Q<Label>("loading-role-perks");
            m_tip = root.Q<Label>("loading-tip");
            m_status = root.Q<Label>("loading-status");
            m_percent = root.Q<Label>("loading-percent");

            // Only the screen itself may catch the pointer, and only while shown: the panel's full-screen root and its
            // template container would otherwise sit invisibly over the menu and take its clicks.
            root.pickingMode = PickingMode.Ignore;
            foreach (var child in root.Children()) child.pickingMode = PickingMode.Ignore;
            if (m_root != null) m_root.pickingMode = PickingMode.Position;
        }

        // ---- Splash ----------------------------------------------------------------------------------

        /// <summary>Shows the startup splash.</summary>
        public static void ShowSplash()
        {
            var screen = Instance;
            if (screen == null) return;

            screen.m_mode = Mode.Splash;
            Section(screen.m_splash, true);
            Section(screen.m_level, false);
            Section(screen.m_tipBlock, false);
            if (screen.m_splashTagline != null) screen.m_splashTagline.text = screen.m_content.SplashTagline;
            if (screen.m_background != null) screen.m_background.style.backgroundImage = StyleKeyword.None;

            screen.SetProgress(0f, "Loading");
            screen.SetVisible(true, instant: true);
        }

        /// <summary>Sets the bar and the line above it, in whatever mode is showing.</summary>
        public static void Report(float progress, string status) => Instance?.SetProgress(progress, status);

        /// <summary>Fades out once the next scene becomes active, e.g. the menu the splash was loading.</summary>
        public static void HideOnNextScene()
        {
            if (Instance != null) Instance.m_hideOnSceneChange = true;
        }

        /// <summary>Fades the screen out now.</summary>
        public static void Hide() => Instance?.HideScreen();

        // ---- Level -----------------------------------------------------------------------------------

        private void Update()
        {
            HookSceneEvents(NetworkManager.Singleton);

            if (m_mode != Mode.Level) return;

            UpdateLevelProgress();
            RotateTip();
        }

        /// <summary>Follows the scene events of whichever Netcode session is running; they are recreated with each one.</summary>
        private void HookSceneEvents(NetworkManager networkManager)
        {
            var scenes = networkManager != null && networkManager.IsListening ? networkManager.SceneManager : null;
            if (scenes == m_hookedScenes) return;

            if (m_hookedScenes != null) m_hookedScenes.OnSceneEvent -= HandleSceneEvent;
            m_hookedScenes = scenes;
            if (m_hookedScenes != null) m_hookedScenes.OnSceneEvent += HandleSceneEvent;
        }

        private void HandleSceneEvent(SceneEvent sceneEvent)
        {
            var networkManager = NetworkManager.Singleton;
            if (sceneEvent == null || networkManager == null || sceneEvent.ClientId != networkManager.LocalClientId) return;
            if (sceneEvent.SceneEventType != SceneEventType.Load) return;

            var flow = FindAnyObjectByType<SessionFlow>();
            if (flow == null || !flow.IsLevel(sceneEvent.SceneName)) return;

            ShowLevel(flow, sceneEvent.SceneName, sceneEvent.AsyncOperation);
        }

        private void ShowLevel(SessionFlow flow, string sceneName, AsyncOperation load)
        {
            m_mode = Mode.Level;
            m_sceneLoad = load;
            m_shownAt = Time.unscaledTime;
            m_spawnedAt = -1f;

            Section(m_splash, false);
            Section(m_level, true);
            Section(m_tipBlock, m_content.Tips != null && m_content.Tips.Count > 0);

            FillLevel(flow, sceneName);
            ShuffleTips();
            SetProgress(0f, "Loading the level");
            SetVisible(true, instant: false);
        }

        /// <summary>The level's name, description and picture, and who this player will be in it.</summary>
        private void FillLevel(SessionFlow flow, string sceneName)
        {
            var level = flow.Levels != null ? flow.Levels.Find(sceneName) : null;

            if (m_levelName != null) m_levelName.text = flow.DisplayNameOf(sceneName);
            if (m_levelDescription != null) m_levelDescription.text = level?.Description ?? string.Empty;

            if (m_background != null)
                m_background.style.backgroundImage = level != null && level.LoadingImage != null
                    ? new StyleBackground(level.LoadingImage)
                    : new StyleBackground(StyleKeyword.None);

            FillRole();
        }

        private void FillRole()
        {
            var character = LocalCharacter();
            Section(m_role, character != null);
            if (character == null) return;

            if (m_roleName != null) m_roleName.text = character.RoleOrName;
            if (m_roleCharacter != null) m_roleCharacter.text = character.DisplayName;

            if (m_roleAbility != null)
            {
                m_roleAbility.text = CharacterCatalog.DescribeAbility(character.Ability);
                Section(m_roleAbility, m_roleAbility.text.Length > 0);
            }

            if (m_rolePerks != null)
            {
                m_rolePerks.text = CharacterCatalog.Summarise(character.Perks);
                Section(m_rolePerks, m_rolePerks.text.Length > 0);
            }
        }

        /// <summary>The character this player will spawn as: their saved one when resuming, else their pick.</summary>
        private static CharacterCatalog.Character LocalCharacter()
        {
            var selections = FindAnyObjectByType<CharacterSelections>();
            var catalog = selections != null ? selections.Catalog : null;
            if (catalog == null) return null;

            var choice = selections.LocalChoice;
            var sessions = FindAnyObjectByType<SessionService>();
            if (sessions != null && sessions.IsConnected)
            {
                foreach (var member in sessions.Members)
                {
                    if (member.IsLocal) choice = selections.RoomChoiceOf(member);
                }
            }

            return catalog.Find(catalog.Resolve(choice).CharacterId);
        }

        private void UpdateLevelProgress()
        {
            var networkManager = NetworkManager.Singleton;
            var elapsed = Time.unscaledTime - m_shownAt;

            // The session ended under the load, or something went wrong: never leave the player staring at this.
            if (networkManager == null || !networkManager.IsListening || elapsed > GiveUpSeconds)
            {
                HideScreen();
                return;
            }

            var sceneLoaded = m_sceneLoad == null || m_sceneLoad.isDone;
            var player = networkManager.SpawnManager != null ? networkManager.SpawnManager.GetLocalPlayerObject() : null;

            if (!sceneLoaded)
            {
                SetProgress(Mathf.Clamp01(m_sceneLoad.progress / 0.9f) * 0.8f, "Loading the level");
                return;
            }

            if (player == null)
            {
                SetProgress(0.9f, networkManager.IsServer ? "Placing your player" : "Waiting for the host");
                return;
            }

            if (m_spawnedAt < 0f) m_spawnedAt = Time.unscaledTime;
            SetProgress(1f, "Ready");

            if (Time.unscaledTime - m_spawnedAt >= SettleAfterSpawnSeconds && elapsed >= m_content.MinimumLevelSeconds) HideScreen();
        }

        // ---- Tips ------------------------------------------------------------------------------------

        private void ShuffleTips()
        {
            m_tipOrder.Clear();
            if (m_content.Tips != null) m_tipOrder.AddRange(m_content.Tips);

            for (var i = m_tipOrder.Count - 1; i > 0; i--)
            {
                var j = Random.Range(0, i + 1);
                (m_tipOrder[i], m_tipOrder[j]) = (m_tipOrder[j], m_tipOrder[i]);
            }

            m_tipIndex = -1;
            m_nextTipAt = 0f;
            RotateTip();
        }

        private void RotateTip()
        {
            if (m_tip == null || m_tipOrder.Count == 0 || Time.unscaledTime < m_nextTipAt) return;

            m_tipIndex = (m_tipIndex + 1) % m_tipOrder.Count;
            m_tip.text = m_tipOrder[m_tipIndex];
            m_nextTipAt = Time.unscaledTime + m_content.SecondsPerTip;
        }

        // ---- Showing ---------------------------------------------------------------------------------

        private void SetProgress(float progress, string status)
        {
            progress = Mathf.Clamp01(progress);
            if (m_progressFill != null) m_progressFill.style.width = Length.Percent(progress * 100f);
            if (m_percent != null) m_percent.text = $"{Mathf.RoundToInt(progress * 100f)}%";
            if (m_status != null) m_status.text = status ?? string.Empty;
        }

        private void HandleActiveSceneChanged(Scene previous, Scene next)
        {
            if (!m_hideOnSceneChange) return;

            m_hideOnSceneChange = false;

            // A beat on the new scene, so its first frame is drawn before the screen lifts.
            m_root?.schedule.Execute(HideScreen).StartingIn(250);
        }

        private void HideScreen()
        {
            m_mode = Mode.Hidden;
            m_sceneLoad = null;
            SetVisible(false, instant: false);
        }

        private void SetVisible(bool visible, bool instant)
        {
            if (m_root == null) return;

            if (visible)
            {
                m_root.RemoveFromClassList("qv-loading--hidden");
                m_root.pickingMode = PickingMode.Position;

                if (instant) m_root.AddToClassList("qv-loading--visible");
                else m_root.schedule.Execute(() => m_root.AddToClassList("qv-loading--visible")).StartingIn(16);
                return;
            }

            m_root.RemoveFromClassList("qv-loading--visible");
            m_root.pickingMode = PickingMode.Ignore;

            if (instant)
            {
                m_root.AddToClassList("qv-loading--hidden");
                return;
            }

            // Out of the layout once the fade has finished, unless something showed it again meanwhile.
            m_root.schedule.Execute(() =>
            {
                if (m_mode == Mode.Hidden) m_root.AddToClassList("qv-loading--hidden");
            }).StartingIn((long)(FadeSeconds * 1000f) + 50);
        }

        private static void Section(VisualElement element, bool shown)
        {
            if (element != null) element.style.display = shown ? DisplayStyle.Flex : DisplayStyle.None;
        }
    }
}
