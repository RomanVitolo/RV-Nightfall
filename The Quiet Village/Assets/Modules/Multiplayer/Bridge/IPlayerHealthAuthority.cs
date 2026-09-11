namespace Modules.Multiplayer.Bridge
{
    /// <summary>
    /// Takes over damage and healing the local UHFPS player detects on itself, so the server decides the result.
    /// </summary>
    /// <remarks>
    /// UHFPS changes player health wherever the cause is noticed: a medkit in the inventory, a damage zone, a
    /// fall. The server owns player health, so each of those becomes a request, and the local value only moves
    /// when the server's answer comes back.
    /// </remarks>
    public interface IPlayerHealthAuthority
    {
        /// <summary>Asks the server to damage this player.</summary>
        void RequestDamage(int damage);

        /// <summary>Asks the server to heal this player.</summary>
        void RequestHeal(int healAmount);
    }
}
