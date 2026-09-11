using System;

namespace Modules.Multiplayer.Bridge
{
    /// <summary>
    /// Decides whether this client may examine a world object right now: first come, first served.
    /// </summary>
    /// <remarks>
    /// Consulted by UHFPS's ExamineController before it lifts an object into view, and by InteractController
    /// before it picks one up — an object in another player's hands is theirs until they put it down.
    /// </remarks>
    public interface IExamineGate
    {
        /// <summary>Asks to examine the object.</summary>
        /// <param name="onGranted">
        /// Invoked once the host grants the object, if it could not be granted immediately.
        /// </param>
        /// <returns><c>true</c> to examine now; <c>false</c> if it is in use or the request is still pending.</returns>
        bool TryBeginExamine(Action onGranted);

        /// <summary>True while another player is examining the object.</summary>
        bool IsHeldByOther { get; }

        /// <summary>Tells the local player why the object did not respond.</summary>
        void NotifyBlocked();
    }
}
