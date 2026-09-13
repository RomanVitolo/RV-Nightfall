using System.IO;
using System.Linq;
using System.Text;
using UHFPS.Runtime;
using UHFPS.Scriptable;
using UnityEditor;
using UnityEngine;

namespace QuietVillage.Multiplayer.Bridge.EditorTools
{
    /// <summary>
    /// Makes inventory items droppable, so what a player carries stays in play when they drop it, die or leave.
    /// </summary>
    /// <remarks>
    /// Dropping, and every multiplayer path built on it (<see cref="World.DroppedItems"/>, <see cref="World.CarriedItems"/>),
    /// needs three things UHFPS's demo data only provides for two items:
    /// <list type="bullet">
    /// <item>the item's <c>ItemObject</c>, the prefab a drop creates;</item>
    /// <item>that prefab listed in the Object References asset, which is how every client finds it by GUID;</item>
    /// <item>a Rigidbody on it, without which <c>Inventory.DropItem</c> refuses to drop.</item>
    /// </list>
    /// Without them, a dead player's keys and scrap vanish with the player.
    ///
    /// The pickups placed in levels have no Rigidbody, so each droppable item gets a drop variant of its pickup prefab in
    /// <c>Assets/QuietVillage/Prefabs/Items/Drops</c> with physics added; the level's own pickups are untouched. The variant's asset GUID
    /// is its reference GUID, the convention the existing references follow.
    ///
    /// This edits two UHFPS data assets, the inventory database and Object References; no code. A UHFPS update that
    /// replaces them reverts it: re-run the tool. Player items (flashlight, weapons, tools) are left out: removing an
    /// equipped item from the inventory is not something UHFPS's item controllers expect.
    ///
    /// Idempotent. An item that already has a drop object keeps it.
    /// </remarks>
    public static class DroppableItemsSetup
    {
        private const string DropsFolder = ProjectPaths.ItemDropPrefabs;
        private const string PlayerItemsSection = "Player Items";

        [MenuItem("Tools/Quiet Village/Multiplayer/Set Up Droppable Items")]
        public static void Run()
        {
            var inventory = PlayerAssetLookup.FindInventory();
            var database = inventory != null ? inventory.inventoryDatabase : null;
            var saveManager = inventory != null ? inventory.GetComponentInChildren<SaveGameManager>(true) : null;
            var references = saveManager != null ? saveManager.ObjectReferences : null;

            if (database == null || references == null)
            {
                Debug.LogError($"{nameof(DroppableItemsSetup)}: the player prefab needs an Inventory with a database and a " +
                               "SaveGameManager with Object References. Run Tools > Quiet Village > Multiplayer > Set Up Networked HEROPLAYER.");
                return;
            }

            var pickups = HeldItemModelsSetup.FindPickupPrefabs();
            var report = new StringBuilder();
            int made = 0, kept = 0, skipped = 0;

            foreach (var section in database.Sections)
            {
                if (section?.Section != null && section.Section.Name == PlayerItemsSection) continue;

                foreach (var item in section.Items)
                {
                    if (item == null) continue;

                    if (item.ItemObject != null && item.ItemObject.Object != null && !string.IsNullOrEmpty(item.ItemObject.GUID))
                    {
                        EnsureReferenced(references, item.ItemObject.GUID, item.ItemObject.Object);
                        MarkDroppable(item);
                        kept++;
                        continue;
                    }

                    if (!pickups.TryGetValue(item.GUID, out var pickup))
                    {
                        report.AppendLine($"  skipped '{item.Title}': no pickup prefab in the project.");
                        skipped++;
                        continue;
                    }

                    var drop = EnsureDropVariant(pickup);
                    if (drop == null)
                    {
                        report.AppendLine($"  FAILED '{item.Title}': could not create a drop prefab from {pickup.name}.");
                        skipped++;
                        continue;
                    }

                    var guid = AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(drop));
                    EnsureReferenced(references, guid, drop);

                    item.ItemObject = new ObjectReference { GUID = guid, Object = drop };
                    MarkDroppable(item);

                    report.AppendLine($"  '{item.Title}' drops as {drop.name}.");
                    made++;
                }
            }

            EditorUtility.SetDirty(database);
            EditorUtility.SetDirty(references);
            AssetDatabase.SaveAssets();

            Debug.Log($"{nameof(DroppableItemsSetup)}: {made} item(s) made droppable, {kept} already were, {skipped} skipped. " +
                      $"Object References now lists {references.References.Count}.\n{report}");
        }

        private static void MarkDroppable(Item item)
        {
            var settings = item.Settings;
            settings.isDroppable = true;
            item.Settings = settings;
        }

        private static void EnsureReferenced(ObjectReferences references, string guid, GameObject prefab)
        {
            if (references.HasReference(guid)) return;

            references.References.Add(new ObjectReferences.ObjectGuidPair { GUID = guid, Object = prefab });
        }

        /// <summary>A variant of a pickup prefab that falls: a Rigidbody, and convex colliders so it may carry one.</summary>
        private static GameObject EnsureDropVariant(GameObject pickup)
        {
            var path = $"{DropsFolder}/Drop_{pickup.name}.prefab";

            var existing = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (existing != null) return existing;

            EnsureFolder(DropsFolder);

            var instance = (GameObject)PrefabUtility.InstantiatePrefab(pickup);
            try
            {
                if (!instance.TryGetComponent<Rigidbody>(out var body)) body = instance.AddComponent<Rigidbody>();
                body.mass = 0.5f;
                body.interpolation = RigidbodyInterpolation.Interpolate;
                body.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;

                // A non-kinematic Rigidbody cannot use a concave mesh collider.
                foreach (var meshCollider in instance.GetComponentsInChildren<MeshCollider>(true))
                    meshCollider.convex = true;

                // Something to land on: a pickup that only had a trigger would fall through the floor.
                if (!instance.GetComponentsInChildren<Collider>(true).Any(c => !c.isTrigger))
                {
                    var bounds = new Bounds(instance.transform.position, Vector3.zero);
                    foreach (var renderer in instance.GetComponentsInChildren<Renderer>(true)) bounds.Encapsulate(renderer.bounds);

                    var box = instance.AddComponent<BoxCollider>();
                    box.center = instance.transform.InverseTransformPoint(bounds.center);
                    box.size = Vector3.Max(bounds.size, Vector3.one * 0.05f);
                }

                return PrefabUtility.SaveAsPrefabAsset(instance, path);
            }
            finally
            {
                Object.DestroyImmediate(instance);
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
