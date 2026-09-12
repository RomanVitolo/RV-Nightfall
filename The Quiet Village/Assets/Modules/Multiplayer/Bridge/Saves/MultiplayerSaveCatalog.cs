using System.Collections.Generic;
using System.Threading.Tasks;
using Modules.Multiplayer.Scripts.Runtime.Saves;
using UnityEngine;

namespace Modules.Multiplayer.Bridge.Saves
{
    /// <summary>
    /// Answers the lobby's questions about saved games, and holds the one the host chose to resume.
    /// </summary>
    /// <remarks>
    /// The save is read in the lobby rather than in the level, so a missing or damaged file is found while there is
    /// still something to say about it, and the room never starts into a half-loaded world.
    ///
    /// What it holds outlives the lobby scene and the level: a level restart resumes the same save again, which is
    /// what "everyone died, try again" should mean once a save is in play.
    /// </remarks>
    public sealed class MultiplayerSaveCatalog : ISaveCatalog
    {
        /// <summary>The save the room is playing, or <c>null</c> for a new game.</summary>
        public static MultiplayerSave Resumed { get; private set; }

        public bool HasResumedSave => Resumed != null;

        /// <summary>Registers this as the catalog the lobby asks, before any scene loads.</summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Register()
        {
            Resumed = null;
            SaveCatalog.Active = new MultiplayerSaveCatalog();
        }

        public async Task<IReadOnlyList<SaveEntry>> ListAsync()
        {
            var saves = await MultiplayerSaveFile.ListAsync();
            var entries = new List<SaveEntry>(saves.Length);

            foreach (var save in saves)
            {
                var played = save.TimePlayed.TotalHours >= 1
                    ? $"{(int)save.TimePlayed.TotalHours}h {save.TimePlayed.Minutes}m"
                    : $"{save.TimePlayed.Minutes}m";

                entries.Add(new SaveEntry(save.Foldername,
                    $"{save.Scene}  ·  {save.TimeSaved:yyyy-MM-dd HH:mm}  ·  {played} played"));
            }

            return entries;
        }

        public async Task<bool> ResumeAsync(string folder)
        {
            var save = await MultiplayerSaveFile.ReadAsync(folder);
            if (save == null) return false;

            Resumed = save;
            return true;
        }

        public void StartFresh() => Resumed = null;

        /// <summary>Plays from this save from now on, after the host saves the game mid-play.</summary>
        /// <remarks>So that restarting the level after a wipe returns to the last save, not to an older one.</remarks>
        internal static void UseAsResumePoint(MultiplayerSave save) => Resumed = save;
    }
}
