using System.Collections.Generic;
using System.Threading.Tasks;

namespace QuietVillage.Multiplayer.Saves
{
    /// <summary>One saved game, as the lobby needs to show it.</summary>
    public readonly struct SaveEntry
    {
        /// <summary>Folder the save lives in; what the lobby hands back to resume it.</summary>
        public readonly string Folder;

        /// <summary>What to show in the list, e.g. when it was saved and for how long it has been played.</summary>
        public readonly string Label;

        /// <summary>Scene name of the level the save was made in; resuming it has to load that level.</summary>
        public readonly string Level;

        public SaveEntry(string folder, string label, string level)
        {
            Folder = folder;
            Label = label;
            Level = level;
        }
    }

    /// <summary>
    /// The saved games on this machine, and the one the host has chosen to resume.
    /// </summary>
    /// <remarks>
    /// Saves are made of UHFPS's own data, which lives in the game assembly along with the bridge that reads it.
    /// This module cannot reference that assembly — the dependency only runs the other way — so the lobby asks for
    /// saves through this, and the bridge answers.
    /// </remarks>
    public interface ISaveCatalog
    {
        /// <summary>True once a save has been chosen and read, so starting the game resumes it.</summary>
        bool HasResumedSave { get; }

        /// <summary>Every save on this machine, newest first.</summary>
        Task<IReadOnlyList<SaveEntry>> ListAsync();

        /// <summary>Reads a save and holds it for the next game this room starts.</summary>
        /// <returns><c>false</c> if it could not be read, in which case nothing is resumed.</returns>
        Task<bool> ResumeAsync(string folder);

        /// <summary>Forgets the chosen save; the next game starts from the beginning.</summary>
        void StartFresh();
    }

    /// <summary>
    /// Where the bridge leaves its <see cref="ISaveCatalog"/> for the lobby to find.
    /// </summary>
    /// <remarks>
    /// A static stands in for the DI container we are deferring, as elsewhere in this module. Null until the bridge
    /// registers, and the lobby simply offers no saves until then.
    /// </remarks>
    public static class SaveCatalog
    {
        public static ISaveCatalog Active { get; set; }
    }
}
