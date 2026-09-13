using System;
using System.Collections;
using System.Collections.Generic;
using QuietVillage.Multiplayer.Bridge.Saves;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Unity.Netcode;
using UHFPS.Runtime;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace QuietVillage.Multiplayer.Bridge.World
{
    /// <summary>
    /// Saving and resuming a room's game: the world from the host, each player from their own client.
    /// </summary>
    /// <remarks>
    /// A save is assembled rather than written. The host owns the world, but only a player's own client knows what
    /// they carry, so saving asks everyone and waits for their answers. Resuming runs the other way: the host reads
    /// the file in the lobby, and as each client finishes loading the level it is sent the world and its own
    /// belongings, before its player is spawned, so nobody appears in a world that has not caught up yet.
    ///
    /// The world is keyed by World Sync ids, which are written into the scene at author time and mean the same
    /// thing on every machine and in every session.
    /// </remarks>
    public partial class WorldSync : QuietVillage.Multiplayer.Flow.ISpawnGate,
        QuietVillage.Multiplayer.Characters.ISavedCharacterSource
    {
        // Well under Netcode's message limit, so a large level still travels in one reliable stream of pieces.
        private const int ChunkSize = 8000;

        private const float SaveGatherTimeout = 3f;
        private const float SpawnHoldTimeout = 5f;

        private const string SavedHint = "Game saved.";
        private const string SaveFailedHint = "The game could not be saved.";

        // Server: what each client said it carries, keyed by account, while a save is being gathered.
        private readonly Dictionary<string, JToken> m_gathered = new();
        private bool m_saving;

        // Server: clients waiting for their world and belongings before their player may spawn.
        private readonly Dictionary<ulong, Action> m_heldSpawns = new();
        private string m_worldJson;

        // Client: the pieces of the world as they arrive, and this player's own slice.
        private readonly List<string> m_worldChunks = new();
        private JToken m_localPlayerSave;

        /// <summary>The save this room resumed, or <c>null</c> for a new game.</summary>
        private static MultiplayerSave ResumedSave => MultiplayerSaveCatalog.Resumed;

        /// <summary>This client's saved belongings, handed to its player as it spawns. Taken once.</summary>
        internal JToken TakeLocalPlayerSave()
        {
            var save = m_localPlayerSave;
            m_localPlayerSave = null;
            return save;
        }

        /// <summary>True while a resumed save is still being applied, so players wait before spawning.</summary>
        internal bool IsResuming => ResumedSave != null;

        // Server: game systems outside World Sync that join the save.
        private readonly List<IWorldSaveParticipant> m_participants = new();

        /// <summary>Server: includes a participant's state in every save from now on.</summary>
        public void RegisterSaveParticipant(IWorldSaveParticipant participant)
        {
            if (participant != null && !m_participants.Contains(participant)) m_participants.Add(participant);
        }

        public void UnregisterSaveParticipant(IWorldSaveParticipant participant) => m_participants.Remove(participant);

        /// <summary>A participant's entry in the save this room resumed, for it to restore as it spawns.</summary>
        /// <remarks>
        /// Static, read from the held save rather than from this level's WorldSync, so a participant spawning before or
        /// after WorldSync gets the same answer.
        /// </remarks>
        /// <returns><c>false</c> for a new game, or a save without this participant.</returns>
        public static bool TryGetResumedState(string saveKey, out JToken state)
        {
            state = null;
            var save = ResumedSave;
            return save != null && !string.IsNullOrEmpty(saveKey)
                                && save.Participants.TryGetValue(saveKey, out state) && state != null;
        }

        /// <summary>
        /// Server: the health a client's player had when the resumed game was saved.
        /// </summary>
        /// <remarks>
        /// Health is decided by the server, so the saved value has to start there. The owner also restores it into its
        /// local UHFPS health with the rest of its save slice; were the server not given it, the owner's HUD would show the
        /// saved value until the first hit put back the server's starting health.
        /// </remarks>
        /// <returns><c>false</c> for a new game, or a player new to the save.</returns>
        internal bool TryGetSavedHealth(ulong clientId, out int health)
        {
            health = 0;
            if (!TryGetSavedSlice(clientId, out var slice)) return false;

            var saved = slice?["localData"]?["health"];
            if (saved == null || saved.Type != JTokenType.Integer && saved.Type != JTokenType.Float) return false;

            health = (int)saved;
            return true;
        }

        /// <summary>Server: the character a client's player was when the resumed game was saved.</summary>
        /// <remarks>
        /// Asked by the spawner as each player spawns. A client's spawn is held until its account arrives, so the account
        /// is known by then.
        /// </remarks>
        public bool TryGetSavedCharacter(ulong clientId, out QuietVillage.Multiplayer.Characters.CharacterChoice choice)
        {
            choice = QuietVillage.Multiplayer.Characters.CharacterChoice.None;
            return TryGetSavedSlice(clientId, out var slice) && PlayerSaveState.TryGetCharacter(slice, out choice);
        }

        /// <summary>Server: a client's own part of the resumed save, found by their account.</summary>
        private bool TryGetSavedSlice(ulong clientId, out JToken slice)
        {
            slice = null;
            var save = ResumedSave;
            if (!IsServer || save == null) return false;

            var account = clientId == NetworkManager.LocalClientId
                ? LocalAccountId()
                : m_accounts.TryGetValue(clientId, out var known) ? known : null;

            return !string.IsNullOrEmpty(account) && save.Players.TryGetValue(account, out slice) && slice != null;
        }

        // ---- Resuming ---------------------------------------------------------------------------------

        private void OnSavesSpawn()
        {
            if (IsServer)
            {
                // The spawner asks this before putting each player into the level.
                QuietVillage.Multiplayer.Flow.SpawnGate.Active = this;
                QuietVillage.Multiplayer.Characters.SavedCharacterSource.Active = this;
                ApplyResumedSaveOnHost();
                return;
            }

            // The host cannot know this client's account, and it decides whose belongings to send by it.
            AnnounceAccountRpc(LocalAccountId());
        }

        private void ApplyResumedSaveOnHost()
        {
            var save = ResumedSave;
            if (save == null) return;

            ApplyWorldState(save.World);
            m_worldJson = save.World?.ToString(Formatting.None) ?? string.Empty;

            // Placed here and registered under keys of their own, rather than published: the other clients are
            // still loading the level, and a message about an object they have no copy of would be dropped. Each
            // is given the items when it arrives, under the same keys, so one item is one item everywhere.
            foreach (var drop in save.Drops)
            {
                var dropped = DroppedItems.CreateCopy(drop.ReferenceGuid, drop.Quantity, drop.Position, drop.Rotation);
                if (dropped == null) continue;

                RegisterDrop(dropped, AllocateDropKeys(), false, NetworkManager.LocalClientId,
                    drop.ReferenceGuid, drop.Quantity);
            }

            // The host's own belongings, which nobody sends it: it is the one holding the save.
            if (save.Players.TryGetValue(LocalAccountId(), out var hostPlayer)) m_localPlayerSave = hostPlayer;

            Debug.Log($"{nameof(WorldSync)}: resumed a save with {save.Players.Count} player(s) " +
                      $"and {save.Drops.Count} dropped item(s).", this);
        }

        /// <summary>
        /// Holds a client's player back until it has the world, then spawns it.
        /// </summary>
        /// <returns><c>true</c> if the spawn was held; <c>false</c> to spawn now, as a new game does.</returns>
        public bool TryHold(ulong clientId, Action spawn)
        {
            if (!IsServer || !IsResuming || spawn == null) return false;

            // The host applies the world itself, before any of this.
            if (clientId == NetworkManager.LocalClientId) return false;

            m_heldSpawns[clientId] = spawn;

            // A client that never asks — an older build, or one that dropped out mid-load — still gets to play.
            StartCoroutine(ReleaseHeldSpawnAfterTimeout(clientId));
            return true;
        }

        private IEnumerator ReleaseHeldSpawnAfterTimeout(ulong clientId)
        {
            yield return new WaitForSeconds(SpawnHoldTimeout);

            if (!m_heldSpawns.TryGetValue(clientId, out var spawn)) yield break;

            Debug.LogWarning($"{nameof(WorldSync)}: client {clientId} never asked for the saved world; " +
                             "spawning them into it anyway.", this);

            m_heldSpawns.Remove(clientId);
            spawn();
        }

        [Rpc(SendTo.Server)]
        private void AnnounceAccountRpc(string accountId, RpcParams rpcParams = default)
        {
            var clientId = rpcParams.Receive.SenderClientId;
            m_accounts[clientId] = accountId ?? string.Empty;

            if (!IsResuming) return;

            SendSavedWorldTo(clientId, accountId);

            // Their world and their belongings are on the way, and arrive before the spawn that follows.
            if (!m_heldSpawns.Remove(clientId, out var spawn)) return;

            spawn();
        }

        private void SendSavedWorldTo(ulong clientId, string accountId)
        {
            var save = ResumedSave;
            if (save == null) return;

            var target = RpcTarget.Single(clientId, RpcTargetUse.Temp);

            for (var offset = 0; offset < m_worldJson.Length; offset += ChunkSize)
            {
                var length = Mathf.Min(ChunkSize, m_worldJson.Length - offset);
                WorldChunkRpc(m_worldJson.Substring(offset, length), false, target);
            }

            WorldChunkRpc(string.Empty, true, target);

            foreach (var drop in m_drops)
            {
                var pickup = drop.Value.Pickup;
                if (pickup == null) continue;

                var pose = pickup.transform;
                RestoredDropRpc(drop.Key, drop.Value.ReferenceGuid, drop.Value.Quantity, pose.position, pose.rotation,
                    target);
            }

            var slice = !string.IsNullOrEmpty(accountId) && save.Players.TryGetValue(accountId, out var player)
                ? player.ToString(Formatting.None)
                : string.Empty;

            PlayerSaveRpc(slice, target);
        }

        [Rpc(SendTo.SpecifiedInParams)]
        private void WorldChunkRpc(string chunk, bool last, RpcParams rpcParams = default)
        {
            if (!last)
            {
                m_worldChunks.Add(chunk);
                return;
            }

            var json = string.Concat(m_worldChunks);
            m_worldChunks.Clear();

            if (string.IsNullOrEmpty(json)) return;

            try
            {
                ApplyWorldState(JToken.Parse(json));
            }
            catch (JsonException exception)
            {
                Debug.LogError($"{nameof(WorldSync)}: the saved world did not arrive intact ({exception.Message}).", this);
            }
        }

        /// <summary>An item the save left lying in the level, under the key the host gave it.</summary>
        [Rpc(SendTo.SpecifiedInParams)]
        private void RestoredDropRpc(uint pickupKey, string referenceGuid, int quantity, Vector3 position,
            Quaternion rotation, RpcParams rpcParams = default)
        {
            var dropped = DroppedItems.CreateCopy(referenceGuid, quantity, position, rotation);
            if (dropped == null) return;

            // The key the host holds it under, minus the slot the pickup sits in, is where its run of keys began.
            RegisterDrop(dropped, pickupKey - DroppedItems.PickupSlot, false, NetworkManager.ServerClientId,
                referenceGuid, quantity);
        }

        [Rpc(SendTo.SpecifiedInParams)]
        private void PlayerSaveRpc(string slice, RpcParams rpcParams = default)
        {
            m_localPlayerSave = string.IsNullOrEmpty(slice) ? null : JToken.Parse(slice);
        }

        // ---- The world's own state --------------------------------------------------------------------

        /// <summary>Every registered object's saved state, keyed by the id it has on every machine.</summary>
        private JObject CaptureWorldState()
        {
            var world = new JObject();

            foreach (var entity in m_entities.Values)
            {
                if (entity == null || string.IsNullOrEmpty(entity.Id)) continue;

                var target = entity.SaveTarget;
                if (target == null) continue;

                var state = target.OnSave();
                if (state != null) world[entity.Id] = JObject.FromObject(state);
            }

            return world;
        }

        private void ApplyWorldState(JToken world)
        {
            if (world == null) return;

            foreach (var entity in m_entities.Values)
            {
                if (entity == null || string.IsNullOrEmpty(entity.Id)) continue;

                var target = entity.SaveTarget;
                var state = world[entity.Id];
                if (target == null || state == null) continue;

                target.OnLoad(state);
            }
        }

        // ---- Saving -----------------------------------------------------------------------------------

        /// <summary>
        /// Host only: saves the room's game, asking every player for their own half first.
        /// </summary>
        internal void SaveGame()
        {
            if (!IsServer || !IsSpawned || m_saving) return;

            StartCoroutine(SaveRoutine());
        }

        private IEnumerator SaveRoutine()
        {
            m_saving = true;
            m_gathered.Clear();

            // The host's own player answers here rather than over the network.
            CaptureLocalPlayer();

            var expected = NetworkManager.ConnectedClientsIds.Count;
            if (expected > 1) RequestPlayerSaveRpc();

            var deadline = Time.realtimeSinceStartup + SaveGatherTimeout;
            while (m_gathered.Count < expected && Time.realtimeSinceStartup < deadline)
            {
                yield return null;
            }

            if (m_gathered.Count < expected)
            {
                // Saving anyway: what did arrive is still better than losing the world's progress, and a player
                // whose answer was late keeps what they carry in their own game.
                Debug.LogWarning($"{nameof(WorldSync)}: only {m_gathered.Count} of {expected} players answered in time; " +
                                 "saving without the rest.", this);
            }

            var save = new MultiplayerSave { World = CaptureWorldState() };
            foreach (var player in m_gathered) save.Players[player.Key] = player.Value;
            foreach (var drop in LiveDrops()) save.Drops.Add(drop);

            foreach (var participant in m_participants)
            {
                if (participant == null || string.IsNullOrEmpty(participant.SaveKey)) continue;

                var state = participant.CaptureSaveState();
                if (state != null) save.Participants[participant.SaveKey] = state;
            }

            save.TimePlayed = (ResumedSave?.TimePlayed ?? 0f) + Time.timeSinceLevelLoad;

            var scene = SceneManager.GetActiveScene().name;
            var write = MultiplayerSaveFile.WriteAsync(save, scene, save.TimePlayed);
            while (!write.IsCompleted) yield return null;

            m_saving = false;

            if (write.IsFaulted || string.IsNullOrEmpty(write.Result))
            {
                Debug.LogError($"{nameof(WorldSync)}: saving failed. {write.Exception?.GetBaseException().Message}", this);
                SaveResultRpc(false);
                yield break;
            }

            // Restarting the level after this resumes what was just saved, rather than an older state.
            MultiplayerSaveCatalog.UseAsResumePoint(save);

            Debug.Log($"{nameof(WorldSync)}: saved the game to '{write.Result}'.", this);
            SaveResultRpc(true);
        }

        /// <summary>Items lying in the level right now, as the save records them.</summary>
        private IEnumerable<MultiplayerSave.DroppedItem> LiveDrops()
        {
            foreach (var drop in m_drops.Values)
            {
                // Taken or destroyed since it was dropped.
                if (drop.Pickup == null) continue;

                var transform = drop.Pickup.transform;
                yield return new MultiplayerSave.DroppedItem
                {
                    ReferenceGuid = drop.ReferenceGuid,
                    Quantity = drop.Quantity,
                    Position = transform.position,
                    Rotation = transform.rotation
                };
            }
        }

        [Rpc(SendTo.NotServer)]
        private void RequestPlayerSaveRpc()
        {
            var state = PlayerSaveState.Capture(LocalPlayerContext.Player);
            if (state == null) return;

            SubmitPlayerSaveRpc(LocalAccountId(), JObject.FromObject(state).ToString(Formatting.None));
        }

        [Rpc(SendTo.Server)]
        private void SubmitPlayerSaveRpc(string accountId, string json)
        {
            if (!m_saving || string.IsNullOrEmpty(json)) return;

            m_gathered[KeyFor(accountId)] = JToken.Parse(json);
        }

        private void CaptureLocalPlayer()
        {
            var state = PlayerSaveState.Capture(LocalPlayerContext.Player);
            if (state != null) m_gathered[KeyFor(LocalAccountId())] = JObject.FromObject(state);
        }

        [Rpc(SendTo.Everyone)]
        private void SaveResultRpc(bool saved)
        {
            var gameManager = LocalPlayerContext.GameManager;
            if (gameManager != null) gameManager.ShowHintMessage(saved ? SavedHint : SaveFailedHint, 2f);
        }

        /// <summary>
        /// This player's Unity account, which a save keys their belongings to.
        /// </summary>
        /// <remarks>
        /// Falling back to the Netcode client id keeps a save working when there is no signed-in session at all,
        /// such as a direct connection in the editor. It is not stable between sessions, so those players are
        /// treated as newcomers next time.
        /// </remarks>
        private string LocalAccountId()
        {
            var sessions = FindAnyObjectByType<QuietVillage.Multiplayer.Sessions.SessionService>();
            var accountId = sessions != null ? sessions.LocalPlayerId : string.Empty;

            return string.IsNullOrEmpty(accountId) ? $"client:{NetworkManager.LocalClientId}" : accountId;
        }

        private static string KeyFor(string accountId) =>
            string.IsNullOrEmpty(accountId) ? Guid.NewGuid().ToString("N") : accountId;
    }
}
