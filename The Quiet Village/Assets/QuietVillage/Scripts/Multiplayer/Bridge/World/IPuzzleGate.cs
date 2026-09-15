using System;

namespace QuietVillage.Multiplayer.Bridge
{
    /// <summary>
    /// Decides whether this client may use a close-up puzzle right now: one player at a time, first come, first served.
    /// </summary>
    /// <remarks>
    /// Consulted by UHFPS's PuzzleBase and PuzzleBaseBlend before they take over the player's camera, and told when the
    /// player leaves the puzzle. Two players working one keypad each saw their own digits and overwrote each other's
    /// state through World Sync; now the second is told it is in use.
    /// </remarks>
    public interface IPuzzleGate
    {
        /// <summary>Asks to use the puzzle.</summary>
        /// <param name="onGranted">Invoked once the host grants it, if it could not be granted immediately.</param>
        /// <returns><c>true</c> to start now; <c>false</c> if it is in use or the request is still pending.</returns>
        bool TryBeginPuzzle(Action onGranted);

        /// <summary>The local player has left the puzzle; others may use it.</summary>
        void EndPuzzle();
    }
}
