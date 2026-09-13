using Unity.Netcode;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace QuietVillage.Multiplayer.Flow
{
    /// <summary>
    /// Sends the player to the lobby when a level is opened without a running network session.
    /// </summary>
    /// <remarks>
    /// Players only exist in the level because the host spawned them there after starting a room. Opening
    /// the level any other way — pressing Play on it in the Editor, or UHFPS's own New Game — would leave
    /// an empty world with no player, no camera and a flood of errors. Redirecting to the lobby turns that
    /// into the correct flow instead.
    /// </remarks>
    [DefaultExecutionOrder(-1000)]
    public class SessionSceneGuard : MonoBehaviour
    {
        [Tooltip("Scene to open instead. Must be in Build Settings.")]
        [SerializeField] private string m_lobbySceneName = "LobbyScene";

        private void Awake()
        {
            var networkManager = NetworkManager.Singleton;
            if (networkManager != null && networkManager.IsListening) return;

            Debug.LogWarning(
                $"{gameObject.scene.name} was opened without a multiplayer session; loading {m_lobbySceneName}. " +
                "Start from the lobby to play.", this);

            SceneManager.LoadScene(m_lobbySceneName);
        }
    }
}
