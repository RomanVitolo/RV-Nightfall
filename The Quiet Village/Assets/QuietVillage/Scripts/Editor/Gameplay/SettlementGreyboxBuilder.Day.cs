using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using QuietVillage.Gameplay.Survival;
using UHFPS.Runtime;
using UnityEditor;
using UnityEngine;
using UnityEngine.AI;
using Object = UnityEngine.Object;
using Random = System.Random;

namespace QuietVillage.Gameplay.EditorTools
{
    /// <summary>
    /// What there is to do before night: crypts to search, some locked, loot to find, and a generator whose floodlights
    /// hold creatures off the chapel for as long as its fuel lasts.
    /// </summary>
    /// <remarks>
    /// Built from UHFPS's own pieces, which World Sync already replicates: pickups, doors, drawers, a padlock puzzle and the
    /// lockpick minigame. Storage trunks and breakable crates are left out on purpose. A trunk's contents are appended to,
    /// not replaced, each time its state arrives, and a crate rolls its loot separately on every machine.
    ///
    /// Loot is placed generously and grouped (<see cref="LootSpot"/>): each game the host's seed keeps a share of every
    /// group, so where things are changes from game to game while the level itself does not. The scrap the greybox already
    /// places stays where it is, always, so a game can always be won.
    ///
    /// Furniture and clutter are plain solid props: the drag and physics UHFPS gives crates are stripped, so they bake into
    /// the NavMesh like graves do and nothing extra replicates.
    /// </remarks>
    public static partial class SettlementGreyboxBuilder
    {
        private const string UhfpsPrefabs = "Assets/ThunderWire Studio/UHFPS/Content/Prefabs";

        private const string DoorPrefabPath = UhfpsPrefabs + "/Interact/Dynamic/Openable/Locked/DynamicDoor_Locked1.prefab";
        private const string CabinetPrefabPath = UhfpsPrefabs + "/Interact/Dynamic/Pullable/Cabinet_Dynamic.prefab";
        private const string PadlockPrefabPath = UhfpsPrefabs + "/Interact/Puzzles/Puzzle_Padlock.prefab";
        private const string ChestLockpickPrefabPath = UhfpsPrefabs + "/Interact/Puzzles/Puzzle_ChestLockpick.prefab";
        private const string DoorLockpickModelPath = UhfpsPrefabs + "/Interact/Puzzles/Lockpicks/Lockpick_Door.prefab";
        private const string NotePrefabPath = UhfpsPrefabs + "/Interact/Papers/Paper_B.prefab";
        private const string GeneratorPrefabPath = UhfpsPrefabs + "/Interact/Generator/Generator.prefab";
        private const string FloodlightPrefabPath = UhfpsPrefabs + "/Interact/Generator/FloodLight.prefab";

        private static readonly string[] CratePaths = { UhfpsPrefabs + "/Props/Crate_Big.prefab", UhfpsPrefabs + "/Props/Crate_Small.prefab" };
        private const string BarrelPath = UhfpsPrefabs + "/Props/Barrel_Heavy.prefab";
        private const string CandlePropPath = UhfpsPrefabs + "/Props/Candle.prefab";

        private const string CanisterItemGuid = "0044e6e7fcbd4fd0925756aea0f621ab";
        private const string LockpickItemGuid = "9c45e9931ae9492dba1cf41854f95de0";

        private static readonly (string Prefab, string Guid)[] Keys =
        {
            (UhfpsPrefabs + "/Interact/Inventory/Keys/Key_01.prefab", "b70a06636e824c48a8631756ccb04b3c"),
            (UhfpsPrefabs + "/Interact/Inventory/Keys/Key_02.prefab", "12bb84ecda3946579a8db52385d1f86b"),
            (UhfpsPrefabs + "/Interact/Inventory/Keys/Key_03.prefab", "eb63b2ffe5af4b8cb8396f2d6eae67e5"),
            (UhfpsPrefabs + "/Interact/Inventory/Keys/Key_04.prefab", "1a62d00e739445248211bcfbc6189138")
        };

        /// <summary>Loot kinds and how often each turns up, in weights.</summary>
        /// <remarks>
        /// Flares and holy water need Tools > Quiet Village > Survival > Set Up Tool Items first; a spot that rolls one
        /// before then is left empty. Weapons only stagger creatures, and pistol ammo is scarce on purpose.
        /// </remarks>
        private static readonly (string Prefab, int Weight)[] OpenLoot =
        {
            (UhfpsPrefabs + "/Interact/Inventory/Other/Misc_Scrap.prefab", 35),
            (UhfpsPrefabs + "/Interact/Inventory/Health/Health_MedicalHerb.prefab", 20),
            (UhfpsPrefabs + "/Interact/Inventory/Reload/Reload_FlashBattery.prefab", 20),
            (UhfpsPrefabs + "/Interact/Generator/Canister.prefab", 15),
            (SurvivalToolsSetup.FlarePickupPath, 12),
            (UhfpsPrefabs + "/Interact/Inventory/Health/Health_FirstAidKit.prefab", 10),
            (UhfpsPrefabs + "/Interact/Inventory/Reload/Reload_PistolAmmo.prefab", 6),
            (SurvivalToolsSetup.HolyWaterPickupPath, 5)
        };

        /// <summary>What a locked crypt holds: worth the trouble of opening it.</summary>
        private static readonly (string Prefab, int Weight)[] LockedLoot =
        {
            (UhfpsPrefabs + "/Interact/Generator/Canister.prefab", 30),
            (UhfpsPrefabs + "/Interact/Inventory/Health/Health_FirstAidKit.prefab", 25),
            (UhfpsPrefabs + "/Interact/Inventory/Other/Misc_Scrap.prefab", 25),
            (UhfpsPrefabs + "/Interact/Inventory/Reload/Reload_FlashBattery.prefab", 10),
            (UhfpsPrefabs + "/Interact/Inventory/Other/Misc_Lockpick.prefab", 10),
            (SurvivalToolsSetup.HolyWaterPickupPath, 15),
            (UhfpsPrefabs + "/Interact/Inventory/PlayerItems/PlayerItem_Axe.prefab", 8),
            (UhfpsPrefabs + "/Interact/Inventory/PlayerItems/PlayerItem_Pistol.prefab", 5)
        };

        private const string CanisterPrefabPath = UhfpsPrefabs + "/Interact/Generator/Canister.prefab";

        private enum CryptLock { None, Key, Lockpick, Padlock }

        private sealed class DayPlan
        {
            /// <summary>Per house, in the layout's order; missing entries are open crypts.</summary>
            public CryptLock[] Locks = Array.Empty<CryptLock>();

            public int OutdoorClutter = 14;
            public int OutdoorLootSpots = 12;
            public int OutdoorLootKept = 5;

            /// <summary>Canister spots outside, of which the game keeps this many, so the generator always has some fuel.</summary>
            public int FuelSpots = 4;
            public int FuelKept = 2;

            public int LockedCryptKeeps = 3;
            public int OpenCryptKeeps = 1;
        }

        private static DayPlan HollowChapelDay() => new()
        {
            Locks = new[] { CryptLock.Key, CryptLock.None, CryptLock.Padlock, CryptLock.Lockpick, CryptLock.Key, CryptLock.None },
            OutdoorClutter = 16,
            OutdoorLootSpots = 12
        };

        private static DayPlan SaltmarshDay() => new()
        {
            Locks = new[]
            {
                CryptLock.None, CryptLock.Key, CryptLock.None, CryptLock.Padlock, CryptLock.None,
                CryptLock.Lockpick, CryptLock.None, CryptLock.Key, CryptLock.None
            },
            OutdoorClutter = 12,
            OutdoorLootSpots = 14,
            OutdoorLootKept = 6
        };

        private const string CryptStoneMaterialPath = CemeteryMaterialFolder + "/CryptStone.mat";

        // ---- Entry -----------------------------------------------------------------------------------

        private static void BuildDayActivities(Layout layout, Transform layoutRoot, Transform[] creatureSpawns,
            Transform[] playerSpawns, StringBuilder report)
        {
            var plan = layout.Day;
            if (plan == null || layout.Cemetery == null) return;

            var root = new GameObject("Day").transform;
            root.SetParent(layoutRoot, false);

            var random = new Random(layout.Seed + 31);
            var counts = new Dictionary<string, int>();
            void Count(string what, int amount = 1) => counts[what] = counts.TryGetValue(what, out var n) ? n + amount : amount;

            var keepClear = BuildKeepClear(layout, layout.Cemetery, layoutRoot, creatureSpawns, playerSpawns);
            var houses = layoutRoot.Find("Houses");
            var shelter = layoutRoot.Find("Shelter");
            var stone = AssetDatabase.LoadAssetAtPath<Material>(CryptStoneMaterialPath);

            // Where keys and notes may be hidden: the open crypts' floors and the ground outside.
            var hidingPlaces = new List<(Transform Parent, Vector3 Position)>();

            var keyIndex = 0;
            var padlocks = new List<(CryptPadlock Lock, PadlockPuzzle Puzzle, string CryptName, string Group)>();
            PendingKeys.Clear();

            for (var i = 0; i < layout.Houses.Length && houses != null; i++)
            {
                var house = houses.Find($"House {i + 1}");
                if (house == null) continue;

                var spec = layout.Houses[i];
                var locked = i < plan.Locks.Length ? plan.Locks[i] : CryptLock.None;

                FurnishCrypt(house, spec.Size, locked != CryptLock.None, i + 1, plan, random, hidingPlaces, Count);

                switch (locked)
                {
                    case CryptLock.Key when keyIndex < Keys.Length:
                    {
                        var door = FitCryptDoor(house, spec.Size, stone, $"Crypt {i + 1}");
                        if (door == null) break;

                        var key = Keys[keyIndex++];
                        LockWithKey(door, key.Guid);
                        PendingKeys.Add((key.Prefab, $"key:crypt {i + 1}"));
                        Count("key-locked crypts");
                        break;
                    }
                    case CryptLock.Lockpick:
                    {
                        var door = FitCryptDoor(house, spec.Size, stone, $"Crypt {i + 1}");
                        if (door == null) break;

                        LockWithLockpick(door);
                        Count("lockpick crypts");
                        break;
                    }
                    case CryptLock.Padlock:
                    {
                        var door = FitCryptDoor(house, spec.Size, stone, $"Crypt {i + 1}");
                        if (door == null) break;

                        var cryptName = CryptNames[i % CryptNames.Length];
                        var padlock = LockWithPadlock(door, $"Crypt {i + 1}", cryptName);
                        if (padlock != null)
                            padlocks.Add((padlock, padlock.GetComponentInChildren<PadlockPuzzle>(true), cryptName, $"note:crypt {i + 1}"));
                        Count("padlocked crypts");
                        break;
                    }
                }
            }

            if (shelter != null) FurnishChapel(layout, shelter, root, random, Count);
            PlaceOutdoorClutter(plan, root, keepClear, random, hidingPlaces, Count);
            PlaceOutdoorLoot(plan, root, keepClear, random, hidingPlaces, Count);

            // Keys and notes last, among every hiding place found above, so each one can turn up in several places.
            foreach (var (prefab, group) in PendingKeys)
                PlaceHidden(prefab, group, 4, hidingPlaces, random, Count, "key spots");
            PendingKeys.Clear();

            foreach (var (padlock, puzzle, cryptName, group) in padlocks)
            {
                var notes = PlaceHidden(NotePrefabPath, group, 3, hidingPlaces, random, Count, "note spots")
                    .Select(note => note.GetComponent<InteractableItem>())
                    .Where(item => item != null)
                    .ToArray();

                padlock.Configure(puzzle, notes, cryptName);
                if (notes.Length == 0) report.AppendLine($"WARNING: nowhere to hide the note for {cryptName}; its padlock cannot be opened.");
            }

            if (shelter != null) BuildGenerator(layout, shelter, root, Count, report);

            report.AppendLine("Day: " + string.Join(", ", counts.OrderBy(pair => pair.Key).Select(pair => $"{pair.Value} {pair.Key}")) + ".");
        }

        // Keys waiting for hiding places, filled while the crypts are built.
        private static readonly List<(string Prefab, string Group)> PendingKeys = new();

        private static readonly string[] CryptNames =
        {
            "the Ashford crypt", "the Harrow crypt", "the Blackwood crypt", "the Morrow crypt", "the Vane crypt",
            "the Aldous crypt", "the Crane crypt", "the Pell crypt", "the Stroud crypt"
        };

        // ---- Crypts ----------------------------------------------------------------------------------

        /// <summary>
        /// A cabinet and a crate against the back wall, a barrel in a corner, a candle, and loot on and beside them.
        /// </summary>
        /// <remarks>In the crypt's own frame: +Z is the door. The middle stays clear, from the door to the back.</remarks>
        private static void FurnishCrypt(Transform house, Vector2 size, bool locked, int number, DayPlan plan, Random random,
            List<(Transform Parent, Vector3 Position)> hidingPlaces, Action<string, int> count)
        {
            var interior = new GameObject("Interior").transform;
            interior.SetParent(house, false);

            // The back wall's inner face.
            var back = -size.y * 0.5f + WallThickness * 0.5f;
            var surfaces = new List<Vector3>();

            var cabinet = PlaceProp(CabinetPrefabPath, interior, new Vector3(-size.x * 0.25f, 0f, back), 0f, keepBehaviour: true);
            if (cabinet != null)
            {
                PushOffWall(cabinet, interior, back + 0.05f);
                surfaces.Add(TopOf(cabinet, interior));
            }

            var crate = PlaceProp(CratePaths[0], interior, new Vector3(size.x * 0.25f, 0f, back), random.Next(-8, 8), keepBehaviour: false);
            if (crate != null)
            {
                PushOffWall(crate, interior, back + 0.05f);

                var top = TopOf(crate, interior);
                surfaces.Add(top);

                var candle = PlaceProp(CandlePropPath, interior, top + new Vector3(0.25f, 0f, 0.1f), 0f, keepBehaviour: true);
                if (candle != null) count("crypt candles", 1);
            }

            var side = random.NextDouble() < 0.5 ? -1f : 1f;
            var barrel = PlaceProp(BarrelPath, interior, new Vector3(side * (size.x * 0.5f - 0.6f), 0f, back * 0.2f), 0f, keepBehaviour: false);
            if (barrel != null) surfaces.Add(TopOf(barrel, interior));

            // Floor by the other side wall, clear of the walk in.
            var floor = new Vector3(-side * (size.x * 0.5f - 0.7f), 0.15f, size.y * 0.05f);
            surfaces.Add(floor);

            foreach (var placed in new[] { cabinet, crate, barrel }) if (placed != null) count("crypt furniture", 1);

            MoveScrapClearOf(house, interior, floor);

            var group = $"crypt {number}";
            var keep = Mathf.RoundToInt(locked ? plan.LockedCryptKeeps : plan.OpenCryptKeeps);
            var table = locked ? LockedLoot : OpenLoot;

            for (var s = 0; s < surfaces.Count; s++)
            {
                var spot = PlaceLoot(PickWeighted(table, random), interior, surfaces[s], $"Loot {group} {s + 1}", group, keep, false);
                if (spot != null) count("crypt loot spots", 1);
            }

            // Only open crypts hide keys and notes: a key locked inside its own crypt could never be reached.
            if (!locked) hidingPlaces.Add((interior, new Vector3(side * (size.x * 0.5f - 0.7f), 0.15f, -size.y * 0.15f)));
        }

        /// <summary>Moves a prop forward until its back clears <paramref name="minZ"/>, so it stands against a wall, not in it.</summary>
        private static void PushOffWall(GameObject prop, Transform space, float minZ)
        {
            var bounds = LocalBounds(prop, space);
            if (bounds.min.z >= minZ) return;

            prop.transform.localPosition += new Vector3(0f, 0f, minZ - bounds.min.z);
        }

        /// <summary>The greybox's own scrap, moved off anything a prop now stands on.</summary>
        private static void MoveScrapClearOf(Transform house, Transform interior, Vector3 freeFloor)
        {
            var props = interior.GetComponentsInChildren<Renderer>(true).Select(renderer => renderer.bounds).ToList();
            var offset = 0;

            foreach (var item in house.GetComponentsInChildren<InteractableItem>(true))
            {
                if (item.transform.IsChildOf(interior)) continue;

                var position = item.transform.position;
                if (!props.Any(bounds => bounds.Contains(new Vector3(position.x, bounds.center.y, position.z)) ||
                                         Vector2.Distance(new Vector2(bounds.center.x, bounds.center.z), new Vector2(position.x, position.z)) <
                                         Mathf.Max(bounds.extents.x, bounds.extents.z) + 0.2f))
                    continue;

                item.transform.position = interior.TransformPoint(freeFloor + new Vector3(0f, 0f, 0.5f * ++offset));
            }
        }

        /// <summary>
        /// A UHFPS door in a crypt's doorway: the opening narrowed to the door's frame with stone, and the leaf carving the
        /// NavMesh while it is shut, so creatures do not try to walk through a closed door.
        /// </summary>
        private static DynamicObject FitCryptDoor(Transform house, Vector2 size, Material stone, string label)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(DoorPrefabPath);
            var frontWall = house.Find("Wall Front");
            if (prefab == null || frontWall == null) return null;

            var door = (GameObject)PrefabUtility.InstantiatePrefab(prefab, frontWall);
            door.name = $"{label} Door";
            door.transform.localPosition = Vector3.zero;
            door.transform.localRotation = Quaternion.identity;

            var bounds = LocalBounds(door, frontWall);
            var width = Mathf.Min(bounds.size.x, OpeningWidth);

            // Centred in the opening, standing on the floor, in the middle of the wall's thickness.
            door.transform.localPosition = new Vector3(-bounds.center.x, -bounds.min.y, -bounds.center.z);

            // Stone either side, and over the top up to the lintel.
            var jamb = (OpeningWidth - width) * 0.5f;
            if (jamb > 0.02f)
            {
                AddBlock(frontWall, "Door Jamb", -OpeningWidth * 0.5f, -OpeningWidth * 0.5f + jamb, 0f, OpeningHeight, stone);
                AddBlock(frontWall, "Door Jamb", OpeningWidth * 0.5f - jamb, OpeningWidth * 0.5f, 0f, OpeningHeight, stone);
            }

            if (bounds.size.y < OpeningHeight - 0.02f)
                AddBlock(frontWall, "Door Head", -OpeningWidth * 0.5f, OpeningWidth * 0.5f, bounds.size.y, OpeningHeight, stone);

            var dynamicObject = door.GetComponentInChildren<DynamicObject>(true);
            if (dynamicObject != null && dynamicObject.target != null) AddDoorObstacle(dynamicObject.target);

            return dynamicObject;
        }

        private static void AddDoorObstacle(Transform leaf)
        {
            var renderers = leaf.GetComponentsInChildren<Renderer>(true);
            if (renderers.Length == 0) return;

            var bounds = new Bounds(leaf.InverseTransformPoint(renderers[0].bounds.center), Vector3.zero);
            foreach (var renderer in renderers)
            {
                var b = renderer.bounds;
                foreach (var corner in Corners(b)) bounds.Encapsulate(leaf.InverseTransformPoint(corner));
            }

            var obstacle = leaf.gameObject.AddComponent<NavMeshObstacle>();
            obstacle.shape = NavMeshObstacleShape.Box;
            obstacle.center = bounds.center;
            obstacle.size = new Vector3(Mathf.Max(bounds.size.x, 0.2f), Mathf.Max(bounds.size.y, 1f), Mathf.Max(bounds.size.z, 0.4f));
            obstacle.carving = true;
            obstacle.carveOnlyStationary = true;
        }

        private static void LockWithKey(DynamicObject door, string keyGuid)
        {
            var serialized = new SerializedObject(door);
            serialized.FindProperty("dynamicStatus").enumValueIndex = (int)DynamicObject.DynamicStatus.Locked;
            serialized.FindProperty("statusChange").enumValueIndex = (int)DynamicObject.StatusChange.InventoryItem;
            serialized.FindProperty("unlockItem.m_GUID").stringValue = keyGuid;
            serialized.FindProperty("keepUnlockItem").boolValue = false;
            SetLockedText(serialized, "Locked. Its key must be somewhere in the cemetery.");
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        /// <summary>The lockpick minigame on a door, set up as UHFPS's own lockpickable chest is, with the door's pick model.</summary>
        private static void LockWithLockpick(DynamicObject door)
        {
            var chest = AssetDatabase.LoadAssetAtPath<GameObject>(ChestLockpickPrefabPath);
            var source = chest != null ? chest.GetComponentInChildren<LockpickInteract>(true) : null;
            var model = AssetDatabase.LoadAssetAtPath<GameObject>(DoorLockpickModelPath);

            var lockpick = door.gameObject.AddComponent<LockpickInteract>();
            if (source != null) EditorUtility.CopySerialized(source, lockpick);

            var pick = new SerializedObject(lockpick);
            if (model != null) pick.FindProperty("LockpickModel").objectReferenceValue = model;
            pick.FindProperty("IsDynamicUnlockComponent").boolValue = true;
            pick.FindProperty("BobbyPinItem.m_GUID").stringValue = LockpickItemGuid;
            pick.FindProperty("DynamicObject").objectReferenceValue = door;
            pick.ApplyModifiedPropertiesWithoutUndo();

            var serialized = new SerializedObject(door);
            serialized.FindProperty("dynamicStatus").enumValueIndex = (int)DynamicObject.DynamicStatus.Locked;
            serialized.FindProperty("statusChange").enumValueIndex = (int)DynamicObject.StatusChange.CustomScript;
            serialized.FindProperty("unlockScript._object").objectReferenceValue = lockpick;
            serialized.FindProperty("unlockItem.m_GUID").stringValue = string.Empty;
            SetLockedText(serialized, "Locked. A lockpick might open it.");
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        /// <summary>A number padlock hung on the door's outer face, whose code a note somewhere gives away.</summary>
        private static CryptPadlock LockWithPadlock(DynamicObject door, string label, string cryptName)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PadlockPrefabPath);
            if (prefab == null) return null;

            var leafBounds = door.target != null ? LocalBounds(door.target.gameObject, door.transform.parent) : LocalBounds(door.gameObject, door.transform.parent);
            var wall = door.transform.parent;

            var padlockObject = (GameObject)PrefabUtility.InstantiatePrefab(prefab, wall);
            padlockObject.name = $"{label} Padlock";

            // Beside the handle side of the leaf, at hand height, just proud of the wall's outer face (+Z).
            padlockObject.transform.localPosition = new Vector3(leafBounds.center.x + leafBounds.extents.x * 0.6f, 1.05f,
                WallThickness * 0.5f + 0.12f);
            padlockObject.transform.localRotation = Quaternion.identity;

            var puzzle = padlockObject.GetComponentInChildren<PadlockPuzzle>(true);
            if (puzzle == null) return null;

            var serialized = new SerializedObject(door);
            serialized.FindProperty("dynamicStatus").enumValueIndex = (int)DynamicObject.DynamicStatus.Locked;
            serialized.FindProperty("statusChange").enumValueIndex = (int)DynamicObject.StatusChange.CustomScript;
            serialized.FindProperty("unlockScript._object").objectReferenceValue = puzzle;
            serialized.FindProperty("unlockItem.m_GUID").stringValue = string.Empty;
            SetLockedText(serialized, "Padlocked. Someone must have written the code down.");
            serialized.ApplyModifiedPropertiesWithoutUndo();

            var codeLock = padlockObject.AddComponent<CryptPadlock>();
            codeLock.Configure(puzzle, Array.Empty<InteractableItem>(), cryptName);
            return codeLock;
        }

        private static void SetLockedText(SerializedObject door, string text)
        {
            door.FindProperty("showLockedText").boolValue = true;

            // A leading '*' tells UHFPS the text is not a localisation key.
            door.FindProperty("lockedText.GlocText").stringValue = GString.EXCLUDE_CHAR + text;
            door.FindProperty("lockedText.NormalText").stringValue = text;
        }

        // ---- Chapel ----------------------------------------------------------------------------------

        /// <summary>A few crates and barrels in the chapel's corners, away from every opening, and a little loot among them.</summary>
        private static void FurnishChapel(Layout layout, Transform shelter, Transform root, Random random, Action<string, int> count)
        {
            var interior = new GameObject("Chapel Interior").transform;
            interior.SetParent(shelter, false);

            var attackPoints = shelter.GetComponentsInChildren<Barricade>(true).Select(b => b.transform.position).ToList();
            var half = layout.ShelterSize * 0.5f - new Vector2(1.1f, 1f);

            var corners = new[] { new Vector3(-half.x, 0f, -half.y), new Vector3(half.x, 0f, -half.y), new Vector3(-half.x, 0f, half.y), new Vector3(half.x, 0f, half.y) }
                .OrderByDescending(corner => attackPoints.Count == 0 ? 0f : attackPoints.Min(p => Vector3.Distance(shelter.TransformPoint(corner), p)))
                .ToList();

            // The quietest corner is kept for the generator.
            var spot = 0;
            foreach (var corner in corners.Skip(1))
            {
                if (attackPoints.Any(p => Vector3.Distance(shelter.TransformPoint(corner), p) < 2.4f)) continue;

                var crate = PlaceProp(CratePaths[random.Next(CratePaths.Length)], interior, corner, random.Next(0, 90), keepBehaviour: false);
                if (crate == null) continue;

                count("chapel furniture", 1);
                PlaceLoot(PickWeighted(OpenLoot, random), interior, TopOf(crate, interior), $"Loot chapel {++spot}", "chapel", 1, false);
                count("chapel loot spots", 1);
            }
        }

        // ---- Generator -------------------------------------------------------------------------------

        /// <summary>
        /// The generator in the chapel's quietest corner, and a floodlight outside each corner of the chapel, each lighting
        /// ground wide enough that together they cover every barricade's attack point.
        /// </summary>
        private static void BuildGenerator(Layout layout, Transform shelter, Transform root, Action<string, int> count, StringBuilder report)
        {
            var generatorPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(GeneratorPrefabPath);
            var floodlightPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(FloodlightPrefabPath);
            if (generatorPrefab == null || floodlightPrefab == null)
            {
                report.AppendLine("WARNING: UHFPS generator or floodlight prefab missing; no generator built.");
                return;
            }

            var barricades = shelter.GetComponentsInChildren<Barricade>(true);
            var attackPoints = barricades.Select(b => b.AttackPoint).ToList();

            var half = layout.ShelterSize * 0.5f - new Vector2(1.1f, 1f);
            var corner = new[] { new Vector3(-half.x, 0f, -half.y), new Vector3(half.x, 0f, -half.y), new Vector3(-half.x, 0f, half.y), new Vector3(half.x, 0f, half.y) }
                .OrderByDescending(c => attackPoints.Count == 0 ? 0f : barricades.Min(b => Vector3.Distance(shelter.TransformPoint(c), b.transform.position)))
                .First();

            var generator = (GameObject)PrefabUtility.InstantiatePrefab(generatorPrefab, shelter);
            generator.name = "Generator";
            generator.transform.localPosition = corner;
            generator.transform.localRotation = Quaternion.LookRotation(-new Vector3(corner.x, 0f, corner.z));
            StripBehaviours(generator);
            SetLayerRecursively(generator, 0);

            foreach (var canvas in generator.GetComponentsInChildren<Canvas>(true)) canvas.gameObject.SetActive(false);

            var body = LocalBounds(generator, generator.transform);
            AddControl(generator.transform, "Switch", body.center, body.size + Vector3.one * 0.1f, GeneratorControl.ControlKind.Switch);

            // The cap sticks out of one side, so it is what the aim finds from that side.
            var capSize = new Vector3(0.35f, Mathf.Max(0.4f, body.size.y * 0.5f), Mathf.Max(0.5f, body.size.z * 0.6f));
            AddControl(generator.transform, "Fuel Cap", body.center + new Vector3(body.extents.x + capSize.x * 0.35f, 0f, 0f), capSize,
                GeneratorControl.ControlKind.FuelCap);
            count("generators", 1);

            // Floodlights outside the chapel's corners, facing out.
            var lightRoot = new GameObject("Floodlights").transform;
            lightRoot.SetParent(root, false);

            var outer = layout.ShelterSize * 0.5f + new Vector2(0.9f, 0.9f);
            var centres = new List<(Floodlight Light, Vector3 Centre)>();

            foreach (var local in new[] { new Vector3(-outer.x, 0f, -outer.y), new Vector3(outer.x, 0f, -outer.y), new Vector3(-outer.x, 0f, outer.y), new Vector3(outer.x, 0f, outer.y) })
            {
                var position = shelter.TransformPoint(local);
                var outward = (position - shelter.position);
                outward.y = 0f;
                outward.Normalize();

                var lamp = (GameObject)PrefabUtility.InstantiatePrefab(floodlightPrefab, lightRoot);
                lamp.name = "Floodlight";
                lamp.transform.position = position;
                lamp.transform.rotation = Quaternion.LookRotation(outward);
                StripBehaviours(lamp);
                SetLayerRecursively(lamp, 0);

                var centre = new GameObject("Lit Ground").transform;
                centre.SetParent(lamp.transform, false);
                centre.position = position + outward * 1.5f;

                var floodlight = lamp.AddComponent<Floodlight>();
                floodlight.Configure(lamp.GetComponentsInChildren<Light>(true), lamp.GetComponentsInChildren<MeshRenderer>(true), centre, 6f);
                centres.Add((floodlight, centre.position));
                count("floodlights", 1);
            }

            // Each attack point is the nearest light's to cover.
            var radii = centres.ToDictionary(c => c.Light, _ => 5f);
            foreach (var point in attackPoints)
            {
                var nearest = centres.OrderBy(c => Vector3.Distance(c.Centre, point)).First();
                var flat = new Vector2(point.x - nearest.Centre.x, point.z - nearest.Centre.z).magnitude;
                radii[nearest.Light] = Mathf.Max(radii[nearest.Light], flat + 0.8f);
            }

            foreach (var (light, _) in centres)
            {
                var serialized = new SerializedObject(light);
                serialized.FindProperty("m_radius").floatValue = Mathf.Min(radii[light], 11f);
                serialized.ApplyModifiedPropertiesWithoutUndo();
            }

            report.AppendLine($"Generator in the chapel; floodlight radii {string.Join(", ", radii.Values.Select(r => Mathf.Min(r, 11f).ToString("0.0")))} m.");
        }

        private static void AddControl(Transform parent, string name, Vector3 centre, Vector3 size, GeneratorControl.ControlKind kind)
        {
            var control = new GameObject(name) { layer = LayerMask.NameToLayer("Interact") };
            control.transform.SetParent(parent, false);

            var box = control.AddComponent<BoxCollider>();
            box.isTrigger = true;
            box.center = centre;
            box.size = size;

            control.AddComponent<GeneratorControl>().Configure(kind);
        }

        // ---- Outdoors --------------------------------------------------------------------------------

        /// <summary>Stacks of crates and barrels among the graves, on free ground only.</summary>
        private static void PlaceOutdoorClutter(DayPlan plan, Transform root, KeepClear keepClear, Random random,
            List<(Transform Parent, Vector3 Position)> hidingPlaces, Action<string, int> count)
        {
            var clutter = new GameObject("Clutter").transform;
            clutter.SetParent(root, false);

            var placed = 0;
            for (var attempt = 0; attempt < plan.OutdoorClutter * 40 && placed < plan.OutdoorClutter; attempt++)
            {
                var position = new Vector3(Lerp(random, -42f, 42f), 0f, Lerp(random, -42f, 42f));
                if (!keepClear.IsFree(position, 2f) || !IsGroundFree(position, 1.4f)) continue;

                var yaw = Lerp(random, 0f, 360f);
                var roll = random.NextDouble();

                GameObject main;
                if (roll < 0.55)
                {
                    main = PlaceProp(CratePaths[0], clutter, position, yaw, keepBehaviour: false, world: true);
                    if (main != null && random.NextDouble() < 0.5)
                        PlaceProp(CratePaths[1], clutter, TopOf(main, clutter) + Vector3.up * 0.01f, yaw + Lerp(random, -20f, 20f), keepBehaviour: false);
                }
                else
                {
                    main = PlaceProp(BarrelPath, clutter, position, yaw, keepBehaviour: false, world: true);
                    if (main != null)
                        PlaceProp(BarrelPath, clutter, position + Quaternion.Euler(0f, yaw, 0f) * new Vector3(0.75f, 0f, 0.2f), yaw, keepBehaviour: false, world: true);
                }

                if (main == null) continue;

                // Tucked behind the stack, if that ground is free.
                var behind = position + Quaternion.Euler(0f, yaw, 0f) * new Vector3(0f, 0.15f, -1.2f);
                if (keepClear.IsFree(behind, 0.3f) && IsGroundFree(behind, 0.35f))
                    hidingPlaces.Add((clutter, clutter.InverseTransformPoint(behind)));

                placed++;
            }

            count("outdoor clutter", placed);
        }

        /// <summary>Loose loot on open ground, and the fuel the generator can count on.</summary>
        private static void PlaceOutdoorLoot(DayPlan plan, Transform root, KeepClear keepClear, Random random,
            List<(Transform Parent, Vector3 Position)> hidingPlaces, Action<string, int> count)
        {
            var loot = new GameObject("Loot").transform;
            loot.SetParent(root, false);

            var general = FreeGround(plan.OutdoorLootSpots + plan.FuelSpots + 8, keepClear, random);

            var i = 0;
            for (; i < plan.OutdoorLootSpots && i < general.Count; i++)
            {
                PlaceLoot(PickWeighted(OpenLoot, random), loot, loot.InverseTransformPoint(general[i]), $"Loot outdoors {i + 1}", "outdoors", plan.OutdoorLootKept, false);
                count("outdoor loot spots", 1);
            }

            for (var f = 0; f < plan.FuelSpots && i < general.Count; f++, i++)
            {
                PlaceLoot(CanisterPrefabPath, loot, loot.InverseTransformPoint(general[i]), $"Loot fuel {f + 1}", "fuel", plan.FuelKept, false);
                count("fuel spots", 1);
            }

            for (; i < general.Count; i++) hidingPlaces.Add((loot, loot.InverseTransformPoint(general[i])));
        }

        private static List<GameObject> PlaceHidden(string prefab, string group, int spots, List<(Transform Parent, Vector3 Position)> hidingPlaces,
            Random random, Action<string, int> count, string what)
        {
            var placed = new List<GameObject>();
            var choices = hidingPlaces.OrderBy(_ => random.Next()).Take(spots).ToList();

            foreach (var (parent, position) in choices)
            {
                hidingPlaces.Remove((parent, position));

                var item = PlaceLoot(prefab, parent, position, $"{group} {placed.Count + 1}", group, 1, false);
                if (item == null) continue;

                placed.Add(item);
                count(what, 1);
            }

            return placed;
        }

        private static List<Vector3> FreeGround(int wanted, KeepClear keepClear, Random random)
        {
            var found = new List<Vector3>();
            for (var attempt = 0; attempt < wanted * 60 && found.Count < wanted; attempt++)
            {
                var position = new Vector3(Lerp(random, -44f, 44f), 0.15f, Lerp(random, -44f, 44f));
                if (!keepClear.IsFree(position, 1f) || !IsGroundFree(position, 0.7f)) continue;
                if (found.Any(other => Vector3.Distance(other, position) < 6f)) continue;

                found.Add(position);
            }

            return found;
        }

        /// <summary>No solid thing (grave, fence, tree, wall, prop) within a box this wide, above the ground.</summary>
        private static bool IsGroundFree(Vector3 position, float halfWidth)
        {
            Physics.SyncTransforms();
            var centre = new Vector3(position.x, 1.1f, position.z);
            return !Physics.CheckBox(centre, new Vector3(halfWidth, 0.95f, halfWidth), Quaternion.identity, ~0, QueryTriggerInteraction.Ignore);
        }

        // ---- Placement -------------------------------------------------------------------------------

        /// <summary>
        /// A UHFPS prop standing at <paramref name="position"/> (local to <paramref name="parent"/> unless <paramref name="world"/>).
        /// Crates and barrels lose their drag and physics, and become solid scenery on the Default layer.
        /// </summary>
        private static GameObject PlaceProp(string path, Transform parent, Vector3 position, float yaw, bool keepBehaviour, bool world = false)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (prefab == null) return null;

            var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab, parent);
            instance.transform.localRotation = Quaternion.Euler(0f, yaw, 0f);
            instance.transform.localPosition = Vector3.zero;

            if (!keepBehaviour)
            {
                StripBehaviours(instance);
                foreach (var body in instance.GetComponentsInChildren<Rigidbody>(true)) Object.DestroyImmediate(body);
                SetLayerRecursively(instance, 0);
            }

            // Stood on its lowest point, wherever the model's pivot is.
            var bounds = LocalBounds(instance, parent);
            var lift = -bounds.min.y;

            if (world) instance.transform.position = position + Vector3.up * lift;
            else instance.transform.localPosition = position + Vector3.up * lift;

            return instance;
        }

        private static GameObject PlaceLoot(string prefabPath, Transform parent, Vector3 localPosition, string name, string group, int keep, bool guaranteed)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            if (prefab == null) return null;

            var item = (GameObject)PrefabUtility.InstantiatePrefab(prefab, parent);
            item.name = name;
            item.transform.localPosition = localPosition + Vector3.up * 0.06f;
            item.transform.localRotation = Quaternion.Euler(0f, (CryptPadlock.StableHash(name) % 360), 0f);

            item.AddComponent<LootSpot>().Configure(group, keep, guaranteed);
            return item;
        }

        /// <summary>The middle of a prop's top, in <paramref name="space"/>.</summary>
        private static Vector3 TopOf(GameObject prop, Transform space)
        {
            var bounds = LocalBounds(prop, space);
            return new Vector3(bounds.center.x, bounds.max.y, bounds.center.z);
        }

        /// <summary>Renderer bounds of an object, in another transform's space.</summary>
        private static Bounds LocalBounds(GameObject target, Transform space)
        {
            var renderers = target.GetComponentsInChildren<Renderer>(true).Where(r => r is MeshRenderer or SkinnedMeshRenderer).ToArray();
            if (renderers.Length == 0) return new Bounds(space.InverseTransformPoint(target.transform.position), Vector3.one * 0.5f);

            var first = true;
            var bounds = new Bounds();
            foreach (var renderer in renderers)
            {
                foreach (var corner in Corners(renderer.bounds))
                {
                    var local = space.InverseTransformPoint(corner);
                    if (first)
                    {
                        bounds = new Bounds(local, Vector3.zero);
                        first = false;
                    }
                    else bounds.Encapsulate(local);
                }
            }

            return bounds;
        }

        private static IEnumerable<Vector3> Corners(Bounds b)
        {
            for (var x = -1; x <= 1; x += 2)
            for (var y = -1; y <= 1; y += 2)
            for (var z = -1; z <= 1; z += 2)
                yield return b.center + Vector3.Scale(b.extents, new Vector3(x, y, z));
        }

        /// <summary>
        /// Removes every script from a prop, each only once nothing left on its object still requires it.
        /// </summary>
        private static void StripBehaviours(GameObject instance)
        {
            for (var pass = 0; pass < 6; pass++)
            {
                var behaviours = instance.GetComponentsInChildren<MonoBehaviour>(true);
                if (behaviours.Length == 0) return;

                foreach (var behaviour in behaviours)
                {
                    if (behaviour == null) continue;

                    var type = behaviour.GetType();
                    var required = behaviour.GetComponents<Component>()
                        .Where(other => other != null && other != behaviour)
                        .SelectMany(other => other.GetType().GetCustomAttributes(typeof(RequireComponent), true).Cast<RequireComponent>())
                        .Any(attribute => new[] { attribute.m_Type0, attribute.m_Type1, attribute.m_Type2 }.Any(t => t != null && t.IsAssignableFrom(type)));

                    if (!required) Object.DestroyImmediate(behaviour);
                }
            }
        }

        private static void SetLayerRecursively(GameObject target, int layer)
        {
            foreach (var child in target.GetComponentsInChildren<Transform>(true)) child.gameObject.layer = layer;
        }

        private static string PickWeighted((string Prefab, int Weight)[] table, Random random)
        {
            var roll = random.Next(table.Sum(entry => entry.Weight));
            foreach (var (prefab, weight) in table)
            {
                if (roll < weight) return prefab;
                roll -= weight;
            }

            return table[0].Prefab;
        }
    }
}
