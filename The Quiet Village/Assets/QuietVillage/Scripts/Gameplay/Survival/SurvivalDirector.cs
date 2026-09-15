using System;
using System.Collections.Generic;
using QuietVillage.Multiplayer.Bridge;
using QuietVillage.Multiplayer.Bridge.World;
using Newtonsoft.Json.Linq;
using UHFPS.Runtime;
using Unity.Netcode;
using UnityEngine;

namespace QuietVillage.Gameplay.Survival
{
    /// <summary>Where a level is in its day.</summary>
    public enum SurvivalPhase : byte
    {
        /// <summary>Explore, gather, barricade. Nothing hunts.</summary>
        Day,

        /// <summary>Creatures hunt until dawn.</summary>
        Night,

        /// <summary>A night was survived and more are to come: a short breather while the creatures die off and the dead return.</summary>
        Dawn,

        /// <summary>The last night was survived. Creatures are gone; the run is won and the clock stops.</summary>
        Won
    }

    /// <summary>
    /// Runs one level's run for the whole room: its days and nights, the barricades and the creatures.
    /// </summary>
    /// <remarks>
    /// Authority: server. The clock, barricade health and creatures are shared outcomes, so the host decides them
    /// and every client reads the result. A client only ever asks, as when building a barricade, and spends its own
    /// inventory once the host has agreed; inventories stay owner-run, as everywhere else in the project.
    ///
    /// The clock replicates as a phase and the server time it ends at, rather than a countdown written every frame,
    /// so it costs nothing between phase changes and a late or lagging client still shows the right time.
    ///
    /// A run is several nights (the room's settings say how many): Day, Night, then a short Dawn before the next Day,
    /// until the last Night ends in Won. Each night brings more creatures. How a run ends, what dawn gives back, and
    /// the end-of-run numbers are in SurvivalDirector.Run.cs.
    ///
    /// Barricade health is one list indexed by each barricade's authored <see cref="Barricade.Order"/>, the same on
    /// every machine, so there is a single network object per level instead of one per barricade.
    ///
    /// One per level, placed in the scene; the greybox builder adds it.
    ///
    /// Also holds the game's loot seed (SurvivalDirector.Loot.cs) and the shelter generator (SurvivalDirector.Power.cs), so
    /// the level still has a single network object for its shared state.
    ///
    /// Joins the co-op save as an <see cref="IWorldSaveParticipant"/>: phase, night, time left, barricade health, the loot
    /// seed and the generator's fuel. Without it,
    /// a game saved at night resumed at daybreak with barricades down but the scrap spent on them still gone. Creatures
    /// are not saved; a night resumes with a fresh set at the spawn points.
    /// </remarks>
    [RequireComponent(typeof(NetworkObject))]
    public partial class SurvivalDirector : NetworkBehaviour, IWorldSaveParticipant
    {
        [Header("Clock")]
        [Tooltip("Seconds of daylight before night falls.")]
        [SerializeField] private float m_dayDuration = 480f;

        [Tooltip("Seconds of night the players must survive.")]
        [SerializeField] private float m_nightDuration = 300f;

        [Tooltip("Seconds of dawn between a night survived and the next day, while creatures die off and the dead return.")]
        [SerializeField] private float m_dawnDuration = 15f;

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

        [Tooltip("Extra share of creatures for each night after the first: 0.35 makes night 3 bring 1.7 times night 1's.")]
        [SerializeField] private float m_creatureGrowthPerNight = 0.35f;

        private const string BarricadeBrokenHint = "A barricade has broken!";

        private readonly NetworkVariable<SurvivalPhase> m_phase =
            new(SurvivalPhase.Day, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

        private readonly NetworkVariable<double> m_phaseEndsAt =
            new(0d, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

        // Which night the run is on, from 1; still the night just survived during its dawn.
        private readonly NetworkVariable<int> m_night =
            new(1, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

        private readonly NetworkVariable<int> m_totalNights =
            new(1, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

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

        /// <summary>The night the run is on, from 1. During dawn, the night just survived.</summary>
        public int Night => m_night.Value;

        /// <summary>Nights this run must survive to be won.</summary>
        public int TotalNights => Mathf.Max(1, m_totalNights.Value);

        public int BarricadeMaxHealth => m_barricadeMaxHealth;

        /// <summary>The inventory item barricades consume.</summary>
        public string BuildItemGuid => m_buildItem;

        public IReadOnlyList<Barricade> Barricades => m_barricades;

        /// <summary>Seconds left in the current phase, as this client estimates server time. Zero once the run is won.</summary>
        public float SecondsRemaining
        {
            get
            {
                if (!IsSpawned || m_phase.Value == SurvivalPhase.Won) return 0f;

                return Mathf.Max(0f, (float)(m_phaseEndsAt.Value - NetworkManager.ServerTime.Time));
            }
        }

        /// <summary>Length of the current phase in seconds. Zero once the run is won, which does not end.</summary>
        public float PhaseDuration => m_phase.Value switch
        {
            SurvivalPhase.Day => DayLength,
            SurvivalPhase.Night => NightLength,
            SurvivalPhase.Dawn => m_dawnDuration,
            _ => 0f
        };

        private void Awake()
        {
            CollectBarricades();
            CollectFloodlights();
        }

        public override void OnNetworkSpawn()
        {
            Active = this;

            m_phase.OnValueChanged += HandlePhaseChanged;
            m_barricadeHealth.OnListChanged += HandleBarricadeHealthChanged;
            m_lootSeed.OnValueChanged += HandleLootSeedChanged;
            m_power.OnValueChanged += HandlePowerChanged;

            if (IsServer)
            {
                m_barricadeHealth.Clear();
                foreach (var _ in m_barricades) m_barricadeHealth.Add(0);

                m_world = FindAnyObjectByType<WorldSync>();
                if (m_world != null) m_world.RegisterSaveParticipant(this);

                // First: phase lengths and the loot share must be set before the phase begins and the seed is applied.
                ApplyRoomSettings();

                var resumed = WorldSync.TryGetResumedState(SaveKey, out var saved);
                ChooseLootSeed(resumed ? saved : null);
                RestorePower(resumed ? saved : null);

                if (resumed) RestoreSaveState(saved);
                else BeginPhase(SurvivalPhase.Day);
            }
            else
            {
                // Arrived with the spawn: this client's loot and padlock codes follow the host's seed at once.
                ApplyLootSeed(m_lootSeed.Value);
            }

            ShowPower();

            m_shownHealth = new int[m_barricades.Count];
            ShowBarricades(announceBreaks: false);

            OnRunSpawn();
            OnToolsSpawn(IsServer && WorldSync.TryGetResumedState(SaveKey, out var toolsState) ? toolsState : null);

            // Presentation belongs to whoever is looking, so every client adds its own.
            if (!TryGetComponent<SurvivalHud>(out _)) gameObject.AddComponent<SurvivalHud>();
            if (!TryGetComponent<RoleAbilities>(out _)) gameObject.AddComponent<RoleAbilities>();
        }

        public override void OnNetworkDespawn()
        {
            m_phase.OnValueChanged -= HandlePhaseChanged;
            m_barricadeHealth.OnListChanged -= HandleBarricadeHealthChanged;
            m_lootSeed.OnValueChanged -= HandleLootSeedChanged;
            m_power.OnValueChanged -= HandlePowerChanged;

            if (m_world != null) m_world.UnregisterSaveParticipant(this);
            m_world = null;

            OnRunDespawn();
            OnToolsDespawn();

            if (ReferenceEquals(Active, this)) Active = null;
        }

        public override void OnDestroy()
        {
            if (ReferenceEquals(Active, this)) Active = null;
            base.OnDestroy();
        }

        private void Update()
        {
            if (!IsSpawned) return;

            ShowPower();
            ClaimDawnSupplies();
            RegisterToolUses();
            if (!IsServer) return;

            PruneFlares();

            // First, and whatever the phase: everyone dying ends the run even in the middle of a dawn.
            WatchForWipe();
            if (m_phase.Value == SurvivalPhase.Won || IsRunOver) return;

            CheckTraps();

            UpdatePower();
            if (NetworkManager.ServerTime.Time < m_phaseEndsAt.Value) return;

            AdvancePhase();
        }

        // ---- Clock (server) --------------------------------------------------------------------------

        /// <param name="remaining">Seconds left in the phase, when resuming part-way through; its full length otherwise.</param>
        private void BeginPhase(SurvivalPhase phase, float? remaining = null)
        {
            var duration = phase switch
            {
                SurvivalPhase.Day => DayLength,
                SurvivalPhase.Night => NightLength,
                SurvivalPhase.Dawn => m_dawnDuration,
                _ => 0f
            };

            if (remaining.HasValue) duration = Mathf.Clamp(remaining.Value, 0f, duration);

            // Before the phase changes: fuel burnt so far is counted under the phase it burnt in.
            StampPowerForPhase();

            m_phaseEndsAt.Value = NetworkManager.ServerTime.Time + duration;
            m_phase.Value = phase;

            if (phase == SurvivalPhase.Night) SpawnCreatures();
            else DespawnCreatures(dieAtDawn: phase is SurvivalPhase.Dawn or SurvivalPhase.Won);

            if (phase == SurvivalPhase.Dawn) BeginDawn();
            else if (phase == SurvivalPhase.Won) EndRun(won: true);
        }

        /// <summary>Server: moves the run on from the phase it is in. Dawn after the last night is the win.</summary>
        private void AdvancePhase()
        {
            switch (m_phase.Value)
            {
                case SurvivalPhase.Day:
                    BeginPhase(SurvivalPhase.Night);
                    break;

                case SurvivalPhase.Night:
                    BeginPhase(m_night.Value >= TotalNights ? SurvivalPhase.Won : SurvivalPhase.Dawn);
                    break;

                case SurvivalPhase.Dawn:
                    m_night.Value = Mathf.Min(m_night.Value + 1, TotalNights);
                    BeginPhase(SurvivalPhase.Day);
                    break;
            }
        }

        /// <summary>Host only: ends the current phase now. For testing a night without waiting out the day.</summary>
        public void SkipPhase()
        {
            if (!IsServer || !IsSpawned || m_phase.Value == SurvivalPhase.Won || IsRunOver) return;

            AdvancePhase();
        }

        // ---- Save (server) ---------------------------------------------------------------------------

        private const string SavePhase = "phase";
        private const string SaveNight = "night";
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
                [SaveNight] = m_night.Value,
                [SaveRemaining] = SecondsRemaining,
                [SaveBarricades] = barricades,
                [SaveTraps] = CaptureTraps(),
                [SaveLootSeed] = m_lootSeed.Value,
                [SavePowerRunning] = m_power.Value.Running,
                [SavePowerFuel] = FuelSeconds,

                // Read back by the lobby, not here: a resumed room is created with these and locked to them.
                [SaveSettings] = m_settings.Encode()
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
                    // Up to reinforced: a Builder's reinforcement survives a save.
                    m_barricadeHealth[i] = Mathf.Clamp((int?)barricades[i] ?? 0, 0, ReinforcedHealth);
                }
            }

            var phaseValue = (int?)state?[SavePhase] ?? (int)SurvivalPhase.Day;
            var phase = Enum.IsDefined(typeof(SurvivalPhase), (byte)phaseValue) ? (SurvivalPhase)phaseValue : SurvivalPhase.Day;
            var remaining = (float?)state?[SaveRemaining];

            // Saves from before runs had several nights were all on the first.
            m_night.Value = Mathf.Clamp((int?)state?[SaveNight] ?? 1, 1, TotalNights);

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

            var catalog = CreatureCatalogOf(m_creaturePrefab);
            // Scaled by the settings, but never below one: a night with nothing in it is not a night.
            // And more each night, so a run builds to its last.
            var growth = 1f + Mathf.Max(0f, m_creatureGrowthPerNight) * (m_night.Value - 1);
            var count = Mathf.Max(1, Mathf.RoundToInt((m_creaturesBase + m_creaturesPerPlayer * living) * m_settings.CreatureCount * growth));
            for (var i = 0; i < count; i++)
            {
                var spawn = m_creatureSpawns[i % m_creatureSpawns.Length];
                if (spawn == null) continue;

                var instance = Instantiate(m_creaturePrefab, spawn.position, spawn.rotation);

                // Before spawning, so the body travels in the spawn and no client ever shows the placeholder first.
                if (catalog != null && instance.TryGetComponent<NightCreature>(out var nightCreature))
                    nightCreature.AssignBody(catalog.Pick(m_creaturesSpawned, m_testBody));

                m_creaturesSpawned++;

                var creature = instance.GetComponent<NetworkObject>();
                creature.Spawn(destroyWithScene: true);
                m_creatures.Add(creature);
            }
        }

        /// <param name="dieAtDawn">True to let each creature play its death before it goes; false to remove them at once.</param>
        private void DespawnCreatures(bool dieAtDawn)
        {
            foreach (var creature in m_creatures)
            {
                if (creature == null || !creature.IsSpawned) continue;

                if (dieAtDawn && creature.TryGetComponent<NightCreature>(out var nightCreature)) nightCreature.DieAtDawn();
                else creature.Despawn();
            }

            m_creatures.Clear();
        }

        // Server: creatures spawned this level, so Cycle selection moves on across nights; and the host's test pick.
        private int m_creaturesSpawned;
        private int m_testBody = -1;

        private static CreatureCatalog CreatureCatalogOf(GameObject prefab) =>
            prefab != null && prefab.TryGetComponent<NightCreature>(out var creature) ? creature.Catalog : null;

        /// <summary>
        /// Host only: dresses every creature from now on in the next body in the catalog, enabled or not, and replaces the
        /// ones out now. For trying bodies one after another without waiting for a new night.
        /// </summary>
        /// <returns>The body now in use, or <c>null</c> if the catalog has none.</returns>
        public CreatureCatalog.Body CycleTestCreature()
        {
            if (!IsServer || !IsSpawned) return null;

            var catalog = CreatureCatalogOf(m_creaturePrefab);
            if (catalog == null) return null;

            m_testBody = catalog.NextUsable(m_testBody);
            if (m_phase.Value == SurvivalPhase.Night)
            {
                DespawnCreatures(dieAtDawn: false);
                SpawnCreatures();
            }

            return catalog.At(m_testBody);
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

        private enum BuildResult : byte { Built, Full, Blocked }

        private const string BlockedHint = "Something is in the way.";

        /// <summary>Owner client: spends one item and asks the host to add its worth of health to a barricade.</summary>
        /// <remarks>
        /// Spent before asking, and handed back if the host refuses. Spending on acceptance instead left a gap: an item
        /// dropped or used while the request travelled was still paid for with nothing, and the barricade went up free.
        /// Inventories are owner-run, so this client does both.
        /// </remarks>
        internal void RequestBuild(Barricade barricade)
        {
            if (!IsSpawned || barricade == null) return;

            var inventory = LocalPlayerContext.Inventory;
            // Checked first: RemoveItem returns the quantity left, which is 0 both for the last one and for none at all.
            if (inventory == null || !inventory.ContainsItem(BuildItemGuid)) return;

            inventory.RemoveItem(BuildItemGuid, 1);

            BuildRpc(barricade.Index);
        }

        [Rpc(SendTo.Server)]
        private void BuildRpc(int index, RpcParams rpcParams = default)
        {
            var sender = rpcParams.Receive.SenderClientId;
            if (index < 0 || index >= m_barricadeHealth.Count || index >= m_barricades.Count)
            {
                BuildResultRpc(BuildResult.Full, RpcTarget.Single(sender, RpcTargetUse.Temp));
                return;
            }

            var health = m_barricadeHealth[index];
            var result = health >= m_barricadeMaxHealth ? BuildResult.Full
                : health == 0 && IsOpeningOccupied(m_barricades[index]) ? BuildResult.Blocked
                : BuildResult.Built;

            if (result == BuildResult.Built)
            {
                m_barricadeHealth[index] = Mathf.Min(m_barricadeMaxHealth, health + HealthPerItemFrom(sender));
                CountRepair(sender);
            }

            BuildResultRpc(result, RpcTarget.Single(sender, RpcTargetUse.Temp));
        }

        /// <summary>Server: what one item adds when this client builds, after their role's repair perk.</summary>
        private int HealthPerItemFrom(ulong clientId)
        {
            var player = NetworkManager.ConnectedClients.TryGetValue(clientId, out var client) ? client.PlayerObject : null;
            var character = player != null ? player.GetComponent<QuietVillage.Multiplayer.Characters.PlayerCharacter>() : null;
            if (character == null) return m_healthPerItem;

            return Mathf.Max(1, Mathf.RoundToInt(m_healthPerItem * character.Perks.BarricadeRepair));
        }

        /// <summary>
        /// Whether closing an open barricade would shut a player or creature inside it.
        /// </summary>
        /// <remarks>
        /// Only for an open barricade: one already standing has its blocker on, so nothing can be inside. A creature
        /// caught by the carve loses the NavMesh under it, and a player caught by the collider is pushed out at random.
        /// </remarks>
        private bool IsOpeningOccupied(Barricade barricade)
        {
            foreach (var player in PlayerHealthSync.Spawned)
            {
                if (player != null && !player.IsDead && barricade.WouldTrap(player.transform.position)) return true;
            }

            foreach (var creature in m_creatures)
            {
                if (creature != null && creature.IsSpawned && barricade.WouldTrap(creature.transform.position)) return true;
            }

            return false;
        }

        /// <summary>Tells the builder how their request went, handing back the item if nothing was built.</summary>
        [Rpc(SendTo.SpecifiedInParams)]
        private void BuildResultRpc(BuildResult result, RpcParams rpcParams = default)
        {
            if (result == BuildResult.Built) return;

            var inventory = LocalPlayerContext.Inventory;
            if (inventory != null) inventory.AddItem(BuildItemGuid, 1, null);

            if (result == BuildResult.Blocked) ShowHint(BlockedHint);
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
        /// <param name="skipLit">Leave out barricades whose attack point is in floodlight, where creatures will not stand.</param>
        public Barricade NearestStandingBarricade(Vector3 position, bool skipLit = false)
        {
            Barricade nearest = null;
            var nearestDistance = float.MaxValue;

            foreach (var barricade in m_barricades)
            {
                if (barricade == null || !barricade.IsStanding) continue;
                if (skipLit && IsLit(barricade.AttackPoint)) continue;

                var distance = Vector3.SqrMagnitude(barricade.AttackPoint - position);
                if (distance >= nearestDistance) continue;

                nearestDistance = distance;
                nearest = barricade;
            }

            return nearest;
        }

        private void HandleBarricadeHealthChanged(NetworkListEvent<int> change) => ShowBarricades(announceBreaks: true);

        private int m_lastBarricadeHit = -1;

        private void PlayBarricadeSound(Barricade barricade, bool broke)
        {
            var sounds = m_creaturePrefab != null && m_creaturePrefab.TryGetComponent<NightCreature>(out var creature) ? creature.Sounds : null;
            if (sounds == null || barricade == null) return;

            var clip = broke && sounds.BarricadeBreak != null
                ? sounds.BarricadeBreak
                : CreatureSounds.Pick(sounds.BarricadeHits, ref m_lastBarricadeHit);

            var source = UHFPS.Tools.GameTools.PlayOneShot3D(barricade.transform.position, clip, sounds.MaxDistance, sounds.BarricadeVolume, "BarricadeSound");
            if (source == null) return;

            source.rolloffMode = AudioRolloffMode.Logarithmic;
            source.minDistance = sounds.MinDistance;
        }

        private void ShowBarricades(bool announceBreaks)
        {
            if (m_shownHealth.Length != m_barricades.Count) m_shownHealth = new int[m_barricades.Count];

            var anyBroke = false;
            for (var i = 0; i < m_barricades.Count; i++)
            {
                var health = i < m_barricadeHealth.Count ? m_barricadeHealth[i] : 0;
                if (announceBreaks && m_shownHealth[i] > 0 && health == 0) anyBroke = true;

                // Heard where it happens: a thud per hit, a crack when it gives way. From the replicated health, so every
                // client hears it without a message of its own.
                if (announceBreaks && health < m_shownHealth[i]) PlayBarricadeSound(m_barricades[i], broke: health == 0);

                m_shownHealth[i] = health;
                m_barricades[i].ShowHealth(health, m_barricadeMaxHealth);
            }

            if (anyBroke) ShowHint(BarricadeBrokenHint);
        }

        // ---- Presentation ----------------------------------------------------------------------------

        private void HandlePhaseChanged(SurvivalPhase previous, SurvivalPhase current)
        {
            // A frame later: the night number changes in the same tick as the phase, and may be applied after it.
            StartCoroutine(ShowPhaseHintNextFrame(current));

            PhaseChanged?.Invoke(current);
        }

        private System.Collections.IEnumerator ShowPhaseHintNextFrame(SurvivalPhase phase)
        {
            yield return null;

            var night = m_night.Value;
            var total = TotalNights;
            var hint = phase switch
            {
                SurvivalPhase.Night => $"Night {night} of {total} has fallen. Get inside and hold the barricades.",
                SurvivalPhase.Dawn => m_deadReturn.Value
                    ? $"Dawn. Night {night} of {total} survived. The fallen return."
                    : $"Dawn. Night {night} of {total} survived.",
                SurvivalPhase.Won => total == 1 ? "Dawn. You survived the night." : $"Dawn. You survived all {total} nights!",
                SurvivalPhase.Day when night > 1 => $"Day {night}. Scavenge and repair before night falls.",
                _ => null
            };

            if (hint != null) ShowHint(hint);
        }

        private static void ShowHint(string message)
        {
            var gameManager = LocalPlayerContext.GameManager;
            if (gameManager != null) gameManager.ShowHintMessage(message, 4f);
        }
    }
}
