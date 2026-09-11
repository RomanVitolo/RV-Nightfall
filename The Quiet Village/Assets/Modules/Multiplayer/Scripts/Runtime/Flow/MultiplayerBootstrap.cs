using Modules.Multiplayer.Scripts.Runtime.Sessions;
using Modules.Multiplayer.Scripts.Runtime.UI;
using Unity.Netcode;
using UnityEngine;

namespace Modules.Multiplayer.Scripts.Runtime.Flow
{
    /// <summary>
    /// Makes sure exactly one persistent multiplayer root exists, and hands it to the lobby screen.
    /// </summary>
    /// <remarks>
    /// The root carries the NetworkManager, which Netcode moves to DontDestroyOnLoad on its own — so it
    /// survives returning to the lobby, while this scene is loaded afresh. Placing the root in the scene
    /// would add a second NetworkManager on every return; Netcode does not remove duplicates, it only
    /// warns. Instead the root is a prefab, instantiated only when no NetworkManager exists yet.
    ///
    /// The lobby screen is a scene object and the root is not, so they cannot reference each other in the
    /// Inspector. This is the one place that joins them, explicitly, instead of the screen reaching for
    /// a static.
    /// </remarks>
    public class MultiplayerBootstrap : MonoBehaviour
    {
        [Tooltip("Prefab holding NetworkManager, SessionService, SessionFlow and the player spawner.")]
        [SerializeField] private GameObject m_multiplayerRootPrefab;

        [SerializeField] private LobbyScreen m_lobbyScreen;

        private void Awake()
        {
            var root = FindOrCreateRoot();
            if (root == null) return;

            var sessions = root.GetComponent<SessionService>();
            var flow = root.GetComponent<SessionFlow>();

            if (sessions == null || flow == null)
            {
                Debug.LogError($"{nameof(MultiplayerBootstrap)}: '{root.name}' lacks SessionService or SessionFlow. " +
                               "Re-run Tools > Multiplayer > Set Up Lobby.", this);
                return;
            }

            if (m_lobbyScreen == null)
            {
                Debug.LogError($"{nameof(MultiplayerBootstrap)}: no LobbyScreen assigned.", this);
                return;
            }

            m_lobbyScreen.Bind(sessions, flow);
        }

        private GameObject FindOrCreateRoot()
        {
            // NetworkManager.Singleton is Netcode's own documented global, and the only object guaranteed
            // to be on the persistent root.
            var existing = NetworkManager.Singleton;
            if (existing != null) return existing.gameObject;

            if (m_multiplayerRootPrefab == null)
            {
                Debug.LogError($"{nameof(MultiplayerBootstrap)}: no multiplayer root prefab assigned.", this);
                return null;
            }

            var root = Instantiate(m_multiplayerRootPrefab);
            root.name = m_multiplayerRootPrefab.name;
            return root;
        }
    }
}
