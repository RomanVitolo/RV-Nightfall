using System;
using System.Threading.Tasks;
using Unity.Services.Authentication;
using Unity.Services.Core;
using Unity.Services.Multiplayer;
using UnityEngine;

namespace Modules.Multiplayer.Scripts.Runtime.Sessions
{
    /// <summary>
    /// Owns the lifetime of one Relay-backed multiplayer session for this process, and exposes it
    /// as observable state for UI.
    /// </summary>
    /// <remarks>
    /// Authority: the Sessions package decides transport configuration and starts the NetworkManager
    /// itself (NetworkManagerSession calls StartHost/StartClient internally). Nothing in this class
    /// may call NetworkManager.StartHost/StartClient — a second start throws away the Relay
    /// allocation the session just negotiated and the join silently fails.
    /// </remarks>
    public class SessionService : MonoBehaviour
    {
        [SerializeField] private int m_maxPlayers = 4;
        [SerializeField] private string m_sessionName = "Playground";

        private ISession m_session;

        /// <summary>Raised whenever <see cref="State"/> changes, on the main thread.</summary>
        public event Action<SessionConnectionState> StateChanged;

        public SessionConnectionState State { get; private set; } = SessionConnectionState.Disconnected;

        /// <summary>Code other players type to join this session. Empty until connected.</summary>
        public string JoinCode => m_session != null ? m_session.Code : string.Empty;

        /// <summary>Human-readable reason for the last <see cref="SessionConnectionState.Error"/>.</summary>
        public string LastError { get; private set; } = string.Empty;

        public bool IsBusy => State == SessionConnectionState.SigningIn || State == SessionConnectionState.Connecting;

        public bool IsConnected => State == SessionConnectionState.Connected;

        /// <summary>
        /// Creates a new session and becomes its host.
        /// </summary>
        /// <param name="profileName">
        /// Distinguishes this process' anonymous identity. Two clients signed in under the same
        /// profile share a player ID, and the second one is rejected as already being in the session.
        /// </param>
        public async Task CreateSessionAsync(string profileName)
        {
            if (IsBusy) return;

            try
            {
                if (!await EnsureSignedInAsync(profileName)) return;

                SetState(SessionConnectionState.Connecting);

                var options = new SessionOptions
                {
                    Name = m_sessionName,
                    MaxPlayers = m_maxPlayers,
                    IsPrivate = false
                }.WithRelayNetwork();

                AttachSession(await MultiplayerService.Instance.CreateSessionAsync(options));
                SetState(SessionConnectionState.Connected);
            }
            catch (Exception exception)
            {
                Fail("Create session failed", exception);
            }
        }

        /// <summary>Joins an existing session using the code its host is displaying.</summary>
        public async Task JoinSessionAsync(string profileName, string joinCode)
        {
            if (IsBusy) return;

            if (string.IsNullOrWhiteSpace(joinCode))
            {
                Fail("Join session failed", new ArgumentException("Join code is empty."));
                return;
            }

            try
            {
                if (!await EnsureSignedInAsync(profileName)) return;

                SetState(SessionConnectionState.Connecting);
                AttachSession(await MultiplayerService.Instance.JoinSessionByCodeAsync(joinCode.Trim()));
                SetState(SessionConnectionState.Connected);
            }
            catch (Exception exception)
            {
                Fail("Join session failed", exception);
            }
        }

        /// <summary>Leaves the current session; the package shuts the NetworkManager down for us.</summary>
        public async Task LeaveSessionAsync()
        {
            if (m_session == null) return;

            try
            {
                var leaving = m_session;
                DetachSession();
                await leaving.LeaveAsync();
            }
            catch (Exception exception)
            {
                Fail("Leave session failed", exception);
                return;
            }

            SetState(SessionConnectionState.Disconnected);
        }

        private async Task<bool> EnsureSignedInAsync(string profileName)
        {
            SetState(SessionConnectionState.SigningIn);

            if (UnityServices.State != ServicesInitializationState.Initialized)
            {
                var options = new InitializationOptions();
                if (!string.IsNullOrWhiteSpace(profileName)) options.SetProfile(profileName.Trim());

                await UnityServices.InitializeAsync(options);
            }

            var authentication = AuthenticationService.Instance;
            if (authentication == null)
            {
                Fail("Sign-in failed", new InvalidOperationException(
                    "AuthenticationService is unavailable. Check that the project is linked to a Unity Cloud project."));
                return false;
            }

            if (!authentication.IsSignedIn) await authentication.SignInAnonymouslyAsync();

            return true;
        }

        private void AttachSession(ISession session)
        {
            m_session = session;
            if (m_session == null) return;

            // The host can delete the session or kick us; either way our NetworkManager is already
            // going down, so the UI must not keep claiming we are connected.
            m_session.RemovedFromSession += HandleSessionEnded;
            m_session.Deleted += HandleSessionEnded;
        }

        private void DetachSession()
        {
            if (m_session == null) return;

            m_session.RemovedFromSession -= HandleSessionEnded;
            m_session.Deleted -= HandleSessionEnded;
            m_session = null;
        }

        private void HandleSessionEnded()
        {
            DetachSession();
            SetState(SessionConnectionState.Disconnected);
        }

        private void Fail(string context, Exception exception)
        {
            // SessionException carries a SessionError code that is far more actionable than the message
            // alone (e.g. which Unity Cloud service is not enabled for the project).
            LastError = exception is SessionException sessionException
                ? $"{context}: {sessionException.Error} — {sessionException.Message}"
                : $"{context}: {exception.Message}";

            Debug.LogError($"{nameof(SessionService)} — {LastError}", this);
            SetState(SessionConnectionState.Error);
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
