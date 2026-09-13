using Modules.Multiplayer.Scripts.Runtime.Levels;
using Unity.Netcode;
using UnityEngine;

namespace Modules.Multiplayer.Scripts.Runtime.Flow
{
    /// <summary>
    /// Spawns each client's player once that client has loaded the level.
    /// </summary>
    /// <remarks>
    /// Authority: server. The NetworkManager deliberately has no Player Prefab: Netcode would otherwise
    /// spawn one the moment a client connects — in the lobby, where the player's HUD, managers and camera
    /// have no level to attach to. Spawning per client on its own load-complete event, rather than once
    /// when everyone has finished, means a slow machine delays only its own player.
    /// </remarks>
    [RequireComponent(typeof(NetworkManager))]
    public class NetworkPlayerSpawner : MonoBehaviour
    {
        [SerializeField] private NetworkManager m_networkManager;

        [Tooltip("Networked player prefab. Must be registered in the NetworkManager's prefab list.")]
        [SerializeField] private GameObject m_playerPrefab;

        [Tooltip("Players are only spawned when one of these levels finishes loading.")]
        [SerializeField] private LevelCatalog m_levels;

        private NetworkSceneManager m_hookedSceneManager;

        private void Awake()
        {
            if (m_networkManager == null) m_networkManager = GetComponent<NetworkManager>();

            if (m_playerPrefab == null || !m_playerPrefab.TryGetComponent<NetworkObject>(out _))
            {
                Debug.LogError(
                    $"{nameof(NetworkPlayerSpawner)}: the player prefab is missing or has no NetworkObject; nobody will spawn. " +
                    "Re-run Tools > Multiplayer > Set Up Lobby.", this);
            }

            if (m_levels == null)
            {
                Debug.LogError(
                    $"{nameof(NetworkPlayerSpawner)}: no {nameof(LevelCatalog)} assigned, so no level spawns players. " +
                    "Re-run Tools > Multiplayer > Set Up Lobby.", this);
            }
        }

        private void OnEnable()
        {
            if (m_networkManager == null) return;

            m_networkManager.OnServerStarted += HookSceneEvents;
            m_networkManager.OnServerStopped += HandleServerStopped;

            if (m_networkManager.IsServer) HookSceneEvents();
        }

        private void OnDisable()
        {
            if (m_networkManager != null)
            {
                m_networkManager.OnServerStarted -= HookSceneEvents;
                m_networkManager.OnServerStopped -= HandleServerStopped;
            }

            UnhookSceneEvents();
        }

        private void HookSceneEvents()
        {
            // The scene manager is recreated each time Netcode starts, so hook whichever instance is current.
            UnhookSceneEvents();

            m_hookedSceneManager = m_networkManager.SceneManager;
            if (m_hookedSceneManager != null) m_hookedSceneManager.OnSceneEvent += HandleSceneEvent;
        }

        private void UnhookSceneEvents()
        {
            if (m_hookedSceneManager == null) return;

            m_hookedSceneManager.OnSceneEvent -= HandleSceneEvent;
            m_hookedSceneManager = null;
        }

        private void HandleServerStopped(bool wasHost) => UnhookSceneEvents();

        private void HandleSceneEvent(SceneEvent sceneEvent)
        {
            if (sceneEvent == null || !m_networkManager.IsServer) return;
            if (sceneEvent.SceneEventType != SceneEventType.LoadComplete) return;
            // Any level, not only the one the room started in: a restart reloads whichever is being played.
            if (m_levels == null || !m_levels.Contains(sceneEvent.SceneName)) return;

            SpawnPlayerFor(sceneEvent.ClientId);
        }

        private void SpawnPlayerFor(ulong clientId)
        {
            if (m_playerPrefab == null) return;

            // A client can report the same load twice (e.g. a resynchronisation); one player each.
            if (!m_networkManager.ConnectedClients.TryGetValue(clientId, out var client)) return;
            if (client.PlayerObject != null) return;

            // When the room is resuming a save, this client's world and belongings go first, and the spawn waits
            // for them. Otherwise a player would appear in a level that still looks untouched, and be moved and
            // handed their inventory a moment later.
            var gate = SpawnGate.Active;
            if (gate != null && gate.TryHold(clientId, () => Spawn(clientId))) return;

            Spawn(clientId);
        }

        private void Spawn(ulong clientId)
        {
            if (!m_networkManager.ConnectedClients.TryGetValue(clientId, out var client) || client.PlayerObject != null)
                return;

            // Spawned at the prefab's origin; the owner moves itself onto a spawn point on arrival
            // (HeroPlayerNetworkSetup), since only it runs the CharacterController that must be teleported.
            NetworkObject.InstantiateAndSpawn(m_playerPrefab, m_networkManager, clientId,
                destroyWithScene: true, isPlayerObject: true);
        }
    }
}
