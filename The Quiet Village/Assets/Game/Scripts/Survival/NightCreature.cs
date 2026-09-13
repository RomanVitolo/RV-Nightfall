using Modules.Multiplayer.Bridge;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.AI;

namespace QuietVillage.Survival
{
    /// <summary>
    /// The night's hunter: goes for the nearest player it can reach, and tears through barricades to reach the rest.
    /// </summary>
    /// <remarks>
    /// Authority: server. The host paths and attacks; clients see its position through the NetworkTransform beside
    /// this and run no agent of their own, like the project's other NPCs.
    ///
    /// Deliberately not a UHFPS NPC state machine. That AI is built around patrols and sight cones, and the night
    /// needs one rule the NavMesh already answers: if a path to the player is complete, chase; if standing
    /// barricades cut it short, break the nearest one. Players outside are therefore always the first to be hunted,
    /// which is what makes leaving the shelter at night a real risk.
    ///
    /// A placeholder body and no sounds yet. It cannot be killed: the night is about holding out, not fighting.
    /// </remarks>
    [RequireComponent(typeof(NetworkObject), typeof(NavMeshAgent))]
    public class NightCreature : NetworkBehaviour
    {
        [SerializeField] private NavMeshAgent m_agent;

        [Tooltip("Seconds between choosing what to go for.")]
        [SerializeField] private float m_thinkInterval = 0.25f;

        [Tooltip("Reach (m) for hitting a player.")]
        [SerializeField] private float m_playerReach = 1.7f;

        [SerializeField] private int m_playerDamage = 25;

        [Tooltip("Seconds between hits on a player.")]
        [SerializeField] private float m_playerAttackInterval = 1.2f;

        [Tooltip("Reach (m) from a barricade's attack point.")]
        [SerializeField] private float m_barricadeReach = 1.2f;

        [SerializeField] private int m_barricadeDamage = 10;

        [Tooltip("Seconds between hits on a barricade. With 100 health and 10 damage, 1.5 s holds for 15 s.")]
        [SerializeField] private float m_barricadeAttackInterval = 1.5f;

        private NavMeshPath m_path;
        private float m_nextThinkAt;
        private float m_nextAttackAt;

        private void Awake()
        {
            if (m_agent == null) m_agent = GetComponent<NavMeshAgent>();
            m_path = new NavMeshPath();
            m_wallMask = LayerMask.GetMask("Default", "Interact", "Ground");
        }

        public override void OnNetworkSpawn()
        {
            if (m_agent == null) return;

            // The agent would pull this copy along its own path, against the position NetworkTransform delivers.
            if (!IsServer)
            {
                m_agent.enabled = false;
                return;
            }

            // Netcode places a spawned object after its components wake, and the agent has already bound itself to
            // the NavMesh nearest the origin by then, which can be inside the shelter. Move it to where it spawned.
            m_agent.Warp(transform.position);
        }

        private void Update()
        {
            if (!IsServer || !IsSpawned || m_agent == null || !m_agent.isOnNavMesh) return;
            if (Time.time < m_nextThinkAt) return;

            m_nextThinkAt = Time.time + m_thinkInterval;
            Think();
        }

        private void Think()
        {
            var director = SurvivalDirector.Active;

            if (TryFindReachablePlayer(out var player))
            {
                m_agent.SetPath(m_path);

                if (Vector3.Distance(transform.position, player.transform.position) <= m_playerReach
                    && CanAttack() && HasClearReach(player.transform.position))
                    Attack(() => player.ApplyServerDamage(m_playerDamage), m_playerAttackInterval);

                return;
            }

            var barricade = director != null ? director.NearestStandingBarricade(transform.position) : null;
            if (barricade == null)
            {
                // Nobody alive, or nobody reachable and nothing left to break.
                if (m_agent.hasPath) m_agent.ResetPath();
                return;
            }

            m_agent.SetDestination(barricade.AttackPoint);

            if (Vector3.Distance(transform.position, barricade.AttackPoint) <= m_barricadeReach && CanAttack())
            {
                FaceTowards(barricade.transform.position);
                Attack(() => director.DamageBarricade(barricade.Index, m_barricadeDamage), m_barricadeAttackInterval);
            }
        }

        /// <summary>
        /// The nearest living player with a complete path, leaving that path in <see cref="m_path"/>.
        /// </summary>
        /// <remarks>
        /// Nearest first by straight-line distance, so the cheap test orders the expensive one, and the first
        /// reachable player wins. A player behind standing barricades has only a partial path and is skipped.
        /// </remarks>
        private bool TryFindReachablePlayer(out PlayerHealthSync reachable)
        {
            reachable = null;

            m_candidates.Clear();
            foreach (var player in PlayerHealthSync.Spawned)
            {
                if (player != null && !player.IsDead) m_candidates.Add(player);
            }

            var origin = transform.position;
            m_candidates.Sort((a, b) => Vector3.SqrMagnitude(a.transform.position - origin)
                .CompareTo(Vector3.SqrMagnitude(b.transform.position - origin)));

            foreach (var candidate in m_candidates)
            {
                if (!NavMesh.CalculatePath(origin, candidate.transform.position, NavMesh.AllAreas, m_path)) continue;
                if (m_path.status != NavMeshPathStatus.PathComplete) continue;

                reachable = candidate;
                return true;
            }

            return false;
        }

        private readonly System.Collections.Generic.List<PlayerHealthSync> m_candidates = new();

        private bool CanAttack() => Time.time >= m_nextAttackAt;

        /// <summary>
        /// Nothing solid between this and the player, so a wall never passes a blow. Reachable by path is not enough:
        /// a player a step away behind a wall is reachable the long way round.
        /// </summary>
        private bool HasClearReach(Vector3 playerPosition)
        {
            var chest = Vector3.up * 1.2f;
            return !Physics.Linecast(transform.position + chest, playerPosition + chest, m_wallMask,
                QueryTriggerInteraction.Ignore);
        }

        // Level geometry, and barricades while standing. Not players, NPCs or this body.
        private int m_wallMask;

        private void Attack(System.Action hit, float interval)
        {
            m_nextAttackAt = Time.time + interval;
            hit();
        }

        private void FaceTowards(Vector3 point)
        {
            var direction = point - transform.position;
            direction.y = 0f;
            if (direction.sqrMagnitude > 0.001f) transform.rotation = Quaternion.LookRotation(direction);
        }
    }
}
