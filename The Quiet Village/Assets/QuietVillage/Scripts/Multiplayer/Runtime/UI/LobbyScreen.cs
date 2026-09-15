using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using QuietVillage.Multiplayer.Characters;
using QuietVillage.Multiplayer.Flow;
using QuietVillage.Multiplayer.Saves;
using QuietVillage.Multiplayer.Sessions;
using UnityEngine;
using UnityEngine.UIElements;

namespace QuietVillage.Multiplayer.UI
{
    /// <summary>
    /// The main menu and lobby: Play, Character, Settings and Quit; then browse rooms, create one, join one, and wait in
    /// it until the host starts the game.
    /// </summary>
    /// <remarks>
    /// A view over <see cref="SessionService"/> and <see cref="SessionFlow"/> that keeps no session state of
    /// its own. Everything shown is re-read from them on each change, so arriving back in the lobby mid-room,
    /// or the room ending under the screen, always displays the truth rather than a stale copy.
    ///
    /// References arrive through <see cref="Bind"/> from <see cref="MultiplayerBootstrap"/>: the services
    /// live on a persistent root that a scene object cannot reference in the Inspector.
    ///
    /// Which of the menu and the room browser shows is the one piece of state kept here, because it is the player's
    /// choice rather than the session's: being in a room always shows the waiting room over both.
    /// </remarks>
    [RequireComponent(typeof(UIDocument))]
    public class LobbyScreen : MonoBehaviour
    {
        [SerializeField] private UIDocument m_document;

        [Tooltip("Seconds between automatic room list refreshes while browsing. Unity Lobby allows roughly " +
                 "one query per second per player, so keep this comfortably above that.")]
        [SerializeField] private float m_autoRefreshInterval = 5f;

        [Tooltip("Minimum seconds between manual refreshes.")]
        [SerializeField] private float m_refreshCooldown = 1.5f;

        private const string DisplayNamePrefsKey = "Modules.Multiplayer.DisplayName";
        private const float RoomRowHeight = 52f;
        private const float PlayerRowHeight = 44f;

        private SessionService m_sessions;
        private SessionFlow m_flow;
        private CharacterSelections m_characters;
        private CharacterPreviewStage m_previewStage;
        private bool m_subscribed;

        private CharacterScreen m_characterScreen;
        private GameplaySettingsPanel m_settingsPanel;
        private Button m_characterButton;
        private Button m_roomCharacterButton;

        private readonly List<RoomListing> m_rooms = new();
        private readonly List<RoomMember> m_members = new();

        private TextField m_nameField;
        private VisualElement m_menuView;
        private VisualElement m_header;
        private Label m_menuProfile;
        private MenuSettingsPanel m_menuSettings;
        private bool m_showBrowser;
        private VisualElement m_browserView;
        private VisualElement m_roomView;
        private Label m_statusBar;
        private VisualElement m_endedNotice;
        private Label m_endedNoticeText;

        private Label m_roomCount;
        private ListView m_roomList;
        private Label m_roomListEmpty;
        private Button m_refreshButton;
        private Button m_createButton;
        private Button m_joinCodeButton;

        private Label m_roomName;
        private Label m_roomLevel;
        private Label m_roomPlayers;
        private Label m_roomCode;
        private Label m_roomPassword;
        private Label m_roomHint;
        private ListView m_playerList;
        private Button m_leaveButton;
        private Button m_startButton;

        private VisualElement m_createDialog;
        private TextField m_createName;
        private SliderInt m_createMaxPlayers;
        private TextField m_createPassword;
        private Label m_createError;
        private DropdownField m_createLevel;
        private Label m_createLevelHint;
        private readonly List<string> m_levelScenes = new();
        private DropdownField m_createSave;
        private Label m_createSaveHint;
        private readonly List<SaveEntry> m_saves = new();
        private Button m_createCancel;
        private Button m_createConfirm;
        private Toggle m_createShowPassword;
        private Label m_summaryName, m_summaryLevel, m_summaryLevelDesc, m_summaryPlayers, m_summaryAccess, m_summaryStart,
            m_summaryHost, m_summarySettings;

        private VisualElement m_joinDialog;
        private Label m_joinTitle;
        private TextField m_joinCode;
        private TextField m_joinPassword;
        private Label m_joinError;
        private Button m_joinCancel;
        private Button m_joinConfirm;

        private VisualElement m_busyOverlay;
        private Label m_busyLabel;

        private bool m_uiReady;
        private bool m_querying;
        private float m_nextAutoRefreshAt;
        private float m_refreshAllowedAt;

        private const string NewGameChoice = "New game";

        // Errors that are not the session's own state: a failed refresh, a failed start.
        private string m_message = string.Empty;

        // The listed room the join dialog is asking a password for; null means "join by code".
        private string m_joinTargetRoomId;

        /// <summary>Connects the screen to the persistent services. Safe to call before or after OnEnable.</summary>
        /// <param name="characters">Who this player is; without it the Character screen stays hidden.</param>
        /// <param name="previewStage">The scene's stage that renders the chosen body; optional.</param>
        public void Bind(SessionService sessions, SessionFlow flow, CharacterSelections characters = null,
            CharacterPreviewStage previewStage = null)
        {
            Unsubscribe();

            m_sessions = sessions;
            m_flow = flow;
            m_characters = characters;
            m_previewStage = previewStage;

            if (!m_uiReady) return;

            m_characterScreen?.Bind(m_characters, m_previewStage);
            m_settingsPanel?.Bind(m_sessions);

            Subscribe();
            RefreshAll();
            RequestRoomRefresh(force: true);
        }

        private void OnEnable()
        {
            if (m_document == null) m_document = GetComponent<UIDocument>();

            var root = m_document != null ? m_document.rootVisualElement : null;
            if (root == null)
            {
                Debug.LogError($"{nameof(LobbyScreen)}: the UIDocument has no visual tree. Assign LobbyScreen.uxml.", this);
                return;
            }

            if (!QueryElements(root))
            {
                Debug.LogError($"{nameof(LobbyScreen)}: LobbyScreen.uxml is missing expected elements.", this);
                return;
            }

            ConfigureElements();
            RegisterCallbacks();
            m_characterScreen?.Bind(m_characters, m_previewStage);
            m_settingsPanel?.Bind(m_sessions);
            m_uiReady = true;

            // Coming back from a game leaves UHFPS's locked, hidden cursor behind, and possibly the stopped
            // time scale of its pause menu. The lobby needs neither.
            UnityEngine.Cursor.lockState = CursorLockMode.None;
            UnityEngine.Cursor.visible = true;
            Time.timeScale = 1f;

            if (m_sessions == null) return;

            Subscribe();
            RefreshAll();
            RequestRoomRefresh(force: true);
        }

        private void OnDisable()
        {
            // Stops the preview camera, which would otherwise keep rendering into a screen nobody can see.
            m_characterScreen?.Close();
            m_menuSettings?.Close();

            m_uiReady = false;
            Unsubscribe();
            SaveDisplayName();
        }

        private void Update()
        {
            if (!m_uiReady || m_sessions == null) return;

            if (IsBrowsing && !m_querying && Time.unscaledTime >= m_nextAutoRefreshAt) RequestRoomRefresh(force: false);
        }

        private bool IsBrowsing => m_sessions != null && !m_sessions.IsConnected && !m_sessions.IsBusy && m_showBrowser;

        private bool IsBusy => (m_sessions != null && m_sessions.IsBusy) || (m_flow != null && m_flow.IsStarting);

        private string DisplayName => SessionService.SanitiseDisplayName(m_nameField != null ? m_nameField.value : null);

        // ---- Setup -----------------------------------------------------------------------------------

        private bool QueryElements(VisualElement root)
        {
            m_nameField = root.Q<TextField>("player-name");

            // Not required: an older LobbyScreen.uxml without the main menu opens straight on the room browser.
            m_menuView = root.Q("menu-view");
            m_header = root.Q("lobby-header");
            m_menuProfile = root.Q<Label>("menu-profile");
            m_showBrowser = m_menuView == null;
            BindMenu(root);

            m_menuSettings = new MenuSettingsPanel(root);
            if (!m_menuSettings.IsAvailable) m_menuSettings = null;

            m_createShowPassword = root.Q<Toggle>("create-show-password");
            m_summaryName = root.Q<Label>("create-summary-name");
            m_summaryLevel = root.Q<Label>("create-summary-level");
            m_summaryLevelDesc = root.Q<Label>("create-summary-level-desc");
            m_summaryPlayers = root.Q<Label>("create-summary-players");
            m_summaryAccess = root.Q<Label>("create-summary-access");
            m_summaryStart = root.Q<Label>("create-summary-start");
            m_summaryHost = root.Q<Label>("create-summary-host");
            m_summarySettings = root.Q<Label>("create-summary-settings");

            m_browserView = root.Q("browser-view");
            m_roomView = root.Q("room-view");
            m_statusBar = root.Q<Label>("status-bar");

            // Not required: an older LobbyScreen.uxml without the notice should still open the lobby.
            m_endedNotice = root.Q("ended-notice");
            m_endedNoticeText = root.Q<Label>("ended-notice-text");
            var dismissNotice = root.Q<Button>("ended-notice-dismiss");
            if (dismissNotice != null) dismissNotice.clicked += DismissEndedNotice;

            m_roomCount = root.Q<Label>("room-count");
            m_roomList = root.Q<ListView>("room-list");
            m_roomListEmpty = root.Q<Label>("room-list-empty");
            m_refreshButton = root.Q<Button>("refresh-button");
            m_createButton = root.Q<Button>("create-button");
            m_joinCodeButton = root.Q<Button>("join-code-button");

            m_roomName = root.Q<Label>("room-name");

            // Not required: an older LobbyScreen.uxml without level controls still plays the first level.
            m_roomLevel = root.Q<Label>("room-level");
            m_createLevel = root.Q<DropdownField>("create-level");
            m_createLevelHint = root.Q<Label>("create-level-hint");

            m_roomPlayers = root.Q<Label>("room-players");
            m_roomCode = root.Q<Label>("room-code");
            m_roomPassword = root.Q<Label>("room-password");
            m_roomHint = root.Q<Label>("room-hint");
            m_playerList = root.Q<ListView>("player-list");
            m_leaveButton = root.Q<Button>("leave-button");
            m_startButton = root.Q<Button>("start-button");

            m_createDialog = root.Q("create-dialog");
            m_createName = root.Q<TextField>("create-name");
            m_createMaxPlayers = root.Q<SliderInt>("create-max-players");
            m_createPassword = root.Q<TextField>("create-password");
            m_createError = root.Q<Label>("create-error");

            // Not required: an older LobbyScreen.uxml without the picker still creates new games.
            m_createSave = root.Q<DropdownField>("create-save");
            m_createSaveHint = root.Q<Label>("create-save-hint");
            m_createCancel = root.Q<Button>("create-cancel");
            m_createConfirm = root.Q<Button>("create-confirm");

            m_joinDialog = root.Q("join-dialog");
            m_joinTitle = root.Q<Label>("join-title");
            m_joinCode = root.Q<TextField>("join-code");
            m_joinPassword = root.Q<TextField>("join-password");
            m_joinError = root.Q<Label>("join-error");
            m_joinCancel = root.Q<Button>("join-cancel");
            m_joinConfirm = root.Q<Button>("join-confirm");

            m_busyOverlay = root.Q("busy-overlay");
            m_busyLabel = root.Q<Label>("busy-label");

            // Not required: an older LobbyScreen.uxml without the Character screen still plays, as the default character.
            m_characterButton = root.Q<Button>("character-button");
            m_roomCharacterButton = root.Q<Button>("room-character-button");
            m_characterScreen = new CharacterScreen(root);
            if (!m_characterScreen.IsAvailable) m_characterScreen = null;

            // Not required either: without it every room plays Normal.
            m_settingsPanel = new GameplaySettingsPanel(root);
            if (!m_settingsPanel.IsAvailable) m_settingsPanel = null;

            return m_nameField != null && m_browserView != null && m_roomView != null && m_statusBar != null
                   && m_roomCount != null && m_roomList != null && m_roomListEmpty != null && m_refreshButton != null
                   && m_createButton != null && m_joinCodeButton != null && m_roomName != null && m_roomPlayers != null
                   && m_roomCode != null && m_roomPassword != null && m_roomHint != null && m_playerList != null
                   && m_leaveButton != null && m_startButton != null && m_createDialog != null && m_createName != null
                   && m_createMaxPlayers != null && m_createPassword != null && m_createError != null
                   && m_createCancel != null && m_createConfirm != null
                   && m_joinDialog != null && m_joinTitle != null && m_joinCode != null && m_joinPassword != null
                   && m_joinError != null && m_joinCancel != null && m_joinConfirm != null
                   && m_busyOverlay != null && m_busyLabel != null;
        }

        /// <summary>Settings that live in code rather than UXML, where the compiler checks them.</summary>
        private void ConfigureElements()
        {
            m_nameField.maxLength = SessionService.MaxDisplayNameLength;
            m_nameField.value = LoadDisplayName();

            m_createName.maxLength = SessionService.MaxRoomNameLength;
            m_createPassword.maxLength = SessionService.MaxPasswordLength;
            m_createPassword.isPasswordField = true;
            m_createMaxPlayers.lowValue = SessionService.MinRoomSize;
            m_createMaxPlayers.highValue = SessionService.MaxRoomSize;
            m_createMaxPlayers.showInputField = true;

            m_joinCode.maxLength = 16;
            m_joinPassword.maxLength = SessionService.MaxPasswordLength;
            m_joinPassword.isPasswordField = true;

            m_roomList.fixedItemHeight = RoomRowHeight;
            m_roomList.selectionType = SelectionType.None;
            m_roomList.itemsSource = m_rooms;
            m_roomList.makeItem = MakeRoomRow;
            m_roomList.bindItem = BindRoomRow;

            m_playerList.fixedItemHeight = PlayerRowHeight;
            m_playerList.selectionType = SelectionType.None;
            m_playerList.itemsSource = m_members;
            m_playerList.makeItem = MakePlayerRow;
            m_playerList.bindItem = BindPlayerRow;
        }

        private void RegisterCallbacks()
        {
            // Elements are rebuilt each time the UIDocument is enabled, so these never double up.
            m_refreshButton.clicked += () => RequestRoomRefresh(force: false);
            m_createButton.clicked += OpenCreateDialog;
            m_joinCodeButton.clicked += () => OpenJoinDialog(null);
            m_startButton.clicked += () => Run(StartGameAsync());
            m_leaveButton.clicked += () =>
            {
                if (m_sessions != null) Run(m_sessions.LeaveSessionAsync());
            };

            m_createCancel.clicked += () => SetHidden(m_createDialog, true);
            m_createConfirm.clicked += ConfirmCreate;
            m_joinCancel.clicked += () => SetHidden(m_joinDialog, true);
            m_joinConfirm.clicked += ConfirmJoin;

            m_nameField.RegisterCallback<FocusOutEvent>(_ => SaveDisplayName());

            if (m_characterButton != null) m_characterButton.clicked += OpenCharacterScreen;
            if (m_roomCharacterButton != null) m_roomCharacterButton.clicked += OpenCharacterScreen;
            if (m_characterScreen != null) m_characterScreen.Confirmed += HandleCharacterConfirmed;

            // Escape closes the join dialog too.
            m_joinDialog.RegisterCallback<KeyDownEvent>(evt =>
            {
                if (evt.keyCode == KeyCode.Escape) SetHidden(m_joinDialog, true);
                else if (evt.keyCode is KeyCode.Return or KeyCode.KeypadEnter) ConfirmJoin();
                else return;

                evt.StopPropagation();
            });

            // A save decides its own level, so picking one moves and locks the level picker to match.
            m_createSave?.RegisterValueChangedCallback(_ => RefreshLevelPicker());
            m_createLevel?.RegisterValueChangedCallback(_ => RefreshLevelPicker());

            // The preview follows every field as it changes.
            m_createName.RegisterValueChangedCallback(_ => RefreshCreateSummary());
            m_createMaxPlayers.RegisterValueChangedCallback(_ => RefreshCreateSummary());
            m_createPassword.RegisterValueChangedCallback(_ => RefreshCreateSummary());
            m_createShowPassword?.RegisterValueChangedCallback(evt =>
            {
                m_createPassword.isPasswordField = !evt.newValue;
                RefreshCreateSummary();
            });

            // Enter creates, Escape cancels, as a dialog should.
            m_createDialog.RegisterCallback<KeyDownEvent>(evt =>
            {
                if (evt.keyCode == KeyCode.Escape) SetHidden(m_createDialog, true);
                else if (evt.keyCode is KeyCode.Return or KeyCode.KeypadEnter) ConfirmCreate();
                else return;

                evt.StopPropagation();
            });
        }

        // ---- Main menu -------------------------------------------------------------------------------

        private void BindMenu(VisualElement root)
        {
            if (m_menuView == null) return;

            var play = root.Q<Button>("menu-play");
            var character = root.Q<Button>("menu-character");
            var settings = root.Q<Button>("menu-settings");
            var quit = root.Q<Button>("menu-quit");
            var version = root.Q<Label>("menu-version");

            if (play != null) play.clicked += () => ShowBrowser(true);
            if (character != null) character.clicked += OpenCharacterScreen;
            if (settings != null) settings.clicked += () => m_menuSettings?.Open();
            if (quit != null) quit.clicked += Quit;
            if (version != null) version.text = $"v{Application.version}";

            var back = root.Q<Button>("browser-back");
            if (back != null) back.clicked += () => ShowBrowser(false);
        }

        private void ShowBrowser(bool show)
        {
            if (m_menuView == null) show = true;
            m_showBrowser = show;

            RefreshAll();
            if (show) RequestRoomRefresh(force: true);
        }

        private static void Quit()
        {
#if UNITY_EDITOR
            UnityEditor.EditorApplication.isPlaying = false;
#else
            Application.Quit();
#endif
        }

        private void RefreshMenu(bool inRoom)
        {
            var showMenu = m_menuView != null && !inRoom && !m_showBrowser;
            SetHidden(m_menuView, !showMenu);
            SetHidden(m_header, showMenu);
            if (!showMenu || m_menuProfile == null) return;

            var role = CanChooseCharacter ? m_characters.Catalog.RoleOf(m_characters.LocalChoice) : string.Empty;
            m_menuProfile.text = string.IsNullOrEmpty(role)
                ? $"Playing as {DisplayName}"
                : $"Playing as {DisplayName}  ·  {role}";
        }

        private void Subscribe()
        {
            if (m_subscribed) return;

            if (m_sessions != null)
            {
                m_sessions.StateChanged += HandleSessionStateChanged;
                m_sessions.RosterChanged += RefreshAll;
            }

            if (m_flow != null) m_flow.StartingChanged += RefreshAll;
            if (m_characters != null) m_characters.LocalChoiceChanged += RefreshAll;
            m_subscribed = true;
        }

        private void Unsubscribe()
        {
            if (!m_subscribed) return;

            if (m_sessions != null)
            {
                m_sessions.StateChanged -= HandleSessionStateChanged;
                m_sessions.RosterChanged -= RefreshAll;
            }

            if (m_flow != null) m_flow.StartingChanged -= RefreshAll;
            if (m_characters != null) m_characters.LocalChoiceChanged -= RefreshAll;
            m_subscribed = false;
        }

        // ---- Rendering -------------------------------------------------------------------------------

        private void RefreshAll()
        {
            if (!m_uiReady || m_sessions == null) return;

            var inRoom = m_sessions.IsConnected;
            RefreshMenu(inRoom);
            SetHidden(m_browserView, inRoom || !m_showBrowser);
            SetHidden(m_roomView, !inRoom);

            // The name travels with the join, so changing it mid-room would not reach anyone.
            m_nameField.SetEnabled(!inRoom && !IsBusy);

            RefreshBusy();
            RefreshBrowser();
            RefreshRoom();
            RefreshStatus();
            RefreshEndedNotice();
            RefreshCharacterButtons();
        }

        // ---- Character -------------------------------------------------------------------------------

        private bool CanChooseCharacter =>
            m_characterScreen != null && m_characters != null && m_characters.Catalog != null
            && m_characters.Catalog.Default != null;

        private void RefreshCharacterButtons()
        {
            var available = CanChooseCharacter;
            SetHidden(m_characterButton, !available);
            SetHidden(m_roomCharacterButton, !available);
            if (!available) return;

            var saved = LocalSavedCharacter();
            var role = m_characters.Catalog.RoleOf(saved ?? m_characters.LocalChoice);
            if (m_characterButton != null) m_characterButton.text = $"Character: {role}";
            if (m_roomCharacterButton != null)
                m_roomCharacterButton.text = saved.HasValue ? "View Character" : "Change Character";

            // Once the game is starting, the host is already spawning from the record; a change now would not show.
            var canChange = !IsBusy;
            m_characterButton?.SetEnabled(canChange);
            m_roomCharacterButton?.SetEnabled(canChange);

            if (!canChange && m_characterScreen.IsOpen) m_characterScreen.Close();
            if (m_characterScreen.IsOpen) m_characterScreen.SetTeammates(Teammates());
        }

        private void OpenCharacterScreen()
        {
            if (!CanChooseCharacter || IsBusy) return;

            var inRoom = m_sessions != null && m_sessions.IsConnected;
            m_characterScreen.Open(m_nameField.value, nameEditable: !inRoom, LocalSavedCharacter(), Teammates());
        }

        /// <summary>The character a room member will spawn as: their saved one in a resumed game, else their pick.</summary>
        private CharacterChoice CharacterOf(RoomMember member) =>
            m_characters != null ? m_characters.RoomChoiceOf(member) : CharacterChoice.Parse(member.Character);

        /// <summary>This player's character in the save the room resumes; <c>null</c> when their pick decides.</summary>
        private CharacterChoice? LocalSavedCharacter()
        {
            if (m_sessions == null || !m_sessions.IsConnected || m_characters?.Catalog == null) return null;

            var saved = CharacterChoice.Parse(m_sessions.SavedCharacterOf(m_sessions.LocalPlayerId));
            return saved.IsEmpty ? null : m_characters.Catalog.Resolve(saved);
        }

        /// <summary>Everyone else in the room with the character they will play, for the Character screen's roster.</summary>
        private List<CharacterScreen.Teammate> Teammates()
        {
            var teammates = new List<CharacterScreen.Teammate>();
            if (m_sessions == null || !m_sessions.IsConnected || m_characters?.Catalog == null) return teammates;

            foreach (var member in m_sessions.Members)
            {
                if (member.IsLocal) continue;
                teammates.Add(new CharacterScreen.Teammate(member.DisplayName,
                    m_characters.Catalog.Resolve(CharacterOf(member)).CharacterId));
            }

            return teammates;
        }

        private void HandleCharacterConfirmed(string playerName)
        {
            // The name can only change outside a room; the screen locks the field otherwise.
            if (m_sessions == null || !m_sessions.IsConnected)
            {
                m_nameField.value = SessionService.SanitiseDisplayName(playerName);
                SaveDisplayName();
            }

            RefreshAll();
        }

        /// <summary>
        /// Explains a room the player did not leave: the host ended it, or the connection dropped.
        /// </summary>
        /// <remarks>
        /// Landing back in the lobby is otherwise silent, and looks like the game simply quit itself. The status
        /// bar is not enough: it is small, and it is cleared by the room refresh that follows a session ending.
        /// </remarks>
        private void RefreshEndedNotice()
        {
            if (m_endedNotice == null || m_endedNoticeText == null) return;

            var reason = m_sessions.SessionEndedReason;
            var show = !string.IsNullOrEmpty(reason) && !m_sessions.IsConnected;

            SetHidden(m_endedNotice, !show);
            if (show) m_endedNoticeText.text = reason;
        }

        private void DismissEndedNotice()
        {
            m_sessions.ClearSessionEndedReason();
            RefreshEndedNotice();
        }

        private void RefreshBusy()
        {
            var text = m_flow != null && m_flow.IsStarting ? "Starting the game…"
                : m_sessions.State == SessionConnectionState.SigningIn ? "Signing in…"
                : m_sessions.State == SessionConnectionState.Connecting ? "Connecting to the room…"
                : null;

            SetHidden(m_busyOverlay, text == null);
            if (text != null) m_busyLabel.text = text;
        }

        private void RefreshBrowser()
        {
            var joinable = 0;
            foreach (var room in m_rooms)
            {
                if (room.IsJoinable) joinable++;
            }

            m_roomCount.text = m_rooms.Count == 0 ? string.Empty : $"{m_rooms.Count} listed · {joinable} open";
            SetHidden(m_roomListEmpty, m_rooms.Count > 0 || m_querying);
            SetHidden(m_roomList, m_rooms.Count == 0);
            m_roomList.Rebuild();

            m_refreshButton.SetEnabled(!m_querying && !IsBusy);
            m_createButton.SetEnabled(!IsBusy);
            m_joinCodeButton.SetEnabled(!IsBusy);
        }

        private void RefreshRoom()
        {
            m_members.Clear();
            if (!m_sessions.IsConnected)
            {
                m_playerList.Rebuild();
                return;
            }

            m_members.AddRange(m_sessions.Members);
            m_playerList.Rebuild();

            m_roomName.text = m_sessions.RoomName;
            if (m_roomLevel != null)
                m_roomLevel.text = m_flow != null && !string.IsNullOrEmpty(m_flow.RoomLevel)
                    ? m_flow.DisplayNameOf(m_flow.RoomLevel)
                    : string.Empty;

            m_roomPlayers.text = $"{m_sessions.PlayerCount} / {m_sessions.MaxPlayers} players";
            m_roomCode.text = m_sessions.JoinCode;
            SetHidden(m_roomPassword, !m_sessions.HasPassword);

            var isHost = m_sessions.IsHost;
            SetHidden(m_startButton, !isHost);
            m_startButton.SetEnabled(m_flow != null && m_flow.CanStartGame);
            m_leaveButton.SetEnabled(!IsBusy);

            m_roomHint.text = isHost
                ? "Start when everyone is here. The room locks once the game begins, so nobody can join mid-game."
                : "Waiting for the host to start the game…";

            // Once the game is starting the host is already loading the level with the settings as they were.
            m_settingsPanel?.Refresh(canEdit: isHost && !IsBusy);
        }

        private void RefreshStatus()
        {
            var text = m_sessions.State == SessionConnectionState.Error ? m_sessions.LastError : m_message;
            m_statusBar.text = text ?? string.Empty;
        }

        private VisualElement MakeRoomRow()
        {
            var row = new VisualElement();
            row.AddToClassList("mp-row");

            var nameCell = new VisualElement();
            nameCell.AddToClassList("mp-col-name");
            nameCell.Add(new Label { name = "name" });

            var passwordBadge = new Label("Password") { name = "password" };
            passwordBadge.AddToClassList("mp-badge");
            nameCell.Add(passwordBadge);
            row.Add(nameCell);

            var players = new Label { name = "players" };
            players.AddToClassList("mp-col-players");
            row.Add(players);

            var status = new Label { name = "status" };
            status.AddToClassList("mp-col-status");
            row.Add(status);

            var actionCell = new VisualElement();
            actionCell.AddToClassList("mp-col-action");

            // Registered once per pooled row; the row carries whichever room it is currently bound to.
            var join = new Button(() =>
            {
                if (row.userData is RoomListing room) JoinListedRoom(room);
            }) { name = "join", text = "Join" };
            join.AddToClassList("mp-button");
            join.AddToClassList("mp-button--small");
            actionCell.Add(join);
            row.Add(actionCell);

            return row;
        }

        private void BindRoomRow(VisualElement row, int index)
        {
            if (index < 0 || index >= m_rooms.Count) return;

            var room = m_rooms[index];
            row.userData = room;

            row.Q<Label>("name").text = room.Name;
            SetHidden(row.Q<Label>("password"), !room.HasPassword);
            row.Q<Label>("players").text = $"{room.PlayerCount} / {room.MaxPlayers}";

            var status = row.Q<Label>("status");
            status.text = room.IsInGame ? "In game" : room.IsFull ? "Full" : "Open";
            status.EnableInClassList("mp-status--ingame", room.IsInGame);
            status.EnableInClassList("mp-status--full", !room.IsInGame && room.IsFull);
            status.EnableInClassList("mp-status--open", room.IsJoinable);

            var join = row.Q<Button>("join");
            join.text = room.HasPassword ? "Join…" : "Join";
            join.SetEnabled(room.IsJoinable && !IsBusy);
        }

        private VisualElement MakePlayerRow()
        {
            var row = new VisualElement();
            row.AddToClassList("mp-row");
            row.Add(new Label { name = "name" });

            var role = new Label { name = "role" };
            role.AddToClassList("mp-player-role");
            row.Add(role);

            var host = new Label("Host") { name = "host" };
            host.AddToClassList("mp-badge");
            host.AddToClassList("mp-badge--host");
            row.Add(host);

            var you = new Label("You") { name = "you" };
            you.AddToClassList("mp-badge");
            you.AddToClassList("mp-badge--you");
            row.Add(you);

            return row;
        }

        private void BindPlayerRow(VisualElement row, int index)
        {
            if (index < 0 || index >= m_members.Count) return;

            var member = m_members[index];
            row.Q<Label>("name").text = member.DisplayName;

            // Resolved like the spawn resolves it, so an unknown or missing character reads as the default role.
            var catalog = m_characters != null ? m_characters.Catalog : null;
            row.Q<Label>("role").text = catalog != null ? catalog.RoleOf(CharacterOf(member)) : string.Empty;

            SetHidden(row.Q<Label>("host"), !member.IsHost);
            SetHidden(row.Q<Label>("you"), !member.IsLocal);
        }

        // ---- Actions ---------------------------------------------------------------------------------

        private void HandleSessionStateChanged(SessionConnectionState state)
        {
            // A new attempt or its outcome replaces whatever the last one said.
            if (state == SessionConnectionState.Connecting || state == SessionConnectionState.Connected) m_message = string.Empty;

            RefreshAll();

            // Just left a room: show the list as it is now, not as it was before joining. Leaving a room lands on the
            // browser it was joined from, not back on the menu.
            if (state == SessionConnectionState.Connected) m_showBrowser = true;
            if (state == SessionConnectionState.Disconnected || state == SessionConnectionState.Error) RequestRoomRefresh(force: true);
        }

        private async void RequestRoomRefresh(bool force)
        {
            if (m_sessions == null || m_querying || m_sessions.IsConnected) return;
            if (!force && Time.unscaledTime < m_refreshAllowedAt) return;

            m_querying = true;
            m_refreshAllowedAt = Time.unscaledTime + m_refreshCooldown;
            m_nextAutoRefreshAt = Time.unscaledTime + m_autoRefreshInterval;
            RefreshAll();

            RoomQueryResult result;
            try
            {
                result = await m_sessions.QueryRoomsAsync();
            }
            catch (Exception exception)
            {
                result = RoomQueryResult.Failed($"Could not list rooms: {exception.Message}");
            }

            m_querying = false;

            // The screen may have closed, e.g. the game started, while the query was in flight.
            if (!m_uiReady) return;

            if (result.Success)
            {
                m_rooms.Clear();
                m_rooms.AddRange(result.Rooms);
                if (m_message.StartsWith("Could not list rooms", StringComparison.Ordinal)) m_message = string.Empty;
            }
            else
            {
                m_message = result.Error;
            }

            RefreshAll();
        }

        private void JoinListedRoom(RoomListing room)
        {
            if (m_sessions == null || IsBusy) return;

            if (room.HasPassword)
            {
                OpenJoinDialog(room);
                return;
            }

            SaveDisplayName();
            Run(m_sessions.JoinRoomAsync(DisplayName, room.Id, null));
        }

        private void OpenCreateDialog()
        {
            m_createName.value = $"{DisplayName}'s room";
            m_createMaxPlayers.value = SessionService.MaxRoomSize;
            m_createPassword.value = string.Empty;
            m_createPassword.isPasswordField = true;
            m_createShowPassword?.SetValueWithoutNotify(false);
            m_createError.text = string.Empty;

            FillLevelPicker();
            _ = RefreshSavesAsync();

            SetHidden(m_createDialog, false);
            RefreshCreateSummary();
            m_createName.Focus();
            m_createName.SelectAll();
        }

        /// <summary>
        /// Offers the host a new game or one of this machine's saves to carry on from.
        /// </summary>
        /// <remarks>
        /// Saves live with whoever hosts them, so this list is the host's own. Players joining bring nothing but
        /// their account, which is what the save keys their belongings to.
        /// </remarks>
        private async Task RefreshSavesAsync()
        {
            if (m_createSave == null) return;

            m_saves.Clear();
            m_createSave.choices = new List<string> { NewGameChoice };
            m_createSave.index = 0;
            if (m_createSaveHint != null) m_createSaveHint.text = string.Empty;

            var catalog = SaveCatalog.Active;
            if (catalog == null) return;

            var saves = await catalog.ListAsync();

            // The dialog may have closed, or the lobby unloaded, while the files were read.
            if (!m_uiReady || m_createSave == null) return;

            m_saves.AddRange(saves);

            var choices = new List<string> { NewGameChoice };
            foreach (var save in m_saves) choices.Add($"{LevelName(save.Level)}  ·  {save.Label}");

            m_createSave.choices = choices;
            m_createSave.index = 0;
            RefreshLevelPicker();

            if (m_createSaveHint != null)
                m_createSaveHint.text = m_saves.Count > 0
                    ? "Everyone keeps what they had when the game was saved, including their character."
                    : "No saved games yet.";
        }

        /// <summary>Lists the catalog's levels in the picker, keeping the one already chosen if it is still there.</summary>
        private void FillLevelPicker()
        {
            if (m_createLevel == null) return;

            var previous = SelectedPickerLevel();

            m_levelScenes.Clear();
            var names = new List<string>();

            var catalog = m_flow != null ? m_flow.Levels : null;
            if (catalog != null)
            {
                foreach (var level in catalog.Levels)
                {
                    if (level == null || string.IsNullOrEmpty(level.SceneName)) continue;

                    m_levelScenes.Add(level.SceneName);
                    names.Add(catalog.DisplayNameOf(level.SceneName));
                }
            }

            m_createLevel.choices = names;
            var index = m_levelScenes.IndexOf(previous);
            m_createLevel.index = index >= 0 ? index : m_levelScenes.Count > 0 ? 0 : -1;

            RefreshLevelPicker();
        }

        /// <summary>Follows the chosen save's level when there is one, and describes the level either way.</summary>
        private void RefreshLevelPicker()
        {
            if (m_createLevel == null) return;

            var save = ChosenSave();
            if (save.HasValue)
            {
                var index = m_levelScenes.IndexOf(save.Value.Level);
                if (index >= 0) m_createLevel.SetValueWithoutNotify(m_createLevel.choices[index]);
            }

            // The save's level is not the host's to change: its world only fits the level it was made in.
            m_createLevel.SetEnabled(!save.HasValue);

            RefreshCreateSummary();

            if (m_createLevelHint == null) return;

            var level = ChosenLevel();
            var catalogLevel = m_flow != null && m_flow.Levels != null ? m_flow.Levels.Find(level) : null;

            m_createLevelHint.text = save.HasValue && catalogLevel == null
                ? $"This save was made in '{level}', which is no longer in the level list."
                : catalogLevel != null ? catalogLevel.Description ?? string.Empty
                : string.Empty;
        }

        /// <summary>The Create Room preview: the room exactly as it will be made, in plain words.</summary>
        private void RefreshCreateSummary()
        {
            if (m_summaryName == null) return;

            var name = string.IsNullOrWhiteSpace(m_createName.value) ? "Untitled room" : m_createName.value.Trim();
            m_summaryName.text = name;

            var level = ChosenLevel();
            var catalogLevel = m_flow != null && m_flow.Levels != null ? m_flow.Levels.Find(level) : null;
            if (m_summaryLevel != null) m_summaryLevel.text = string.IsNullOrEmpty(level) ? "None available" : LevelName(level);
            if (m_summaryLevelDesc != null)
            {
                m_summaryLevelDesc.text = catalogLevel?.Description ?? string.Empty;
                SetHidden(m_summaryLevelDesc, string.IsNullOrWhiteSpace(m_summaryLevelDesc.text));
            }

            if (m_summaryPlayers != null) m_summaryPlayers.text = $"Up to {m_createMaxPlayers.value}";

            if (m_summaryAccess != null)
            {
                var password = m_createPassword.value ?? string.Empty;
                m_summaryAccess.text = password.Length == 0 ? "Open to anyone"
                    : m_createShowPassword != null && m_createShowPassword.value ? $"Password: {password}"
                    : $"Password ({password.Length} characters)";
            }

            var save = ChosenSave();
            if (m_summaryStart != null) m_summaryStart.text = save.HasValue ? $"Continue save ({save.Value.Label})" : "New game";
            if (m_summaryHost != null) m_summaryHost.text = DisplayName;

            if (m_summarySettings != null)
                m_summarySettings.text = save.HasValue
                    ? "The save's own settings, locked."
                    : $"{GameplaySettingsPanel.Remembered.Preset}: {GameplaySettingsPanel.Remembered.Summary()}\nYou can change these in the room before starting.";
        }

        /// <summary>The save picked in the dialog, or <c>null</c> for a new game.</summary>
        private SaveEntry? ChosenSave()
        {
            var chosen = m_createSave != null ? m_createSave.index - 1 : -1;
            return chosen >= 0 && chosen < m_saves.Count ? m_saves[chosen] : null;
        }

        private string SelectedPickerLevel()
        {
            if (m_createLevel == null) return string.Empty;

            var index = m_createLevel.index;
            return index >= 0 && index < m_levelScenes.Count ? m_levelScenes[index] : string.Empty;
        }

        /// <summary>The level the room will play: the save's own, or the one picked.</summary>
        private string ChosenLevel()
        {
            var save = ChosenSave();
            if (save.HasValue) return save.Value.Level;

            var picked = SelectedPickerLevel();
            if (!string.IsNullOrEmpty(picked)) return picked;

            // No picker in this layout: the flow's own default.
            return m_flow != null ? m_flow.RoomLevel : string.Empty;
        }

        private string LevelName(string sceneName) =>
            m_flow != null ? m_flow.DisplayNameOf(sceneName) : sceneName;

        private void ConfirmCreate()
        {
            if (m_sessions == null) return;

            var invalid = SessionService.ValidateRoomSettings(m_createName.value, m_createPassword.value);
            if (invalid == null && m_flow != null) invalid = m_flow.ValidateLevel(ChosenLevel());
            if (invalid != null)
            {
                m_createError.text = invalid;
                return;
            }

            SetHidden(m_createDialog, true);
            SaveDisplayName();
            Run(CreateRoomAsync());
        }

        /// <summary>Reads the chosen save, if any, then opens the room that will play it.</summary>
        private async Task CreateRoomAsync()
        {
            var catalog = SaveCatalog.Active;
            var save = ChosenSave();
            var level = ChosenLevel();
            Dictionary<string, string> savedCharacters = null;

            // A new room starts with the host's last settings; a resumed one with the save's, locked.
            var settings = GameplaySettingsPanel.Remembered;

            if (catalog != null)
            {
                if (save.HasValue)
                {
                    // Read now, in the lobby: a damaged save should not be found halfway into starting a game.
                    if (!await catalog.ResumeAsync(save.Value.Folder))
                    {
                        m_message = "That save could not be read, so it was not loaded.";
                        RefreshAll();
                        return;
                    }

                    savedCharacters = new Dictionary<string, string>();
                    foreach (var saved in catalog.ResumedCharacters()) savedCharacters[saved.Key] = saved.Value.Encode();

                    // A save from before settings existed was played as the levels were built: Normal.
                    settings = GameplaySettings.Parse(catalog.ResumedGameplaySettings());
                }
                else
                {
                    catalog.StartFresh();
                }
            }

            await m_sessions.CreateRoomAsync(DisplayName, m_createName.value, m_createMaxPlayers.value,
                m_createPassword.value, level, savedCharacters, settings, settingsLocked: save.HasValue);
        }

        /// <param name="room">The listed room needing a password, or <c>null</c> to join by code.</param>
        private void OpenJoinDialog(RoomListing? room)
        {
            m_joinTargetRoomId = room?.Id;
            m_joinTitle.text = room.HasValue ? $"Join {room.Value.Name}" : "Join by Code";
            m_joinCode.value = string.Empty;
            m_joinPassword.value = string.Empty;
            m_joinError.text = string.Empty;

            // A listed room is already identified; only a code join needs the code.
            SetHidden(m_joinCode, room.HasValue);
            m_joinPassword.label = room.HasValue ? "Password" : "Password (if any)";

            SetHidden(m_joinDialog, false);
            if (room.HasValue) m_joinPassword.Focus();
            else m_joinCode.Focus();
        }

        private void ConfirmJoin()
        {
            if (m_sessions == null) return;

            if (m_joinTargetRoomId == null && string.IsNullOrWhiteSpace(m_joinCode.value))
            {
                m_joinError.text = "Enter the room code the host shared.";
                return;
            }

            SetHidden(m_joinDialog, true);
            SaveDisplayName();

            Run(m_joinTargetRoomId != null
                ? m_sessions.JoinRoomAsync(DisplayName, m_joinTargetRoomId, m_joinPassword.value)
                : m_sessions.JoinRoomByCodeAsync(DisplayName, m_joinCode.value, m_joinPassword.value));
        }

        private async Task StartGameAsync()
        {
            if (m_flow == null) return;

            m_message = string.Empty;
            if (!await m_flow.StartGameAsync()) m_message = m_flow.LastStartError;

            RefreshAll();
        }

        /// <summary>Runs a fire-and-forget UI action, logging what would otherwise be an unobserved exception.</summary>
        private async void Run(Task task)
        {
            if (task == null) return;

            try
            {
                await task;
            }
            catch (Exception exception)
            {
                Debug.LogException(exception, this);
                m_message = exception.Message;
                RefreshAll();
            }
        }

        // ---- Helpers ---------------------------------------------------------------------------------

        private static void SetHidden(VisualElement element, bool hidden)
        {
            if (element != null) element.EnableInClassList("mp-hidden", hidden);
        }

        private static string LoadDisplayName()
        {
            var saved = PlayerPrefs.GetString(DisplayNamePrefsKey, string.Empty);
            return string.IsNullOrWhiteSpace(saved)
                ? $"Worker {UnityEngine.Random.Range(1000, 10000)}"
                : SessionService.SanitiseDisplayName(saved);
        }

        private void SaveDisplayName()
        {
            if (m_nameField == null) return;

            PlayerPrefs.SetString(DisplayNamePrefsKey, DisplayName);
            PlayerPrefs.Save();
        }
    }
}
