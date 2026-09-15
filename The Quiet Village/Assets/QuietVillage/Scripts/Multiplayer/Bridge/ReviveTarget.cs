using UHFPS.Runtime;
using UnityEngine;

namespace QuietVillage.Multiplayer.Bridge
{
    /// <summary>
    /// Lets the local player revive a downed teammate by holding Use on their body.
    /// </summary>
    /// <remarks>
    /// A UHFPS timed interaction, like building a barricade, so it has the same reticle, prompt and progress bar. Remote
    /// bodies have no collider of their own (their CharacterController is off), so this carries a trigger on the Interact
    /// layer, switched on only while the player is down: a teammate on their feet is not something to interact with.
    /// A trigger rather than a solid box, so it neither blocks movement nor the creatures' line of sight.
    ///
    /// The hold is timed here, shortened by the local player's revive perk; the host checks who asked and from how far
    /// (<see cref="PlayerHealthSync.RequestRevive"/>).
    ///
    /// Remote copies only: <see cref="HeroPlayerNetworkSetup"/> attaches it to bodies this client does not own.
    /// </remarks>
    public class ReviveTarget : MonoBehaviour, IInteractTimed, IInteractTitle
    {
        private const float BaseReviveSeconds = 4f;

        // Lying down: long in whichever way they fell, and low.
        private static readonly Vector3 ColliderSize = new(2.2f, 0.8f, 2.2f);
        private static readonly Vector3 ColliderCenter = new(0f, 0.4f, 0f);

        private PlayerHealthSync m_health;
        private HeroPlayerNetworkSetup m_player;
        private BoxCollider m_collider;

        /// <summary>Makes a remote player's body revivable.</summary>
        public static ReviveTarget Attach(PlayerHealthSync health, HeroPlayerNetworkSetup player)
        {
            var holder = new GameObject("ReviveTarget");
            holder.transform.SetParent(health.transform, false);

            var layer = LayerMask.NameToLayer("Interact");
            if (layer >= 0) holder.layer = layer;
            else Debug.LogWarning($"{nameof(ReviveTarget)}: no Interact layer, so downed teammates cannot be revived.", health);

            var target = holder.AddComponent<ReviveTarget>();
            target.m_health = health;
            target.m_player = player;

            target.m_collider = holder.AddComponent<BoxCollider>();
            target.m_collider.isTrigger = true;
            target.m_collider.size = ColliderSize;
            target.m_collider.center = ColliderCenter;
            target.m_collider.enabled = false;

            return target;
        }

        private void Update()
        {
            var downed = m_health != null && m_health.IsDowned;
            if (m_collider != null && m_collider.enabled != downed) m_collider.enabled = downed;
        }

        /// <summary>Seconds to hold, after the local player's revive perk. Read by UHFPS as the hold begins.</summary>
        public float InteractTime
        {
            get => BaseReviveSeconds / Mathf.Max(0.1f, LocalReviveSpeed);
            set { }
        }

        public bool NoInteract => m_health == null || !m_health.IsDowned || !LocalPlayerStanding;

        public void InteractTimed()
        {
            if (!NoInteract) m_health.RequestRevive();
        }

        public TitleParams InteractTitle()
        {
            var seconds = m_health != null ? Mathf.CeilToInt(m_health.BleedOutRemaining) : 0;
            var who = m_player != null ? m_player.DisplayName : "Teammate";

            return new TitleParams
            {
                title = $"{who} is down ({seconds} s)",
                button1 = NoInteract ? null : "Revive"
            };
        }

        private static float LocalReviveSpeed
        {
            get
            {
                var player = LocalPlayerContext.Player;
                var character = player != null ? player.GetComponent<Characters.PlayerCharacter>() : null;
                return character != null ? character.Perks.ReviveSpeed : 1f;
            }
        }

        private static bool LocalPlayerStanding
        {
            get
            {
                var player = LocalPlayerContext.Player;
                var health = player != null ? player.GetComponent<PlayerHealthSync>() : null;
                return health != null && health.IsStanding;
            }
        }
    }
}
