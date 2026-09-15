using System;
using System.IO;
using QuietVillage.Gameplay.Survival;
using QuietVillage.Multiplayer.Bridge.EditorTools;
using UHFPS.Runtime;
using UHFPS.Scriptable;
using UnityEditor;
using UnityEngine;

namespace QuietVillage.Gameplay.EditorTools
{
    /// <summary>
    /// Adds the survival tools that are inventory items, Flare and Holy Water, to UHFPS's inventory database, with a pickup
    /// prefab each for the level builder to place as loot.
    /// </summary>
    /// <remarks>
    /// Both are UHFPS custom-event items, removed on use: <see cref="SurvivalDirector"/> hooks their use by title, so the
    /// titles must match <see cref="SurvivalDirector.FlareTitle"/> and <see cref="SurvivalDirector.HolyWaterTitle"/>. Their
    /// localization keys start with UHFPS's '*' (plain text), so the game's localization does not rename them.
    ///
    /// Each is modelled on the healing potion (an ordinary item, outside UHFPS's Player Items section, which the drop setup
    /// leaves out) and takes its icon from a look-alike: the candle for the flare. Each pickup is an unpacked copy of a
    /// look-alike's pickup pointed at the new item; unpacked because the drop setup finds pickups by their serialized item
    /// field, which a prefab variant does not write out. Then Set Up Droppable Items runs, so both can be dropped and
    /// survive a death like the rest.
    ///
    /// Edits UHFPS's inventory database and Object References, as Set Up Droppable Items does. Idempotent: an item that
    /// already exists by title keeps its GUID. After running it, rebuild a level (Tools > Quiet Village > Build Greybox)
    /// to have flares and holy water among its loot; dawn supplies hand out flares either way.
    /// </remarks>
    public static class SurvivalToolsSetup
    {
        private const string UhfpsPrefabs = "Assets/ThunderWire Studio/UHFPS/Content/Prefabs";
        private const string PlayerItemsSection = "Player Items";
        private const string TemplateTitle = "Healing Potion";
        private const string PickupsFolder = "Assets/QuietVillage/Prefabs/Items/Pickups";

        public const string FlarePickupPath = PickupsFolder + "/Pickup_Flare.prefab";
        public const string HolyWaterPickupPath = PickupsFolder + "/Pickup_HolyWater.prefab";

        private const string CandlePickupPath = UhfpsPrefabs + "/Interact/Inventory/PlayerItems/PlayerItem_Candle.prefab";
        private const string PotionPickupPath = UhfpsPrefabs + "/Interact/Inventory/Health/Health_HealingPotion.prefab";

        [MenuItem("Tools/Quiet Village/Survival/Set Up Tool Items")]
        public static void Run()
        {
            var inventory = PlayerAssetLookup.FindInventory();
            var database = inventory != null ? inventory.inventoryDatabase : null;
            if (database == null)
            {
                Debug.LogError($"{nameof(SurvivalToolsSetup)}: the player prefab has no Inventory with a database. " +
                               "Run Tools > Quiet Village > Multiplayer > Set Up Networked HEROPLAYER first.");
                return;
            }

            var flare = EnsureItem(database, SurvivalDirector.FlareTitle, "Candle",
                "A road flare. Strike it and drop it: creatures will not step into its red light while it burns.");
            var holyWater = EnsureItem(database, SurvivalDirector.HolyWaterTitle, TemplateTitle,
                "Blessed water from the chapel font. Throw it at your feet to drive back and stun what comes close.");

            if (flare == null || holyWater == null)
            {
                Debug.LogError($"{nameof(SurvivalToolsSetup)}: the Candle or Healing Potion item to copy from is missing " +
                               "from the inventory database; nothing was added.");
                return;
            }

            EditorUtility.SetDirty(database);
            AssetDatabase.SaveAssets();

            var flarePickup = EnsurePickup(FlarePickupPath, CandlePickupPath, flare.GUID);
            var holyWaterPickup = EnsurePickup(HolyWaterPickupPath, PotionPickupPath, holyWater.GUID);

            // So a dead player's flares and holy water drop where they fall, like everything else.
            DroppableItemsSetup.Run();

            Debug.Log($"{nameof(SurvivalToolsSetup)}: '{flare.Title}' ({flare.GUID}) and '{holyWater.Title}' ({holyWater.GUID}) " +
                      $"are in the inventory database; pickups {(flarePickup != null ? "ok" : "MISSING")} / " +
                      $"{(holyWaterPickup != null ? "ok" : "MISSING")}. Rebuild the levels to place them as loot.");
        }

        private static Item EnsureItem(InventoryDatabase database, string title, string iconTitle, string description)
        {
            InventoryDatabase.ItemsSection templateSection = null, existingSection = null;
            Item template = null, icon = null, existing = null;

            foreach (var section in database.Sections)
            {
                foreach (var item in section.Items)
                {
                    if (item == null) continue;

                    if (item.Title == title)
                    {
                        existing = item;
                        existingSection = section;
                    }

                    if (template == null && item.Title == TemplateTitle)
                    {
                        template = item;
                        templateSection = section;
                    }

                    if (icon == null && item.Title == iconTitle) icon = item;
                }
            }

            if (template == null) return null;

            if (existing != null)
            {
                // An earlier run filed it among the player items, where drops are never set up; move it, keeping its GUID.
                if (existingSection != templateSection && existingSection?.Section?.Name == PlayerItemsSection)
                {
                    existingSection.Items.Remove(existing);
                    existing.SectionGUID = template.SectionGUID;
                    templateSection.Items.Add(existing);
                }

                return existing;
            }

            var created = template.DeepCopy();
            if (icon != null) created.Icon = icon.Icon;
            created.GUID = Guid.NewGuid().ToString("N");
            created.SectionGUID = template.SectionGUID;
            created.Title = title;
            created.Description = description;
            created.ItemObject = new ObjectReference();
            created.CombineSettings = Array.Empty<Item.ItemCombineSettings>();

            created.Settings = new Item.ItemSettings
            {
                isUsable = true,
                isStackable = true,
                isDroppable = true,
                isDiscardable = true,
                canBindShortcut = true
            };

            created.UsableSettings = new Item.ItemUsableSettings
            {
                usableType = UsableType.CustomEvent,
                playerItemIndex = -1,
                removeOnUse = true
            };

            created.Properties = new Item.ItemProperties { maxStack = 5 };

            // '*' tells UHFPS's localization to show the text as written.
            created.LocalizationSettings = new Item.Localization
            {
                titleKey = new GString("*" + title, title),
                descriptionKey = new GString("*" + description, description)
            };

            templateSection.Items.Add(created);
            return created;
        }

        private static GameObject EnsurePickup(string path, string templatePath, string itemGuid)
        {
            var existing = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (existing != null && PrefabUtility.GetPrefabAssetType(existing) != PrefabAssetType.Variant) return existing;

            // Made by an earlier run as a variant; rebuilt standalone.
            if (existing != null) AssetDatabase.DeleteAsset(path);

            var template = AssetDatabase.LoadAssetAtPath<GameObject>(templatePath);
            if (template == null)
            {
                Debug.LogError($"{nameof(SurvivalToolsSetup)}: pickup to copy not found at {templatePath}.");
                return null;
            }

            EnsureFolder(PickupsFolder);

            var instance = (GameObject)PrefabUtility.InstantiatePrefab(template);
            try
            {
                PrefabUtility.UnpackPrefabInstance(instance, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);

                var interactable = instance.GetComponent<InteractableItem>();
                if (interactable == null) return null;

                interactable.InteractableType = InteractableItem.InteractableTypeEnum.InventoryItem;
                interactable.PickupItem.GUID = itemGuid;
                interactable.Quantity = 1;
                interactable.UseInventoryTitle = true;
                interactable.AutoEquip = false;
                interactable.AutoShortcut = false;

                return PrefabUtility.SaveAsPrefabAsset(instance, path);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(instance);
            }
        }

        private static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path)) return;

            var parent = Path.GetDirectoryName(path)?.Replace('\\', '/');
            EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, Path.GetFileName(path));
        }
    }
}
