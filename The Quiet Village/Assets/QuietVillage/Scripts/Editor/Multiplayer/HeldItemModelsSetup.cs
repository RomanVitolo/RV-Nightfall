using System.Collections.Generic;
using System.IO;
using System.Text;
using UHFPS.Runtime;
using UnityEditor;
using UnityEngine;

namespace QuietVillage.Multiplayer.Bridge.EditorTools
{
    /// <summary>
    /// Builds <see cref="AvatarHeldItemModels"/>: the prop a teammate's body carries for each player item.
    /// </summary>
    /// <remarks>
    /// The inventory database lists the player items but links none of them to a model, while every item already
    /// has a pickup prefab whose <c>InteractableItem</c> names it by GUID. This joins the two.
    ///
    /// Safe to re-run after adding items. Existing entries keep their model and grip, because grips are tuned by
    /// hand in play and a setup tool should not silently revert that; clear an entry's model to have it found again.
    /// </remarks>
    public static class HeldItemModelsSetup
    {
        private const string AssetPath =
            ProjectPaths.RuntimeResources + "/" + AvatarHeldItemModels.ResourcePath + ".asset";

        [MenuItem("Tools/Quiet Village/Multiplayer/Set Up Held Item Models")]
        public static void Run()
        {
            var inventory = PlayerAssetLookup.FindInventory();
            if (inventory == null || inventory.inventoryDatabase == null)
            {
                Debug.LogError($"{nameof(HeldItemModelsSetup)}: the player prefab has no Inventory with a database. " +
                               "Run Tools > Quiet Village > Multiplayer > Set Up Networked HEROPLAYER first.");
                return;
            }

            var pickups = FindPickupPrefabs();
            var models = LoadOrCreateAsset();
            var report = new StringBuilder($"{nameof(HeldItemModelsSetup)}: ");
            var missing = 0;
            var listed = new HashSet<int>();

            foreach (var section in inventory.inventoryDatabase.Sections)
            {
                foreach (var item in section.Items)
                {
                    if (item == null || item.UsableSettings.usableType != UsableType.PlayerItem) continue;

                    // First item per index wins, as in AvatarHeldItem's database fallback. Items that are not usable
                    // keep default settings that also read as player item 0, but come after the real one. Not skipped
                    // outright: some unusable items, like the candle, are still equipped by combining.
                    var index = item.UsableSettings.playerItemIndex;
                    if (index < 0 || !listed.Add(index)) continue;

                    var entry = models.Entries.Find(e => e != null && e.PlayerItemIndex == index);
                    if (entry == null)
                    {
                        entry = new AvatarHeldItemModels.Entry { PlayerItemIndex = index };
                        models.Entries.Add(entry);
                    }

                    entry.Title = item.Title;
                    if (entry.Model == null && pickups.TryGetValue(item.GUID, out var prefab)) entry.Model = prefab;

                    report.Append($"\n  [{index}] {item.Title}: {(entry.Model != null ? entry.Model.name : "NO MODEL")}");
                    if (entry.Model == null) missing++;
                }
            }

            models.Entries.Sort((a, b) => a.PlayerItemIndex.CompareTo(b.PlayerItemIndex));

            EditorUtility.SetDirty(models);
            AssetDatabase.SaveAssets();

            report.Insert(0, missing > 0 ? $"{missing} item(s) have no model. " : "every player item has a model. ");
            if (missing > 0) Debug.LogWarning(report.ToString(), models);
            else Debug.Log(report.ToString(), models);
        }

        /// <summary>Maps an inventory item GUID to the prefab that puts it on the floor.</summary>
        /// <remarks>
        /// Only prefabs with the <c>InteractableItem</c> on their root count: a locked door or a fusebox also
        /// mentions its key's GUID, and a hand holding a whole door is not the intent. The file text is checked
        /// before loading, so the art packs' prefabs are never deserialized.
        /// </remarks>
        internal static Dictionary<string, GameObject> FindPickupPrefabs()
        {
            var pickups = new Dictionary<string, GameObject>();
            var paths = new List<string>();

            foreach (var guid in AssetDatabase.FindAssets("t:Prefab"))
                paths.Add(AssetDatabase.GUIDToAssetPath(guid));

            // Deterministic: the same prefab wins every run when two pick up the same item.
            paths.Sort(System.StringComparer.Ordinal);

            foreach (var path in paths)
            {
                if (!File.Exists(path) || !File.ReadAllText(path).Contains("PickupItem:")) continue;

                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                var interactable = prefab != null ? prefab.GetComponent<InteractableItem>() : null;
                if (interactable == null || interactable.PickupItem == null) continue;

                var itemGuid = interactable.PickupItem.GUID;
                if (string.IsNullOrEmpty(itemGuid) || pickups.ContainsKey(itemGuid)) continue;

                pickups.Add(itemGuid, prefab);
            }

            return pickups;
        }

        private static AvatarHeldItemModels LoadOrCreateAsset()
        {
            var models = AssetDatabase.LoadAssetAtPath<AvatarHeldItemModels>(AssetPath);
            if (models != null) return models;

            var folder = Path.GetDirectoryName(AssetPath)?.Replace('\\', '/');
            if (!string.IsNullOrEmpty(folder) && !AssetDatabase.IsValidFolder(folder))
                AssetDatabase.CreateFolder(Path.GetDirectoryName(folder)?.Replace('\\', '/'), Path.GetFileName(folder));

            models = ScriptableObject.CreateInstance<AvatarHeldItemModels>();
            AssetDatabase.CreateAsset(models, AssetPath);
            return models;
        }
    }
}
