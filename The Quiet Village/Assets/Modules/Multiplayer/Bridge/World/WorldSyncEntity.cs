using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UHFPS.Runtime;
using UnityEngine;

namespace Modules.Multiplayer.Bridge.World
{
    /// <summary>
    /// One replicated aspect of a level object — a pickup being taken, a door moving, a puzzle's state —
    /// identified the same way on every client.
    /// </summary>
    /// <remarks>
    /// A plain MonoBehaviour, deliberately not a NetworkBehaviour. The level holds a hundred-odd interactables;
    /// making each a NetworkObject would bring Netcode's rules with it — no deactivating a spawned object on a
    /// client, no nesting one inside another, a GlobalObjectIdHash to persist for each — and UHFPS deactivates,
    /// nests and destroys these objects freely. Instead every entity talks through the level's single
    /// <see cref="WorldSync"/>, keyed by <see cref="Id"/>.
    ///
    /// The id is the target component's GlobalObjectId, written at author time by
    /// Tools > Multiplayer > Set Up World Sync. That makes it identical in every build of the same scene, and
    /// unique even for duplicated objects and prefab instances, which a hand-assigned GUID would not be.
    /// </remarks>
    public abstract class WorldSyncEntity : MonoBehaviour
    {
        [Tooltip("Identity shared by every client. Written by Tools > Multiplayer > Set Up World Sync — do not edit.")]
        [SerializeField] private string m_id;

        private static readonly JsonSerializerSettings SerializerSettings = new()
        {
            // The same settings UHFPS saves with, so a state serialises here exactly as it would to a save file.
            ReferenceLoopHandling = ReferenceLoopHandling.Ignore
        };

        public string Id => m_id;

        /// <summary>Compact form of <see cref="Id"/> used on the wire.</summary>
        public uint Key { get; private set; }

        protected WorldSync World { get; private set; }

        /// <summary>True while this entity is registered with a spawned <see cref="WorldSync"/>.</summary>
        protected bool IsLive => World != null && World.IsSpawned;

        /// <summary>True when local changes should be published. False while the level is still settling.</summary>
        protected bool CanPublish => IsLive && !World.IsSettling;

        internal void Bind(WorldSync world, uint key)
        {
            World = world;
            Key = key;
            OnBound();
        }

        internal void Unbind()
        {
            OnUnbound();
            World = null;
        }

        protected virtual void OnBound() { }

        protected virtual void OnUnbound() { }

        // Inbound traffic. Each subclass overrides only what it replicates.
        internal virtual void ApplyRemoteState(string json) { }

        internal virtual void ApplyRemoteMotionBegin(string json, ulong author) { }

        internal virtual void ApplyRemoteMotion(Vector3 position, Quaternion rotation, ulong author) { }

        internal virtual void ApplyRemoteMotionEnd(string json, ulong author) { }

        internal virtual void OnMotionDenied() { }

        internal virtual void ApplyRemoteTaken() { }

        internal virtual void OnTakeDenied() { }

        internal virtual void ApplyLockOwner(ulong owner) { }

        internal virtual void OnLockDenied() { }

        /// <summary>32-bit FNV-1a of the id: stable across processes, unlike string.GetHashCode.</summary>
        /// <remarks>Public so the setup tool, in the editor assembly, can reject ids whose keys would collide.</remarks>
        public static uint KeyFor(string id)
        {
            unchecked
            {
                var hash = 2166136261u;
                foreach (var character in id)
                {
                    hash ^= character;
                    hash *= 16777619u;
                }

                return hash;
            }
        }

        protected static string Serialize(StorableCollection state) =>
            state == null ? null : JsonConvert.SerializeObject(state, Formatting.None, SerializerSettings);

        protected static JToken Parse(string json)
        {
            if (string.IsNullOrEmpty(json)) return null;

            try
            {
                return JToken.Parse(json);
            }
            catch (JsonException exception)
            {
                Debug.LogWarning($"{nameof(WorldSyncEntity)}: discarded malformed state ({exception.Message}).");
                return null;
            }
        }
    }
}
