using Unity.Netcode;
using UnityEngine;

namespace _Scripts.Premade_Scripts
{
    public class GameMotor : NetworkBehaviour
    {
        [SerializeField] private MultiplayerUI m_multiplayerUI;
        
        private void Start()
        {
            if (m_multiplayerUI == null) return;
            m_multiplayerUI.OnStartHost += StartHost;
            m_multiplayerUI.OnStartClient += StartClient;
            m_multiplayerUI.OnDisconnectClient += DisconnectClient;
        }

        private void DisconnectClient()
        {
            m_multiplayerUI.EnableButtons();
            NetworkManager.Shutdown();
        }

        private void StartClient()
        {
            m_multiplayerUI.DisableButtons();
            NetworkManager.StartClient();
        }

        private void StartHost()
        {
            m_multiplayerUI.DisableButtons();
            NetworkManager.StartHost();
        }
    }
}