using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Unity.Services.Authentication;
using Unity.Services.Core;
using Unity.Services.Multiplayer;
using UnityEngine;

namespace Modules.GameMotor.Runtime
{
    /// <summary>
    /// SessionManager: asegura que solo exista UNA sesión activa.
    /// - Al iniciar, intenta unirse a una sesión joinable (AvailableSlots > 0).
    /// - Si no hay, crea una nueva.
    /// - Se suscribe a eventos de ISession para reflejar cambios.
    /// - Expone Leave/Refresh/Reconnect helpers.
    /// Requiere un RVSingleton<T> seguro (persistente + anti-duplicados).
    /// </summary>
    public class SessionManager : RVSingleton<SessionManager>
    {
        // ===========================
        // Configuración / Constantes
        // ===========================
        private const string PlayerNamePropertyKey = "playerName";
        private const int DefaultMaxPlayers = 4;

        // ==================================
        // Estado y propiedades públicas
        // ==================================
        private ISession _activeSession;
        private bool _eventsHooked;
        private bool _initializing;
        private bool _initialized;
        private bool _connecting;

        /// <summary>Sesión activa (hosteada o unida). Cambiarla reconfigura eventos.</summary>
        public ISession ActiveSession
        {
            get => _activeSession;
            private set
            {
                if (_activeSession == value) return;

                if (_activeSession != null && _eventsHooked)
                {
                    UnregisterSessionEvents(_activeSession);
                    _eventsHooked = false;
                }

                _activeSession = value;

                if (_activeSession != null && !_eventsHooked)
                {
                    RegisterSessionEvents(_activeSession);
                    _eventsHooked = true;
                }

                Debug.Log($"[Session] Active: {(_activeSession != null ? _activeSession.Id : "null")}");
            }
        }

        // ==================================
        // RVSingleton config
        // ==================================
        protected override bool IsPersistent => true;
        protected override bool DestroyDuplicates => true;
        protected override bool AutoCreateIfMissing => false;

        // ==================================
        // Ciclo de vida
        // ==================================
        private async void Start()
        {
            // Evitar dobles inits si la escena vuelve a cargar el prefab accidentalmente
            if (_initializing || _initialized) return;

            _initializing = true;
            try
            {
                await EnsureServicesAsync();
                await EnsureOrJoinSessionAsync();
                _initialized = true;
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
            }
            finally
            {
                _initializing = false;
            }
        }

        private async Task EnsureServicesAsync()
        {
            if (UnityServices.State == ServicesInitializationState.Initialized)
            {
                if (!AuthenticationService.Instance.IsSignedIn)
                {
                    await AuthenticationService.Instance.SignInAnonymouslyAsync();
                }
                return;
            }

            await UnityServices.InitializeAsync();

            if (!AuthenticationService.Instance.IsSignedIn)
            {
                await AuthenticationService.Instance.SignInAnonymouslyAsync();
                Debug.Log($"UGS Signed In (Anon). PlayerID: {AuthenticationService.Instance.PlayerId}");
            }
        }

        // ==================================
        // Crear o unirse a sesión existente
        // ==================================
        /// <summary>
        /// Busca una sesión con espacio y se une; si no hay, crea una nueva.
        /// Reintenta si hay carrera (join falla porque se llenó/cerró en el medio).
        /// </summary>
        private async Task EnsureOrJoinSessionAsync()
        {
            if (_connecting) return;

            _connecting = true;
            try
            {
                // 1) Query sessions
                var sessions = await QuerySessionsAsync();
                var target = PickJoinableSession(sessions);

                // 2) Intentar Join
                if (target != null)
                {
                    if (await TryJoinByIdAsync(target.Id))
                        return;

                    // Típica carrera: si falló, refrescar y reintentar con el siguiente joinable
                    sessions = await QuerySessionsAsync();
                    foreach (var s in sessions.Where(s => s.AvailableSlots > 0))
                    {
                        if (await TryJoinByIdAsync(s.Id))
                            return;
                    }
                }

                // 3) Ninguna joinable → crear
                await CreateAndHostSessionAsync();
            }
            finally
            {
                _connecting = false;
            }
        }

        /// <summary>
        /// Devuelve la primera sesión con AvailableSlots > 0.
        /// NOTA: ISessionInfo no expone IsPrivate/IsLocked en todas las versiones.
        /// </summary>
        private static ISessionInfo PickJoinableSession(IList<ISessionInfo> list)
        {
            if (list == null || list.Count == 0) return null;

            return list
                .Where(s => s.AvailableSlots > 0)
                .OrderByDescending(s => s.AvailableSlots)
                .FirstOrDefault();
        }

        private async Task<bool> TryJoinByIdAsync(string sessionId)
        {
            try
            {
                ActiveSession = await MultiplayerService.Instance.JoinSessionByIdAsync(sessionId);
                Debug.Log($"[Session] Joined session {ActiveSession.Id}");
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Session] Join failed for {sessionId}: {ex.Message}");
                return false;
            }
        }

        private async Task CreateAndHostSessionAsync()
        {
            try
            {
                var playerProps = await BuildPlayerPropertiesAsync();

                var options = new SessionOptions
                {
                    MaxPlayers = DefaultMaxPlayers,
                    IsLocked = false,      // permitir joins
                    IsPrivate = false,     // visible en queries
                    PlayerProperties = playerProps
                };

                // Si usás Relay via Multiplayer Center:
                // options = options.WithRelayNetwork();

                ActiveSession = await MultiplayerService.Instance.CreateSessionAsync(options);
                Debug.Log($"[Session] Created {ActiveSession.Id} | Code: {ActiveSession.Code}");
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
                throw;
            }
        }

        // ==================================
        // Eventos de sesión
        // ==================================
        private void RegisterSessionEvents(ISession s)
        {
            s.Changed += OnSessionChanged;
            s.StateChanged += OnSessionStateChanged;
            s.PlayerJoined += OnPlayerJoined;
#pragma warning disable CS0618
            s.PlayerLeft += OnPlayerLeftDeprecated;
#pragma warning restore CS0618
            s.PlayerLeaving += OnPlayerLeaving;
            s.PlayerHasLeft += OnPlayerHasLeft;
            s.SessionPropertiesChanged += OnSessionPropertiesChanged;
            s.PlayerPropertiesChanged += OnPlayerPropertiesChanged;
            s.RemovedFromSession += OnRemovedFromSession;
            s.Deleted += OnDeleted;
            s.SessionHostChanged += OnSessionHostChanged;
        }

        private void UnregisterSessionEvents(ISession s)
        {
            s.Changed -= OnSessionChanged;
            s.StateChanged -= OnSessionStateChanged;
            s.PlayerJoined -= OnPlayerJoined;
#pragma warning disable CS0618
            s.PlayerLeft -= OnPlayerLeftDeprecated;
#pragma warning restore CS0618
            s.PlayerLeaving -= OnPlayerLeaving;
            s.PlayerHasLeft -= OnPlayerHasLeft;
            s.SessionPropertiesChanged -= OnSessionPropertiesChanged;
            s.PlayerPropertiesChanged -= OnPlayerPropertiesChanged;
            s.RemovedFromSession -= OnRemovedFromSession;
            s.Deleted -= OnDeleted;
            s.SessionHostChanged -= OnSessionHostChanged;
        }

        private void OnSessionChanged()
        {
            Debug.Log("[Session] Changed");
            // TODO: refrescar UI de lista de jugadores, slots, etc.
        }

        private void OnSessionStateChanged(SessionState state)
        {
            Debug.Log($"[Session] State → {state}");
            // TODO: si usás un flujo por estados, adaptar (ej: Lobby → InGame)
        }

        private void OnPlayerJoined(string playerId)
        {
            Debug.Log($"[Session] PlayerJoined: {playerId}");
            // TODO: actualizar UI/contador/ready checks
        }

#pragma warning disable CS0618
        private void OnPlayerLeftDeprecated(string playerId)
        {
            Debug.Log($"[Session] PlayerLeft(deprecated): {playerId}");
        }
#pragma warning restore CS0618

        private void OnPlayerLeaving(string playerId)
        {
            Debug.Log($"[Session] PlayerLeaving: {playerId}");
        }

        private void OnPlayerHasLeft(string playerId)
        {
            Debug.Log($"[Session] PlayerHasLeft: {playerId}");
            // Si el host se va, SessionHostChanged disparará con el nuevo host
        }

        private void OnSessionPropertiesChanged()
        {
            Debug.Log("[Session] Properties changed");
        }

        private void OnPlayerPropertiesChanged()
        {
            Debug.Log("[Session] Player properties changed");
        }

        private void OnRemovedFromSession()
        {
            Debug.LogWarning("[Session] RemovedFromSession");
            ActiveSession = null;
        }

        private void OnDeleted()
        {
            Debug.LogWarning("[Session] Deleted");
            ActiveSession = null;
        }

        private void OnSessionHostChanged(string newHostPlayerId)
        {
            Debug.Log($"[Session] Host → {newHostPlayerId}");
            // TODO: si vos eras host y cambió, reasignar roles/lógica
        }

        // ==================================
        // Helpers públicos
        // ==================================
        public async Task LeaveSessionAsync()
        {
            if (ActiveSession == null) return;

            try
            {
                await ActiveSession.LeaveAsync();
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Session] Leave error: {e.Message}");
            }
            finally
            {
                ActiveSession = null;
            }
        }

        /// <summary>
        /// Refresca sesión al volver el foco. Si no es posible, intenta Reconnect.
        /// </summary>
        private async void OnApplicationFocus(bool hasFocus)
        {
            if (!hasFocus) return;

            if (ActiveSession == null) return;

            try
            {
                await ActiveSession.RefreshAsync();
            }
            catch
            {
                try
                {
                    await ActiveSession.ReconnectAsync();
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[Session] Reconnect failed: {e.Message}");
                }
            }
        }

        // ==================================
        // UGS Utility methods
        // ==================================
        private async Task<IList<ISessionInfo>> QuerySessionsAsync()
        {
            try
            {
                var q = new QuerySessionsOptions();
                var results = await MultiplayerService.Instance.QuerySessionsAsync(q);
                return results.Sessions;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Session] QuerySessions failed: {ex.Message}");
                return Array.Empty<ISessionInfo>();
            }
        }

        private async Task<Dictionary<string, PlayerProperty>> BuildPlayerPropertiesAsync()
        {
            string name = null;
            try
            {
                // Si no usás Player Names en UGS, podés setear uno fallback
                name = await AuthenticationService.Instance.GetPlayerNameAsync();
            }
            catch
            {
                // Algunas cuentas no tienen PlayerNames; usar fallback
                name = $"Player_{UnityEngine.Random.Range(1000, 9999)}";
            }

            var nameProperty = new PlayerProperty(name, VisibilityPropertyOptions.Member);
            return new Dictionary<string, PlayerProperty> { { PlayerNamePropertyKey, nameProperty } };
        }
    }
}
