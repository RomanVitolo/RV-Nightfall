using System;
using Unity.Netcode;
using UHFPS.Runtime;
using UnityEngine;

namespace Modules.Multiplayer.Bridge
{
    /// <summary>
    /// Replicates player health, with the server as the sole authority over the value.
    /// </summary>
    /// <remarks>
    /// Authority: server. Health is the one player value a client must never be able to declare for
    /// itself — otherwise any modified client is immortal, and two clients can disagree about who
    /// died. Clients <em>request</em> damage through <see cref="RequestDamageRpc"/>; only the server
    /// writes the result.
    ///
    /// The owner mirrors the replicated value into its local <see cref="PlayerHealth"/> by assigning
    /// <c>EntityHealth</c> rather than calling <c>ApplyDamage</c>. The setter on
    /// <c>BaseHealthEntity</c> already raises UHFPS's health-changed and death callbacks, so the
    /// existing UI, blood overlay and death flow keep working without the damage being applied twice.
    /// </remarks>
    public class PlayerHealthSync : NetworkBehaviour
    {
        [SerializeField] private PlayerHealth m_playerHealth;

        private readonly NetworkVariable<int> m_health =
            new(100, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

        private int m_maxHealth = 100;

        /// <summary>Current replicated health. Readable on every client, written only by the server.</summary>
        public int Health => m_health.Value;

        /// <summary>Raised on every client when this player's health changes.</summary>
        public event Action<int, int> HealthChanged;

        public override void OnNetworkSpawn()
        {
            if (m_playerHealth != null)
            {
                m_maxHealth = (int)m_playerHealth.MaxHealth;

                // UHFPS applies fall damage locally on whichever client detects the landing, which
                // would move health without the server's knowledge and be overwritten on the next
                // replication. Fall damage has to come back through RequestDamageRpc to be authoritative.
                m_playerHealth.EnableFallDamage = false;
            }

            if (IsServer)
            {
                var start = m_playerHealth != null ? (int)m_playerHealth.StartHealth : m_maxHealth;
                m_health.Value = Mathf.Clamp(start, 0, m_maxHealth);
            }

            m_health.OnValueChanged += HandleHealthChanged;

            // A late joiner receives the current value as initial state rather than as a change, so
            // apply it once on spawn or its UI would sit at the prefab default.
            if (IsOwner) MirrorToLocalHealth(m_health.Value);
        }

        public override void OnNetworkDespawn()
        {
            m_health.OnValueChanged -= HandleHealthChanged;
        }

        /// <summary>
        /// Asks the server to damage this player. Safe to call from any client.
        /// </summary>
        /// <param name="damage">Damage amount; non-positive values are ignored.</param>
        [Rpc(SendTo.Server)]
        public void RequestDamageRpc(int damage)
        {
            if (damage <= 0 || m_health.Value <= 0) return;

            m_health.Value = Mathf.Clamp(m_health.Value - damage, 0, m_maxHealth);
        }

        /// <summary>Asks the server to heal this player. Safe to call from any client.</summary>
        /// <param name="healAmount">Heal amount; non-positive values are ignored.</param>
        [Rpc(SendTo.Server)]
        public void RequestHealRpc(int healAmount)
        {
            if (healAmount <= 0) return;

            m_health.Value = Mathf.Clamp(m_health.Value + healAmount, 0, m_maxHealth);
        }

        private void HandleHealthChanged(int previous, int current)
        {
            // Only the owner runs the full UHFPS health stack; on remote copies PlayerHealth is
            // disabled and driving it would reach into this client's own HUD.
            if (IsOwner) MirrorToLocalHealth(current);

            HealthChanged?.Invoke(previous, current);
        }

        private void MirrorToLocalHealth(int value)
        {
            if (m_playerHealth == null) return;

            m_playerHealth.EntityHealth = value;
        }
    }
}
