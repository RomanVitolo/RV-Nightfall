using Newtonsoft.Json.Linq;

namespace QuietVillage.Multiplayer.Bridge.World
{
    /// <summary>
    /// Level state that belongs in a co-op save but is not a UHFPS saveable, such as the survival clock and barricades.
    /// </summary>
    /// <remarks>
    /// World Sync saves the level through its entities, which stand for UHFPS <c>ISaveable</c>s. Game systems built on
    /// Netcode keep their state in NetworkVariables instead, and wrapping them as saveables would make World Sync
    /// replicate them a second time. So they join the save directly: the host captures them when saving, and they read
    /// their own entry back when they spawn into a resumed game.
    ///
    /// Server only. A participant registers with the level's <see cref="WorldSync"/> on the host and asks
    /// <see cref="WorldSync.TryGetResumedState"/> for its entry when it spawns; nothing runs on clients, which receive the
    /// result through the participant's own replication.
    /// </remarks>
    public interface IWorldSaveParticipant
    {
        /// <summary>Unique name of this participant's entry in the save.</summary>
        string SaveKey { get; }

        /// <summary>The state to save, on the host. <c>null</c> saves nothing.</summary>
        JToken CaptureSaveState();
    }
}
