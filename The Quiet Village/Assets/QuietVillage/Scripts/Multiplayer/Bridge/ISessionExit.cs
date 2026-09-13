namespace QuietVillage.Multiplayer.Bridge
{
    /// <summary>
    /// Stands in for UHFPS's single-player scene changes while the local player is in a multiplayer session.
    /// </summary>
    /// <remarks>
    /// UHFPS restarts, loads saves, changes level and quits to the main menu with plain SceneManager loads. In a
    /// session each of those takes one player out of the room, and the room is locked, so there is no way back.
    /// </remarks>
    public interface ISessionExit
    {
        /// <summary>Leaves the session for the lobby. On the host this ends the game for everyone.</summary>
        void LeaveGame();

        /// <summary>Tells the player why a restart, load or level change did nothing.</summary>
        void NotifySceneChangeBlocked();
    }
}
