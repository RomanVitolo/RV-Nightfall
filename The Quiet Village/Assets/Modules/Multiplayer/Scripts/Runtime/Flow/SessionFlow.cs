using System;
using System.Threading.Tasks;
using Modules.Multiplayer.Scripts.Runtime.Levels;
using Modules.Multiplayer.Scripts.Runtime.Sessions;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Modules.Multiplayer.Scripts.Runtime.Flow
{
    /// <summary>
    /// Moves the room between the lobby and its level: starts the game for everyone, and brings players
    /// back when the room ends under them.
    /// </summary>
    /// <remarks>
    /// Authority: host. Only the host starts the game, and it does so through Netcode's scene manager, so
    /// every connected client loads the level in the same scene event instead of each deciding on its own.
    /// That shared start is the point of the waiting room: a fresh level loaded by everyone at once is the only
    /// moment all worlds are known to match. The room is locked first for the same reason — nobody may arrive
    /// after that moment.
    ///
    /// Which level is the room's own <see cref="SessionService.RoomLevel"/>, chosen when it was created, so the
    /// players waiting see the same answer the host loads. Anything in <see cref="LevelCatalog"/> counts as a
    /// level; nothing else does.
    ///
    /// Lives on the persistent multiplayer root next to <see cref="SessionService"/>, because it has to
    /// outlive the lobby scene it starts from.
    /// </remarks>
    public class SessionFlow : MonoBehaviour
    {
        [SerializeField] private SessionService m_sessions;

        [Tooltip("Scene holding the lobby screen. Must be in Build Settings.")]
        [SerializeField] private string m_lobbySceneName = "LobbyScene";

        [Tooltip("Every level a room can play. Each must be in Build Settings: Netcode loads scenes by build entry.")]
        [SerializeField] private LevelCatalog m_levels;

        /// <summary>Raised when <see cref="IsStarting"/> changes.</summary>
        public event Action StartingChanged;

        /// <summary>True from the host pressing Start until the level load is under way or has failed.</summary>
        public bool IsStarting { get; private set; }

        /// <summary>Why the last start failed; empty if it did not.</summary>
        public string LastStartError { get; private set; } = string.Empty;

        /// <summary>The levels a room can be played in, or <c>null</c> if the root was not set up with them.</summary>
        public LevelCatalog Levels => m_levels;

        /// <summary>True while any level is the active scene.</summary>
        public bool IsInGameplay => IsLevel(SceneManager.GetActiveScene().name);

        public bool IsInLobby => SceneManager.GetActiveScene().name == m_lobbySceneName;

        public bool CanStartGame => m_sessions != null && m_sessions.IsConnected && m_sessions.IsHost
                                    && IsInLobby && !IsStarting;

        /// <summary>The scene this room will load: the level it was created with, or the catalog's first.</summary>
        /// <remarks>
        /// The fallback covers a room made before levels existed, or by a build that did not send one. A level the
        /// room names but the catalog no longer lists is returned as-is, so starting reports it instead of quietly
        /// taking everyone somewhere else.
        /// </remarks>
        public string RoomLevel
        {
            get
            {
                var chosen = m_sessions != null ? m_sessions.RoomLevel : string.Empty;
                if (!string.IsNullOrEmpty(chosen)) return chosen;

                var fallback = m_levels != null ? m_levels.Default : null;
                return fallback != null ? fallback.SceneName : string.Empty;
            }
        }

        /// <summary>True if the scene is one of the catalog's levels.</summary>
        public bool IsLevel(string sceneName) => m_levels != null && m_levels.Contains(sceneName);

        private void Awake()
        {
            if (m_sessions == null) m_sessions = GetComponent<SessionService>();
            if (m_sessions == null)
            {
                Debug.LogError($"{nameof(SessionFlow)} has no {nameof(SessionService)}; the lobby cannot start games.", this);
                enabled = false;
                return;
            }

            if (m_levels == null || m_levels.Default == null)
                Debug.LogError($"{nameof(SessionFlow)}: no {nameof(LevelCatalog)} with a level assigned, so no game can " +
                               "start. Run Tools > Multiplayer > Set Up Lobby.", this);
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

        /// <summary>Host only: locks the room and loads its level on every connected client.</summary>
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

            // Checked before the room locks, so a bad level leaves it open for another try.
            var level = RoomLevel;
            var invalid = ValidateLevel(level);
            if (invalid != null)
            {
                LastStartError = invalid;
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

                var status = networkManager.SceneManager.LoadScene(level, LoadSceneMode.Single);
                if (status == SceneEventProgressStatus.Started) return true;

                // Reopen the room so players can still join the game that did not start.
                await m_sessions.SetRoomLockedAsync(false);
                LastStartError = $"Could not load {DisplayNameOf(level)} ({status}).";
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
        /// Host only: reloads the level being played for every player in the room, e.g. once everyone has died.
        /// </summary>
        /// <remarks>
        /// The active scene rather than <see cref="RoomLevel"/>: restarting means this level again, whatever the room
        /// was first created with. Through Netcode's scene manager, like the first start, so every client reloads in
        /// the same scene event. Players are spawned with the scene and destroyed with it, and each comes back fresh
        /// when its client finishes loading (NetworkPlayerSpawner).
        /// </remarks>
        /// <returns><c>true</c> if the reload started.</returns>
        public bool RestartLevel()
        {
            var networkManager = NetworkManager.Singleton;
            if (!IsInGameplay || networkManager == null || !networkManager.IsServer || networkManager.SceneManager == null)
                return false;

            var level = SceneManager.GetActiveScene().name;
            var status = networkManager.SceneManager.LoadScene(level, LoadSceneMode.Single);
            if (status == SceneEventProgressStatus.Started) return true;

            Debug.LogError($"{nameof(SessionFlow)}: could not restart {level} ({status}).", this);
            return false;
        }

        /// <summary>Why a level cannot be started, or <c>null</c> if it can.</summary>
        public string ValidateLevel(string sceneName)
        {
            if (m_levels == null) return "No levels are set up. Run Tools > Multiplayer > Set Up Lobby.";
            if (string.IsNullOrEmpty(sceneName)) return "This room has no level to play.";

            if (!m_levels.Contains(sceneName))
                return $"'{sceneName}' is not in the level list, so it cannot be played. Was it removed?";

            if (!Application.CanStreamedLevelBeLoaded(sceneName))
                return $"{DisplayNameOf(sceneName)} is not in Build Settings. Run Tools > Multiplayer > Levels > " +
                       "Set Up Open Scene As Level on it.";

            return null;
        }

        /// <summary>The lobby name of a level scene, or the scene name if the catalog does not know it.</summary>
        public string DisplayNameOf(string sceneName) =>
            m_levels != null ? m_levels.DisplayNameOf(sceneName) : sceneName;

        private void SetStarting(bool starting)
        {
            if (IsStarting == starting) return;

            IsStarting = starting;
            StartingChanged?.Invoke();
        }

        private void HandleSessionStateChanged(SessionConnectionState state)
        {
            if (state != SessionConnectionState.Disconnected && state != SessionConnectionState.Error) return;

            // Only pull players out of a level. Anywhere else they chose to be, including UHFPS's own
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
            if (next.name == m_lobbySceneName || IsLevel(next.name)) return;

            _ = m_sessions.LeaveSessionAsync();
        }
    }
}
