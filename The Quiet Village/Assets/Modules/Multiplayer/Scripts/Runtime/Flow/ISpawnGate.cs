using System;

namespace Modules.Multiplayer.Scripts.Runtime.Flow
{
    /// <summary>
    /// Decides whether a player may be spawned yet, or must wait for the world they are joining.
    /// </summary>
    /// <remarks>
    /// A room resuming a save has to give each client the saved world before its player appears in it, or the
    /// player arrives in an untouched level and is corrected a moment later. What "ready" means belongs to the
    /// bridge, which owns the world and the save; this module only asks.
    /// </remarks>
    public interface ISpawnGate
    {
        /// <summary>Holds a spawn until this client is ready, and runs it then.</summary>
        /// <returns><c>false</c> to spawn immediately, which is the normal case.</returns>
        bool TryHold(ulong clientId, Action spawn);
    }

    /// <summary>Where the bridge leaves its <see cref="ISpawnGate"/>. Null means spawn at once.</summary>
    public static class SpawnGate
    {
        public static ISpawnGate Active { get; set; }
    }
}
