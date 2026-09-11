using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Unity.Netcode;
using Unity.Services.Authentication;
using Unity.Services.Core;
using Unity.Services.Multiplayer;
using UnityEngine;

namespace Modules.Multiplayer.Scripts.Runtime.Sessions
{
    /// <summary>
    /// Owns this process' membership of one Relay-backed room, and the room browser's queries, exposing
    /// both as observable state for UI.
    /// </summary>
    /// <remarks>
    /// Authority: the Sessions package decides transport configuration and starts the NetworkManager
    /// itself (NetworkManagerSession calls StartHost/StartClient internally). Nothing in this class
    /// may call NetworkManager.StartHost/StartClient — a second start throws away the Relay
    /// allocation the session just negotiated and the join silently fails.
    ///
    /// Lives on the persistent multiplayer root, so the room survives the scene change from the lobby
    /// into the level.
    /// </remarks>
    public class SessionService : MonoBehaviour
    {
        /// <summary>Largest room allowed. GameplayScene has one spawn point per player.</summary>
        public const int MaxRoomSize = 4;

        public const int MinRoomSize = 2;

        /// <summary>Unity Lobby rejects passwords shorter than this.</summary>
        public const int MinPasswordLength = 8;

        public const int MaxPasswordLength = 64;
        public const int MaxRoomNameLength = 40;
        public const int MaxDisplayNameLength = 24;

        // Client-side key the package tracks sessions by. Fixing it means a second create or join while
        // already in a room fails loudly, instead of quietly holding two memberships at once.
        private const string SessionType = "the-quiet-village";

        private const string DisplayNameProperty = "displayName";
        private const int QueryPageSize = 50;

        private ISession m_session;
        private NetworkManager m_watchedNetworkManager;
        private string m_profile;
        private readonly List<RoomMember> m_members = new();

        /// <summary>Raised whenever <see cref="State"/> changes, on the main thread.</summary>
        public event Action<SessionConnectionState> StateChanged;

        /// <summary>Raised when players join, leave or rename, and when the room's own details change.</summary>
        public event Action RosterChanged;

        public SessionConnectionState State { get; private set; } = SessionConnectionState.Disconnected;

        /// <summary>Human-readable reason for the last failure. Kept after recovering, for display.</summary>
        public string LastError { get; private set; } = string.Empty;

        public bool IsBusy => State == SessionConnectionState.SigningIn || State == SessionConnectionState.Connecting;

        public bool IsConnected => State == SessionConnectionState.Connected && m_session != null;

        public bool IsHost => m_session != null && m_session.IsHost;

        public string RoomName => m_session != null ? m_session.Name : string.Empty;

        /// <summary>Code other players can type to join this room. Empty until connected.</summary>
        public string JoinCode => m_session != null ? m_session.Code : string.Empty;

        public int PlayerCount => m_session != null ? m_session.PlayerCount : 0;

        public int MaxPlayers => m_session != null ? m_session.MaxPlayers : 0;

        public bool IsRoomLocked => m_session != null && m_session.IsLocked;

        public bool HasPassword => m_session != null && m_session.HasPassword;

        /// <summary>Everyone in the room, host first. Empty when not connected.</summary>
        public IReadOnlyList<RoomMember> Members => m_members;

        /// <summary>Signs in once per process. Safe to call repeatedly.</summary>
        /// <remarks>
        /// Each process gets its own random authentication profile. Multiplayer Play Mode runs several
        /// players on one machine, and two clients under the same profile share a player ID — the second
        /// is then rejected as already being in the room. Display names are separate and user-chosen.
        /// </remarks>
        public async Task<bool> SignInAsync()
        {
            try
            {
                if (UnityServices.State == ServicesInitializationState.Initialized
                    && AuthenticationService.Instance != null
                    && AuthenticationService.Instance.IsSignedIn)
                {
                    return true;
                }

                var previous = State;
                SetState(SessionConnectionState.SigningIn);

                if (UnityServices.State != ServicesInitializationState.Initialized)
                {
                    m_profile ??= $"p{Guid.NewGuid().ToString("N").Substring(0, 12)}";
                    var options = new InitializationOptions();
                    options.SetProfile(m_profile);
                    await UnityServices.InitializeAsync(options);
                }

                var authentication = AuthenticationService.Instance;
                if (authentication == null)
                {
                    Fail("Could not sign in", new InvalidOperationException(
                        "AuthenticationService is unavailable. Check that the project is linked to a Unity Cloud project."));
                    return false;
                }

                if (!authentication.IsSignedIn) await authentication.SignInAnonymouslyAsync();

                SetState(previous == SessionConnectionState.Error ? SessionConnectionState.Disconnected : previous);
                return true;
            }
            catch (Exception exception)
            {
                Fail("Could not sign in", exception);
                return false;
            }
        }

        /// <summary>Lists joinable and in-progress rooms, newest first.</summary>
        /// <remarks>Does not touch <see cref="State"/>: a failed refresh is not a failed connection.</remarks>
        public async Task<RoomQueryResult> QueryRoomsAsync()
        {
            if (!await SignInAsync()) return RoomQueryResult.Failed(LastError);

            try
            {
                var options = new QuerySessionsOptions { Count = QueryPageSize };
                options.SortOptions.Add(new SortOption(SortOrder.Descending, SortField.CreationTime));

                var results = await MultiplayerService.Instance.QuerySessionsAsync(options);
                var rooms = new List<RoomListing>();

                if (results?.Sessions != null)
                {
                    foreach (var info in results.Sessions)
                    {
                        if (info == null) continue;

                        rooms.Add(new RoomListing(info.Id, info.Name, info.MaxPlayers - info.AvailableSlots,
                            info.MaxPlayers, info.IsLocked, info.HasPassword));
                    }
                }

                return RoomQueryResult.Succeeded(rooms);
            }
            catch (Exception exception)
            {
                var message = Describe("Could not list rooms", exception);
                Debug.LogWarning($"{nameof(SessionService)} — {message}", this);
                return RoomQueryResult.Failed(message);
            }
        }

        /// <summary>
        /// Checks room settings before they reach the service, so the UI can show why instead of a raw error.
        /// </summary>
        /// <returns>An error to show, or <c>null</c> if the settings are acceptable.</returns>
        public static string ValidateRoomSettings(string roomName, string password)
        {
            if (string.IsNullOrWhiteSpace(roomName)) return "Give the room a name.";
            if (roomName.Trim().Length > MaxRoomNameLength) return $"Room names are limited to {MaxRoomNameLength} characters.";

            if (!string.IsNullOrEmpty(password)
                && (password.Length < MinPasswordLength || password.Length > MaxPasswordLength))
            {
                return $"Passwords must be {MinPasswordLength}–{MaxPasswordLength} characters, or left empty.";
            }

            return null;
        }

        /// <summary>Creates a room and becomes its host.</summary>
        /// <param name="password">Empty for an open room.</param>
        public async Task CreateRoomAsync(string displayName, string roomName, int maxPlayers, string password)
        {
            if (IsBusy || IsConnected) return;

            var invalid = ValidateRoomSettings(roomName, password);
            if (invalid != null)
            {
                Fail("Could not create the room", new ArgumentException(invalid));
                return;
            }

            if (!await SignInAsync()) return;

            try
            {
                SetState(SessionConnectionState.Connecting);

                var options = new SessionOptions
                {
                    Type = SessionType,
                    Name = roomName.Trim(),
                    MaxPlayers = Mathf.Clamp(maxPlayers, MinRoomSize, MaxRoomSize),
                    IsPrivate = false,
                    Password = string.IsNullOrEmpty(password) ? null : password,
                    PlayerProperties = PlayerPropertiesFor(displayName)
                }.WithRelayNetwork();

                AttachSession(await MultiplayerService.Instance.CreateSessionAsync(options));
                SetState(SessionConnectionState.Connected);
            }
            catch (Exception exception)
            {
                Fail("Could not create the room", exception);
            }
        }

        /// <summary>Joins a room picked from the browser.</summary>
        public Task JoinRoomAsync(string displayName, string roomId, string password)
        {
            if (string.IsNullOrWhiteSpace(roomId))
            {
                Fail("Could not join the room", new ArgumentException("No room selected."));
                return Task.CompletedTask;
            }

            return JoinAsync(displayName, password,
                options => MultiplayerService.Instance.JoinSessionByIdAsync(roomId, options));
        }

        /// <summary>Joins a room by the code its host shared.</summary>
        public Task JoinRoomByCodeAsync(string displayName, string joinCode, string password)
        {
            if (string.IsNullOrWhiteSpace(joinCode))
            {
                Fail("Could not join the room", new ArgumentException("Enter a room code."));
                return Task.CompletedTask;
            }

            // Codes are issued in upper case; players type them however they like.
            var code = joinCode.Trim().ToUpperInvariant();
            return JoinAsync(displayName, password,
                options => MultiplayerService.Instance.JoinSessionByCodeAsync(code, options));
        }

        private async Task JoinAsync(string displayName, string password, Func<JoinSessionOptions, Task<ISession>> join)
        {
            if (IsBusy || IsConnected) return;
            if (!await SignInAsync()) return;

            try
            {
                SetState(SessionConnectionState.Connecting);

                var options = new JoinSessionOptions
                {
                    Type = SessionType,
                    Password = string.IsNullOrEmpty(password) ? null : password,
                    PlayerProperties = PlayerPropertiesFor(displayName)
                };

                AttachSession(await join(options));
                SetState(SessionConnectionState.Connected);
            }
            catch (Exception exception)
            {
                Fail("Could not join the room", exception);
            }
        }

        /// <summary>Leaves the current room; the package shuts the NetworkManager down for us.</summary>
        public async Task LeaveSessionAsync()
        {
            if (m_session == null) return;

            var leaving = m_session;
            DetachSession();

            try
            {
                await leaving.LeaveAsync();
            }
            catch (Exception exception)
            {
                // We are out either way; the service times stale members out on its own.
                Debug.LogWarning($"{nameof(SessionService)} — leaving the room reported: {exception.Message}", this);
            }

            SetState(SessionConnectionState.Disconnected);
        }

        /// <summary>
        /// Locks or unlocks the room. Host only. A locked room is listed as in-game and cannot be joined.
        /// </summary>
        /// <returns><c>true</c> if the service accepted the change.</returns>
        public async Task<bool> SetRoomLockedAsync(bool locked)
        {
            if (m_session == null || !m_session.IsHost) return false;

            try
            {
                var host = m_session.AsHost();
                host.IsLocked = locked;
                await host.SavePropertiesAsync();
                return true;
            }
            catch (Exception exception)
            {
                // Not a connection failure: the room is still up, so report without leaving it.
                LastError = Describe(locked ? "Could not lock the room" : "Could not unlock the room", exception);
                Debug.LogError($"{nameof(SessionService)} — {LastError}", this);
                return false;
            }
        }

        private static Dictionary<string, PlayerProperty> PlayerPropertiesFor(string displayName)
        {
            // Member visibility: only people in the same room need to see the name.
            return new Dictionary<string, PlayerProperty>
            {
                [DisplayNameProperty] = new(SanitiseDisplayName(displayName), VisibilityPropertyOptions.Member)
            };
        }

        /// <summary>Trims and caps a display name, falling back to "Player" when nothing usable is left.</summary>
        public static string SanitiseDisplayName(string displayName)
        {
            var name = string.IsNullOrWhiteSpace(displayName) ? "Player" : displayName.Trim();
            return name.Length > MaxDisplayNameLength ? name.Substring(0, MaxDisplayNameLength) : name;
        }

        private void AttachSession(ISession session)
        {
            m_session = session;
            if (m_session == null) return;

            // The host can delete the room or kick us; either way our NetworkManager is already going down,
            // so the UI must not keep claiming we are connected.
            m_session.RemovedFromSession += HandleRemovedFromSession;
            m_session.Deleted += HandleSessionDeleted;
            m_session.Changed += RefreshRoster;
            m_session.PlayerJoined += HandlePlayerChanged;
            m_session.PlayerHasLeft += HandlePlayerChanged;
            m_session.PlayerPropertiesChanged += RefreshRoster;

            // A host that vanishes without deleting the room (crash, network loss) never raises Deleted.
            // Netcode notices first, as the client stopping.
            m_watchedNetworkManager = NetworkManager.Singleton;
            if (m_watchedNetworkManager != null) m_watchedNetworkManager.OnClientStopped += HandleClientStopped;

            RefreshRoster();
        }

        private void DetachSession()
        {
            if (m_watchedNetworkManager != null)
            {
                m_watchedNetworkManager.OnClientStopped -= HandleClientStopped;
                m_watchedNetworkManager = null;
            }

            if (m_session != null)
            {
                m_session.RemovedFromSession -= HandleRemovedFromSession;
                m_session.Deleted -= HandleSessionDeleted;
                m_session.Changed -= RefreshRoster;
                m_session.PlayerJoined -= HandlePlayerChanged;
                m_session.PlayerHasLeft -= HandlePlayerChanged;
                m_session.PlayerPropertiesChanged -= RefreshRoster;
                m_session = null;
            }

            RefreshRoster();
        }

        private void HandlePlayerChanged(string playerId) => RefreshRoster();

        private void RefreshRoster()
        {
            m_members.Clear();

            if (m_session?.Players != null)
            {
                var localId = m_session.CurrentPlayer != null ? m_session.CurrentPlayer.Id : string.Empty;

                foreach (var player in m_session.Players)
                {
                    if (player == null) continue;

                    var name = player.Properties != null
                               && player.Properties.TryGetValue(DisplayNameProperty, out var property)
                               && property != null
                        ? property.Value
                        : null;

                    var isHost = player.Id == m_session.Host;
                    var member = new RoomMember(player.Id, SanitiseDisplayName(name), isHost, player.Id == localId);

                    if (isHost) m_members.Insert(0, member);
                    else m_members.Add(member);
                }
            }

            RosterChanged?.Invoke();
        }

        private void HandleRemovedFromSession() => EndUnexpectedly("You were removed from the room.");

        private void HandleSessionDeleted() => EndUnexpectedly("The host closed the room.");

        private void HandleClientStopped(bool wasHost)
        {
            if (m_session == null) return;

            var session = m_session;
            EndUnexpectedly(wasHost ? "The room shut down." : "Lost the connection to the host.");

            // Our membership may still be registered with the service; clear it so the room's player count
            // is right for everyone else. Failure is fine — stale members time out.
            _ = LeaveQuietlyAsync(session);
        }

        private static async Task LeaveQuietlyAsync(ISession session)
        {
            try
            {
                await session.LeaveAsync();
            }
            catch (Exception)
            {
                // Expected when the room is already gone.
            }
        }

        private void EndUnexpectedly(string reason)
        {
            DetachSession();
            LastError = reason;
            SetState(SessionConnectionState.Error);
        }

        private void Fail(string context, Exception exception)
        {
            LastError = Describe(context, exception);
            Debug.LogError($"{nameof(SessionService)} — {LastError}\n{exception}", this);
            SetState(SessionConnectionState.Error);
        }

        /// <summary>Turns service failures into sentences a player can act on.</summary>
        private static string Describe(string context, Exception exception)
        {
            // Lobby reports a wrong password only as a reason name deep in the exception chain. Matching it
            // chooses the wording and nothing else — control flow never depends on this text.
            if (exception.ToString().IndexOf("IncorrectPassword", StringComparison.OrdinalIgnoreCase) >= 0)
                return $"{context}: wrong password.";

            if (exception is ArgumentException) return $"{context}: {exception.Message}";

            if (exception is SessionException sessionException)
            {
                switch (sessionException.Error)
                {
                    case SessionError.SessionNotFound:
                    case SessionError.SessionDeleted:
                        return $"{context}: that room no longer exists.";
                    case SessionError.RateLimitExceeded:
                        return $"{context}: too many requests — wait a moment and try again.";
                    case SessionError.Forbidden:
                    case SessionError.NotAuthorized:
                        return $"{context}: the room is full, already playing, or needs a password.";
                    default:
                        // The error code is far more actionable than the message alone (e.g. which Unity
                        // Cloud service is not enabled for the project).
                        return $"{context}: {sessionException.Error} — {sessionException.Message}";
                }
            }

            return $"{context}: {exception.Message}";
        }

        private void SetState(SessionConnectionState state)
        {
            if (State == state) return;

            State = state;
            StateChanged?.Invoke(state);
        }

        private void OnDestroy()
        {
            DetachSession();
        }
    }
}
