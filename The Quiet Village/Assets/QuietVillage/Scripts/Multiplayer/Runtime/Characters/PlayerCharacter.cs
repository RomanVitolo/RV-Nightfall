using Unity.Netcode;
using UnityEngine;

namespace QuietVillage.Multiplayer.Characters
{
    /// <summary>
    /// The character a spawned player is: which body everyone else sees, and which role's perks apply to them.
    /// </summary>
    /// <remarks>
    /// Authority: server, fixed at spawn. <see cref="Flow.NetworkPlayerSpawner"/> assigns it before spawning, and it travels
    /// inside the spawn itself (<see cref="OnSynchronize{T}"/>) rather than as a NetworkVariable. So every client knows
    /// the character in its very first <c>OnNetworkSpawn</c>, before the bridge builds the remote body, the name tag and
    /// the held item around it. A value arriving a moment later would mean building the Worker, then tearing it down.
    ///
    /// It never changes during a level. Rooms lock when the game starts, and a restart spawns everyone afresh.
    ///
    /// Also carries whether this body is a player coming back mid-level (<see cref="Returning"/>), for the same reason:
    /// the owner decides its starting kit and the host its starting health as the object spawns.
    /// </remarks>
    public class PlayerCharacter : NetworkBehaviour
    {
        [SerializeField] private CharacterCatalog m_catalog;

        public CharacterCatalog Catalog => m_catalog;

        /// <summary>The resolved choice. Valid on every client from <c>OnNetworkSpawn</c> on.</summary>
        public CharacterChoice Choice { get; private set; } = CharacterChoice.None;

        /// <summary>The character, or <c>null</c> if the catalog is missing or empty.</summary>
        public CharacterCatalog.Character Character => m_catalog != null ? m_catalog.Find(Choice.CharacterId) : null;

        /// <summary>The body variant, or <c>null</c> without a character.</summary>
        public CharacterCatalog.Variant Variant => Character?.VariantAt(Choice.Variant);

        /// <summary>This player's perks; perks that change nothing when the character is unknown.</summary>
        public CharacterCatalog.Perks Perks => Character?.Perks ?? CharacterCatalog.NoPerks;

        /// <summary>
        /// True for a player put back into a level they already died in, e.g. at dawn. Valid on every client from
        /// <c>OnNetworkSpawn</c> on.
        /// </summary>
        /// <remarks>
        /// A returning player starts empty-handed and weak: their belongings were dropped where they died, and neither a
        /// second starter kit nor the health a resumed save gave them at the start is theirs to have again.
        /// </remarks>
        public bool Returning { get; private set; }

        /// <summary>Server, before spawning: decides who this player is.</summary>
        /// <param name="returning">True when respawning a player who died earlier in this level.</param>
        public void Assign(CharacterChoice choice, bool returning = false)
        {
            if (IsSpawned)
            {
                Debug.LogWarning($"{nameof(PlayerCharacter)}: a character can only be assigned before the player spawns.", this);
                return;
            }

            Choice = m_catalog != null ? m_catalog.Resolve(choice) : choice;
            Returning = returning;
        }

        protected override void OnSynchronize<T>(ref BufferSerializer<T> serializer)
        {
            var encoded = Choice.Encode();
            var returning = Returning;
            serializer.SerializeValue(ref encoded);
            serializer.SerializeValue(ref returning);

            if (serializer.IsReader)
            {
                Returning = returning;
                var received = CharacterChoice.Parse(encoded);
                Choice = m_catalog != null ? m_catalog.Resolve(received) : received;
            }
        }
    }
}
