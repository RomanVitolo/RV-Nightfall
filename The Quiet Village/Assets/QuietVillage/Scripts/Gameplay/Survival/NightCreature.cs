using QuietVillage.Multiplayer.Bridge;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.AI;

namespace QuietVillage.Gameplay.Survival
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
    /// Its body comes from <see cref="CreatureCatalog"/>: the director picks one before spawning, and it travels inside the
    /// spawn (<see cref="OnSynchronize{T}"/>), so every client builds the right model at once. Without a catalog, or with
    /// an unusable entry, it keeps the placeholder capsule. No sounds yet. It cannot be killed: the night is about
    /// holding out, not fighting. At dawn it dies where it stands and is removed once the death has played.
    /// </remarks>
    [RequireComponent(typeof(NetworkObject), typeof(NavMeshAgent))]
    public class NightCreature : NetworkBehaviour
    {
        [SerializeField] private NavMeshAgent m_agent;

        [Tooltip("Bodies the director can dress this creature in. Empty keeps the placeholder capsule.")]
        [SerializeField] private CreatureCatalog m_catalog;

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
        private float m_attackEndsAt;
        private int m_attackCount;

        // Which catalog body this creature wears; fixed at spawn, -1 for the placeholder.
        private int m_bodyIndex = -1;
        private CreatureBody m_body;

        private readonly NetworkVariable<bool> m_dying =
            new(false, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

        public CreatureCatalog Catalog => m_catalog;

        /// <summary>The body it wears, or <c>null</c> for the placeholder.</summary>
        public CreatureCatalog.Body Body => m_catalog != null ? m_catalog.At(m_bodyIndex) : null;

        /// <summary>True once dawn has come for it; it no longer hunts and is about to be removed.</summary>
        public bool IsDying => m_dying.Value;

        private void Awake()
        {
            if (m_agent == null) m_agent = GetComponent<NavMeshAgent>();
            m_path = new NavMeshPath();
            m_wallMask = LayerMask.GetMask("Default", "Interact", "Ground");
        }

        /// <summary>Server, before spawning: picks the body every client will build.</summary>
        public void AssignBody(int bodyIndex)
        {
            if (IsSpawned)
            {
                Debug.LogWarning($"{nameof(NightCreature)}: a body can only be assigned before the creature spawns.", this);
                return;
            }

            m_bodyIndex = bodyIndex;
        }

        protected override void OnSynchronize<T>(ref BufferSerializer<T> serializer)
        {
            serializer.SerializeValue(ref m_bodyIndex);
        }

        public override void OnNetworkSpawn()
        {
            BuildBody();
            m_dying.OnValueChanged += HandleDyingChanged;
            if (m_dying.Value) HandleDyingChanged(false, true);

            if (m_agent == null) return;

            // The agent would pull this copy along its own path, against the position NetworkTransform delivers.
            if (!IsServer)
            {
                m_agent.enabled = false;
                return;
            }

            var body = Body;
            if (body != null) m_agent.speed = body.MoveSpeed;

            // Netcode places a spawned object after its components wake, and the agent has already bound itself to
            // the NavMesh nearest the origin by then, which can be inside the shelter. Move it to where it spawned.
            m_agent.Warp(transform.position);

            // Held back while the entrance plays, so a creature rising from the ground does not glide off mid-rise.
            if (body != null && body.HasEntrance) m_nextThinkAt = Time.time + EntranceHold;
        }

        public override void OnNetworkDespawn()
        {
            m_dying.OnValueChanged -= HandleDyingChanged;
        }

        private const float EntranceHold = 1.5f;

        private void BuildBody()
        {
            var body = Body;
            if (body == null) return;

            m_body = GetComponent<CreatureBody>();
            if (m_body == null) m_body = gameObject.AddComponent<CreatureBody>();

            if (!m_body.Build(body))
            {
                Debug.LogWarning($"{nameof(NightCreature)}: could not build body '{body.Id}'; showing the placeholder.", this);
                return;
            }

            m_body.PlayEntrance();
        }

        /// <summary>Server: ends this creature's night. It stops, plays its death, and is removed after it.</summary>
        public void DieAtDawn()
        {
            if (!IsServer || !IsSpawned || m_dying.Value) return;

            m_dying.Value = true;
            if (m_agent != null && m_agent.isOnNavMesh) m_agent.ResetPath();

            var body = Body;
            var delay = body != null ? body.DeathDuration : 0f;
            if (delay <= 0f) NetworkObject.Despawn();
            else StartCoroutine(DespawnAfter(delay));
        }

        private System.Collections.IEnumerator DespawnAfter(float seconds)
        {
            yield return new WaitForSeconds(seconds);

            if (IsSpawned) NetworkObject.Despawn();
        }

        private void HandleDyingChanged(bool previous, bool current)
        {
            if (m_body != null) m_body.SetDead(current);
        }

        private void Update()
        {
            if (!IsServer || !IsSpawned || m_dying.Value || m_agent == null || Time.time < m_nextThinkAt) return;

            // Standing its ground mid-swing: the blow is timed to the clip, and sliding after a player looks like skating.
            if (Time.time < m_attackEndsAt) return;
            if (m_agent.isOnNavMesh && m_agent.isStopped) m_agent.isStopped = false;

            m_nextThinkAt = Time.time + m_thinkInterval;

            if (!m_agent.isOnNavMesh)
            {
                ReturnToNavMesh();
                return;
            }

            Think();
        }

        /// <summary>
        /// Puts an agent that lost the NavMesh under it back on the nearest part of it.
        /// </summary>
        /// <remarks>
        /// A carve appearing beneath it, as when a barricade goes up in the opening it stands in, leaves an agent with
        /// no NavMesh. It then cannot path at all, and without this it would stand there until dawn. Builds are refused
        /// while an opening is occupied, so this is the safety net for whatever else carves or moves it.
        /// </remarks>
        private void ReturnToNavMesh()
        {
            if (NavMesh.SamplePosition(transform.position, out var hit, RecoverRadius, NavMesh.AllAreas))
                m_agent.Warp(hit.position);
        }

        private const float RecoverRadius = 4f;

        private void Think()
        {
            var director = SurvivalDirector.Active;

            if (TryFindReachablePlayer(out var player))
            {
                m_agent.SetPath(m_path);

                if (Vector3.Distance(transform.position, player.transform.position) <= m_playerReach
                    && CanAttack() && HasClearReach(player.transform.position))
                {
                    FaceTowards(player.transform.position);

                    // Checked again when the blow lands: a player who stepped out of reach or around a corner mid-swing
                    // was dodged, not hit.
                    Attack(() =>
                    {
                        if (player == null || player.IsDead) return;
                        if (Vector3.Distance(transform.position, player.transform.position) > m_playerReach * DodgeMargin) return;
                        if (!HasClearReach(player.transform.position)) return;

                        player.ApplyServerDamage(m_playerDamage);
                    }, m_playerAttackInterval);
                }

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

        // A little slack on reach when the blow lands, so a swing is not whiffed by a player merely shuffling in place.
        private const float DodgeMargin = 1.3f;

        /// <summary>Swings for everyone to see, and lands the blow when the body's clip says it connects.</summary>
        private void Attack(System.Action hit, float interval)
        {
            m_nextAttackAt = Time.time + interval;

            var body = Body;
            var hitDelay = body != null ? body.AttackHitDelay : 0f;
            var duration = body != null ? Mathf.Max(body.AttackDuration, hitDelay) : 0f;

            if (duration > 0f)
            {
                m_attackEndsAt = Time.time + duration;
                if (m_agent.isOnNavMesh) m_agent.isStopped = true;
            }

            // Cycled rather than random, so consecutive swings visibly differ.
            PlayAttackRpc((byte)(m_attackCount++ % CreatureBody.AttackVariants));

            if (hitDelay <= 0f) hit();
            else StartCoroutine(LandAfter(hit, hitDelay));
        }

        private System.Collections.IEnumerator LandAfter(System.Action hit, float seconds)
        {
            yield return new WaitForSeconds(seconds);

            // Dawn can come mid-swing; a dying creature hits nothing.
            if (IsSpawned && !m_dying.Value) hit();
        }

        /// <summary>Plays a swing on every copy of this creature, the host's included.</summary>
        [Rpc(SendTo.Everyone)]
        private void PlayAttackRpc(byte variant)
        {
            if (m_body != null) m_body.PlayAttack(variant);
        }

        private void FaceTowards(Vector3 point)
        {
            var direction = point - transform.position;
            direction.y = 0f;
            if (direction.sqrMagnitude > 0.001f) transform.rotation = Quaternion.LookRotation(direction);
        }
    }
}
