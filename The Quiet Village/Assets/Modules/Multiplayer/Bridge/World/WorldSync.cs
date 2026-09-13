using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

namespace Modules.Multiplayer.Bridge.World
{
    /// <summary>
    /// Replicates the level's interactable objects: what was taken, what moved, what changed state.
    /// </summary>
    /// <remarks>
    /// Authority: the client that acts on an object simulates it, and the server relays and arbitrates.
    /// UHFPS interactions are built around the local player — its inventory, its HUD, its camera, puzzle
    /// screens drawn on its display — so they can only run on the client doing the interacting. That client
    /// runs UHFPS unchanged and reports the result; everyone else applies it.
    ///
    /// The server decides the two things that must not be settled by whoever speaks last:
    /// <list type="bullet">
    /// <item>Pickups — the first request wins, so two players grabbing one key do not both end up with it.</item>
    /// <item>Motion — one client at a time may stream an object's movement; the rest follow that stream.</item>
    /// </list>
    ///
    /// One in-scene NetworkObject for the whole level, rather than one per interactable — see
    /// <see cref="WorldSyncEntity"/>. Rooms lock when the game starts, so there are no late joiners to catch
    /// up: every client loads the level fresh in the same scene event, and only changes need to travel.
    /// </remarks>
    public partial class WorldSync : NetworkBehaviour
    {
        [Tooltip("Seconds after the level spawns during which local changes are not published. Rigidbodies " +
                 "settle and scripts initialise independently on every client, and none of that is a player action.")]
        [SerializeField] private float m_settleTime = 2f;

        [Tooltip("Seconds without a motion packet after which the server lets another client move an object.")]
        [SerializeField] private float m_motionOwnershipTimeout = 1.5f;

        private readonly Dictionary<uint, WorldSyncEntity> m_entities = new();

        // Server-side arbitration state.
        private readonly HashSet<uint> m_taken = new();
        private readonly Dictionary<uint, MotionOwner> m_motionOwners = new();
        private readonly Dictionary<uint, ulong> m_locks = new();
        private uint m_dropSerial;

        // Items this client dropped, waiting for the keys the host allocates them. Keyed by a local request number.
        private readonly Dictionary<int, GameObject> m_pendingDrops = new();
        private int m_dropRequest;

        // Server: what each client last said it carries, and where its player last stood. A player's object is
        // already despawned by the time its disconnect is reported, so the position has to be remembered.
        private readonly Dictionary<ulong, string> m_carried = new();
        private readonly Dictionary<ulong, Vector3> m_lastPositions = new();

        // Server: which account each client plays as, so a save gives them back their own belongings.
        private readonly Dictionary<ulong, string> m_accounts = new();

        // Items dropped in the level and still lying there, for a save to record. Keyed by the pickup's key.
        private readonly Dictionary<uint, LiveDrop> m_drops = new();

        /// <summary>An item on the ground: what it is, and the pickup that will be gone once it is taken.</summary>
        private readonly struct LiveDrop
        {
            public readonly string ReferenceGuid;
            public readonly int Quantity;
            public readonly SyncedPickup Pickup;

            public LiveDrop(string referenceGuid, int quantity, SyncedPickup pickup)
            {
                ReferenceGuid = referenceGuid;
                Quantity = quantity;
                Pickup = pickup;
            }
        }

        /// <summary>Lock owner meaning nobody.</summary>
        public const ulong NoOwner = ulong.MaxValue;

        private float m_spawnedAt;

        private struct MotionOwner
        {
            public ulong ClientId;
            public float LastHeard;
        }

        /// <summary>True during the first moments of the level, when local changes are not player actions.</summary>
        public bool IsSettling => !IsSpawned || Time.time - m_spawnedAt < m_settleTime;

        /// <summary>This process' Netcode client id, for entities comparing lock owners.</summary>
        public ulong LocalClientId => NetworkManager != null ? NetworkManager.LocalClientId : NoOwner;

        public override void OnNetworkSpawn()
        {
            m_spawnedAt = Time.time;
            RegisterSceneEntities();

            if (IsServer) NetworkManager.OnClientDisconnectCallback += HandleClientDisconnected;

            // After the entities are registered: a resumed save is applied to them.
            OnSavesSpawn();
        }

        public override void OnNetworkDespawn()
        {
            if (NetworkManager != null) NetworkManager.OnClientDisconnectCallback -= HandleClientDisconnected;

            foreach (var entity in m_entities.Values)
            {
                if (entity != null) entity.Unbind();
            }

            m_entities.Clear();
            m_taken.Clear();
            m_motionOwners.Clear();
            m_locks.Clear();
            m_pendingDrops.Clear();
            m_carried.Clear();
            m_lastPositions.Clear();
            m_accounts.Clear();
            m_drops.Clear();
            m_heldSpawns.Clear();
            m_participants.Clear();

            if (ReferenceEquals(Modules.Multiplayer.Scripts.Runtime.Flow.SpawnGate.Active, this))
                Modules.Multiplayer.Scripts.Runtime.Flow.SpawnGate.Active = null;
        }

        private void Update()
        {
            if (!IsServer || !IsSpawned) return;

            foreach (var client in NetworkManager.ConnectedClientsList)
            {
                var player = client.PlayerObject;
                if (player != null) m_lastPositions[client.ClientId] = player.transform.position;
            }
        }

        private void RegisterSceneEntities()
        {
            var registered = 0;

            // Inactive objects included: a pickup can start hidden and be revealed by a puzzle later.
            foreach (var entity in FindObjectsByType<WorldSyncEntity>(FindObjectsInactive.Include))
            {
                if (entity == null || entity.gameObject.scene != gameObject.scene) continue;

                if (string.IsNullOrEmpty(entity.Id))
                {
                    Debug.LogError($"{nameof(WorldSync)}: '{entity.name}' has no id and will not sync. " +
                                   "Re-run Tools > Multiplayer > Set Up World Sync.", entity);
                    continue;
                }

                var key = WorldSyncEntity.KeyFor(entity.Id);
                if (m_entities.TryGetValue(key, out var existing))
                {
                    Debug.LogError($"{nameof(WorldSync)}: '{entity.name}' collides with '{existing.name}'; only the first " +
                                   "will sync. Re-run Tools > Multiplayer > Set Up World Sync.", entity);
                    continue;
                }

                m_entities.Add(key, entity);
                entity.Bind(this, key);
                registered++;
            }

            Debug.Log($"{nameof(WorldSync)}: {registered} objects synchronised in {gameObject.scene.name}.", this);
        }

        /// <summary>The entity registered under a wire key, e.g. a hiding place a player reports being in.</summary>
        internal bool TryGetEntity(uint key, out WorldSyncEntity entity) =>
            m_entities.TryGetValue(key, out entity) && entity != null;

        private bool IsFromSelf(ulong author) => author == NetworkManager.LocalClientId;

        // ---- Discrete state --------------------------------------------------------------------------

        internal void PublishState(WorldSyncEntity entity, string json)
        {
            if (!IsSpawned || entity == null || json == null) return;

            SubmitStateRpc(entity.Key, json);
        }

        [Rpc(SendTo.Server)]
        private void SubmitStateRpc(uint key, string json, RpcParams rpcParams = default)
        {
            if (!m_entities.ContainsKey(key)) return;

            ApplyStateRpc(key, json, rpcParams.Receive.SenderClientId);
        }

        [Rpc(SendTo.Everyone)]
        private void ApplyStateRpc(uint key, string json, ulong author)
        {
            if (IsFromSelf(author) || !TryGetEntity(key, out var entity)) return;

            entity.ApplyRemoteState(json);
        }

        // ---- Motion ----------------------------------------------------------------------------------

        internal void PublishMotionBegin(WorldSyncEntity entity, string json)
        {
            if (!IsSpawned || entity == null) return;

            BeginMotionRpc(entity.Key, json ?? string.Empty);
        }

        internal void PublishMotion(WorldSyncEntity entity, Vector3 position, Quaternion rotation)
        {
            if (!IsSpawned || entity == null) return;

            MotionRpc(entity.Key, position, rotation);
        }

        internal void PublishMotionEnd(WorldSyncEntity entity, string json)
        {
            if (!IsSpawned || entity == null) return;

            EndMotionRpc(entity.Key, json ?? string.Empty);
        }

        [Rpc(SendTo.Server)]
        private void BeginMotionRpc(uint key, string json, RpcParams rpcParams = default)
        {
            if (!m_entities.ContainsKey(key)) return;

            var sender = rpcParams.Receive.SenderClientId;

            // Two clients started moving the same object (e.g. both pushed a crate). The first keeps it; the
            // other is told to stop publishing and will follow the first one's stream instead.
            if (m_motionOwners.TryGetValue(key, out var owner) && owner.ClientId != sender
                && Time.time - owner.LastHeard < m_motionOwnershipTimeout)
            {
                MotionDeniedRpc(key, RpcTarget.Single(sender, RpcTargetUse.Temp));
                return;
            }

            m_motionOwners[key] = new MotionOwner { ClientId = sender, LastHeard = Time.time };
            ApplyMotionBeginRpc(key, json, sender);
        }

        // Unreliable: a lost pose is superseded by the next one a fiftieth of a second later, and the final
        // resting state travels reliably in EndMotionRpc regardless.
        [Rpc(SendTo.Server, Delivery = RpcDelivery.Unreliable)]
        private void MotionRpc(uint key, Vector3 position, Quaternion rotation, RpcParams rpcParams = default)
        {
            var sender = rpcParams.Receive.SenderClientId;
            if (!m_motionOwners.TryGetValue(key, out var owner) || owner.ClientId != sender) return;

            owner.LastHeard = Time.time;
            m_motionOwners[key] = owner;
            ApplyMotionRpc(key, position, rotation, sender);
        }

        [Rpc(SendTo.Server)]
        private void EndMotionRpc(uint key, string json, RpcParams rpcParams = default)
        {
            var sender = rpcParams.Receive.SenderClientId;

            // A denied client may still send its end; only the current mover's end means anything.
            if (m_motionOwners.TryGetValue(key, out var owner) && owner.ClientId != sender) return;

            m_motionOwners.Remove(key);
            ApplyMotionEndRpc(key, json, sender);
        }

        [Rpc(SendTo.Everyone)]
        private void ApplyMotionBeginRpc(uint key, string json, ulong author)
        {
            if (IsFromSelf(author) || !TryGetEntity(key, out var entity)) return;

            entity.ApplyRemoteMotionBegin(json, author);
        }

        [Rpc(SendTo.Everyone, Delivery = RpcDelivery.Unreliable)]
        private void ApplyMotionRpc(uint key, Vector3 position, Quaternion rotation, ulong author)
        {
            if (IsFromSelf(author) || !TryGetEntity(key, out var entity)) return;

            entity.ApplyRemoteMotion(position, rotation, author);
        }

        [Rpc(SendTo.Everyone)]
        private void ApplyMotionEndRpc(uint key, string json, ulong author)
        {
            if (IsFromSelf(author) || !TryGetEntity(key, out var entity)) return;

            entity.ApplyRemoteMotionEnd(json, author);
        }

        [Rpc(SendTo.SpecifiedInParams)]
        private void MotionDeniedRpc(uint key, RpcParams rpcParams = default)
        {
            if (TryGetEntity(key, out var entity)) entity.OnMotionDenied();
        }

        // ---- Exclusive use ---------------------------------------------------------------------------

        internal void RequestLock(WorldSyncEntity entity)
        {
            if (!IsSpawned || entity == null) return;

            LockRpc(entity.Key);
        }

        internal void ReleaseLock(WorldSyncEntity entity)
        {
            if (!IsSpawned || entity == null) return;

            UnlockRpc(entity.Key);
        }

        [Rpc(SendTo.Server)]
        private void LockRpc(uint key, RpcParams rpcParams = default)
        {
            if (!m_entities.ContainsKey(key)) return;

            var sender = rpcParams.Receive.SenderClientId;

            // First come, first served: whoever holds it keeps it until they let go or leave.
            if (m_locks.TryGetValue(key, out var owner) && owner != sender)
            {
                LockDeniedRpc(key, RpcTarget.Single(sender, RpcTargetUse.Temp));
                return;
            }

            m_locks[key] = sender;
            LockChangedRpc(key, sender);
        }

        [Rpc(SendTo.Server)]
        private void UnlockRpc(uint key, RpcParams rpcParams = default)
        {
            if (!m_locks.TryGetValue(key, out var owner) || owner != rpcParams.Receive.SenderClientId) return;

            m_locks.Remove(key);
            LockChangedRpc(key, NoOwner);
        }

        // Everyone, the requester included: the grant is how it learns it may go ahead, and everyone else learns
        // to refuse the object locally, without a round trip.
        [Rpc(SendTo.Everyone)]
        private void LockChangedRpc(uint key, ulong owner)
        {
            if (TryGetEntity(key, out var entity)) entity.ApplyLockOwner(owner);
        }

        [Rpc(SendTo.SpecifiedInParams)]
        private void LockDeniedRpc(uint key, RpcParams rpcParams = default)
        {
            if (TryGetEntity(key, out var entity)) entity.OnLockDenied();
        }

        /// <summary>
        /// A player who leaves must not take anything with them: not what they carry, not an object's lock.
        /// </summary>
        private void HandleClientDisconnected(ulong clientId)
        {
            DropCarriedItems(clientId);

            var released = new List<uint>();
            foreach (var pair in m_locks)
            {
                if (pair.Value == clientId) released.Add(pair.Key);
            }

            foreach (var key in released)
            {
                m_locks.Remove(key);
                LockChangedRpc(key, NoOwner);
            }

            var abandoned = new List<uint>();
            foreach (var pair in m_motionOwners)
            {
                if (pair.Value.ClientId == clientId) abandoned.Add(pair.Key);
            }

            foreach (var key in abandoned) m_motionOwners.Remove(key);
        }

        // ---- Pickups ---------------------------------------------------------------------------------

        internal void RequestTake(WorldSyncEntity entity)
        {
            if (!IsSpawned || entity == null) return;

            TakeRpc(entity.Key);
        }

        [Rpc(SendTo.Server)]
        private void TakeRpc(uint key, RpcParams rpcParams = default)
        {
            if (!m_entities.ContainsKey(key)) return;

            var sender = rpcParams.Receive.SenderClientId;
            if (!m_taken.Add(key))
            {
                TakeDeniedRpc(key, RpcTarget.Single(sender, RpcTargetUse.Temp));
                return;
            }

            ApplyTakenRpc(key, sender);
        }

        [Rpc(SendTo.Everyone)]
        private void ApplyTakenRpc(uint key, ulong taker)
        {
            if (IsFromSelf(taker) || !TryGetEntity(key, out var entity)) return;

            entity.ApplyRemoteTaken();
        }

        [Rpc(SendTo.SpecifiedInParams)]
        private void TakeDeniedRpc(uint key, RpcParams rpcParams = default)
        {
            if (TryGetEntity(key, out var entity)) entity.OnTakeDenied();
        }

        // ---- Dropped items ---------------------------------------------------------------------------

        /// <summary>Asks the host to make an item this client just dropped exist for everyone.</summary>
        internal void PublishDrop(GameObject dropped, string referenceGuid, int quantity)
        {
            if (!IsSpawned || dropped == null) return;

            var request = ++m_dropRequest;
            m_pendingDrops[request] = dropped;

            var pose = dropped.transform;
            DropRpc(request, referenceGuid, quantity, pose.position, pose.rotation);
        }

        [Rpc(SendTo.Server)]
        private void DropRpc(int request, string referenceGuid, int quantity, Vector3 position, Quaternion rotation,
            RpcParams rpcParams = default)
        {
            ApplyDropRpc(AllocateDropKeys(), request, referenceGuid, quantity, position, rotation,
                rpcParams.Receive.SenderClientId);
        }

        /// <summary>A run of <see cref="DroppedItems.KeyCount"/> keys no entity holds.</summary>
        /// <remarks>
        /// Only the host allocates, and each drop gets a new serial, so drops never share keys; the check only guards
        /// against a hash landing on a scene object's key.
        /// </remarks>
        private uint AllocateDropKeys()
        {
            while (true)
            {
                var key = WorldSyncEntity.KeyFor($"drop:{++m_dropSerial}");

                var free = true;
                for (uint i = 0; i < DroppedItems.KeyCount && free; i++)
                {
                    free = !m_entities.ContainsKey(key + i);
                }

                if (free) return key;
            }
        }

        [Rpc(SendTo.Everyone)]
        private void ApplyDropRpc(uint key, int request, string referenceGuid, int quantity, Vector3 position,
            Quaternion rotation, ulong author)
        {
            if (!IsFromSelf(author))
            {
                var copy = DroppedItems.CreateCopy(referenceGuid, quantity, position, rotation);
                if (copy != null) RegisterDrop(copy, key, false, author, referenceGuid, quantity);
                return;
            }

            if (m_pendingDrops.Remove(request, out var dropped) && dropped != null)
            {
                RegisterDrop(dropped, key, true, author, referenceGuid, quantity);
                return;
            }

            // Taken back before its keys arrived, so the pickup was never reported. Everyone else has a copy now.
            DropGoneRpc(key + DroppedItems.PickupSlot);
        }

        private void RegisterDrop(GameObject dropped, uint key, bool isAuthor, ulong author,
            string referenceGuid, int quantity)
        {
            var entities = DroppedItems.AttachEntities(dropped);
            for (var i = 0; i < entities.Length; i++)
            {
                var entity = entities[i];
                if (entity == null) continue;

                var entityKey = key + (uint)i;
                m_entities[entityKey] = entity;
                entity.Bind(this, entityKey);
            }

            if (entities[DroppedItems.PickupSlot] is SyncedPickup pickup)
                m_drops[key + DroppedItems.PickupSlot] = new LiveDrop(referenceGuid, quantity, pickup);

            if (entities[DroppedItems.MotionSlot] is not SyncedMotionEntity motion) return;

            if (isAuthor) motion.StartAuthoring();
            else motion.FollowFrom(author);
        }

        [Rpc(SendTo.Server)]
        private void DropGoneRpc(uint pickupKey, RpcParams rpcParams = default)
        {
            if (!m_taken.Add(pickupKey)) return;

            ApplyTakenRpc(pickupKey, rpcParams.Receive.SenderClientId);
        }

        // ---- Carried items ---------------------------------------------------------------------------

        /// <summary>Tells the host what this client carries, so it can drop it for them if they leave.</summary>
        /// <param name="encoded">See <see cref="DroppedItems.Encode"/>.</param>
        internal void ReportCarried(string encoded)
        {
            if (!IsSpawned) return;

            CarriedRpc(encoded ?? string.Empty);
        }

        [Rpc(SendTo.Server)]
        private void CarriedRpc(string encoded, RpcParams rpcParams = default)
        {
            m_carried[rpcParams.Receive.SenderClientId] = encoded;
        }

        /// <summary>
        /// Drops what a departed player carried where they last stood, as the host: the player's own client, which
        /// would normally drop things, is gone.
        /// </summary>
        /// <remarks>
        /// A player who died has already dropped everything and reported an empty inventory, so this finds nothing.
        /// </remarks>
        private void DropCarriedItems(ulong clientId)
        {
            // Without a position there is nowhere sensible to put things; a player who never spawned carried nothing.
            var hadPosition = m_lastPositions.Remove(clientId, out var feet);
            if (!m_carried.Remove(clientId, out var encoded) || !hadPosition) return;

            var items = DroppedItems.Decode(encoded);
            for (var i = 0; i < items.Count; i++)
            {
                var pose = DroppedItems.ScatterPose(feet, i, items.Count);
                var item = items[i];

                var dropped = DroppedItems.CreateCopy(item.ReferenceGuid, item.Quantity, pose.position, pose.rotation);
                if (dropped != null) PublishDrop(dropped, item.ReferenceGuid, item.Quantity);
            }
        }
    }
}
