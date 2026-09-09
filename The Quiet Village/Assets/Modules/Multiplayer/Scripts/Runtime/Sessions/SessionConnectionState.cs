namespace Modules.Multiplayer.Scripts.Runtime.Sessions
{
    /// <summary>
    /// Lifecycle of this process' connection to a multiplayer session, surfaced for UI.
    /// </summary>
    public enum SessionConnectionState
    {
        Disconnected,
        SigningIn,
        Connecting,
        Connected,
        Error
    }
}
