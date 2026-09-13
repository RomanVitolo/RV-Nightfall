using System;
using System.Collections.Generic;
using Modules.Multiplayer.Bridge;
using Modules.Multiplayer.Bridge.World;
using Newtonsoft.Json.Linq;
using UHFPS.Runtime;
using Unity.Netcode;
using UnityEngine;

namespace QuietVillage.Survival
{
    /// <summary>Where a level is in its day.</summary>
    public enum SurvivalPhase : byte
    {
        /// <summary>Explore, gather, barricade. Nothing hunts.</summary>
        Day,

        /// <summary>Creatures hunt until dawn.</summary>
        Night,

        /// <summary>The night was survived. Creatures are gone; the run is won.</summary>
        Dawn
    }

    /// <summary>
    /// Runs one level's day and night for the whole room: the clock, the barricades and the creatures.
    /// </summary>
    /// <remarks>
    /// Authority: server. The clock, barricade health and creatures are shared outcomes, so the host decides them
    /// and every client reads the result. A client only ever asks, as when building a barricade, and spends its own
    /// inventory once the host has agreed; inventories stay owner-run, as everywhere else in the project.
    ///
    /// The clock replicates as a phase and the server time it ends at, rather than a countdown written every frame,
    /// so it costs nothing between phase changes and a late or lagging client still shows the right time.
    ///
    /// Barricade health is one list indexed by each barricade's authored <see cref="Barricade.Order"/>, the same on
    /// every machine, so there is a single network object per level instead of one per barricade.
    ///
    /// One per level, placed in the scene; the greybox builder adds it.
    ///
    /// Joins the co-op save as an <see cref="IWorldSaveParticipant"/>: phase, time left and barricade health. Without it,
    /// a game saved at night resumed at daybreak with barricades down but the scrap spent on them still gone. Creatures
    /// are not saved; a night resumes with a fresh set at the spawn points.
    /// </remarks>
    [RequireComponent(typeof(NetworkObject))]
    public class SurvivalDirector : NetworkBehaviour, IWorldSaveParticipant
    {
        [Header("Clock")]
        [Tooltip("Seconds of daylight before night falls.")]
        [SerializeField] private float m_dayDuration = 480f;

        [Tooltip("Seconds of night the players must survive.")]
        [SerializeField] private float m_nightDuration = 300f;

        [Header("Barricades")]
        [Tooltip("Inventory item spent on a barricade. Scrap stands in until the game has wood.")]
        [SerializeField] private ItemGuid m_buildItem;

        [Tooltip("Health of a fully built barricade.")]
        [SerializeField] private int m_barricadeMaxHealth = 100;

        [Tooltip("Health one item adds, so a barricade from nothing costs max / this items.")]
        [SerializeField] private int m_healthPerItem = 50;

        [Header("Creatures")]
        [Tooltip("Networked creature prefab, registered in the network prefab list.")]
        [SerializeField] private GameObject m_creaturePrefab;

        [Tooltip("Where creatures come from when night falls, away from the shelter.")]
        [SerializeField] private Transform[] m_creatureSpawns = Array.Empty<Transform>();

        [Tooltip("Creatures every night, whatever the room size.")]
        [SerializeField] private int m_creaturesBase = 1;

        [Tooltip("More creatures for each living player.")]
        [SerializeField] private int m_creaturesPerPlayer = 1;

        private const string NightFallsHint = "Night has fallen. Get inside and hold the barricades.";
        private const string DawnHint = "Dawn. You survived the night.";
        private const string BarricadeBrokenHint = "A barricade has broken!";

        private readonly NetworkVariable<SurvivalPhase> m_phase =
            new(SurvivalPhase.Day, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

        private readonly NetworkVariable<double> m_phaseEndsAt =
            new(0d, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

        private readonly NetworkList<int> m_barricadeHealth =
            new(null, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

        private WorldSync m_world;
        private readonly List<Barricade> m_barricades = new();
        private readonly List<NetworkObject> m_creatures = new();
        private int[] m_shownHealth = Array.Empty<int>();

        /// <summary>The director of the loaded level, or <c>null</c> in a level without one.</summary>
        public static SurvivalDirector Active { get; private set; }

        /// <summary>Raised on every client when the phase changes.</summary>
        public event Action<SurvivalPhase> PhaseChanged;

        public SurvivalPhase Phase => m_phase.Value;

        public int BarricadeMaxHealth => m_barricadeMaxHealth;

        /// <summary>The inventory item barricades consume.</summary>
        public string BuildItemGuid => m_buildItem;

        public IReadOnlyList<Barricade> Barricades => m_barricades;

        /// <summary>Seconds left in the current phase, as this client estimates server time. Zero at dawn.</summary>
        public float SecondsRemaining
        {
            get
            {
                if (!IsSpawned || m_phase.Value == SurvivalPhase.Dawn) return 0f;

                return Mathf.Max(0f, (float)(m_phaseEndsAt.Value - NetworkManager.ServerTime.Time));
            }
        }

        /// <summary>Length of the current phase in seconds. Zero at dawn, which does not end.</summary>
        public float PhaseDuration => m_phase.Value switch
        {
            SurvivalPhase.Day => m_dayDuration,
            SurvivalPhase.Night => m_nightDuration,
            _ => 0f
        };

        private void Awake()
        {
            CollectBarricades();
        }

        public override void OnNetworkSpawn()
        {
            Active = this;

            m_phase.OnValueChanged += HandlePhaseChanged;
            m_barricadeHealth.OnListChanged += HandleBarricadeHealthChanged;

            if (IsServer)
            {
                m_barricadeHealth.Clear();
                foreach (var _ in m_barricades) m_barricadeHealth.Add(0);

                m_world = FindAnyObjectByType<WorldSync>();
                if (m_world != null) m_world.RegisterSaveParticipant(this);

                if (WorldSync.TryGetResumedState(SaveKey, out var saved)) RestoreSaveState(saved);
                else BeginPhase(SurvivalPhase.Day);
            }

            m_shownHealth = new int[m_barricades.Count];
            ShowBarricades(announceBreaks: false);

            // Presentation belongs to whoever is looking, so every client adds its own.
            if (!TryGetComponent<SurvivalHud>(out _)) gameObject.AddComponent<SurvivalHud>();
        }

        public override void OnNetworkDespawn()
        {
            m_phase.OnValueChanged -= HandlePhaseChanged;
            m_barricadeHealth.OnListChanged -= HandleBarricadeHealthChanged;

            if (m_world != null) m_world.UnregisterSaveParticipant(this);
            m_world = null;

            if (ReferenceEquals(Active, this)) Active = null;
        }

        public override void OnDestroy()
        {
            if (ReferenceEquals(Active, this)) Active = null;
            base.OnDestroy();
        }

        private void Update()
        {
            if (!IsServer || !IsSpawned || m_phase.Value == SurvivalPhase.Dawn) return;
            if (NetworkManager.ServerTime.Time < m_phaseEndsAt.Value) return;

            BeginPhase(m_phase.Value == SurvivalPhase.Day ? SurvivalPhase.Night : SurvivalPhase.Dawn);
        }

        // ---- Clock (server) --------------------------------------------------------------------------

        /// <param name="remaining">Seconds left in the phase, when resuming part-way through; its full length otherwise.</param>
        private void BeginPhase(SurvivalPhase phase, float? remaining = null)
        {
            var duration = phase switch
            {
                SurvivalPhase.Day => m_dayDuration,
                SurvivalPhase.Night => m_nightDuration,
                _ => 0f
            };

            if (remaining.HasValue) duration = Mathf.Clamp(remaining.Value, 0f, duration);

            m_phaseEndsAt.Value = NetworkManager.ServerTime.Time + duration;
            m_phase.Value = phase;

            if (phase == SurvivalPhase.Night) SpawnCreatures();
            else DespawnCreatures();
        }

        /// <summary>Host only: ends the current phase now. For testing a night without waiting out the day.</summary>
        public void SkipPhase()
        {
            if (!IsServer || !IsSpawned || m_phase.Value == SurvivalPhase.Dawn) return;

            BeginPhase(m_phase.Value == SurvivalPhase.Day ? SurvivalPhase.Night : SurvivalPhase.Dawn);
        }

        // ---- Save (server) ---------------------------------------------------------------------------

        private const string SavePhase = "phase";
        private const string SaveRemaining = "remaining";
        private const string SaveBarricades = "barricades";

        public string SaveKey => "survival";

        public JToken CaptureSaveState()
        {
            if (!IsServer || !IsSpawned) return null;

            var barricades = new JArray();
            foreach (var health in m_barricadeHealth) barricades.Add(health);

            return new JObject
            {
                [SavePhase] = (int)m_phase.Value,
                [SaveRemaining] = SecondsRemaining,
                [SaveBarricades] = barricades
            };
        }

        /// <summary>Puts the level back where the save left it. Called once, as the director spawns on the host.</summary>
        /// <remarks>
        /// Barricades are matched by position in the list, which is their authored order. A level rebuilt with a
        /// different number of barricades restores the ones both share and leaves the rest down.
        /// </remarks>
        private void RestoreSaveState(JToken state)
        {
            if (state?[SaveBarricades] is JArray barricades)
            {
                var count = Mathf.Min(barricades.Count, m_barricadeHealth.Count);
                for (var i = 0; i < count; i++)
                {
                    m_barricadeHealth[i] = Mathf.Clamp((int?)barricades[i] ?? 0, 0, m_barricadeMaxHealth);
                }
            }

            var phaseValue = (int?)state?[SavePhase] ?? (int)SurvivalPhase.Day;
            var phase = Enum.IsDefined(typeof(SurvivalPhase), (byte)phaseValue) ? (SurvivalPhase)phaseValue : SurvivalPhase.Day;
            var remaining = (float?)state?[SaveRemaining];

            BeginPhase(phase, remaining);
        }

        // ---- Creatures (server) ----------------------------------------------------------------------

        private void SpawnCreatures()
        {
            if (m_creaturePrefab == null || m_creatureSpawns.Length == 0)
            {
                Debug.LogWarning($"{nameof(SurvivalDirector)}: no creature prefab or spawn points; the night is empty.", this);
                return;
            }

            var living = 0;
            foreach (var player in PlayerHealthSync.Spawned)
            {
                if (player != null && !player.IsDead) living++;
            }

            var count = Mathf.Max(0, m_creaturesBase + m_creaturesPerPlayer * living);
            for (var i = 0; i < count; i++)
            {
                var spawn = m_creatureSpawns[i % m_creatureSpawns.Length];
                if (spawn == null) continue;

                var creature = NetworkObject.InstantiateAndSpawn(m_creaturePrefab, NetworkManager,
                    destroyWithScene: true, position: spawn.position, rotation: spawn.rotation);

                if (creature != null) m_creatures.Add(creature);
            }
        }

        private void DespawnCreatures()
        {
            foreach (var creature in m_creatures)
            {
                if (creature != null && creature.IsSpawned) creature.Despawn();
            }

            m_creatures.Clear();
        }

        // ---- Barricades ------------------------------------------------------------------------------

        /// <summary>
        /// Finds the level's barricades in authored order, which every client shares.
        /// </summary>
        private void CollectBarricades()
        {
            m_barricades.Clear();
            m_barricades.AddRange(FindObjectsByType<Barricade>(FindObjectsInactive.Include, FindObjectsSortMode.None));
            m_barricades.Sort((a, b) => a.Order.CompareTo(b.Order));

            for (var i = 0; i < m_barricades.Count; i++)
            {
                if (i > 0 && m_barricades[i].Order == m_barricades[i - 1].Order)
                    Debug.LogError($"{nameof(SurvivalDirector)}: barricades '{m_barricades[i - 1].name}' and " +
                                   $"'{m_barricades[i].name}' share order {m_barricades[i].Order}; clients may disagree " +
                                   "about which is which. Re-run the greybox builder or renumber them.", this);

                m_barricades[i].Bind(this, i);
            }
        }

        /// <summary>Owner client: asks the host to add one item's worth of health to a barricade.</summary>
        internal void RequestBuild(Barricade barricade)
        {
            if (!IsSpawned || barricade == null) return;

            BuildRpc(barricade.Index);
        }

        [Rpc(SendTo.Server)]
        private void BuildRpc(int index, RpcParams rpcParams = default)
        {
            var sender = rpcParams.Receive.SenderClientId;
            if (index < 0 || index >= m_barricadeHealth.Count) return;

            var health = m_barricadeHealth[index];
            var accepted = health < m_barricadeMaxHealth;
            if (accepted) m_barricadeHealth[index] = Mathf.Min(m_barricadeMaxHealth, health + m_healthPerItem);

            BuildResultRpc(accepted, RpcTarget.Single(sender, RpcTargetUse.Temp));
        }

        /// <summary>
        /// Tells the builder whether their item was used.
        /// </summary>
        /// <remarks>
        /// The item is spent only after the host accepts, so two players repairing the same full barricade at once
        /// do not both lose one. Inventories are owner-run, so the builder's own client removes it.
        /// </remarks>
        [Rpc(SendTo.SpecifiedInParams)]
        private void BuildResultRpc(bool accepted, RpcParams rpcParams = default)
        {
            if (!accepted) return;

            var inventory = LocalPlayerContext.Inventory;
            if (inventory != null) inventory.RemoveItem(BuildItemGuid, 1);
        }

        /// <summary>Server only: damages a barricade, e.g. from a creature's attack.</summary>
        public void DamageBarricade(int index, int damage)
        {
            if (!IsServer || damage <= 0 || index < 0 || index >= m_barricadeHealth.Count) return;

            var health = m_barricadeHealth[index];
            if (health <= 0) return;

            m_barricadeHealth[index] = Mathf.Max(0, health - damage);
        }

        /// <summary>The standing barricade nearest a position, or <c>null</c> if every one is down.</summary>
        public Barricade NearestStandingBarricade(Vector3 position)
        {
            Barricade nearest = null;
            var nearestDistance = float.MaxValue;

            foreach (var barricade in m_barricades)
            {
                if (barricade == null || !barricade.IsStanding) continue;

                var distance = Vector3.SqrMagnitude(barricade.AttackPoint - position);
                if (distance >= nearestDistance) continue;

                nearestDistance = distance;
                nearest = barricade;
            }

            return nearest;
        }

        private void HandleBarricadeHealthChanged(NetworkListEvent<int> change) => ShowBarricades(announceBreaks: true);

        private void ShowBarricades(bool announceBreaks)
        {
            if (m_shownHealth.Length != m_barricades.Count) m_shownHealth = new int[m_barricades.Count];

            var anyBroke = false;
            for (var i = 0; i < m_barricades.Count; i++)
            {
                var health = i < m_barricadeHealth.Count ? m_barricadeHealth[i] : 0;
                if (announceBreaks && m_shownHealth[i] > 0 && health == 0) anyBroke = true;

                m_shownHealth[i] = health;
                m_barricades[i].ShowHealth(health, m_barricadeMaxHealth);
            }

            if (anyBroke) ShowHint(BarricadeBrokenHint);
        }

        // ---- Presentation ----------------------------------------------------------------------------

        private void HandlePhaseChanged(SurvivalPhase previous, SurvivalPhase current)
        {
            if (current == SurvivalPhase.Night) ShowHint(NightFallsHint);
            else if (current == SurvivalPhase.Dawn) ShowHint(DawnHint);

            PhaseChanged?.Invoke(current);
        }

        private static void ShowHint(string message)
        {
            var gameManager = LocalPlayerContext.GameManager;
            if (gameManager != null) gameManager.ShowHintMessage(message, 4f);
        }
    }
}
