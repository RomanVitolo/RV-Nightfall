using System;
using Modules.Multiplayer.Scripts.Runtime.Sessions;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Modules.Multiplayer.Scripts.Runtime.UI
{
    /// <summary>
    /// Throwaway IMGUI panel for driving <see cref="SessionService"/> while the networked player is
    /// being brought up. Replace with a UI Toolkit screen once movement sync is proven.
    /// </summary>
    public class SessionDebugUI : MonoBehaviour
    {
        [SerializeField] private SessionService m_sessionService;

        private string m_profileName;
        private string m_joinCode = string.Empty;

        private void Awake()
        {
            if (m_sessionService == null) m_sessionService = GetComponent<SessionService>();
            if (m_sessionService == null)
            {
                Debug.LogError($"{nameof(SessionDebugUI)} has no {nameof(SessionService)} assigned.", this);
                enabled = false;
                return;
            }

            // Each Play Mode instance needs its own anonymous identity, and nothing about the process
            // itself distinguishes them — so seed a random one the tester can overwrite.
            m_profileName = $"player-{Guid.NewGuid().ToString("N").Substring(0, 6)}";
        }

        private void Update()
        {
            // StarterAssetsInputs locks the cursor as soon as a player spawns, which would leave these
            // buttons unclickable for the rest of the session.
            var keyboard = Keyboard.current;
            if (keyboard == null || !keyboard.escapeKey.wasPressedThisFrame) return;

            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }

        private void OnGUI()
        {
            if (m_sessionService == null) return;

            GUILayout.BeginArea(new Rect(12f, 12f, 320f, 260f), GUI.skin.box);

            GUILayout.Label($"Session: {m_sessionService.State}");

            if (m_sessionService.IsConnected) DrawConnected();
            else DrawDisconnected();

            if (m_sessionService.State == SessionConnectionState.Error)
            {
                GUILayout.Label(m_sessionService.LastError);
            }

            GUILayout.EndArea();
        }

        private void DrawConnected()
        {
            GUILayout.Label($"Join code: {m_sessionService.JoinCode}");

            if (GUILayout.Button("Leave")) _ = m_sessionService.LeaveSessionAsync();
        }

        private void DrawDisconnected()
        {
            var wasEnabled = GUI.enabled;
            GUI.enabled = wasEnabled && !m_sessionService.IsBusy;

            GUILayout.Label("Profile");
            m_profileName = GUILayout.TextField(m_profileName);

            if (GUILayout.Button("Create Session")) _ = m_sessionService.CreateSessionAsync(m_profileName);

            GUILayout.Space(8f);

            GUILayout.Label("Join code");
            m_joinCode = GUILayout.TextField(m_joinCode);

            if (GUILayout.Button("Join")) _ = m_sessionService.JoinSessionAsync(m_profileName, m_joinCode);

            GUI.enabled = wasEnabled;
        }
    }
}
