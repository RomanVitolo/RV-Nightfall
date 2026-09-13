using Unity.Netcode;
using UHFPS.Runtime;
using UHFPS.Runtime.States;
using UnityEngine;
using UnityEngine.AI;

namespace QuietVillage.Multiplayer.Bridge
{
    /// <summary>
    /// Runs an NPC on the host for everyone: it chooses which networked player to hunt, and its health and
    /// death are the same on every screen.
    /// </summary>
    /// <remarks>
    /// Authority: server. UHFPS AI used to run on every client against that client's own player, so each player
    /// was chased by a private copy of the same zombie. Now only the host simulates it — decision making,
    /// navigation, attacks — and clients show the result: position through NetworkTransform, animation through
    /// NetworkAnimator, both added beside this component.
    ///
    /// Health is server-authoritative too. A client's weapon still hits the zombie's local hitboxes, so UHFPS's
    /// hit detection is untouched, but <see cref="NPCHealth"/> hands the damage to this relay, which sends it to
    /// the host. Clients then replay each confirmed drop in health locally, so damage sounds, hit events and the
    /// ragdoll death all play normally on every screen, at the same health values.
    /// </remarks>
    [RequireComponent(typeof(NetworkObject))]
    public class NetworkedNpc : NetworkBehaviour, INpcDamageRelay
    {
        [SerializeField] private NPCStateMachine m_machine;
        [SerializeField] private NPCHealth m_health;
        [SerializeField] private NavMeshAgent m_agent;

        [Tooltip("Seconds between re-evaluating which player to pursue.")]
        [SerializeField] private float m_retargetInterval = 0.5f;

        [Tooltip("Height (m) used for a player's eyes when their camera holder cannot be found.")]
        [SerializeField] private float m_fallbackEyeHeight = 1.6f;

        private readonly NetworkVariable<int> m_networkHealth =
            new(0, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

        private bool m_replaying;
        private float m_nextRetargetAt;

        public override void OnNetworkSpawn()
        {
            if (m_health != null) m_health.DamageRelay = this;

            if (IsServer)
            {
                if (m_health != null) m_networkHealth.Value = m_health.EntityHealth;
                return;
            }

            StopLocalSimulation();
            m_networkHealth.OnValueChanged += HandleHealthChanged;
            ReconcileHealth(m_networkHealth.Value);
        }

        public override void OnNetworkDespawn()
        {
            m_networkHealth.OnValueChanged -= HandleHealthChanged;
            if (m_health != null && ReferenceEquals(m_health.DamageRelay, this)) m_health.DamageRelay = null;
        }

        /// <summary>Clients show the host's zombie; they must not also run their own.</summary>
        private void StopLocalSimulation()
        {
            if (m_machine != null)
            {
                m_machine.enabled = false;

                // Root motion would move this copy on its own, against the position NetworkTransform delivers.
                if (m_machine.Animator != null) m_machine.Animator.applyRootMotion = false;
            }

            // The agent snaps the transform back onto its own path every frame.
            if (m_agent != null) m_agent.enabled = false;
        }

        private void Update()
        {
            if (!IsServer || !IsSpawned) return;

            PublishHealth();
            Retarget();
        }

        // ---- Targeting (server) ----------------------------------------------------------------------

        private void Retarget()
        {
            if (m_machine == null || (m_health != null && m_health.IsDead) || Time.time < m_nextRetargetAt) return;

            m_nextRetargetAt = Time.time + m_retargetInterval;
            m_machine.SetTarget(ChooseTarget());
        }

        /// <summary>
        /// Keeps the player being pursued while they live; otherwise the nearest player in sight, else the nearest.
        /// </summary>
        /// <remarks>
        /// Sticking with the current target stops a zombie flip-flopping between two players mid-chase. Falling
        /// back to the nearest player even when none is visible keeps UHFPS's own perception working: the patrol
        /// state tests sight and proximity against whoever the target is, and it needs someone to test.
        /// </remarks>
        private PlayerStateMachine ChooseTarget()
        {
            var current = m_machine.Player;
            if (IsValidTarget(current) && IsPursuing()) return current;

            PlayerStateMachine nearestVisible = null;
            PlayerStateMachine nearest = null;
            var nearestVisibleDistance = float.MaxValue;
            var nearestDistance = float.MaxValue;

            foreach (var client in NetworkManager.ConnectedClientsList)
            {
                var playerObject = client.PlayerObject;
                if (playerObject == null) continue;

                var candidate = playerObject.GetComponent<PlayerStateMachine>();
                if (!IsValidTarget(candidate)) continue;

                var distance = Vector3.Distance(transform.position, candidate.transform.position);
                if (distance < nearestDistance)
                {
                    nearestDistance = distance;
                    nearest = candidate;
                }

                if (distance < nearestVisibleDistance && CanSee(candidate, distance))
                {
                    nearestVisibleDistance = distance;
                    nearestVisible = candidate;
                }
            }

            return nearestVisible != null ? nearestVisible : nearest;
        }

        private static bool IsValidTarget(PlayerStateMachine player)
        {
            if (player == null) return false;

            var target = player.GetComponent<IAITarget>();
            return target != null && !target.IsDead;
        }

        /// <summary>Anything but patrolling counts as pursuing: chasing, attacking, searching a hiding place.</summary>
        private bool IsPursuing()
        {
            var state = m_machine.CurrentState;
            return state.HasValue && !(state.Value.StateData.StateAsset is ZombiePatrolState);
        }

        /// <summary>The same test UHFPS's sight check makes: range, field of view, nothing solid in between.</summary>
        private bool CanSee(PlayerStateMachine player, float distance)
        {
            var target = player.GetComponent<IAITarget>();
            if (target == null || target.IsFullyHidden || target.IsInvisibleTo(m_machine.NPCType)) return false;
            if (distance > m_machine.SightsDistance) return false;

            var eyes = EyesOf(player);
            var head = m_machine.HeadBone != null ? m_machine.HeadBone.position : transform.position + Vector3.up * m_fallbackEyeHeight;

            var toPlayer = eyes - head;
            toPlayer.y = 0f;
            if (Vector3.Angle(transform.forward, toPlayer) > m_machine.SightsFOV * 0.5f) return false;

            return !Physics.Linecast(head, eyes, m_machine.SightsMask, QueryTriggerInteraction.Collide);
        }

        private Vector3 EyesOf(PlayerStateMachine player)
        {
            var manager = player.GetComponent<PlayerManager>();
            return manager != null && manager.CameraHolder != null
                ? manager.CameraHolder.position
                : player.transform.position + Vector3.up * m_fallbackEyeHeight;
        }

        // ---- Health ----------------------------------------------------------------------------------

        private void PublishHealth()
        {
            if (m_health == null) return;

            var health = m_health.EntityHealth;
            if (m_networkHealth.Value != health) m_networkHealth.Value = health;
        }

        public bool TryRelayDamage(int damage)
        {
            // The server applies damage itself; a replay of the server's damage must apply locally, not bounce back.
            if (!IsSpawned || IsServer || m_replaying) return false;

            DamageRpc(damage);
            return true;
        }

        public bool TryRelayKill()
        {
            if (!IsSpawned || IsServer || m_replaying) return false;

            KillRpc();
            return true;
        }

        [Rpc(SendTo.Server)]
        private void DamageRpc(int damage)
        {
            if (damage > 0 && m_health != null) m_health.ApplyDamage(damage);
        }

        [Rpc(SendTo.Server)]
        private void KillRpc()
        {
            if (m_health != null) m_health.ApplyDamageMax();
        }

        private void HandleHealthChanged(int previous, int current) => ReconcileHealth(current);

        /// <summary>
        /// Brings this client's copy to the server's health by replaying the drop as ordinary damage.
        /// </summary>
        /// <remarks>
        /// Replaying through ApplyDamage rather than setting the value keeps UHFPS's side effects — damage sounds,
        /// OnTakeDamage, and at zero the ragdoll and death events. Measured against local health rather than the
        /// previous network value, so a missed update is made up rather than lost.
        /// </remarks>
        private void ReconcileHealth(int serverHealth)
        {
            if (m_health == null) return;

            var local = m_health.EntityHealth;
            if (serverHealth < local)
            {
                m_replaying = true;
                try
                {
                    m_health.ApplyDamage(local - serverHealth);
                }
                finally
                {
                    m_replaying = false;
                }
            }
            else if (serverHealth > local)
            {
                m_health.EntityHealth = serverHealth;
            }
        }
    }
}
