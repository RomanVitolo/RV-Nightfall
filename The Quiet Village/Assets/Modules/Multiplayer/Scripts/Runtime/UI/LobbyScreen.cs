using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Modules.Multiplayer.Scripts.Runtime.Flow;
using Modules.Multiplayer.Scripts.Runtime.Sessions;
using UnityEngine;
using UnityEngine.UIElements;

namespace Modules.Multiplayer.Scripts.Runtime.UI
{
    /// <summary>
    /// The lobby: browse rooms, create one, join one, and wait in it until the host starts the game.
    /// </summary>
    /// <remarks>
    /// A view over <see cref="SessionService"/> and <see cref="SessionFlow"/> that keeps no session state of
    /// its own. Everything shown is re-read from them on each change, so arriving back in the lobby mid-room,
    /// or the room ending under the screen, always displays the truth rather than a stale copy.
    ///
    /// References arrive through <see cref="Bind"/> from <see cref="MultiplayerBootstrap"/>: the services
    /// live on a persistent root that a scene object cannot reference in the Inspector.
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
        private bool m_subscribed;

        private readonly List<RoomListing> m_rooms = new();
        private readonly List<RoomMember> m_members = new();

        private TextField m_nameField;
        private VisualElement m_browserView;
        private VisualElement m_roomView;
        private Label m_statusBar;

        private Label m_roomCount;
        private ListView m_roomList;
        private Label m_roomListEmpty;
        private Button m_refreshButton;
        private Button m_createButton;
        private Button m_joinCodeButton;

        private Label m_roomName;
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
        private Button m_createCancel;
        private Button m_createConfirm;

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

        // Errors that are not the session's own state: a failed refresh, a failed start.
        private string m_message = string.Empty;

        // The listed room the join dialog is asking a password for; null means "join by code".
        private string m_joinTargetRoomId;

        /// <summary>Connects the screen to the persistent services. Safe to call before or after OnEnable.</summary>
        public void Bind(SessionService sessions, SessionFlow flow)
        {
            Unsubscribe();

            m_sessions = sessions;
            m_flow = flow;

            if (!m_uiReady) return;

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
            m_uiReady = false;
            Unsubscribe();
            SaveDisplayName();
        }

        private void Update()
        {
            if (!m_uiReady || m_sessions == null) return;

            if (IsBrowsing && !m_querying && Time.unscaledTime >= m_nextAutoRefreshAt) RequestRoomRefresh(force: false);
        }

        private bool IsBrowsing => m_sessions != null && !m_sessions.IsConnected && !m_sessions.IsBusy;

        private bool IsBusy => (m_sessions != null && m_sessions.IsBusy) || (m_flow != null && m_flow.IsStarting);

        private string DisplayName => SessionService.SanitiseDisplayName(m_nameField != null ? m_nameField.value : null);

        // ---- Setup -----------------------------------------------------------------------------------

        private bool QueryElements(VisualElement root)
        {
            m_nameField = root.Q<TextField>("player-name");
            m_browserView = root.Q("browser-view");
            m_roomView = root.Q("room-view");
            m_statusBar = root.Q<Label>("status-bar");

            m_roomCount = root.Q<Label>("room-count");
            m_roomList = root.Q<ListView>("room-list");
            m_roomListEmpty = root.Q<Label>("room-list-empty");
            m_refreshButton = root.Q<Button>("refresh-button");
            m_createButton = root.Q<Button>("create-button");
            m_joinCodeButton = root.Q<Button>("join-code-button");

            m_roomName = root.Q<Label>("room-name");
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
            m_subscribed = false;
        }

        // ---- Rendering -------------------------------------------------------------------------------

        private void RefreshAll()
        {
            if (!m_uiReady || m_sessions == null) return;

            var inRoom = m_sessions.IsConnected;
            SetHidden(m_browserView, inRoom);
            SetHidden(m_roomView, !inRoom);

            // The name travels with the join, so changing it mid-room would not reach anyone.
            m_nameField.SetEnabled(!inRoom && !IsBusy);

            RefreshBusy();
            RefreshBrowser();
            RefreshRoom();
            RefreshStatus();
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
            SetHidden(row.Q<Label>("host"), !member.IsHost);
            SetHidden(row.Q<Label>("you"), !member.IsLocal);
        }

        // ---- Actions ---------------------------------------------------------------------------------

        private void HandleSessionStateChanged(SessionConnectionState state)
        {
            // A new attempt or its outcome replaces whatever the last one said.
            if (state == SessionConnectionState.Connecting || state == SessionConnectionState.Connected) m_message = string.Empty;

            RefreshAll();

            // Just left a room: show the list as it is now, not as it was before joining.
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
            m_createError.text = string.Empty;

            SetHidden(m_createDialog, false);
            m_createName.Focus();
        }

        private void ConfirmCreate()
        {
            if (m_sessions == null) return;

            var invalid = SessionService.ValidateRoomSettings(m_createName.value, m_createPassword.value);
            if (invalid != null)
            {
                m_createError.text = invalid;
                return;
            }

            SetHidden(m_createDialog, true);
            SaveDisplayName();
            Run(m_sessions.CreateRoomAsync(DisplayName, m_createName.value, m_createMaxPlayers.value, m_createPassword.value));
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
