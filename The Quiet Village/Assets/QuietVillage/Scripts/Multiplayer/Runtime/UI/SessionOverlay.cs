using System;
using QuietVillage.Multiplayer.Flow;
using QuietVillage.Multiplayer.Sessions;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UIElements;

namespace QuietVillage.Multiplayer.UI
{
    /// <summary>
    /// In-game room panel: who is here, the room code, and the way back to the lobby.
    /// </summary>
    /// <remarks>
    /// Lives on the persistent multiplayer root, so it exists in every level without each scene having to
    /// carry it, and shows itself only while a room is actually being played.
    ///
    /// Opening it frees the cursor so the buttons can be clicked. UHFPS's look controller only reads the
    /// mouse while the cursor is locked, so the camera stops turning by itself while the panel is up — no
    /// UHFPS code is involved. Closing it restores whatever cursor state the game had before.
    /// </remarks>
    [RequireComponent(typeof(UIDocument))]
    public class SessionOverlay : MonoBehaviour
    {
        [SerializeField] private UIDocument m_document;
        [SerializeField] private SessionService m_sessions;
        [SerializeField] private SessionFlow m_flow;

        [Tooltip("Toggles the panel in game. Tab and the other obvious keys are already bound by UHFPS.")]
        [SerializeField] private Key m_toggleKey = Key.F1;

        private VisualElement m_root;
        private Label m_hint;
        private VisualElement m_panel;
        private Label m_room;
        private Label m_code;
        private VisualElement m_players;

        private bool m_uiReady;
        private bool m_visible;
        private bool m_panelOpen;
        private CursorLockMode m_cursorLockBeforeOpen;
        private bool m_cursorVisibleBeforeOpen;

        private void Awake()
        {
            if (m_sessions == null) m_sessions = GetComponent<SessionService>();
            if (m_flow == null) m_flow = GetComponent<SessionFlow>();
        }

        private void OnEnable()
        {
            if (m_document == null) m_document = GetComponent<UIDocument>();

            var root = m_document != null ? m_document.rootVisualElement : null;

            // This panel exists in the lobby too (it rides on the persistent root) and draws above it. Its
            // full-screen container would swallow the lobby's clicks even with its contents hidden.
            if (root != null) root.pickingMode = PickingMode.Ignore;

            m_root = root?.Q("overlay-root");
            m_hint = root?.Q<Label>("overlay-hint");
            m_panel = root?.Q("overlay-panel");
            m_room = root?.Q<Label>("overlay-room");
            m_code = root?.Q<Label>("overlay-code");
            m_players = root?.Q("overlay-players");
            var close = root?.Q<Button>("overlay-close");
            var leave = root?.Q<Button>("overlay-leave");

            if (m_root == null || m_hint == null || m_panel == null || m_room == null || m_code == null
                || m_players == null || close == null || leave == null)
            {
                Debug.LogError($"{nameof(SessionOverlay)}: SessionOverlay.uxml is missing or incomplete.", this);
                return;
            }

            close.clicked += () => SetPanelOpen(false);
            leave.clicked += LeaveToLobby;

            if (m_sessions != null) m_sessions.RosterChanged += Refresh;

            m_uiReady = true;
            m_visible = false;
            m_panelOpen = false;
            Refresh();
        }

        private void OnDisable()
        {
            if (m_sessions != null) m_sessions.RosterChanged -= Refresh;
            if (m_panelOpen) SetPanelOpen(false);

            m_uiReady = false;
        }

        private void Update()
        {
            if (!m_uiReady) return;

            var shouldShow = m_flow != null && m_flow.IsInGameplay && m_sessions != null && m_sessions.IsConnected;
            if (shouldShow != m_visible)
            {
                m_visible = shouldShow;
                m_root.EnableInClassList("mp-hidden", !shouldShow);

                // Leaving the level with the panel open must not strand the cursor in its freed state.
                if (!shouldShow && m_panelOpen) SetPanelOpen(false);
                Refresh();
            }

            if (!m_visible) return;

            var keyboard = Keyboard.current;
            if (keyboard != null && keyboard[m_toggleKey].wasPressedThisFrame) SetPanelOpen(!m_panelOpen);
        }

        private void SetPanelOpen(bool open)
        {
            if (m_panelOpen == open) return;

            m_panelOpen = open;
            m_panel.EnableInClassList("mp-hidden", !open);

            if (open)
            {
                m_cursorLockBeforeOpen = UnityEngine.Cursor.lockState;
                m_cursorVisibleBeforeOpen = UnityEngine.Cursor.visible;
                UnityEngine.Cursor.lockState = CursorLockMode.None;
                UnityEngine.Cursor.visible = true;
                Refresh();
            }
            else
            {
                UnityEngine.Cursor.lockState = m_cursorLockBeforeOpen;
                UnityEngine.Cursor.visible = m_cursorVisibleBeforeOpen;
            }
        }

        private void Refresh()
        {
            if (!m_uiReady || m_sessions == null) return;

            m_hint.text = $"{m_toggleKey}  ·  {m_sessions.RoomName}  ·  {m_sessions.PlayerCount}/{m_sessions.MaxPlayers}";
            m_room.text = m_sessions.RoomName;
            m_code.text = string.IsNullOrEmpty(m_sessions.JoinCode) ? string.Empty : $"Room code  {m_sessions.JoinCode}";

            m_players.Clear();
            foreach (var member in m_sessions.Members)
            {
                var row = new VisualElement();
                row.AddToClassList("mp-player-row");
                row.Add(new Label(member.DisplayName));

                if (member.IsHost) row.Add(Badge("Host", "mp-badge--host"));
                if (member.IsLocal) row.Add(Badge("You", "mp-badge--you"));

                m_players.Add(row);
            }
        }

        private static Label Badge(string text, string modifier)
        {
            var badge = new Label(text);
            badge.AddToClassList("mp-badge");
            badge.AddToClassList(modifier);
            return badge;
        }

        private async void LeaveToLobby()
        {
            if (m_sessions == null) return;

            // The lobby frees the cursor itself; restoring the game's locked cursor here would only flash.
            m_panelOpen = false;
            m_panel.EnableInClassList("mp-hidden", true);

            try
            {
                // SessionFlow sees the room end and brings this client back to the lobby.
                await m_sessions.LeaveSessionAsync();
            }
            catch (Exception exception)
            {
                Debug.LogException(exception, this);
            }
        }
    }
}
