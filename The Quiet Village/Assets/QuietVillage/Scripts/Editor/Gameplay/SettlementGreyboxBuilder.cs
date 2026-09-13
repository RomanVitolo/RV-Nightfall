using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using QuietVillage.Multiplayer.Bridge.EditorTools;
using QuietVillage.Multiplayer.Levels;
using QuietVillage.Multiplayer.Player;
using QuietVillage.Gameplay.Survival;
using Unity.AI.Navigation;
using Unity.Netcode;
using Unity.Netcode.Components;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace QuietVillage.Gameplay.EditorTools
{
    /// <summary>
    /// Builds the greybox settlements: placeholder buildings, a shelter with barricade openings, trees, scrap, creature
    /// spawns, the survival director and a baked NavMesh, then runs level setup on the result.
    /// </summary>
    /// <remarks>
    /// Layouts live in code so a settlement can be rebuilt from nothing, and the loop can be played before any real
    /// level art exists. Each rebuild replaces only the objects this tool generated (the roots named below); the game
    /// systems and anything else placed in the scene stay. Hand-built levels need none of this: a
    /// <see cref="SurvivalDirector"/>, some <see cref="Barricade"/>s and a NavMesh are all the loop depends on.
    ///
    /// Everything is plain primitives with flat-coloured materials. Replace them freely; only the barricade objects
    /// and the director carry behaviour.
    /// </remarks>
    public static class SettlementGreyboxBuilder
    {
        private const string ArtFolder = ProjectPaths.GreyboxMaterials;
        private const string PrefabFolder = ProjectPaths.CreaturePrefabs;
        private const string CreaturePrefabPath = PrefabFolder + "/NightCreature.prefab";
        private const string ScrapPrefabPath =
            "Assets/ThunderWire Studio/UHFPS/Content/Prefabs/Interact/Inventory/Other/Misc_Scrap.prefab";

        // UHFPS's Scrap item, the stand-in building material until the game has wood.
        private const string ScrapItemGuid = "29901ad3ad2a4edf828062ef0684be91";

        private const string LayoutRootName = "Layout";
        private const string DirectorName = "SurvivalDirector";
        private const string NavMeshName = "NavMesh";
        private const string CreatureSpawnsName = "CreatureSpawns";
        private const string SpawnPointsName = "PlayerSpawnPoints";

        private const float WallHeight = 3f;
        private const float WallThickness = 0.25f;
        private const float OpeningWidth = 1.6f;
        private const float OpeningHeight = 2.2f;

        // Layers the NavMesh must not be baked from: pickups and barricades (Interact), and anything alive.
        private static readonly string[] NonWalkableLayers = { "Interact", "Player", "NPC", "Ignore Raycast", "UI", "BodyPart" };

        // ---- Layouts ---------------------------------------------------------------------------------

        private enum Side { Front, Back, Left, Right }

        private readonly struct Opening
        {
            public readonly Side Side;
            public readonly float Offset;

            public Opening(Side side, float offset)
            {
                Side = side;
                Offset = offset;
            }
        }

        private sealed class House
        {
            public Vector3 Centre;
            public float Yaw;
            public Vector2 Size = new(8f, 6f);
        }

        private sealed class Layout
        {
            public string SceneName;
            public string DisplayName;
            public string Description;
            public int Seed;

            public Vector3 ShelterCentre;
            public float ShelterYaw;
            public Vector2 ShelterSize;
            public Opening[] ShelterOpenings;

            public House[] Houses;
            public int TreeCount;
            public float TreeInnerRadius = 16f;
            public int ScrapPerHouse = 2;
            public Vector3[] LooseScrap = Array.Empty<Vector3>();

            public bool SeaWall;
            public Color GroundColour;

            public float DaySeconds;
            public float NightSeconds;
            public int CreaturesBase;
            public int CreaturesPerPlayer;

            public string ScenePath => $"{LevelSetup.ScenesFolder}/{SceneName}.unity";
        }

        /// <summary>
        /// First settlement: a chapel in a clearing, cottages in the woods. Trees hide the approach, so the night is
        /// about hearing creatures before seeing them.
        /// </summary>
        private static Layout ForestVillage() => new()
        {
            SceneName = "ForestVillage",
            DisplayName = "Forest Village",
            Description = "A chapel in a clearing, cottages in the trees. Four openings to hold.",
            Seed = 1701,
            ShelterCentre = Vector3.zero,
            ShelterYaw = 0f,
            ShelterSize = new Vector2(12f, 9f),
            ShelterOpenings = new[]
            {
                new Opening(Side.Front, 0f), new Opening(Side.Back, 2.5f),
                new Opening(Side.Left, -1f), new Opening(Side.Right, 1.5f)
            },
            Houses = new[]
            {
                new House { Centre = new Vector3(-22f, 0f, 14f), Yaw = 90f },
                new House { Centre = new Vector3(20f, 0f, 18f), Yaw = -90f },
                new House { Centre = new Vector3(-18f, 0f, -20f), Yaw = 30f },
                new House { Centre = new Vector3(24f, 0f, -16f), Yaw = 200f },
                new House { Centre = new Vector3(0f, 0f, 32f), Yaw = 180f },
                new House { Centre = new Vector3(-36f, 0f, -2f), Yaw = 90f, Size = new Vector2(6f, 5f) }
            },
            TreeCount = 70,
            LooseScrap = new[] { new Vector3(9f, 0f, 12f), new Vector3(-12f, 0f, 4f), new Vector3(6f, 0f, -26f) },
            GroundColour = new Color(0.24f, 0.3f, 0.2f),
            DaySeconds = 360f,
            NightSeconds = 240f,
            CreaturesBase = 1,
            CreaturesPerPlayer = 1
        };

        /// <summary>
        /// Second settlement: rows of houses by the sea and a warehouse with more ways in. Open sightlines, so the
        /// creatures are seen coming, but there are more of them, five openings, and a longer night.
        /// </summary>
        private static Layout CoastalTown() => new()
        {
            SceneName = "CoastalTown",
            DisplayName = "Coastal Town",
            Description = "A harbour warehouse with five ways in. Fewer trees, more creatures, a longer night.",
            Seed = 4242,
            ShelterCentre = new Vector3(14f, 0f, -16f),
            ShelterYaw = 90f,
            ShelterSize = new Vector2(14f, 10f),
            ShelterOpenings = new[]
            {
                new Opening(Side.Front, -3f), new Opening(Side.Front, 3f), new Opening(Side.Back, 0f),
                new Opening(Side.Left, 2f), new Opening(Side.Right, -2f)
            },
            Houses = new[]
            {
                new House { Centre = new Vector3(-30f, 0f, 12f), Yaw = 180f },
                new House { Centre = new Vector3(-15f, 0f, 12f), Yaw = 180f },
                new House { Centre = new Vector3(0f, 0f, 12f), Yaw = 180f },
                new House { Centre = new Vector3(15f, 0f, 12f), Yaw = 180f },
                new House { Centre = new Vector3(30f, 0f, 12f), Yaw = 180f },
                new House { Centre = new Vector3(-22f, 0f, 30f), Yaw = 0f },
                new House { Centre = new Vector3(-6f, 0f, 30f), Yaw = 0f },
                new House { Centre = new Vector3(10f, 0f, 30f), Yaw = 0f },
                new House { Centre = new Vector3(26f, 0f, 30f), Yaw = 0f }
            },
            TreeCount = 22,
            TreeInnerRadius = 38f,
            LooseScrap = new[] { new Vector3(-8f, 0f, -24f), new Vector3(34f, 0f, -28f) },
            SeaWall = true,
            GroundColour = new Color(0.46f, 0.43f, 0.36f),
            DaySeconds = 300f,
            NightSeconds = 300f,
            CreaturesBase = 2,
            CreaturesPerPlayer = 1
        };

        // ---- Menu ------------------------------------------------------------------------------------

        [MenuItem("Tools/Quiet Village/Build Greybox/Forest Village", priority = 0)]
        private static void BuildForestFromMenu() => BuildFromMenu(ForestVillage());

        [MenuItem("Tools/Quiet Village/Build Greybox/Coastal Town", priority = 1)]
        private static void BuildCoastalFromMenu() => BuildFromMenu(CoastalTown());

        /// <summary>Entry point for <c>-executeMethod</c>: builds every settlement.</summary>
        public static void BuildAllFromCommandLine()
        {
            var succeeded = Build(ForestVillage()) && Build(CoastalTown());
            if (Application.isBatchMode) EditorApplication.Exit(succeeded ? 0 : 1);
        }

        private static void BuildFromMenu(Layout layout)
        {
            if (EditorApplication.isPlaying || EditorApplication.isPlayingOrWillChangePlaymode)
            {
                EditorUtility.DisplayDialog("Build Greybox", "Exit Play Mode first.", "OK");
                return;
            }

            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;

            if (File.Exists(layout.ScenePath) && !EditorUtility.DisplayDialog("Build Greybox",
                    $"Rebuild {layout.DisplayName}? Generated buildings, scrap and barricades are replaced; " +
                    "anything else in the scene is kept.", "Rebuild", "Cancel"))
                return;

            Build(layout);
        }

        // ---- Build -----------------------------------------------------------------------------------

        private static bool Build(Layout layout)
        {
            var report = new StringBuilder($"Greybox: {layout.DisplayName}\n");

            if (!File.Exists(layout.ScenePath) && !LevelSetup.CreateLevel(layout.ScenePath))
            {
                Debug.LogError($"Greybox: could not create {layout.ScenePath}.");
                return false;
            }

            if (!EnsureCreaturePrefab(report))
            {
                Debug.LogError($"Greybox aborted.\n{report}");
                return false;
            }

            var scene = EditorSceneManager.OpenScene(layout.ScenePath, OpenSceneMode.Single);
            if (!scene.IsValid())
            {
                Debug.LogError($"Greybox: could not open {layout.ScenePath}.");
                return false;
            }

            // Loaded after the scene opens: opening a scene unloads assets nothing references, and a material or
            // prefab loaded before it would be assigned as a destroyed object.
            var materials = new Materials();
            var creaturePrefab = AssetDatabase.LoadAssetAtPath<GameObject>(CreaturePrefabPath);
            var scrapPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(ScrapPrefabPath);
            if (scrapPrefab == null) report.AppendLine($"WARNING: {ScrapPrefabPath} missing; no scrap placed.");

            ClearGenerated();

            var layoutRoot = new GameObject(LayoutRootName).transform;
            ColourGround(materials, layout);

            var barricades = BuildShelter(layout, layoutRoot, materials);
            var houseCount = BuildHouses(layout, layoutRoot, materials, scrapPrefab);
            var looseScrap = PlaceLooseScrap(layout, layoutRoot, scrapPrefab);
            BuildTrees(layout, layoutRoot, materials);
            BuildBoundary(layout, layoutRoot, materials);

            var creatureSpawns = BuildCreatureSpawns();
            BuildPlayerSpawns(layout);
            BuildDirector(layout, creaturePrefab, creatureSpawns, report);

            report.AppendLine($"Shelter with {barricades} barricade openings, {houseCount} houses, " +
                              $"{houseCount * layout.ScrapPerHouse + looseScrap} scrap, {layout.TreeCount} trees.");

            if (!BakeNavMesh(scene.name, LevelSetup.NavMeshFolder, report))
            {
                Debug.LogError($"Greybox aborted.\n{report}");
                return false;
            }

            EditorSceneManager.MarkSceneDirty(scene);
            if (!EditorSceneManager.SaveScene(scene))
            {
                Debug.LogError($"Greybox: could not save {layout.ScenePath}.\n{report}");
                return false;
            }

            Debug.Log(report.ToString());

            // World Sync for the scrap, the director's network hash, Build Settings and the catalog.
            if (!LevelSetup.SetUpLevel(layout.ScenePath)) return false;

            NameInCatalog(layout);
            return true;
        }

        private static void ClearGenerated()
        {
            var scene = EditorSceneManager.GetActiveScene();
            var names = new[] { LayoutRootName, DirectorName, NavMeshName, CreatureSpawnsName, SpawnPointsName };

            foreach (var root in scene.GetRootGameObjects())
            {
                if (names.Contains(root.name)) Object.DestroyImmediate(root);
            }
        }

        private static void ColourGround(Materials materials, Layout layout)
        {
            var ground = GameObject.Find("Greybox/Ground");
            if (ground == null || !ground.TryGetComponent<MeshRenderer>(out var renderer)) return;

            renderer.sharedMaterial = materials.Get("Ground", layout.GroundColour);
        }

        // ---- Buildings -------------------------------------------------------------------------------

        private static int BuildShelter(Layout layout, Transform parent, Materials materials)
        {
            var shelter = new GameObject("Shelter").transform;
            shelter.SetParent(parent, false);
            shelter.SetPositionAndRotation(layout.ShelterCentre, Quaternion.Euler(0f, layout.ShelterYaw, 0f));

            var order = 0;
            BuildBuilding(shelter, layout.ShelterSize, layout.ShelterOpenings, materials.Get("Shelter", new Color(0.55f, 0.52f, 0.48f)),
                materials.Get("Roof", new Color(0.3f, 0.22f, 0.2f)), (wall, offset) =>
                    CreateBarricade(wall, offset, order++, materials.Get("Wood", new Color(0.52f, 0.36f, 0.2f))));

            return order;
        }

        private static int BuildHouses(Layout layout, Transform parent, Materials materials, GameObject scrapPrefab)
        {
            var root = new GameObject("Houses").transform;
            root.SetParent(parent, false);

            var random = new System.Random(layout.Seed);
            var wall = materials.Get("House", new Color(0.62f, 0.6f, 0.55f));
            var roof = materials.Get("Roof", new Color(0.3f, 0.22f, 0.2f));

            for (var i = 0; i < layout.Houses.Length; i++)
            {
                var spec = layout.Houses[i];

                var house = new GameObject($"House {i + 1}").transform;
                house.SetParent(root, false);
                house.SetPositionAndRotation(spec.Centre, Quaternion.Euler(0f, spec.Yaw, 0f));

                BuildBuilding(house, spec.Size, new[] { new Opening(Side.Front, 0f) }, wall, roof, null);

                // Inside, away from the door, so finding scrap means going in.
                for (var s = 0; s < layout.ScrapPerHouse; s++)
                {
                    var local = new Vector3(Lerp(random, -0.3f, 0.3f) * spec.Size.x, 0.15f,
                        Lerp(random, -0.35f, 0f) * spec.Size.y);
                    PlaceScrap(scrapPrefab, house, house.TransformPoint(local));
                }
            }

            return layout.Houses.Length;
        }

        private static int PlaceLooseScrap(Layout layout, Transform parent, GameObject scrapPrefab)
        {
            foreach (var position in layout.LooseScrap) PlaceScrap(scrapPrefab, parent, position + Vector3.up * 0.15f);
            return layout.LooseScrap.Length;
        }

        private static void PlaceScrap(GameObject scrapPrefab, Transform parent, Vector3 position)
        {
            if (scrapPrefab == null) return;

            var scrap = (GameObject)PrefabUtility.InstantiatePrefab(scrapPrefab, parent);
            scrap.transform.position = position;
        }

        /// <summary>
        /// Four walls with doorway-sized openings, lintels over them and a flat roof. Local +Z is the front.
        /// </summary>
        /// <param name="onOpening">Called per opening with the wall it is in and its offset along it, or <c>null</c>.</param>
        private static void BuildBuilding(Transform building, Vector2 size, Opening[] openings, Material wallMaterial,
            Material roofMaterial, Action<Transform, float> onOpening)
        {
            foreach (Side side in Enum.GetValues(typeof(Side)))
            {
                var alongX = side is Side.Front or Side.Back;

                // Front and back run the full width plus the side walls' thickness, closing the corners.
                var length = alongX ? size.x + WallThickness : size.y - WallThickness;
                var outward = side switch
                {
                    Side.Front => Vector3.forward,
                    Side.Back => Vector3.back,
                    Side.Left => Vector3.left,
                    _ => Vector3.right
                };

                var wall = new GameObject($"Wall {side}").transform;
                wall.SetParent(building, false);
                wall.localPosition = Vector3.Scale(outward, new Vector3(size.x, 0f, size.y)) * 0.5f;
                wall.localRotation = Quaternion.LookRotation(outward);

                var gaps = openings.Where(o => o.Side == side).Select(o => o.Offset).OrderBy(o => o).ToList();

                // Segments between the gaps, left to right in the wall's own space.
                var cursor = -length * 0.5f;
                foreach (var gap in gaps)
                {
                    AddBlock(wall, "Segment", cursor, gap - OpeningWidth * 0.5f, 0f, WallHeight, wallMaterial);
                    AddBlock(wall, "Lintel", gap - OpeningWidth * 0.5f, gap + OpeningWidth * 0.5f, OpeningHeight,
                        WallHeight, wallMaterial);

                    onOpening?.Invoke(wall, gap);
                    cursor = gap + OpeningWidth * 0.5f;
                }

                AddBlock(wall, "Segment", cursor, length * 0.5f, 0f, WallHeight, wallMaterial);
            }

            var roof = GameObject.CreatePrimitive(PrimitiveType.Cube);
            roof.name = "Roof";
            roof.transform.SetParent(building, false);
            roof.transform.localPosition = new Vector3(0f, WallHeight + 0.1f, 0f);
            roof.transform.localScale = new Vector3(size.x + 0.6f, 0.2f, size.y + 0.6f);
            roof.GetComponent<MeshRenderer>().sharedMaterial = roofMaterial;
        }

        /// <summary>A block of wall from <paramref name="from"/> to <paramref name="to"/> along the wall, between two heights.</summary>
        private static void AddBlock(Transform wall, string name, float from, float to, float bottom, float top, Material material)
        {
            if (to - from < 0.05f || top - bottom < 0.05f) return;

            var block = GameObject.CreatePrimitive(PrimitiveType.Cube);
            block.name = name;
            block.transform.SetParent(wall, false);
            block.transform.localPosition = new Vector3((from + to) * 0.5f, (bottom + top) * 0.5f, 0f);
            block.transform.localScale = new Vector3(to - from, top - bottom, WallThickness);
            block.GetComponent<MeshRenderer>().sharedMaterial = material;
        }

        /// <summary>
        /// A barricade filling an opening: the interaction trigger, the blocker and NavMesh obstacle it switches on
        /// when standing, three planks, and the point outside where creatures attack from.
        /// </summary>
        private static void CreateBarricade(Transform wall, float offset, int order, Material wood)
        {
            var barricadeObject = new GameObject($"Barricade {order}")
            {
                layer = LayerMask.NameToLayer("Interact")
            };

            var transform = barricadeObject.transform;
            transform.SetParent(wall, false);
            transform.localPosition = new Vector3(offset, OpeningHeight * 0.5f, 0f);

            // Thick enough to aim at from either side of the wall.
            var trigger = barricadeObject.AddComponent<BoxCollider>();
            trigger.isTrigger = true;
            trigger.size = new Vector3(OpeningWidth, OpeningHeight, 0.9f);

            var blocker = barricadeObject.AddComponent<BoxCollider>();
            blocker.size = new Vector3(OpeningWidth, OpeningHeight, 0.15f);
            blocker.enabled = false;

            var obstacle = barricadeObject.AddComponent<NavMeshObstacle>();
            obstacle.shape = NavMeshObstacleShape.Box;
            obstacle.size = new Vector3(OpeningWidth + 0.2f, OpeningHeight, 0.8f);
            obstacle.carving = true;
            obstacle.carveOnlyStationary = true;
            obstacle.enabled = false;

            var planks = new GameObject[3];
            for (var i = 0; i < planks.Length; i++)
            {
                var plank = GameObject.CreatePrimitive(PrimitiveType.Cube);
                plank.name = $"Plank {i + 1}";
                Object.DestroyImmediate(plank.GetComponent<BoxCollider>());

                plank.transform.SetParent(transform, false);
                plank.transform.localPosition = new Vector3(0f, -0.6f + i * 0.6f, 0.12f);
                plank.transform.localRotation = Quaternion.Euler(0f, 0f, i % 2 == 0 ? 8f : -6f);
                plank.transform.localScale = new Vector3(OpeningWidth + 0.35f, 0.24f, 0.06f);
                plank.GetComponent<MeshRenderer>().sharedMaterial = wood;
                plank.SetActive(false);
                planks[i] = plank;
            }

            var attackPoint = new GameObject("AttackPoint").transform;
            attackPoint.SetParent(transform, false);
            attackPoint.localPosition = new Vector3(0f, -OpeningHeight * 0.5f, 1.1f);

            var barricade = barricadeObject.AddComponent<Barricade>();
            var serialized = new SerializedObject(barricade);
            serialized.FindProperty("m_order").intValue = order;
            serialized.FindProperty("m_blocker").objectReferenceValue = blocker;
            serialized.FindProperty("m_obstacle").objectReferenceValue = obstacle;
            serialized.FindProperty("m_attackPoint").objectReferenceValue = attackPoint;

            var plankList = serialized.FindProperty("m_planks");
            plankList.arraySize = planks.Length;
            for (var i = 0; i < planks.Length; i++) plankList.GetArrayElementAtIndex(i).objectReferenceValue = planks[i];

            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        // ---- Nature and edges ------------------------------------------------------------------------

        private static void BuildTrees(Layout layout, Transform parent, Materials materials)
        {
            var root = new GameObject("Trees").transform;
            root.SetParent(parent, false);

            var random = new System.Random(layout.Seed + 1);
            var trunk = materials.Get("Trunk", new Color(0.28f, 0.2f, 0.14f));
            var leaves = materials.Get("Leaves", new Color(0.12f, 0.22f, 0.12f));

            var keepClear = layout.Houses
                .Select(h => (Centre: h.Centre, Radius: Mathf.Max(h.Size.x, h.Size.y) * 0.5f + 3f))
                .Append((Centre: layout.ShelterCentre,
                    Radius: Mathf.Max(layout.ShelterSize.x, layout.ShelterSize.y) * 0.5f + 6f))
                .ToList();

            var placed = 0;
            for (var attempt = 0; attempt < layout.TreeCount * 20 && placed < layout.TreeCount; attempt++)
            {
                var position = new Vector3(Lerp(random, -46f, 46f), 0f, Lerp(random, -46f, 46f));
                if (new Vector2(position.x, position.z).magnitude < layout.TreeInnerRadius) continue;
                if (layout.SeaWall && position.z < -30f) continue;
                if (keepClear.Any(c => Vector3.Distance(c.Centre, position) < c.Radius)) continue;

                var height = Lerp(random, 4f, 7f);

                var tree = new GameObject($"Tree {placed + 1}").transform;
                tree.SetParent(root, false);
                tree.localPosition = position;

                var stem = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                stem.name = "Trunk";
                stem.transform.SetParent(tree, false);
                stem.transform.localPosition = new Vector3(0f, height * 0.5f, 0f);
                stem.transform.localScale = new Vector3(0.45f, height * 0.5f, 0.45f);
                stem.GetComponent<MeshRenderer>().sharedMaterial = trunk;

                var crown = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                crown.name = "Crown";
                Object.DestroyImmediate(crown.GetComponent<SphereCollider>());
                crown.transform.SetParent(tree, false);
                crown.transform.localPosition = new Vector3(0f, height, 0f);
                crown.transform.localScale = Vector3.one * Lerp(random, 2.6f, 4f);
                crown.GetComponent<MeshRenderer>().sharedMaterial = leaves;

                placed++;
            }
        }

        /// <summary>A wall round the ground's edge, and a sea wall along the shore for the coast.</summary>
        private static void BuildBoundary(Layout layout, Transform parent, Materials materials)
        {
            var root = new GameObject("Boundary").transform;
            root.SetParent(parent, false);

            var fence = materials.Get("Boundary", new Color(0.18f, 0.17f, 0.16f));
            AddEdge(root, "North", new Vector3(0f, 1.25f, 49.5f), new Vector3(100f, 2.5f, 1f), fence);
            AddEdge(root, "South", new Vector3(0f, 1.25f, -49.5f), new Vector3(100f, 2.5f, 1f), fence);
            AddEdge(root, "East", new Vector3(49.5f, 1.25f, 0f), new Vector3(1f, 2.5f, 100f), fence);
            AddEdge(root, "West", new Vector3(-49.5f, 1.25f, 0f), new Vector3(1f, 2.5f, 100f), fence);

            if (!layout.SeaWall) return;

            AddEdge(root, "Sea Wall", new Vector3(0f, 0.6f, -34f), new Vector3(100f, 1.2f, 1f), fence);

            // Water is a flat, collider-less plate: the sea wall is what keeps players and creatures out of it.
            var sea = GameObject.CreatePrimitive(PrimitiveType.Cube);
            sea.name = "Sea";
            Object.DestroyImmediate(sea.GetComponent<BoxCollider>());
            sea.transform.SetParent(root, false);
            sea.transform.localPosition = new Vector3(0f, 0.03f, -42f);
            sea.transform.localScale = new Vector3(100f, 0.02f, 15f);
            sea.GetComponent<MeshRenderer>().sharedMaterial = materials.Get("Sea", new Color(0.1f, 0.2f, 0.28f));
        }

        private static void AddEdge(Transform parent, string name, Vector3 position, Vector3 scale, Material material)
        {
            var edge = GameObject.CreatePrimitive(PrimitiveType.Cube);
            edge.name = name;
            edge.transform.SetParent(parent, false);
            edge.transform.localPosition = position;
            edge.transform.localScale = scale;
            edge.GetComponent<MeshRenderer>().sharedMaterial = material;
        }

        // ---- Spawns and director ---------------------------------------------------------------------

        private static Transform[] BuildCreatureSpawns()
        {
            var root = new GameObject(CreatureSpawnsName).transform;

            var corners = new[]
            {
                new Vector3(-43f, 0f, 43f), new Vector3(43f, 0f, 43f), new Vector3(43f, 0f, -26f), new Vector3(-43f, 0f, -26f)
            };

            return corners.Select((position, i) =>
            {
                var spawn = new GameObject($"Creature Spawn {i + 1}").transform;
                spawn.SetParent(root, false);
                spawn.position = position;
                spawn.rotation = Quaternion.LookRotation(-new Vector3(position.x, 0f, position.z));
                return spawn;
            }).ToArray();
        }

        /// <summary>Players start just outside the shelter's front, facing out into the settlement.</summary>
        private static void BuildPlayerSpawns(Layout layout)
        {
            var root = new GameObject(SpawnPointsName);
            var spawnPoints = root.AddComponent<PlayerSpawnPoints>();

            var shelterRotation = Quaternion.Euler(0f, layout.ShelterYaw, 0f);
            var front = shelterRotation * Vector3.forward;
            var across = shelterRotation * Vector3.right;
            var origin = layout.ShelterCentre + front * (layout.ShelterSize.y * 0.5f + 3f);

            var serialized = new SerializedObject(spawnPoints);
            var list = serialized.FindProperty("m_spawnPoints");
            list.arraySize = SessionMaxPlayers;

            for (var i = 0; i < SessionMaxPlayers; i++)
            {
                var point = new GameObject($"Spawn {i}").transform;
                point.SetParent(root.transform, false);
                point.position = origin + across * ((i - (SessionMaxPlayers - 1) * 0.5f) * 1.5f) + Vector3.up * 0.2f;
                point.rotation = Quaternion.LookRotation(front);
                list.GetArrayElementAtIndex(i).objectReferenceValue = point;
            }

            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        private static int SessionMaxPlayers => QuietVillage.Multiplayer.Sessions.SessionService.MaxRoomSize;

        private static void BuildDirector(Layout layout, GameObject creaturePrefab, Transform[] creatureSpawns,
            StringBuilder report)
        {
            var directorObject = new GameObject(DirectorName);

            var networkObject = directorObject.AddComponent<NetworkObject>();
            networkObject.SynchronizeTransform = false;
            networkObject.AutoObjectParentSync = false;

            var director = directorObject.AddComponent<SurvivalDirector>();
            var lighting = directorObject.AddComponent<DayNightLighting>();

            var serialized = new SerializedObject(director);
            serialized.FindProperty("m_dayDuration").floatValue = layout.DaySeconds;
            serialized.FindProperty("m_nightDuration").floatValue = layout.NightSeconds;
            serialized.FindProperty("m_buildItem.m_GUID").stringValue = ScrapItemGuid;
            serialized.FindProperty("m_creaturePrefab").objectReferenceValue = creaturePrefab;
            serialized.FindProperty("m_creaturesBase").intValue = layout.CreaturesBase;
            serialized.FindProperty("m_creaturesPerPlayer").intValue = layout.CreaturesPerPlayer;

            var spawns = serialized.FindProperty("m_creatureSpawns");
            spawns.arraySize = creatureSpawns.Length;
            for (var i = 0; i < creatureSpawns.Length; i++) spawns.GetArrayElementAtIndex(i).objectReferenceValue = creatureSpawns[i];

            serialized.ApplyModifiedPropertiesWithoutUndo();

            // The level's directional light: Create New Level's moonlight, or whatever directional light is there.
            var sun = Object.FindObjectsByType<Light>(FindObjectsInactive.Exclude, FindObjectsSortMode.None)
                .FirstOrDefault(l => l.type == LightType.Directional);

            if (sun == null) report.AppendLine("WARNING: no directional light; the day/night cycle only changes fog.");

            var lightingSerialized = new SerializedObject(lighting);
            lightingSerialized.FindProperty("m_sun").objectReferenceValue = sun;
            lightingSerialized.ApplyModifiedPropertiesWithoutUndo();

            if (creaturePrefab == null) report.AppendLine("WARNING: no creature prefab assigned; nights will be empty.");
        }

        private static void NameInCatalog(Layout layout)
        {
            var catalog = AssetDatabase.LoadAssetAtPath<LevelCatalog>(LevelSetup.CatalogPath);
            var level = catalog != null ? catalog.EditableLevels.FirstOrDefault(l => l.SceneName == layout.SceneName) : null;
            if (level == null) return;

            level.DisplayName = layout.DisplayName;
            level.Description = layout.Description;

            // Settlements before anything else, in build order, so the lobby offers the first settlement by default
            // rather than the template's demo.
            var levels = catalog.EditableLevels;
            var settlements = new[] { ForestVillage().SceneName, CoastalTown().SceneName };
            levels.Sort((a, b) => Rank(a).CompareTo(Rank(b)));

            int Rank(LevelCatalog.Level entry)
            {
                var index = Array.IndexOf(settlements, entry?.SceneName);
                return index >= 0 ? index : settlements.Length;
            }

            EditorUtility.SetDirty(catalog);
            AssetDatabase.SaveAssets();
        }

        // ---- NavMesh ---------------------------------------------------------------------------------

        /// <summary>
        /// Bakes walkable ground from colliders, leaving out barricades and pickups, and saves it beside the scene.
        /// </summary>
        /// <remarks>
        /// Barricades are baked open: standing ones carve themselves out at runtime, so the same NavMesh serves every
        /// state of the shelter.
        /// </remarks>
        private static bool BakeNavMesh(string sceneName, string folder, StringBuilder report)
        {
            Physics.SyncTransforms();

            var surface = new GameObject(NavMeshName).AddComponent<NavMeshSurface>();
            surface.collectObjects = CollectObjects.All;
            surface.useGeometry = NavMeshCollectGeometry.PhysicsColliders;
            surface.layerMask = ~LayerMask.GetMask(NonWalkableLayers);
            surface.BuildNavMesh();

            if (surface.navMeshData == null)
            {
                report.AppendLine("FAILED: the NavMesh bake produced no data.");
                return false;
            }

            EnsureFolder(folder);
            var path = $"{folder}/NavMesh-{sceneName}.asset";
            AssetDatabase.DeleteAsset(path);
            AssetDatabase.CreateAsset(surface.navMeshData, path);
            EditorUtility.SetDirty(surface);

            var triangulation = NavMesh.CalculateTriangulation();
            report.AppendLine($"Baked NavMesh ({triangulation.indices.Length / 3} triangles) to {path}.");
            return true;
        }

        // ---- Creature prefab -------------------------------------------------------------------------

        /// <summary>
        /// Creates the placeholder creature once, registers it as a network prefab, and checks its hash reached disk.
        /// </summary>
        /// <remarks>
        /// Left alone once it exists, so a creature someone has reworked is never overwritten; delete it to regenerate.
        /// The hash check is the same trap the player prefab has: in memory it can look set while the file holds 0.
        /// </remarks>
        private static bool EnsureCreaturePrefab(StringBuilder report)
        {
            if (AssetDatabase.LoadAssetAtPath<GameObject>(CreaturePrefabPath) == null)
            {
                EnsureFolder(PrefabFolder);
                var materials = new Materials();

                var root = new GameObject("NightCreature");
                try
                {
                    var body = GameObject.CreatePrimitive(PrimitiveType.Capsule);
                    body.name = "Body";
                    body.transform.SetParent(root.transform, false);
                    body.transform.localPosition = new Vector3(0f, 1.15f, 0f);
                    body.transform.localScale = new Vector3(0.8f, 1.15f, 0.8f);
                    body.GetComponent<MeshRenderer>().sharedMaterial = materials.Get("Creature", new Color(0.04f, 0.04f, 0.05f));

                    var eye = materials.GetEmissive("CreatureEyes", new Color(1f, 0.1f, 0.05f), 4f);
                    foreach (var x in new[] { -0.14f, 0.14f })
                    {
                        var eyeObject = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                        eyeObject.name = "Eye";
                        Object.DestroyImmediate(eyeObject.GetComponent<SphereCollider>());
                        eyeObject.transform.SetParent(root.transform, false);
                        eyeObject.transform.localPosition = new Vector3(x, 1.95f, 0.33f);
                        eyeObject.transform.localScale = Vector3.one * 0.09f;
                        eyeObject.GetComponent<MeshRenderer>().sharedMaterial = eye;
                        eyeObject.GetComponent<MeshRenderer>().shadowCastingMode = ShadowCastingMode.Off;
                    }

                    var agent = root.AddComponent<NavMeshAgent>();
                    agent.speed = 3.4f;
                    agent.angularSpeed = 360f;
                    agent.acceleration = 12f;
                    agent.stoppingDistance = 0.9f;
                    agent.radius = 0.4f;
                    agent.height = 2.3f;

                    root.AddComponent<NetworkObject>();
                    ConfigureServerTransform(root.AddComponent<NetworkTransform>());

                    var creature = root.AddComponent<NightCreature>();
                    var serialized = new SerializedObject(creature);
                    serialized.FindProperty("m_agent").objectReferenceValue = agent;
                    serialized.ApplyModifiedPropertiesWithoutUndo();

                    PrefabUtility.SaveAsPrefabAsset(root, CreaturePrefabPath);
                    report.AppendLine($"Created {CreaturePrefabPath}.");
                }
                finally
                {
                    Object.DestroyImmediate(root);
                }
            }

            AssetDatabase.ImportAsset(CreaturePrefabPath, ImportAssetOptions.ForceUpdate);
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(CreaturePrefabPath);
            if (prefab == null || !prefab.TryGetComponent<NetworkObject>(out var networkObject))
            {
                report.AppendLine($"FAILED: could not load {CreaturePrefabPath}.");
                return false;
            }

            if (networkObject.PrefabIdHash == 0)
            {
                EditorUtility.SetDirty(networkObject);
                PrefabUtility.SavePrefabAsset(prefab);
                AssetDatabase.SaveAssets();
                AssetDatabase.ImportAsset(CreaturePrefabPath, ImportAssetOptions.ForceUpdate);
                prefab = AssetDatabase.LoadAssetAtPath<GameObject>(CreaturePrefabPath);
                networkObject = prefab.GetComponent<NetworkObject>();
            }

            if (networkObject.PrefabIdHash == 0)
            {
                report.AppendLine("FAILED: the creature prefab's network hash is still 0; it could not spawn.");
                return false;
            }

            var list = AssetDatabase.LoadAssetAtPath<NetworkPrefabsList>(LobbySetup.NetworkPrefabsListPath);
            if (list == null)
            {
                report.AppendLine($"FAILED: {LobbySetup.NetworkPrefabsListPath} not found; creatures could not spawn.");
                return false;
            }

            if (!list.Contains(prefab))
            {
                list.Add(new NetworkPrefab { Prefab = prefab });
                EditorUtility.SetDirty(list);
                AssetDatabase.SaveAssets();
                report.AppendLine("Registered NightCreature in DefaultNetworkPrefabs.");
            }

            return true;
        }

        /// <summary>Host-driven movement, position plus facing, as the project's other NPCs replicate.</summary>
        private static void ConfigureServerTransform(NetworkTransform networkTransform)
        {
            networkTransform.AuthorityMode = NetworkTransform.AuthorityModes.Server;
            networkTransform.SyncPositionX = networkTransform.SyncPositionY = networkTransform.SyncPositionZ = true;
            networkTransform.SyncRotAngleX = networkTransform.SyncRotAngleZ = false;
            networkTransform.SyncRotAngleY = true;
            networkTransform.SyncScaleX = networkTransform.SyncScaleY = networkTransform.SyncScaleZ = false;
            networkTransform.InLocalSpace = false;
            networkTransform.Interpolate = true;
        }

        // ---- Helpers ---------------------------------------------------------------------------------

        /// <summary>Flat-coloured placeholder materials, one asset per name, reused between builds.</summary>
        private sealed class Materials
        {
            private readonly Dictionary<string, Material> m_cache = new();

            public Material Get(string name, Color colour) => Load(name, colour, false, 0f);

            public Material GetEmissive(string name, Color colour, float intensity) => Load(name, colour, true, intensity);

            private Material Load(string name, Color colour, bool emissive, float intensity)
            {
                if (m_cache.TryGetValue(name, out var cached) && cached != null) return cached;

                EnsureFolder(ArtFolder);
                var path = $"{ArtFolder}/Greybox_{name}.mat";

                var material = AssetDatabase.LoadAssetAtPath<Material>(path);
                if (material == null)
                {
                    var shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
                    material = new Material(shader);
                    if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", colour);
                    material.color = colour;

                    if (emissive)
                    {
                        material.EnableKeyword("_EMISSION");
                        material.SetColor("_EmissionColor", colour * intensity);
                    }

                    AssetDatabase.CreateAsset(material, path);
                }

                // Matte, enforced every build: at URP's default smoothness every upward face reflects the same grey
                // sky, and roofs, ground and paths become indistinguishable from above.
                if (material.HasProperty("_Smoothness") && material.GetFloat("_Smoothness") > 0.1f)
                {
                    material.SetFloat("_Smoothness", 0.1f);
                    EditorUtility.SetDirty(material);
                }

                m_cache[name] = material;
                return material;
            }
        }

        private static float Lerp(System.Random random, float min, float max) =>
            Mathf.Lerp(min, max, (float)random.NextDouble());

        private static void EnsureFolder(string path)
        {
            if (string.IsNullOrEmpty(path) || AssetDatabase.IsValidFolder(path)) return;

            var parent = Path.GetDirectoryName(path)?.Replace('\\', '/');
            EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, Path.GetFileName(path));
        }
    }
}
