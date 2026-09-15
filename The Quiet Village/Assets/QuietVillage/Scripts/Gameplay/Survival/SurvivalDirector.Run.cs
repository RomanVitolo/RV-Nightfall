using System.Collections;
using System.Collections.Generic;
using System.Text;
using QuietVillage.Multiplayer.Bridge;
using QuietVillage.Multiplayer.Characters;
using QuietVillage.Multiplayer.Flow;
using UHFPS.Runtime;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

namespace QuietVillage.Gameplay.Survival
{
    /// <summary>How a run finished, if it has.</summary>
    public enum RunOutcome : byte { None, Won, Lost }

    /// <summary>
    /// The run as a whole: what each dawn gives back, how the run ends, and the numbers shown when it does.
    /// </summary>
    /// <remarks>
    /// Authority: server. The host decides when the run is over (the last night survived, or everyone dead) and writes
    /// the outcome, how many nights were survived and a summary of each player's run; every client shows them in a
    /// <see cref="RunEndScreen"/>. Those arrive as NetworkVariables rather than a message, and the screen reads them as they
    /// come, so it never matters which lands first.
    ///
    /// Each dawn between nights, on the host: downed players get up, the dead come back as new players at a spawn point
    /// (weak and empty-handed; their belongings were dropped where they fell) unless the room's settings make death
    /// permanent, and the supply round moves on. Supplies are
    /// then claimed by each client for its own player, because inventories are owner-run: one number replicates, and a
    /// player who was dead at dawn claims theirs once they are back on their feet.
    ///
    /// Counts per player (downed, revives, deaths, repairs) are kept only on the host, from <see cref="PlayerHealthSync"/>'s
    /// server events and barricade builds, keyed by client so a player who leaves still appears in the summary.
    /// </remarks>
    public partial class SurvivalDirector
    {
        [Header("Dawn")]
        [Tooltip("Scrap each player receives at every dawn between nights.")]
        [SerializeField] private int m_dawnScrap = 2;

        [Tooltip("Fuel canisters each player receives at every dawn between nights, in a level with a generator.")]
        [SerializeField] private int m_dawnCanisters = 1;

        [Tooltip("Flares each player receives at every dawn between nights, once the Flare item is set up.")]
        [SerializeField] private int m_dawnFlares = 1;

        private readonly NetworkVariable<RunOutcome> m_outcome =
            new(RunOutcome.None, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

        private readonly NetworkVariable<int> m_nightsSurvived =
            new(0, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

        private readonly NetworkVariable<FixedString4096Bytes> m_summary =
            new(default, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

        private readonly NetworkVariable<int> m_supplyRound =
            new(0, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

        /// <summary>How the run ended, or None while it is still going.</summary>
        public RunOutcome Outcome => m_outcome.Value;

        public bool IsRunOver => m_outcome.Value != RunOutcome.None;

        /// <summary>Nights survived when the run ended.</summary>
        public int NightsSurvived => m_nightsSurvived.Value;

        /// <summary>One line per player with their part in the run, rich text; empty until the host has written it.</summary>
        public string RunSummary => m_summary.Value.ToString();

        private void OnRunSpawn()
        {
            // Every client: the survival level ends a wipe with its own screen.
            SessionDeathScreen.WipeHandledByLevel = true;
            if (!TryGetComponent<RunEndScreen>(out _)) gameObject.AddComponent<RunEndScreen>();

            // Rounds that happened before this client arrived are not theirs to claim.
            m_claimedSupplyRound = m_supplyRound.Value;

            if (!IsServer) return;

            PlayerHealthSync.ServerDowned += HandlePlayerDowned;
            PlayerHealthSync.ServerRevived += HandlePlayerRevived;
            PlayerHealthSync.ServerDied += HandlePlayerDied;
        }

        private void OnRunDespawn()
        {
            SessionDeathScreen.WipeHandledByLevel = false;

            PlayerHealthSync.ServerDowned -= HandlePlayerDowned;
            PlayerHealthSync.ServerRevived -= HandlePlayerRevived;
            PlayerHealthSync.ServerDied -= HandlePlayerDied;
        }

        // ---- Ending (server) -------------------------------------------------------------------------

        private void WatchForWipe()
        {
            if (!IsRunOver && PlayerHealthSync.EveryoneDead) EndRun(won: false);
        }

        private void EndRun(bool won)
        {
            if (!IsServer || IsRunOver) return;

            // A night is survived at its dawn: dying in a day or night leaves it uncounted, dying during a dawn does not.
            var survived = won ? TotalNights
                : m_phase.Value == SurvivalPhase.Dawn ? m_night.Value
                : m_night.Value - 1;

            m_nightsSurvived.Value = Mathf.Clamp(survived, 0, TotalNights);

            var summary = new FixedString4096Bytes();
            summary.CopyFromTruncated(BuildSummary());
            m_summary.Value = summary;

            // Last: the screen opens on this, and the rest is already on its way.
            m_outcome.Value = won ? RunOutcome.Won : RunOutcome.Lost;

            Debug.Log($"{nameof(SurvivalDirector)}: the run is {(won ? "won" : "lost")} after {m_nightsSurvived.Value} " +
                      $"of {TotalNights} night(s).", this);
        }

        // ---- Dawn (server) ---------------------------------------------------------------------------

        /// <summary>Server, as a dawn between nights begins: the downed get up, the dead return, supplies are due.</summary>
        private void BeginDawn()
        {
            foreach (var player in PlayerHealthSync.Spawned)
            {
                if (player != null && player.IsDowned) player.ServerRevive();
            }

            m_supplyRound.Value++;

            // On Hard (or when the host turned it off), the dead stay dead.
            if (m_settings.DeadReturn) StartCoroutine(ReturnTheDead());
        }

        /// <remarks>
        /// A dead player's object is spent: UHFPS's death cannot be undone on it, so each comes back as a new player, the
        /// same way a restart brings everyone back. Despawned first and spawned a frame later, once Netcode has let go
        /// of the old one as the client's player object.
        /// </remarks>
        private IEnumerator ReturnTheDead()
        {
            var spawner = FindAnyObjectByType<NetworkPlayerSpawner>();
            if (spawner == null)
            {
                Debug.LogWarning($"{nameof(SurvivalDirector)}: no {nameof(NetworkPlayerSpawner)}, so the dead cannot return.", this);
                yield break;
            }

            var returning = new List<ulong>();
            foreach (var player in new List<PlayerHealthSync>(PlayerHealthSync.Spawned))
            {
                if (player == null || !player.IsDead || !player.IsSpawned) continue;

                returning.Add(player.OwnerClientId);
                player.NetworkObject.Despawn(destroy: true);
            }

            if (returning.Count == 0) yield break;

            yield return null;

            foreach (var clientId in returning)
            {
                if (IsRunOver) yield break;

                if (!spawner.Respawn(clientId))
                    Debug.LogWarning($"{nameof(SurvivalDirector)}: could not bring client {clientId} back at dawn.", this);
            }
        }

        // ---- Dawn supplies (every client) ------------------------------------------------------------

        private const float SupplySettleSeconds = 1f;

        private int m_claimedSupplyRound;
        private GameObject m_supplyPlayer;
        private float m_supplyClaimableAt;

        /// <summary>This client: takes the dawn's supplies into its own player's inventory, once they can hold them.</summary>
        private void ClaimDawnSupplies()
        {
            if (!IsSpawned || m_claimedSupplyRound >= m_supplyRound.Value) return;

            var player = LocalPlayerContext.Player;
            if (player == null) return;

            // A player just back from the dead: their inventory starts up a moment after they spawn.
            if (player != m_supplyPlayer)
            {
                m_supplyPlayer = player;
                m_supplyClaimableAt = Time.time + SupplySettleSeconds;
            }

            if (Time.time < m_supplyClaimableAt) return;

            var health = player.GetComponent<PlayerHealthSync>();
            var inventory = LocalPlayerContext.Inventory;
            if (health == null || !health.IsStanding || inventory == null) return;

            m_claimedSupplyRound = m_supplyRound.Value;

            var received = new List<string>();
            if (Give(inventory, BuildItemGuid, m_dawnScrap)) received.Add($"{m_dawnScrap} Scrap");
            if (HasGenerator && Give(inventory, FuelItemGuid, m_dawnCanisters))
                received.Add(m_dawnCanisters == 1 ? "1 fuel canister" : $"{m_dawnCanisters} fuel canisters");
            if (Give(inventory, ItemGuidByTitle(inventory, FlareTitle), m_dawnFlares))
                received.Add(m_dawnFlares == 1 ? "1 flare" : $"{m_dawnFlares} flares");

            if (received.Count > 0) ShowHint($"Dawn supplies: {string.Join(", ", received)}.");
        }

        private bool Give(Inventory inventory, string itemGuid, int quantity)
        {
            if (quantity <= 0 || string.IsNullOrEmpty(itemGuid)) return false;
            if (inventory.AddItem(itemGuid, (ushort)Mathf.Min(quantity, ushort.MaxValue), new ItemCustomData())) return true;

            Debug.LogWarning($"{nameof(SurvivalDirector)}: no room for dawn supplies ({itemGuid}).", this);
            return false;
        }

        // ---- Run numbers (server) --------------------------------------------------------------------

        private sealed class PlayerRun
        {
            public string Name;
            public string Role;
            public int Downed;
            public int Revives;
            public int Deaths;
            public int Repairs;
        }

        private readonly Dictionary<ulong, PlayerRun> m_playerRuns = new();

        private PlayerRun RunOf(ulong clientId)
        {
            if (!m_playerRuns.TryGetValue(clientId, out var run)) m_playerRuns[clientId] = run = new PlayerRun();

            // Refreshed whenever the player is around, so the name survives them leaving later.
            if (NetworkManager.ConnectedClients.TryGetValue(clientId, out var client) && client.PlayerObject != null)
            {
                var setup = client.PlayerObject.GetComponent<HeroPlayerNetworkSetup>();
                if (setup != null) run.Name = setup.DisplayName;

                var character = client.PlayerObject.GetComponent<PlayerCharacter>();
                if (character != null && character.Character != null) run.Role = character.Character.RoleOrName;
            }

            run.Name ??= $"Player {clientId + 1}";
            return run;
        }

        private void HandlePlayerDowned(PlayerHealthSync player) => RunOf(player.OwnerClientId).Downed++;

        private void HandlePlayerDied(PlayerHealthSync player) => RunOf(player.OwnerClientId).Deaths++;

        private void HandlePlayerRevived(PlayerHealthSync player, PlayerHealthSync reviver)
        {
            if (reviver != null) RunOf(reviver.OwnerClientId).Revives++;
        }

        private void CountRepair(ulong clientId) => RunOf(clientId).Repairs++;

        private string BuildSummary()
        {
            // Everyone still here, even with nothing to their name.
            foreach (var clientId in NetworkManager.ConnectedClientsIds) RunOf(clientId);

            var ids = new List<ulong>(m_playerRuns.Keys);
            ids.Sort();

            var builder = new StringBuilder();
            foreach (var id in ids)
            {
                var run = m_playerRuns[id];
                if (builder.Length > 0) builder.Append('\n');

                builder.Append("<b>").Append(run.Name).Append("</b>");
                if (!string.IsNullOrEmpty(run.Role)) builder.Append("  <color=#a0a0a0>").Append(run.Role).Append("</color>");

                builder.Append("\n<size=80%><color=#c8c8c8>")
                    .Append($"Repairs {run.Repairs}  ·  Revives {run.Revives}  ·  Downed {run.Downed}  ·  Deaths {run.Deaths}")
                    .Append("</color></size>");
            }

            return builder.ToString();
        }
    }
}
