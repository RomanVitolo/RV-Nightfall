using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Unity.Netcode;
using Unity.Services.Authentication;
using Unity.Services.Core;
using Unity.Services.Multiplayer;
using UnityEngine;

namespace QuietVillage.Multiplayer.Sessions
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
        private const string CharacterProperty = "character";
        private const string LevelProperty = "level";
        private const string SavedCharactersProperty = "savedCharacters";
        private const string SettingsProperty = "settings";
        private const string SettingsLockedProperty = "settingsLocked";
        private const int QueryPageSize = 50;

        // Kept well under the service's limit on one property's value. A save with more players than fit simply leaves
        // the rest unannounced; the host still spawns them as saved.
        private const int MaxSavedCharactersLength = 1000;
        private const char SavedCharacterSeparator = ';';
        private const char SavedCharacterAssignment = '=';

        private ISession m_session;
        private NetworkManager m_watchedNetworkManager;
        private string m_profile;
        private readonly List<RoomMember> m_members = new();

        // The encoded character choice this player shows the room; published with every join and on each change.
        private string m_localCharacter = string.Empty;
        private bool m_publishingCharacter;
        private bool m_characterDirty;

        /// <summary>Raised whenever <see cref="State"/> changes, on the main thread.</summary>
        public event Action<SessionConnectionState> StateChanged;

        /// <summary>Raised when players join, leave or rename, and when the room's own details change.</summary>
        public event Action RosterChanged;

        public SessionConnectionState State { get; private set; } = SessionConnectionState.Disconnected;

        /// <summary>Human-readable reason for the last failure. Kept after recovering, for display.</summary>
        public string LastError { get; private set; } = string.Empty;

        /// <summary>
        /// Why the last room ended under this player, for the lobby to explain when it takes them back; empty when
        /// they left of their own accord.
        /// </summary>
        /// <remarks>
        /// Separate from <see cref="LastError"/>, which is also a failed join or a failed room list, and is cleared
        /// as soon as the next thing succeeds. This survives the trip back to the lobby, because that trip is the
        /// only chance to say what happened: the level is already unloading when it starts.
        /// </remarks>
        public string SessionEndedReason { get; private set; } = string.Empty;

        public bool IsBusy => State == SessionConnectionState.SigningIn || State == SessionConnectionState.Connecting;

        public bool IsConnected => State == SessionConnectionState.Connected && m_session != null;

        public bool IsHost => m_session != null && m_session.IsHost;

        /// <summary>
        /// This player's Unity account id, the same across sessions and machines, or empty when signed out.
        /// </summary>
        /// <remarks>Saves key each player's belongings to this, so resuming one hands people back their own.</remarks>
        public string LocalPlayerId => m_session != null && m_session.CurrentPlayer != null
            ? m_session.CurrentPlayer.Id
            : string.Empty;

        public string RoomName => m_session != null ? m_session.Name : string.Empty;

        /// <summary>Scene name of the level the host chose for this room, or empty if none was set.</summary>
        /// <remarks>
        /// A room property rather than something the host keeps to itself, so everyone waiting sees where they are
        /// going. The host is still the only one who loads it: Netcode's scene event takes every client along.
        /// </remarks>
        public string RoomLevel =>
            m_session?.Properties != null && m_session.Properties.TryGetValue(LevelProperty, out var property)
                                          && property != null
                ? property.Value ?? string.Empty
                : string.Empty;

        /// <summary>How the room plays, as the host last set it; Normal when not in a room or never set.</summary>
        /// <remarks>
        /// A room property rather than something the host keeps to itself, so everyone waiting sees what they are in for.
        /// The host's own copy wins while it is still being saved to the service, so its sliders never jump back.
        /// </remarks>
        public GameplaySettings RoomSettings
        {
            get
            {
                if (m_session != null && m_session.IsHost && m_pendingSettings.HasValue) return m_pendingSettings.Value;

                return m_session?.Properties != null && m_session.Properties.TryGetValue(SettingsProperty, out var property)
                                                     && property != null
                    ? GameplaySettings.Parse(property.Value)
                    : GameplaySettings.Normal;
            }
        }

        /// <summary>True when the room resumes a save, whose settings it keeps; nobody may change them.</summary>
        public bool RoomSettingsLocked =>
            m_session?.Properties != null && m_session.Properties.TryGetValue(SettingsLockedProperty, out var property)
                                          && property != null && property.Value == "1";

        /// <summary>Host: changes how the room plays, for everyone waiting.</summary>
        /// <remarks>
        /// Coalesced like character changes: dragging a slider sends one update per round trip, not one per frame, which
        /// keeps inside the service's rate limit. Refused when the room resumes a save.
        /// </remarks>
        public void SetRoomSettings(GameplaySettings settings)
        {
            if (m_session == null || !m_session.IsHost || RoomSettingsLocked) return;

            m_pendingSettings = settings;
            RosterChanged?.Invoke();
            _ = PublishSettingsAsync();
        }

        private GameplaySettings? m_pendingSettings;
        private bool m_publishingSettings;
        private bool m_settingsDirty;

        private async Task PublishSettingsAsync()
        {
            m_settingsDirty = true;
            if (m_publishingSettings) return;

            m_publishingSettings = true;
            try
            {
                while (m_settingsDirty && m_session != null && m_session.IsHost && m_pendingSettings.HasValue)
                {
                    m_settingsDirty = false;

                    var host = m_session.AsHost();
                    host.SetProperty(SettingsProperty,
                        new SessionProperty(m_pendingSettings.Value.Encode(), VisibilityPropertyOptions.Member));
                    await host.SavePropertiesAsync();
                }
            }
            catch (Exception exception)
            {
                LastError = Describe("Could not update the game settings", exception);
                Debug.LogWarning($"{nameof(SessionService)} — {LastError}", this);
            }
            finally
            {
                m_publishingSettings = false;

                // Saved and read back: the room's own value is the truth again.
                if (!m_settingsDirty) m_pendingSettings = null;
            }
        }

        /// <summary>
        /// The character a room member had in the save this room resumes, encoded; empty for a new game or a player new to
        /// the save.
        /// </summary>
        /// <remarks>
        /// Display only, like the members' own character property: the host spawns from the save it holds. It is here so
        /// a player joining a resumed game sees, before it starts, that the save decides who they are.
        /// </remarks>
        public string SavedCharacterOf(string playerId)
        {
            if (string.IsNullOrEmpty(playerId) || m_session?.Properties == null
                || !m_session.Properties.TryGetValue(SavedCharactersProperty, out var property) || property?.Value == null)
                return string.Empty;

            foreach (var entry in property.Value.Split(SavedCharacterSeparator))
            {
                var assignment = entry.IndexOf(SavedCharacterAssignment);
                if (assignment > 0 && entry.Substring(0, assignment) == playerId) return entry.Substring(assignment + 1);
            }

            return string.Empty;
        }

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
        /// <param name="level">Scene name of the level to play, published as <see cref="RoomLevel"/>.</param>
        /// <param name="savedCharacters">
        /// Encoded characters by account id from the save being resumed, published for <see cref="SavedCharacterOf"/>.
        /// </param>
        /// <param name="settings">How the room plays to begin with; Normal when omitted.</param>
        /// <param name="settingsLocked">True when resuming a save, whose settings nobody may change.</param>
        public async Task CreateRoomAsync(string displayName, string roomName, int maxPlayers, string password,
            string level = null, IReadOnlyDictionary<string, string> savedCharacters = null,
            GameplaySettings? settings = null, bool settingsLocked = false)
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
                // Whatever happened to the last room is past once they are starting another.
                ClearSessionEndedReason();
                SetState(SessionConnectionState.Connecting);

                var options = new SessionOptions
                {
                    Type = SessionType,
                    Name = roomName.Trim(),
                    MaxPlayers = Mathf.Clamp(maxPlayers, MinRoomSize, MaxRoomSize),
                    IsPrivate = false,
                    Password = string.IsNullOrEmpty(password) ? null : password,
                    PlayerProperties = PlayerPropertiesFor(displayName),
                    SessionProperties = RoomPropertiesFor(level, savedCharacters, settings ?? GameplaySettings.Normal, settingsLocked)
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
                ClearSessionEndedReason();
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

        /// <summary>Forgets why the last room ended, once the player has been told.</summary>
        public void ClearSessionEndedReason() => SessionEndedReason = string.Empty;

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

        private static Dictionary<string, SessionProperty> RoomPropertiesFor(string level,
            IReadOnlyDictionary<string, string> savedCharacters, GameplaySettings settings, bool settingsLocked)
        {
            var properties = new Dictionary<string, SessionProperty>
            {
                [SettingsProperty] = new(settings.Encode(), VisibilityPropertyOptions.Member)
            };

            if (settingsLocked) properties[SettingsLockedProperty] = new SessionProperty("1", VisibilityPropertyOptions.Member);

            // Member visibility, like names: only people in the room need to know where it is headed.
            if (!string.IsNullOrEmpty(level))
                properties[LevelProperty] = new SessionProperty(level, VisibilityPropertyOptions.Member);

            var encoded = EncodeSavedCharacters(savedCharacters);
            if (encoded.Length > 0)
                properties[SavedCharactersProperty] = new SessionProperty(encoded, VisibilityPropertyOptions.Member);

            return properties;
        }

        /// <summary>Writes <c>account=choice;account=choice</c>, skipping anything that would not read back.</summary>
        private static string EncodeSavedCharacters(IReadOnlyDictionary<string, string> savedCharacters)
        {
            if (savedCharacters == null) return string.Empty;

            var builder = new System.Text.StringBuilder();
            foreach (var saved in savedCharacters)
            {
                if (string.IsNullOrEmpty(saved.Key) || string.IsNullOrEmpty(saved.Value)) continue;
                if (saved.Key.IndexOf(SavedCharacterSeparator) >= 0 || saved.Key.IndexOf(SavedCharacterAssignment) >= 0
                    || saved.Value.IndexOf(SavedCharacterSeparator) >= 0) continue;

                var entry = $"{saved.Key}{SavedCharacterAssignment}{saved.Value}";
                if (builder.Length + entry.Length + 1 > MaxSavedCharactersLength) break;

                if (builder.Length > 0) builder.Append(SavedCharacterSeparator);
                builder.Append(entry);
            }

            return builder.ToString();
        }

        private Dictionary<string, PlayerProperty> PlayerPropertiesFor(string displayName)
        {
            // Member visibility: only people in the same room need to see the name or the character.
            var properties = new Dictionary<string, PlayerProperty>
            {
                [DisplayNameProperty] = new(SanitiseDisplayName(displayName), VisibilityPropertyOptions.Member)
            };

            // Left out rather than sent empty: a room is joinable without any characters set up.
            if (!string.IsNullOrEmpty(m_localCharacter))
                properties[CharacterProperty] = new PlayerProperty(m_localCharacter, VisibilityPropertyOptions.Member);

            return properties;
        }

        /// <summary>
        /// Sets the character this player shows the room: sent with the next create or join, and published at once if
        /// already in a room.
        /// </summary>
        /// <remarks>
        /// Display only; the host spawns from <c>CharacterSelections</c>' own record. Publishing is coalesced: a
        /// player clicking through colours sends one update per round trip rather than one per click, which keeps well
        /// inside Lobby's rate limit.
        /// </remarks>
        /// <param name="encodedCharacter">The choice as <c>CharacterChoice.Encode</c> wrote it.</param>
        public void SetLocalCharacter(string encodedCharacter)
        {
            encodedCharacter ??= string.Empty;
            if (encodedCharacter == m_localCharacter) return;

            m_localCharacter = encodedCharacter;
            if (IsConnected && encodedCharacter.Length > 0) _ = PublishLocalCharacterAsync();
        }

        private async Task PublishLocalCharacterAsync()
        {
            m_characterDirty = true;
            if (m_publishingCharacter) return;

            m_publishingCharacter = true;
            try
            {
                while (m_characterDirty && m_session?.CurrentPlayer != null)
                {
                    m_characterDirty = false;

                    m_session.CurrentPlayer.SetProperty(CharacterProperty,
                        new PlayerProperty(m_localCharacter, VisibilityPropertyOptions.Member));
                    await m_session.SaveCurrentPlayerDataAsync();
                }
            }
            catch (Exception exception)
            {
                // Cosmetic for the room list; the spawn does not depend on it, so the room is not failed over it.
                Debug.LogWarning($"{nameof(SessionService)} — could not show your character to the room: {exception.Message}", this);
            }
            finally
            {
                m_publishingCharacter = false;
            }
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

            // A change still unsaved when the last room ended belongs to that room.
            m_pendingSettings = null;
            m_settingsDirty = false;
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

                    var character = player.Properties != null
                                    && player.Properties.TryGetValue(CharacterProperty, out var characterProperty)
                                    && characterProperty != null
                        ? characterProperty.Value ?? string.Empty
                        : string.Empty;

                    var isHost = player.Id == m_session.Host;
                    var member = new RoomMember(player.Id, SanitiseDisplayName(name), isHost, player.Id == localId,
                        character);

                    if (isHost) m_members.Insert(0, member);
                    else m_members.Add(member);
                }
            }

            RosterChanged?.Invoke();
        }

        private void HandleRemovedFromSession() => EndUnexpectedly("The host removed you from the room.");

        private void HandleSessionDeleted() => EndUnexpectedly("The host ended the game, so the room is closed.");

        private void HandleClientStopped(bool wasHost)
        {
            if (m_session == null) return;

            var session = m_session;
            EndUnexpectedly(wasHost
                ? "The room shut down."
                : "Lost the connection to the host. They may have left, or the connection dropped.");

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
            SessionEndedReason = reason;
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
