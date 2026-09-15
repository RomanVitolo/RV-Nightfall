using System.Collections.Generic;
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
    /// an unusable entry, it keeps the placeholder capsule. The body also brings its behaviour
    /// (<see cref="CreatureBehaviour"/>): a Brute is slow and wrecks barricades, a Stalker is fast and hunts whoever is
    /// alone, a Screamer calls the others to what it has found.
    ///
    /// It cannot be killed: the night is about holding out, not fighting. Players can still buy time. The host can stun
    /// it (stops dead), slow it and repel it (backs away), from holy water, the chapel bell, traps and weapon hits
    /// (<see cref="CreatureHitbox"/>, which staggers it briefly, with a moment's immunity so fast fire cannot pin it).
    /// A stun replicates as the server time it ends, so every client shows the flinch without a message. At dawn it
    /// dies where it stands and is removed once the death has played.
    /// </remarks>
    [RequireComponent(typeof(NetworkObject), typeof(NavMeshAgent))]
    public class NightCreature : NetworkBehaviour
    {
        [SerializeField] private NavMeshAgent m_agent;

        [Tooltip("Bodies the director can dress this creature in. Empty keeps the placeholder capsule.")]
        [SerializeField] private CreatureCatalog m_catalog;

        [Tooltip("What it sounds like, unless its body brings its own. Empty is silent.")]
        [SerializeField] private CreatureSounds m_sounds;

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

        [Header("Being hit")]
        [Tooltip("Seconds a weapon hit stops it.")]
        [SerializeField] private float m_staggerSeconds = 1f;

        [Tooltip("Seconds after a stagger during which weapon hits do not stagger it again.")]
        [SerializeField] private float m_staggerImmunity = 2.5f;

        [Header("Behaviours")]
        [Tooltip("How far (m) a Screamer's call carries to the other creatures.")]
        [SerializeField] private float m_screamCallRadius = 20f;

        [Tooltip("Seconds called creatures go after the Screamer's find before choosing for themselves again.")]
        [SerializeField] private float m_screamCallSeconds = 8f;

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

        // Server time a stun ends. Every client reads it to show the flinch; only the host acts on it.
        private readonly NetworkVariable<double> m_stunnedUntil =
            new(0d, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

        private static readonly List<NightCreature> s_all = new();

        /// <summary>Every creature spawned in the level, on every client.</summary>
        public static IReadOnlyList<NightCreature> All => s_all;

        public CreatureCatalog Catalog => m_catalog;

        /// <summary>The sounds this creature makes: its body's own, or the shared set.</summary>
        public CreatureSounds Sounds => Body != null && Body.Sounds != null ? Body.Sounds : m_sounds;

        private CreatureVoice m_voice;

        // Server: whether it was after a player at its last thought, so spotting one is noticed once, not every think.
        private bool m_hadPlayerTarget;
        private float m_nextSpottedAt;
        private const float SpottedRpcInterval = 6f;

        // Server: effects players put on it.
        private float m_baseSpeed;
        private float m_slowUntil;
        private float m_slowFactor = 1f;
        private float m_staggerImmuneUntil;
        private float m_repelUntil;
        private PlayerHealthSync m_calledTarget;
        private float m_calledUntil;

        /// <summary>The body it wears, or <c>null</c> for the placeholder.</summary>
        public CreatureCatalog.Body Body => m_catalog != null ? m_catalog.At(m_bodyIndex) : null;

        /// <summary>How this creature hunts, from its body.</summary>
        public CreatureBehaviour Behaviour => Body != null ? Body.Behaviour : CreatureBehaviour.Standard;

        /// <summary>True once dawn has come for it; it no longer hunts and is about to be removed.</summary>
        public bool IsDying => m_dying.Value;

        /// <summary>True while stunned, as this client estimates server time.</summary>
        public bool IsStunned => IsSpawned && NetworkManager.ServerTime.Time < m_stunnedUntil.Value;

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
            if (!s_all.Contains(this)) s_all.Add(this);

            BuildBody();
            BuildVoice();
            AddHitboxes();

            m_dying.OnValueChanged += HandleDyingChanged;
            m_stunnedUntil.OnValueChanged += HandleStunned;
            if (m_dying.Value) HandleDyingChanged(false, true);

            if (m_agent == null) return;

            // The agent would pull this copy along its own path, against the position NetworkTransform delivers.
            if (!IsServer)
            {
                m_agent.enabled = false;
                return;
            }

            var body = Body;
            m_baseSpeed = (body != null ? body.MoveSpeed : m_agent.speed) * BehaviourSpeed;

            // The room's settings, which only the host applies: it alone paths and hits.
            var director = SurvivalDirector.Active;
            if (director != null) m_baseSpeed *= director.Settings.CreatureSpeed;

            m_agent.speed = m_baseSpeed;

            // Netcode places a spawned object after its components wake, and the agent has already bound itself to
            // the NavMesh nearest the origin by then, which can be inside the shelter. Move it to where it spawned.
            m_agent.Warp(transform.position);

            // Held back while the entrance plays, so a creature rising from the ground does not glide off mid-rise.
            if (body != null && body.HasEntrance) m_nextThinkAt = Time.time + EntranceHold;
        }

        public override void OnNetworkDespawn()
        {
            s_all.Remove(this);
            m_dying.OnValueChanged -= HandleDyingChanged;
            m_stunnedUntil.OnValueChanged -= HandleStunned;
        }

        public override void OnDestroy()
        {
            s_all.Remove(this);
            base.OnDestroy();
        }

        private const float EntranceHold = 1.5f;

        private void BuildVoice()
        {
            var sounds = Sounds;
            if (sounds == null) return;

            m_voice = GetComponent<CreatureVoice>();
            if (m_voice == null) m_voice = gameObject.AddComponent<CreatureVoice>();

            m_voice.Configure(sounds);

            // Already dying when this client joined in: no scream on its way out.
            if (!m_dying.Value) m_voice.PlayRise();
        }

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

        /// <summary>Lets UHFPS's weapons find this creature on whichever collider they hit.</summary>
        /// <remarks>
        /// Weapons look for a damage receiver on the collider's own object, and the collider sits on a child of the
        /// creature, not on this root.
        /// </remarks>
        private void AddHitboxes()
        {
            foreach (var collider in GetComponentsInChildren<Collider>(true))
            {
                if (collider == null || collider.isTrigger || collider.GetComponent<CreatureHitbox>() != null) continue;
                collider.gameObject.AddComponent<CreatureHitbox>().Bind(this);
            }
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
            if (current && m_voice != null) m_voice.PlayDeath();
        }

        private void HandleStunned(double previous, double current)
        {
            if (current > previous && m_voice != null) m_voice.PlayHurt();
        }

        // ---- Effects (server) ------------------------------------------------------------------------

        /// <summary>Server: stops it where it stands. A Brute shrugs off half.</summary>
        public void Stun(float seconds)
        {
            if (!IsServer || !IsSpawned || m_dying.Value || seconds <= 0f) return;

            if (Behaviour == CreatureBehaviour.Brute) seconds *= 0.5f;

            var until = NetworkManager.ServerTime.Time + seconds;
            if (until > m_stunnedUntil.Value) m_stunnedUntil.Value = until;

            // A stun cancels a swing in progress, so the blow never lands (LandAfter checks it too).
            m_attackEndsAt = 0f;
            if (m_agent != null && m_agent.isOnNavMesh) m_agent.isStopped = true;
        }

        /// <summary>Server: slows it to a share of its speed for a while.</summary>
        public void Slow(float factor, float seconds)
        {
            if (!IsServer || seconds <= 0f) return;

            m_slowFactor = Mathf.Clamp(factor, 0.1f, 1f);
            m_slowUntil = Mathf.Max(m_slowUntil, Time.time + seconds);
        }

        /// <summary>Server: makes it back away from <paramref name="from"/> and ignore everyone for a while.</summary>
        public void Repel(Vector3 from, float seconds)
        {
            if (!IsServer || !IsSpawned || m_dying.Value || m_agent == null || !m_agent.isOnNavMesh) return;

            var away = transform.position - from;
            away.y = 0f;
            if (away.sqrMagnitude < 0.01f) away = transform.forward;

            var target = transform.position + away.normalized * RepelDistance;
            if (NavMesh.SamplePosition(target, out var hit, RepelDistance, NavMesh.AllAreas)) m_agent.SetDestination(hit.position);

            m_repelUntil = Mathf.Max(m_repelUntil, Time.time + seconds);
        }

        private const float RepelDistance = 6f;

        /// <summary>Server: sends it after a player another creature found, for a while.</summary>
        public void CallTo(PlayerHealthSync target, float seconds)
        {
            if (!IsServer || target == null) return;

            m_calledTarget = target;
            m_calledUntil = Time.time + seconds;
        }

        /// <summary>Any client: a player's weapon hit it. Asks the host to stagger it.</summary>
        internal void RequestStagger()
        {
            if (IsSpawned && !m_dying.Value) RequestStaggerRpc();
        }

        [Rpc(SendTo.Server, InvokePermission = RpcInvokePermission.Everyone)]
        private void RequestStaggerRpc()
        {
            if (m_dying.Value || Time.time < m_staggerImmuneUntil) return;

            m_staggerImmuneUntil = Time.time + m_staggerImmunity;
            Stun(m_staggerSeconds);
        }

        // ---- Hunting (server) ------------------------------------------------------------------------

        private void Update()
        {
            // Every client: the body flinches for as long as the stun lasts.
            if (m_body != null) m_body.SetStunned(IsStunned);

            if (!IsServer || !IsSpawned || m_dying.Value || m_agent == null) return;

            if (IsStunned)
            {
                if (m_agent.isOnNavMesh && !m_agent.isStopped) m_agent.isStopped = true;
                return;
            }

            if (Time.time < m_nextThinkAt) return;

            // Standing its ground mid-swing: the blow is timed to the clip, and sliding after a player looks like skating.
            if (Time.time < m_attackEndsAt) return;
            if (m_agent.isOnNavMesh && m_agent.isStopped) m_agent.isStopped = false;

            m_nextThinkAt = Time.time + m_thinkInterval;
            m_agent.speed = Time.time < m_slowUntil ? m_baseSpeed * m_slowFactor : m_baseSpeed;

            if (!m_agent.isOnNavMesh)
            {
                ReturnToNavMesh();
                return;
            }

            // Backing away from holy water: it keeps going where it was sent.
            if (Time.time < m_repelUntil) return;

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

        private const float LightRetreatHold = 1.5f;

        // A Stalker slips back to the edge of the light and is ready again sooner, circling for a way round.
        private float LightHold => Behaviour == CreatureBehaviour.Stalker ? 0.5f : LightRetreatHold;

        private void Think()
        {
            var director = SurvivalDirector.Active;

            // Caught in light: back out to the dark and do nothing else, then wait a moment at the edge rather than
            // stepping straight back in after whoever it wants.
            if (director != null && director.IsLit(transform.position))
            {
                m_agent.SetDestination(director.EdgeOfLight(transform.position));
                m_nextThinkAt = Time.time + LightHold;
                return;
            }

            var hasPlayer = TryFindReachablePlayer(out var player, director);
            // Rate-limited here too: a path that flickers between complete and not would otherwise send one every think.
            if (hasPlayer && !m_hadPlayerTarget && Time.time >= m_nextSpottedAt)
            {
                PlaySpottedRpc();
                m_nextSpottedAt = Time.time + SpottedRpcInterval;

                if (Behaviour == CreatureBehaviour.Screamer && director != null)
                    director.CallCreatures(this, player, m_screamCallRadius, m_screamCallSeconds);
            }

            m_hadPlayerTarget = hasPlayer;

            if (hasPlayer)
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

                        player.ApplyServerDamage(ScaledDamage(m_playerDamage * PlayerDamageShare));
                    }, m_playerAttackInterval);
                }

                return;
            }

            var barricade = director != null ? director.NearestStandingBarricade(transform.position, skipLit: true) : null;
            if (barricade == null)
            {
                // Every barricade it could break is lit: prowl at the edge of the light nearest one, ready when it fails.
                var litBarricade = director != null ? director.NearestStandingBarricade(transform.position) : null;
                if (litBarricade != null)
                {
                    m_agent.SetDestination(director.EdgeOfLight(litBarricade.AttackPoint));
                    return;
                }

                // Nobody alive, or nobody reachable and nothing left to break.
                if (m_agent.hasPath) m_agent.ResetPath();
                return;
            }

            m_agent.SetDestination(barricade.AttackPoint);

            if (Vector3.Distance(transform.position, barricade.AttackPoint) <= m_barricadeReach && CanAttack())
            {
                FaceTowards(barricade.transform.position);
                Attack(() => director.DamageBarricade(barricade.Index, ScaledDamage(m_barricadeDamage * BarricadeDamageShare)),
                    m_barricadeAttackInterval);
            }
        }

        // ---- Behaviours ------------------------------------------------------------------------------

        private float BehaviourSpeed => Behaviour switch
        {
            CreatureBehaviour.Brute => 0.75f,
            CreatureBehaviour.Stalker => 1.25f,
            _ => 1f
        };

        private float BarricadeDamageShare => Behaviour == CreatureBehaviour.Brute ? 3f : 1f;

        private float PlayerDamageShare => Behaviour switch
        {
            CreatureBehaviour.Brute => 1.25f,
            CreatureBehaviour.Screamer => 0.7f,
            _ => 1f
        };

        /// <summary>
        /// The player it should go for with a complete path, leaving that path in <see cref="m_path"/>.
        /// </summary>
        /// <remarks>
        /// Candidates are tried best first, so the cheap ordering decides which expensive path test comes first, and the
        /// first reachable player wins. A player behind standing barricades has only a partial path and is skipped.
        ///
        /// Order: a player a Screamer called it to; then players on their feet before downed ones, whatever the distance
        /// (a creature that has just put someone down turns on whoever could revive them, and only comes back to finish
        /// the fallen when nobody else is in reach); then the nearest, or for a Stalker the most alone.
        /// </remarks>
        private bool TryFindReachablePlayer(out PlayerHealthSync reachable, SurvivalDirector director)
        {
            reachable = null;

            m_candidates.Clear();
            foreach (var player in PlayerHealthSync.Spawned)
            {
                // A player standing in light is out of bounds for it, however close.
                if (player != null && !player.IsDead && (director == null || !director.IsLit(player.transform.position)))
                    m_candidates.Add(player);
            }

            if (Time.time >= m_calledUntil) m_calledTarget = null;

            var origin = transform.position;
            var stalker = Behaviour == CreatureBehaviour.Stalker;
            m_candidates.Sort((a, b) =>
            {
                var aCalled = a == m_calledTarget;
                var bCalled = b == m_calledTarget;
                if (aCalled != bCalled) return aCalled ? -1 : 1;
                if (a.IsDowned != b.IsDowned) return a.IsDowned ? 1 : -1;

                return stalker
                    ? Isolation(b).CompareTo(Isolation(a))
                    : Vector3.SqrMagnitude(a.transform.position - origin).CompareTo(Vector3.SqrMagnitude(b.transform.position - origin));
            });

            foreach (var candidate in m_candidates)
            {
                if (!NavMesh.CalculatePath(origin, candidate.transform.position, NavMesh.AllAreas, m_path)) continue;
                if (m_path.status != NavMeshPathStatus.PathComplete) continue;

                reachable = candidate;
                return true;
            }

            return false;
        }

        /// <summary>How far a player is from their nearest teammate on their feet; far for someone on their own.</summary>
        private static float Isolation(PlayerHealthSync player)
        {
            var nearest = float.MaxValue;
            foreach (var other in PlayerHealthSync.Spawned)
            {
                if (other == null || other == player || !other.IsStanding) continue;
                nearest = Mathf.Min(nearest, Vector3.SqrMagnitude(other.transform.position - player.transform.position));
            }

            return nearest;
        }

        private readonly List<PlayerHealthSync> m_candidates = new();

        private bool CanAttack() => Time.time >= m_nextAttackAt;

        /// <summary>Server: a blow's damage after the room's settings, never below one so a hit always counts.</summary>
        private static int ScaledDamage(float damage)
        {
            var director = SurvivalDirector.Active;
            var scale = director != null ? director.Settings.CreatureDamage : 1f;
            return Mathf.Max(1, Mathf.RoundToInt(damage * scale));
        }

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

            // Dawn can come mid-swing, or a stun; a dying or stunned creature hits nothing.
            if (IsSpawned && !m_dying.Value && !IsStunned) hit();
        }

        /// <summary>Plays a swing on every copy of this creature, the host's included.</summary>
        [Rpc(SendTo.Everyone)]
        private void PlayAttackRpc(byte variant)
        {
            if (m_body != null) m_body.PlayAttack(variant);
            if (m_voice != null) m_voice.PlayAttack();
        }

        /// <summary>It has just found a player it can reach: a scream everyone hears, if it has not screamed lately.</summary>
        [Rpc(SendTo.Everyone)]
        private void PlaySpottedRpc()
        {
            if (m_voice != null) m_voice.PlaySpotted();
        }

        private void FaceTowards(Vector3 point)
        {
            var direction = point - transform.position;
            direction.y = 0f;
            if (direction.sqrMagnitude > 0.001f) transform.rotation = Quaternion.LookRotation(direction);
        }
    }
}
