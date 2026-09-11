using System;
using System.Threading.Tasks;
using Modules.Multiplayer.Scripts.Runtime.Sessions;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Modules.Multiplayer.Scripts.Runtime.Flow
{
    /// <summary>
    /// Moves the room between the lobby and the level: starts the game for everyone, and brings players
    /// back when the room ends under them.
    /// </summary>
    /// <remarks>
    /// Authority: host. Only the host starts the game, and it does so through Netcode's scene manager, so
    /// every connected client loads the level in the same scene event instead of each deciding on its own.
    /// That shared start is the point of the waiting room: doors, pickups and puzzles are not replicated
    /// yet, so a fresh level loaded by everyone at once is the only moment all worlds are known to match.
    /// The room is locked first for the same reason — nobody may arrive after that moment.
    ///
    /// Lives on the persistent multiplayer root next to <see cref="SessionService"/>, because it has to
    /// outlive the lobby scene it starts from.
    /// </remarks>
    public class SessionFlow : MonoBehaviour
    {
        [SerializeField] private SessionService m_sessions;

        [Tooltip("Scene holding the lobby screen. Must be in Build Settings.")]
        [SerializeField] private string m_lobbySceneName = "LobbyScene";

        [Tooltip("Level the host starts. Must be in Build Settings: Netcode loads scenes by build entry.")]
        [SerializeField] private string m_gameplaySceneName = "GameplayScene";

        /// <summary>Raised when <see cref="IsStarting"/> changes.</summary>
        public event Action StartingChanged;

        /// <summary>True from the host pressing Start until the level load is under way or has failed.</summary>
        public bool IsStarting { get; private set; }

        /// <summary>Why the last start failed; empty if it did not.</summary>
        public string LastStartError { get; private set; } = string.Empty;

        public bool IsInGameplay => SceneManager.GetActiveScene().name == m_gameplaySceneName;

        public bool IsInLobby => SceneManager.GetActiveScene().name == m_lobbySceneName;

        public bool CanStartGame => m_sessions != null && m_sessions.IsConnected && m_sessions.IsHost
                                    && IsInLobby && !IsStarting;

        private void Awake()
        {
            if (m_sessions == null) m_sessions = GetComponent<SessionService>();
            if (m_sessions == null)
            {
                Debug.LogError($"{nameof(SessionFlow)} has no {nameof(SessionService)}; the lobby cannot start games.", this);
                enabled = false;
            }
        }

        private void OnEnable()
        {
            if (m_sessions != null) m_sessions.StateChanged += HandleSessionStateChanged;
            SceneManager.activeSceneChanged += HandleActiveSceneChanged;
        }

        private void OnDisable()
        {
            if (m_sessions != null) m_sessions.StateChanged -= HandleSessionStateChanged;
            SceneManager.activeSceneChanged -= HandleActiveSceneChanged;
        }

        /// <summary>Host only: locks the room and loads the level on every connected client.</summary>
        /// <returns><c>true</c> if the level load started.</returns>
        public async Task<bool> StartGameAsync()
        {
            if (!CanStartGame) return false;

            var networkManager = NetworkManager.Singleton;
            if (networkManager == null || !networkManager.IsServer || networkManager.SceneManager == null)
            {
                LastStartError = "The network session is not running as host.";
                return false;
            }

            SetStarting(true);
            LastStartError = string.Empty;

            try
            {
                if (!await m_sessions.SetRoomLockedAsync(true))
                {
                    LastStartError = m_sessions.LastError;
                    return false;
                }

                var status = networkManager.SceneManager.LoadScene(m_gameplaySceneName, LoadSceneMode.Single);
                if (status == SceneEventProgressStatus.Started) return true;

                // Reopen the room so players can still join the game that did not start.
                await m_sessions.SetRoomLockedAsync(false);
                LastStartError = $"Could not load {m_gameplaySceneName} ({status}). Is it in Build Settings?";
                return false;
            }
            finally
            {
                SetStarting(false);
            }
        }

        /// <summary>
        /// Leaves the room from inside the level and returns to the lobby. On the host this ends the game for
        /// everyone; the others are brought back by their own session ending.
        /// </summary>
        public async Task LeaveGameAsync()
        {
            if (m_sessions != null && m_sessions.IsConnected)
            {
                // The session reports Disconnected once it is left, and HandleSessionStateChanged takes it from there.
                await m_sessions.LeaveSessionAsync();
                return;
            }

            // No room to leave, e.g. a session that ended a moment ago, or a NetworkManager started without one.
            if (IsInGameplay) ReturnToLobby();
        }

        /// <summary>
        /// Host only: reloads the level for every player in the room, e.g. once everyone has died.
        /// </summary>
        /// <remarks>
        /// Through Netcode's scene manager, like the first start, so every client reloads in the same scene event.
        /// Players are spawned with the scene and destroyed with it, and each comes back fresh when its client
        /// finishes loading (NetworkPlayerSpawner).
        /// </remarks>
        /// <returns><c>true</c> if the reload started.</returns>
        public bool RestartLevel()
        {
            var networkManager = NetworkManager.Singleton;
            if (!IsInGameplay || networkManager == null || !networkManager.IsServer || networkManager.SceneManager == null)
                return false;

            var status = networkManager.SceneManager.LoadScene(m_gameplaySceneName, LoadSceneMode.Single);
            if (status == SceneEventProgressStatus.Started) return true;

            Debug.LogError($"{nameof(SessionFlow)}: could not restart {m_gameplaySceneName} ({status}).", this);
            return false;
        }

        private void SetStarting(bool starting)
        {
            if (IsStarting == starting) return;

            IsStarting = starting;
            StartingChanged?.Invoke();
        }

        private void HandleSessionStateChanged(SessionConnectionState state)
        {
            if (state != SessionConnectionState.Disconnected && state != SessionConnectionState.Error) return;

            // Only pull players out of the level. Anywhere else they chose to be, including UHFPS's own
            // main menu, and this must not override that.
            if (IsInGameplay) ReturnToLobby();
        }

        private void ReturnToLobby()
        {
            // A non-networked scene load while Netcode still runs scene management would fight it. The
            // package normally shuts it down on leave; this covers the paths where it has not yet.
            var networkManager = NetworkManager.Singleton;
            if (networkManager != null && networkManager.IsListening) networkManager.Shutdown();

            SceneManager.LoadScene(m_lobbySceneName);
        }

        /// <summary>
        /// Ends the room when something outside the multiplayer flow takes the player elsewhere.
        /// </summary>
        /// <remarks>
        /// Typically UHFPS's pause menu loading its main menu. A room cannot follow the player there, and
        /// leaving it open would keep the others waiting on someone who has already gone.
        /// </remarks>
        private void HandleActiveSceneChanged(Scene previous, Scene next)
        {
            if (m_sessions == null || !m_sessions.IsConnected) return;
            if (next.name == m_lobbySceneName || next.name == m_gameplaySceneName) return;

            _ = m_sessions.LeaveSessionAsync();
        }
    }
}
