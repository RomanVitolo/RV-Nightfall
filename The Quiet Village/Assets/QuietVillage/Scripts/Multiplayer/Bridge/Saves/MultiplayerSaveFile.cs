using System;
using System.IO;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UHFPS.Runtime;
using UHFPS.Scriptable;
using UHFPS.Tools;
using UnityEngine;

namespace QuietVillage.Multiplayer.Bridge.Saves
{
    /// <summary>
    /// Reads and writes a co-op save, in the same folders and format as UHFPS's own.
    /// </summary>
    /// <remarks>
    /// A save is a folder holding an info file (scene, date, time played) and a data file. Keeping that shape means
    /// the game's existing save list can read these, and the two file names and encryption come from the same
    /// settings asset rather than being invented here.
    ///
    /// UHFPS writes a save from one machine, because in single player there is only one. Here the host assembles it:
    /// the world as the host has it, plus each player's own data, gathered from them. See
    /// <see cref="MultiplayerSave"/> for what goes inside.
    /// </remarks>
    public static class MultiplayerSaveFile
    {
        private static SerializationAsset Settings => SaveGameManager.SerializationAsset;

        /// <summary>Writes a new save folder and returns its name.</summary>
        /// <param name="save">The whole game state: world, players, dropped items.</param>
        /// <param name="scene">Level to reopen when the save is resumed.</param>
        /// <param name="timePlayed">Seconds played, carried forward from earlier sessions of the same save.</param>
        public static async Task<string> WriteAsync(MultiplayerSave save, string scene, float timePlayed)
        {
            var settings = Settings;
            var savesPath = settings.GetSavesPath();
            if (!Directory.Exists(savesPath)) Directory.CreateDirectory(savesPath);

            var folderName = NextFolderName(savesPath, settings, scene);
            var folderPath = Path.Combine(savesPath, folderName);
            if (!Directory.Exists(folderPath)) Directory.CreateDirectory(folderPath);

            var saveId = GameTools.GetGuid();
            var dataFilename = settings.SaveDataName + settings.SaveExtension;

            // The same keys UHFPS's reader expects of an info file; "data" points at the file beside it.
            var info = new StorableCollection
            {
                { "id", saveId },
                { "scene", scene },
                { "dateTime", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") },
                { "timePlayed", timePlayed },
                { "saveType", 0 },
                { "data", dataFilename },
                { "thumbnail", "" }
            };

            var data = save.ToJson(saveId);

            await Write(info, Path.Combine(folderPath, settings.SaveInfoName + settings.SaveExtension));
            await Write(data, Path.Combine(folderPath, dataFilename));

            return folderName;
        }

        /// <summary>Reads a save folder written by <see cref="WriteAsync"/>.</summary>
        /// <returns>The save, or <c>null</c> if the folder is missing, unreadable or not a co-op save.</returns>
        public static async Task<MultiplayerSave> ReadAsync(string folderName)
        {
            var settings = Settings;
            var folderPath = Path.Combine(settings.GetSavesPath(), folderName);
            if (!Directory.Exists(folderPath)) return null;

            try
            {
                var info = await SaveGameManager.SaveGameReader.ReadSave(folderName);

                var dataPath = Path.Combine(folderPath, info.Dataname);
                if (!File.Exists(dataPath)) return null;

                var json = await SerializableEncryptor.Decrypt(settings, dataPath);
                var state = JObject.Parse(json);

                // The pair belong together: an info file left beside another save's data would restore the wrong world.
                if (info.Id != (string)state["id"])
                {
                    Debug.LogError($"{nameof(MultiplayerSaveFile)}: '{folderName}' has mismatched save files.");
                    return null;
                }

                return MultiplayerSave.FromJson(state);
            }
            catch (Exception exception)
            {
                Debug.LogError($"{nameof(MultiplayerSaveFile)}: could not read '{folderName}': {exception.Message}");
                return null;
            }
        }

        /// <summary>Every save on this machine, newest first.</summary>
        public static async Task<SavedGameInfo[]> ListAsync()
        {
            try
            {
                var saves = await SaveGameManager.SaveGameReader.ReadSavesMeta();
                Array.Sort(saves, (a, b) => b.TimeSaved.CompareTo(a.TimeSaved));
                return saves;
            }
            catch (Exception exception)
            {
                Debug.LogError($"{nameof(MultiplayerSaveFile)}: could not list saves: {exception.Message}");
                return Array.Empty<SavedGameInfo>();
            }
        }

        private static async Task Write(object contents, string path)
        {
            var json = JsonConvert.SerializeObject(contents, Formatting.Indented,
                new JsonSerializerSettings { ReferenceLoopHandling = ReferenceLoopHandling.Ignore });

            await SerializableEncryptor.Encrypt(Settings, path, json);
        }

        /// <summary>
        /// A new numbered folder per save, or, in single-save mode, one folder per level that each save replaces.
        /// </summary>
        /// <remarks>
        /// Single-save mode ignores the settings' <c>UseSceneNames</c> and always names the folder after the level. With
        /// UHFPS's one shared folder, saving in one settlement overwrote the room's game in every other, and the lobby
        /// resumes a save into its own level, so one save per level is what it can offer.
        ///
        /// Numbered folders continue from the highest number present rather than from how many there are: after a save
        /// in the middle is deleted, the count points at a folder that still exists and would overwrite it.
        /// </remarks>
        private static string NextFolderName(string savesPath, SerializationAsset settings, string scene)
        {
            var prefix = settings.SaveFolderPrefix;

            if (settings.SingleSave) return prefix + scene.Replace(" ", string.Empty);

            var highest = -1;
            foreach (var directory in Directory.GetDirectories(savesPath, $"{prefix}*"))
            {
                var suffix = Path.GetFileName(directory).Substring(prefix.Length);
                if (int.TryParse(suffix, out var number) && number > highest) highest = number;
            }

            return prefix + (highest + 1).ToString("D3");
        }
    }
}
