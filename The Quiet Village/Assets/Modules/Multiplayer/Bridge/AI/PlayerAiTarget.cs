using Modules.Multiplayer.Bridge.World;
using Unity.Netcode;
using UHFPS.Runtime;
using UHFPS.Runtime.States;
using UnityEngine;

namespace Modules.Multiplayer.Bridge
{
    /// <summary>
    /// Presents one player to the host's AI: alive or dead, hidden or not, and where to send damage.
    /// </summary>
    /// <remarks>
    /// Authority, per value:
    /// <list type="bullet">
    /// <item>Health and death: server — read from <see cref="PlayerHealthSync"/>, which owns them.</item>
    /// <item>Hiding and invisibility: owner. Only the owner runs UHFPS's player state machine, so only it
    /// knows; it publishes what it sees. The flags only change what the AI perceives, not any outcome a
    /// client could profit from misreporting beyond hiding, which UHFPS already lets it do.</item>
    /// <item>Damage and forced unhiding: server-originated only, because only the host runs the AI.</item>
    /// </list>
    /// </remarks>
    public class PlayerAiTarget : NetworkBehaviour, IAITarget
    {
        private const uint NoHidingPlace = 0;

        [SerializeField] private PlayerHealthSync m_health;
        [SerializeField] private PlayerStateMachine m_stateMachine;
        [SerializeField] private PlayerHealth m_playerHealth;

        private readonly NetworkVariable<bool> m_hiding =
            new(false, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);

        private readonly NetworkVariable<bool> m_fullyHidden =
            new(false, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);

        // The hiding place's World Sync key: the only identity a scene object has on every client.
        private readonly NetworkVariable<uint> m_hidingPlaceKey =
            new(NoHidingPlace, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);

        private readonly NetworkVariable<bool> m_invisibleToEnemies =
            new(false, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);

        private readonly NetworkVariable<bool> m_invisibleToAllies =
            new(false, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);

        private WorldSync m_world;

        public bool IsDead => m_health != null && m_health.Health <= 0;

        public int Health => m_health != null ? m_health.Health : 0;

        public bool IsHiding => m_hiding.Value;

        public bool IsFullyHidden => m_fullyHidden.Value;

        public HideInteract HidingPlace
        {
            get
            {
                var key = m_hidingPlaceKey.Value;
                if (key == NoHidingPlace) return null;

                // The level's WorldSync, found on first need: it lives in the level scene, this on the player.
                if (m_world == null) m_world = FindAnyObjectByType<WorldSync>();

                return m_world != null && m_world.TryGetEntity(key, out var entity) && entity is SyncedHidingPlace place
                    ? place.HideInteract
                    : null;
            }
        }

        public bool IsInvisibleTo(NPCStateMachine.NPCTypeEnum npcType) =>
            npcType == NPCStateMachine.NPCTypeEnum.Enemy ? m_invisibleToEnemies.Value : m_invisibleToAllies.Value;

        public void ApplyDamage(int damage, Transform source)
        {
            // An attack animation plays on every client, and its event reaches the AI state there too; only the
            // server's copy of that attack is real.
            if (!IsServer || m_health == null) return;

            m_health.ApplyServerDamage(damage);
        }

        public void ForceUnhide()
        {
            if (!IsServer) return;

            if (IsOwner) UnhideLocally();
            else UnhideRpc();
        }

        /// <summary>Hiding is driven by the owner's UHFPS state, so only the owner can end it.</summary>
        [Rpc(SendTo.Owner, InvokePermission = RpcInvokePermission.Server)]
        private void UnhideRpc() => UnhideLocally();

        private void UnhideLocally()
        {
            var place = CurrentHidingState()?.HidingPlace;
            if (place != null) place.Unhide(true);
        }

        private void Update()
        {
            if (IsOwner) PublishLocalState();
        }

        private void PublishLocalState()
        {
            var hideState = CurrentHidingState();
            var hiding = hideState != null;
            var fullyHidden = hiding && hideState.IsFullyHidden;

            var place = hiding ? hideState.HidingPlace : null;
            var key = place != null && place.TryGetComponent(out SyncedHidingPlace syncedPlace) ? syncedPlace.Key : NoHidingPlace;

            // Written only on change, so an unchanged value is never marked dirty and resent.
            if (m_hiding.Value != hiding) m_hiding.Value = hiding;
            if (m_fullyHidden.Value != fullyHidden) m_fullyHidden.Value = fullyHidden;
            if (m_hidingPlaceKey.Value != key) m_hidingPlaceKey.Value = key;

            if (m_playerHealth == null) return;

            if (m_invisibleToEnemies.Value != m_playerHealth.IsInvisibleToEnemies)
                m_invisibleToEnemies.Value = m_playerHealth.IsInvisibleToEnemies;
            if (m_invisibleToAllies.Value != m_playerHealth.IsInvisibleToAllies)
                m_invisibleToAllies.Value = m_playerHealth.IsInvisibleToAllies;
        }

        /// <summary>UHFPS's hiding state for this player, or <c>null</c> when not hiding. Owner only.</summary>
        private HidingStateAsset.HidingPlayerState CurrentHidingState()
        {
            if (m_stateMachine == null || !m_stateMachine.IsCurrent(PlayerStateMachine.HIDING_STATE)) return null;

            return m_stateMachine.GetState<HidingStateAsset>() as HidingStateAsset.HidingPlayerState;
        }
    }
}
