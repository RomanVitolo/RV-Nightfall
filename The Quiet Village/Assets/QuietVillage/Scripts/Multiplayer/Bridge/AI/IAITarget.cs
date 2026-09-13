using UHFPS.Runtime;
using UnityEngine;

namespace QuietVillage.Multiplayer.Bridge
{
    /// <summary>
    /// What an AI needs to know about the player it pursues, answered the same way for every player.
    /// </summary>
    /// <remarks>
    /// The host runs the AI, and on the host a remote player's UHFPS components are switched off: its
    /// PlayerHealth never changes and its state machine never enters Hiding. Reading those directly would give
    /// the AI a picture of every remote player frozen at the moment they spawned — never dead, never hidden.
    /// This interface answers from replicated state instead, and routes the AI's actions (damage, dragging a
    /// player out of a hiding place) to wherever they have to happen.
    /// </remarks>
    public interface IAITarget
    {
        /// <summary>Dead by the server-authoritative health.</summary>
        bool IsDead { get; }

        /// <summary>Current server-authoritative health.</summary>
        int Health { get; }

        /// <summary>In a hiding place, as reported by the player's own client.</summary>
        bool IsHiding { get; }

        /// <summary>Hidden completely, so that looking at the hiding place does not reveal them.</summary>
        bool IsFullyHidden { get; }

        /// <summary>The hiding place the player is in, or <c>null</c>.</summary>
        HideInteract HidingPlace { get; }

        bool IsInvisibleTo(NPCStateMachine.NPCTypeEnum npcType);

        /// <summary>Damages the player. Only takes effect on the server, which owns player health.</summary>
        void ApplyDamage(int damage, Transform source);

        /// <summary>Pulls the player out of their hiding place, on the client that controls them.</summary>
        void ForceUnhide();
    }

    /// <summary>
    /// Lets a networked NPC take over damage dealt to it, so a client's shot counts on the host that owns the NPC.
    /// </summary>
    public interface INpcDamageRelay
    {
        /// <returns><c>true</c> if the damage was sent elsewhere and must not be applied here.</returns>
        bool TryRelayDamage(int damage);

        /// <returns><c>true</c> if the kill was sent elsewhere and must not be applied here.</returns>
        bool TryRelayKill();
    }
}
