using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using QuietVillage.Gameplay.Environment;
using QuietVillage.Gameplay.Survival;
using UnityEditor;
using UnityEngine;
using UnityEngine.AI;
using Object = UnityEngine.Object;
using Random = System.Random;

namespace QuietVillage.Gameplay.EditorTools
{
    /// <summary>
    /// Dresses a greybox settlement as an old cemetery with the Old Cemetery pack: stone chapel and crypts, grave plots,
    /// iron fencing and a gate, angels, dead trees, candles, ivy, paths, and each level's weather.
    /// </summary>
    /// <remarks>
    /// Dressing, not a new layout: the shelter, its barricades, the houses and their scrap, and every spawn stay exactly
    /// where the greybox puts them, so the loop plays and balances as before. The shelter reads as the chapel and the
    /// houses as family crypts; the pack has no buildings of its own.
    ///
    /// Everything placed here is kept out of what play needs to be open: a margin round the buildings, the ground in front
    /// of every doorway, both kinds of spawn, loose scrap, and the paths. Plots are filled cell by cell, so a cell that
    /// would crowd any of those is simply left as grass. After the NavMesh is baked, <see cref="CheckCemeteryPaths"/> proves
    /// every creature spawn can still reach every barricade and every player spawn, and reports any that cannot.
    ///
    /// Solid pieces (graves, stones, statues, fences, trees) get colliders, so players cannot walk through them and the
    /// bake routes creatures round them. Grass, ivy, candles and paving stay walk-through. The pack's models ship without
    /// colliders; those are sized here from the mesh. Seeded, so a rebuild places everything in the same spot.
    /// </remarks>
    public static partial class SettlementGreyboxBuilder
    {
        private const string WeatherName = "Weather";
        private const string DressingName = "Cemetery";

        private const string Pack = "Assets/Old Cemetery/Assets";
        private const string CemeteryMaterialFolder = ProjectPaths.Art + "/Materials/Cemetery";

        // A generous floor under the fence line, so the dead trees and fog beyond it do not stand over nothing.
        private const float CemeteryGroundScale = 16f;
        private const float GroundTile = 3f;

        private const float FenceInset = 48.6f;

        // ---- Styles ----------------------------------------------------------------------------------

        private sealed class Plot
        {
            public Vector3 Centre;
            public Vector2 Size;
            public float Yaw;
            public bool Fenced;

            /// <summary>Side of the plot, in its own frame, the fence leaves open.</summary>
            public Side Opening = Side.Front;
        }

        private sealed class Statue
        {
            public Vector3 Position;
            public float Yaw;
            public bool Kneeling;
        }

        private sealed class CemeteryStyle
        {
            public string GroundLayer;
            public Plot[] Plots = Array.Empty<Plot>();
            public Vector3[][] Paths = Array.Empty<Vector3[]>();
            public Vector3 Gate;
            public float GateYaw;
            public Statue[] Statues = Array.Empty<Statue>();
            public Vector3[] Candles = Array.Empty<Vector3>();
            public int CandlesOnGraves;
            public int OldGraveClusters;
            public float OldGraveMinRadius = 18f;
            public int GrassTufts;
            public int OuterTrees;
            public Vector2 TreeScale = new(0.4f, 0.7f);
            public bool Rain;
            public bool SeaBeyond;

            public Color DayFog;
            public float DayFogDensity;
            public float DayIntensity;
            public Color DayAmbient;
            public Color NightFog;
            public float NightFogDensity;
        }

        /// <summary>
        /// Forest Village as a wooded graveyard: a chapel in its clearing, fenced plots along an avenue to the north crypt,
        /// old headstones scattered in the trees, and heavy, still fog.
        /// </summary>
        private static CemeteryStyle HollowChapelStyle() => new()
        {
            GroundLayer = "layer_Cemetery-grassy",
            Paths = new[]
            {
                new[] { new Vector3(0f, 0f, -48f), new Vector3(0f, 0f, -7.4f) },
                new[] { new Vector3(0f, 0f, 7.6f), new Vector3(0f, 0f, 28f) },
                new[] { new Vector3(0f, 0f, 14.5f), new Vector3(-17.5f, 0f, 14f) },
                new[] { new Vector3(0f, 0f, 14.5f), new Vector3(16f, 0f, 18f) }
            },
            Plots = new[]
            {
                new Plot { Centre = new Vector3(-8.5f, 0f, 21.5f), Size = new Vector2(8f, 8f), Fenced = true, Opening = Side.Right },
                new Plot { Centre = new Vector3(8.5f, 0f, 22f), Size = new Vector2(8f, 7f), Fenced = true, Opening = Side.Left },
                new Plot { Centre = new Vector3(-6.5f, 0f, -18f), Size = new Vector2(7f, 10f) },
                new Plot { Centre = new Vector3(6.5f, 0f, -18f), Size = new Vector2(7f, 10f) },
                new Plot { Centre = new Vector3(-30f, 0f, 30f), Size = new Vector2(10f, 8f), Yaw = 15f, Fenced = true, Opening = Side.Back },
                new Plot { Centre = new Vector3(30f, 0f, 34f), Size = new Vector2(10f, 8f), Yaw = -10f, Fenced = true },
                new Plot { Centre = new Vector3(-32f, 0f, -34f), Size = new Vector2(9f, 8f), Yaw = 20f }
            },
            Gate = new Vector3(0f, 0f, -FenceInset),
            GateYaw = 0f,
            Statues = new[]
            {
                new Statue { Position = new Vector3(-3.4f, 0f, 9.8f), Yaw = 180f },
                new Statue { Position = new Vector3(3.4f, 0f, 9.8f), Yaw = 180f },
                new Statue { Position = new Vector3(-2.6f, 0f, -44f), Yaw = 0f, Kneeling = true },
                new Statue { Position = new Vector3(2.6f, 0f, -44f), Yaw = 0f, Kneeling = true }
            },
            Candles = new[]
            {
                new Vector3(-1.9f, 0f, 5.0f), new Vector3(1.9f, 0f, 5.0f), new Vector3(0.6f, 0f, -5.0f), new Vector3(4.4f, 0f, -5.0f)
            },
            CandlesOnGraves = 6,
            OldGraveClusters = 14,
            GrassTufts = 360,
            OuterTrees = 70,
            TreeScale = new Vector2(0.4f, 0.7f),
            DayFog = new Color(0.47f, 0.5f, 0.48f),
            DayFogDensity = 0.014f,
            DayIntensity = 0.75f,
            DayAmbient = new Color(0.34f, 0.36f, 0.36f),
            NightFog = new Color(0.02f, 0.025f, 0.03f),
            NightFogDensity = 0.07f
        };

        /// <summary>
        /// Coastal Town as a seaside cemetery: open grave fields between the chapel and the crypt rows, a crypt street,
        /// a plot along the north wall, few trees, rain, and the sea past the sea wall.
        /// </summary>
        private static CemeteryStyle SaltmarshStyle() => new()
        {
            GroundLayer = "layer_Cemetery-ground",
            Paths = new[]
            {
                new[] { new Vector3(2f, 0f, 47.5f), new Vector3(2f, 0f, 21f) },
                new[] { new Vector3(-42f, 0f, 21f), new Vector3(42f, 0f, 21f) },
                new[] { new Vector3(22.5f, 0f, 21f), new Vector3(22.5f, 0f, -9.5f) }
            },
            Plots = new[]
            {
                new Plot { Centre = new Vector3(-33f, 0f, -15f), Size = new Vector2(10f, 22f), Fenced = true },
                new Plot { Centre = new Vector3(-19f, 0f, -15f), Size = new Vector2(10f, 22f), Fenced = true },
                new Plot { Centre = new Vector3(-6f, 0f, -11.5f), Size = new Vector2(8f, 16f) },
                new Plot { Centre = new Vector3(35f, 0f, -15.5f), Size = new Vector2(12f, 18f), Fenced = true, Opening = Side.Left },
                new Plot { Centre = new Vector3(-19f, 0f, 41.5f), Size = new Vector2(34f, 8f) },
                new Plot { Centre = new Vector3(22f, 0f, 41.5f), Size = new Vector2(34f, 8f) }
            },
            Gate = new Vector3(2f, 0f, FenceInset),
            GateYaw = 180f,
            Statues = new[]
            {
                new Statue { Position = new Vector3(-26f, 0f, 2.5f), Yaw = 180f },
                new Statue { Position = new Vector3(-12f, 0f, 2.5f), Yaw = 180f },
                new Statue { Position = new Vector3(-1f, 0f, 44.5f), Yaw = 180f, Kneeling = true },
                new Statue { Position = new Vector3(5f, 0f, 44.5f), Yaw = 180f, Kneeling = true }
            },
            Candles = new[] { new Vector3(19.7f, 0f, -11.4f), new Vector3(19.7f, 0f, -20.6f) },
            CandlesOnGraves = 5,
            OldGraveClusters = 5,
            OldGraveMinRadius = 26f,
            GrassTufts = 200,
            OuterTrees = 26,
            TreeScale = new Vector2(0.45f, 0.7f),
            Rain = true,
            SeaBeyond = true,
            DayFog = new Color(0.55f, 0.58f, 0.62f),
            DayFogDensity = 0.009f,
            DayIntensity = 0.7f,
            DayAmbient = new Color(0.36f, 0.38f, 0.42f),
            NightFog = new Color(0.02f, 0.025f, 0.035f),
            NightFogDensity = 0.06f
        };

        private static void ApplyCemeteryWeather(CemeteryStyle style, SerializedObject lighting)
        {
            lighting.FindProperty("m_dayFog").colorValue = style.DayFog;
            lighting.FindProperty("m_dayFogDensity").floatValue = style.DayFogDensity;
            lighting.FindProperty("m_dayIntensity").floatValue = style.DayIntensity;
            lighting.FindProperty("m_dayAmbient").colorValue = style.DayAmbient;
            lighting.FindProperty("m_nightFog").colorValue = style.NightFog;
            lighting.FindProperty("m_nightFogDensity").floatValue = style.NightFogDensity;
        }

        // ---- Dressing --------------------------------------------------------------------------------

        /// <summary>Everything that has to stay open, as circles and corridors on the ground.</summary>
        private sealed class KeepClear
        {
            private readonly List<(Vector2 Centre, float Radius)> m_circles = new();
            private readonly List<(Vector2 From, Vector2 To, float HalfWidth)> m_corridors = new();
            private readonly List<(Vector3 Centre, Quaternion Rotation, Vector2 Half)> m_boxes = new();

            public void Circle(Vector3 centre, float radius) => m_circles.Add((Flat(centre), radius));

            public void Corridor(Vector3 from, Vector3 to, float width) => m_corridors.Add((Flat(from), Flat(to), width * 0.5f));

            public void Box(Vector3 centre, float yaw, Vector2 size) =>
                m_boxes.Add((centre, Quaternion.Euler(0f, yaw, 0f), size * 0.5f));

            /// <summary>Whether a footprint of <paramref name="radius"/> at <paramref name="position"/> is free.</summary>
            public bool IsFree(Vector3 position, float radius)
            {
                var point = Flat(position);

                foreach (var (centre, circleRadius) in m_circles)
                    if (Vector2.Distance(point, centre) < circleRadius + radius) return false;

                foreach (var (from, to, halfWidth) in m_corridors)
                    if (DistanceToSegment(point, from, to) < halfWidth + radius) return false;

                foreach (var (centre, rotation, half) in m_boxes)
                {
                    var local = Quaternion.Inverse(rotation) * (position - centre);
                    if (Mathf.Abs(local.x) < half.x + radius && Mathf.Abs(local.z) < half.y + radius) return false;
                }

                return true;
            }

            private static Vector2 Flat(Vector3 v) => new(v.x, v.z);

            private static float DistanceToSegment(Vector2 p, Vector2 a, Vector2 b)
            {
                var ab = b - a;
                var t = ab.sqrMagnitude > 0f ? Mathf.Clamp01(Vector2.Dot(p - a, ab) / ab.sqrMagnitude) : 0f;
                return Vector2.Distance(p, a + ab * t);
            }
        }

        private sealed class CemeteryAssets
        {
            public GameObject[] GraveSlabs, Headstones, OldStones, TallStones, Pots, Trees, Grass, Ivy;
            public GameObject Mound, Candle, CandleTrio, AngelStatue, AngelKneeling, FenceSection, FenceCorner,
                PerimeterSection, Gate, Pavement, Rain;
            public Material Skybox, Stone, CryptStone, Slate, Ground;

            public bool IsComplete(StringBuilder report)
            {
                var missing = GetType().GetFields()
                    .Where(field => field.GetValue(this) == null
                                    || field.GetValue(this) is Array array && (array.Length == 0 || array.Cast<object>().Any(item => item == null)))
                    .Select(field => field.Name).ToList();

                if (missing.Count == 0) return true;

                report.AppendLine($"FAILED: Old Cemetery assets missing ({string.Join(", ", missing)}). Import the pack and its URP package.");
                return false;
            }
        }

        private static void DressAsCemetery(Layout layout, Transform layoutRoot, Transform[] creatureSpawns,
            Transform[] playerSpawns, StringBuilder report)
        {
            var style = layout.Cemetery;
            var assets = LoadCemeteryAssets(style);
            if (!assets.IsComplete(report)) return;

            var random = new Random(layout.Seed + 7);
            var root = new GameObject(DressingName).transform;
            root.SetParent(layoutRoot, false);

            var keepClear = BuildKeepClear(layout, style, layoutRoot, creatureSpawns, playerSpawns);
            var counts = new Dictionary<string, int>();
            void Count(string what, int amount = 1) => counts[what] = counts.TryGetValue(what, out var n) ? n + amount : amount;

            DressGroundAndSky(style, assets);
            DressBuildings(layout, layoutRoot, root, assets, random, Count);
            DressPaths(style, root, assets, Count);

            // Solid things first, so everything after them stays out of their way.
            var occupied = new KeepClear();
            foreach (var plot in style.Plots)
            {
                DressPlot(plot, root, assets, random, keepClear, occupied, Count);

                // The whole plot, fence line included, so trees and stray headstones keep to the unfenced ground.
                occupied.Box(plot.Centre, plot.Yaw, plot.Size + new Vector2(3f, 3f));
            }

            DressStatues(style, root, assets, keepClear, occupied, Count);
            DressPerimeter(style, root, layoutRoot, assets, Count);
            DressOldGraves(style, root, assets, random, keepClear, occupied, Count);
            DressTrees(layout, style, root, assets, random, keepClear, occupied, Count);
            DressCandles(style, root, assets, random, occupied, Count);
            DressGrass(style, root, assets, random, keepClear, Count);
            if (style.Rain) AddRain(assets);

            report.AppendLine("Cemetery: " + string.Join(", ", counts.OrderBy(pair => pair.Key).Select(pair => $"{pair.Value} {pair.Key}")) +
                              (style.Rain ? ", rain." : "."));
        }

        private static CemeteryAssets LoadCemeteryAssets(CemeteryStyle style)
        {
            GameObject Model(string name) => AssetDatabase.LoadAssetAtPath<GameObject>($"{Pack}/Models/{name}.fbx");
            GameObject Prefab(string name) => AssetDatabase.LoadAssetAtPath<GameObject>($"{Pack}/Prefabs/{name}.prefab");

            return new CemeteryAssets
            {
                GraveSlabs = Enumerable.Range(1, 5).Select(i => Model($"Grave{i}")).ToArray(),
                Headstones = new[] { 2, 4, 5, 6, 7, 8, 9, 10 }.Select(i => Model($"Gravestone{i}")).ToArray(),
                OldStones = Enumerable.Range(1, 19).Where(i => i != 3).Select(i => Model($"GravestonesOld{i}")).ToArray(),
                TallStones = new[] { Prefab("Gravestone1"), Prefab("Gravestone11"), Prefab("Gravestone12"), Model("Gravestone3") },
                Pots = Enumerable.Range(1, 5).Select(i => Prefab($"GravePot{i}")).ToArray(),
                Trees = new[] { Prefab("CemeteryTree1"), Prefab("CemeteryTree2") },
                Grass = new[] { Prefab("CemeteryGrass1"), Prefab("CemeteryGrass2") },
                Ivy = Enumerable.Range(1, 5).Select(i => Model($"Ivy{i}")).ToArray(),
                Mound = Prefab("GraveMound"),
                Candle = Prefab("CemeteryCandle"),
                CandleTrio = Prefab("CemeteryCandleTrio"),
                AngelStatue = Prefab("AngelStatue-LODs"),
                AngelKneeling = Prefab("Angel-kneeling-LODs"),
                FenceSection = Model("CemeteryFenceA2"),
                FenceCorner = Model("CemeteryFenceAcorner"),
                PerimeterSection = Model("CemeteryFenceB3"),
                Gate = Model("CemeteryGate"),
                Pavement = Model("Pavement"),
                Rain = Prefab("ParticleEffect-RAIN"),
                Skybox = AssetDatabase.LoadAssetAtPath<Material>($"{Pack}/Materials/CemeterySkybox.mat"),
                Stone = StoneMaterial("ChapelStone", new Color(0.62f, 0.6f, 0.56f), "layer_Cemetery-cobblestone"),
                CryptStone = StoneMaterial("CryptStone", new Color(0.46f, 0.46f, 0.45f), "layer_Cemetery-cobblestone"),
                Slate = StoneMaterial("Slate", new Color(0.12f, 0.12f, 0.14f), null),
                Ground = GroundMaterial(style.GroundLayer)
            };
        }

        // ---- Materials -------------------------------------------------------------------------------

        /// <summary>A matte stone material, textured with a terrain layer's masonry when one is named.</summary>
        /// <remarks>
        /// Wall blocks are stretched cubes, so the texture stretches with them. A coarse, dark masonry at a small tiling
        /// hides that far better than a flat colour hides that the walls are boxes. Enforced every run.
        /// </remarks>
        private static Material StoneMaterial(string name, Color colour, string textureLayer)
        {
            EnsureFolder(CemeteryMaterialFolder);
            var path = $"{CemeteryMaterialFolder}/{name}.mat";

            var material = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (material == null)
            {
                material = new Material(Shader.Find("Universal Render Pipeline/Lit"));
                AssetDatabase.CreateAsset(material, path);
            }

            material.SetColor("_BaseColor", colour);
            material.SetFloat("_Smoothness", 0.08f);

            var layer = textureLayer != null ? LoadTerrainLayer(textureLayer) : null;
            if (layer != null && layer.diffuseTexture != null)
            {
                material.SetTexture("_BaseMap", layer.diffuseTexture);
                material.SetTextureScale("_BaseMap", new Vector2(1.5f, 1.5f));

                if (layer.normalMapTexture != null)
                {
                    material.SetTexture("_BumpMap", layer.normalMapTexture);
                    material.EnableKeyword("_NORMALMAP");
                }
            }

            EditorUtility.SetDirty(material);
            return material;
        }

        private static TerrainLayer LoadTerrainLayer(string layerName)
        {
            var guid = AssetDatabase.FindAssets($"{layerName} t:TerrainLayer", new[] { "Assets/Old Cemetery" }).FirstOrDefault();
            return guid != null ? AssetDatabase.LoadAssetAtPath<TerrainLayer>(AssetDatabase.GUIDToAssetPath(guid)) : null;
        }

        /// <summary>
        /// A ground material from one of the pack's terrain layers, tiled at the layer's own size over the whole floor.
        /// </summary>
        /// <remarks>
        /// Made from the layer's textures rather than using the pack's <c>CemeteryGround</c> material, which is tiled for a
        /// grave mound. Rebuilt every run, so a change to the ground's size is picked up.
        /// </remarks>
        private static Material GroundMaterial(string layerName)
        {
            var layer = LoadTerrainLayer(layerName);
            if (layer == null || layer.diffuseTexture == null) return null;

            EnsureFolder(CemeteryMaterialFolder);
            var path = $"{CemeteryMaterialFolder}/Ground_{layerName}.mat";

            var material = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (material == null)
            {
                material = new Material(Shader.Find("Universal Render Pipeline/Lit"));
                AssetDatabase.CreateAsset(material, path);
            }

            // The ground plane is 10 m per unit of scale; its UVs span it once.
            var tiles = 10f * CemeteryGroundScale / Mathf.Max(0.5f, layer.tileSize.x > 0f ? layer.tileSize.x : GroundTile);

            material.SetTexture("_BaseMap", layer.diffuseTexture);
            material.SetTextureScale("_BaseMap", new Vector2(tiles, tiles));
            material.SetColor("_BaseColor", new Color(0.8f, 0.8f, 0.8f));
            material.SetFloat("_Smoothness", 0.05f);

            if (layer.normalMapTexture != null)
            {
                material.SetTexture("_BumpMap", layer.normalMapTexture);
                material.EnableKeyword("_NORMALMAP");
            }

            EditorUtility.SetDirty(material);
            return material;
        }

        // ---- Ground, sky, buildings, paths ------------------------------------------------------------

        private static void DressGroundAndSky(CemeteryStyle style, CemeteryAssets assets)
        {
            var ground = GameObject.Find("Greybox/Ground");
            if (ground != null)
            {
                ground.transform.localScale = new Vector3(CemeteryGroundScale, 1f, CemeteryGroundScale);
                if (ground.TryGetComponent<MeshRenderer>(out var renderer)) renderer.sharedMaterial = assets.Ground;
            }

            RenderSettings.skybox = assets.Skybox;
        }

        /// <summary>Stone and slate for the chapel and crypts, ivy on their walls, and pots and candles at the crypt doors.</summary>
        private static void DressBuildings(Layout layout, Transform layoutRoot, Transform root, CemeteryAssets assets, Random random,
            Action<string, int> count)
        {
            var shelter = layoutRoot.Find("Shelter");
            var houses = layoutRoot.Find("Houses");

            Restyle(shelter, assets.Stone, assets.Slate);
            Restyle(houses, assets.CryptStone, assets.Slate);

            AddGableRoof(shelter, assets.Stone, assets.Slate, withCross: true);
            if (houses != null)
                foreach (Transform house in houses) AddGableRoof(house, assets.CryptStone, assets.Slate, withCross: false);

            foreach (var building in new[] { shelter }.Concat(houses != null ? houses.Cast<Transform>() : Enumerable.Empty<Transform>()))
            {
                if (building == null) continue;

                foreach (Transform wall in building)
                {
                    if (!wall.name.StartsWith("Wall")) continue;

                    foreach (Transform block in wall)
                    {
                        // Only long stretches of wall, and never every one: ivy that covers everything reads as a hedge.
                        if (block.name != "Segment" || block.localScale.x < 2.6f || random.NextDouble() < 0.45) continue;

                        var ivy = Pick(assets.Ivy, random);
                        var position = block.position + wall.forward * (WallThickness * 0.5f + 0.12f);
                        position.y = 0f;

                        // The pack's ivy grows out along its -Z with its flat back at +Z, so +Z faces the wall.
                        var placed = Place(ivy, root, position, Quaternion.LookRotation(-wall.forward).eulerAngles.y, Lerp(random, 0.6f, 0.8f), false);
                        placed.name = $"Ivy ({building.name})";
                        count("ivy", 1);
                    }
                }
            }

            if (houses == null) return;

            foreach (Transform house in houses)
            {
                var frontWall = house.Find("Wall Front");
                var door = frontWall != null ? frontWall.position + house.forward * 0.6f : house.position + house.forward * 3.6f;
                door.y = 0f;

                foreach (var side in new[] { -1f, 1f })
                {
                    Place(Pick(assets.Pots, random), root, door + house.right * (1.35f * side), house.eulerAngles.y, 1f, false);
                    count("grave pots", 1);
                }
            }
        }

        /// <summary>
        /// A steep slate roof over a building's flat one, with stepped stone gables front and back, and a cross on the chapel.
        /// </summary>
        /// <remarks>
        /// Visual only. The flat roof keeps its collider and just stops drawing, so nothing about what can stand where, or
        /// what the NavMesh sees, changes. The gables step up above the wall top rather than being one rotated block, which
        /// would reach down through the doorways below.
        /// </remarks>
        private static void AddGableRoof(Transform building, Material stone, Material slate, bool withCross)
        {
            var flat = building != null ? building.Find("Roof") : null;
            if (flat == null) return;

            if (flat.TryGetComponent<MeshRenderer>(out var flatRenderer)) flatRenderer.enabled = false;

            // The flat roof overhangs each wall by 0.3 m.
            var width = flat.localScale.x - 0.6f;
            var depth = flat.localScale.z - 0.6f;
            var rise = width * 0.5f;

            var roof = new GameObject("Gable Roof").transform;
            roof.SetParent(building, false);

            const float slabThickness = 0.18f;
            const float overhang = 0.45f;
            var slabLength = rise * Mathf.Sqrt(2f) + overhang;

            foreach (var side in new[] { -1f, 1f })
            {
                var slab = VisualCube("Roof Slab", roof, slate);
                slab.localRotation = Quaternion.Euler(0f, 0f, -45f * side);
                slab.localScale = new Vector3(slabLength, slabThickness, depth + 0.6f);

                // Centred on its slope, pushed out by half its thickness so the gable ends meet its underside.
                var alongSlope = new Vector3(side * rise * 0.5f, WallHeight + rise * 0.5f, 0f);
                var outward = new Vector3(side, 1f, 0f).normalized * (slabThickness * 0.5f);
                slab.localPosition = alongSlope + outward + new Vector3(side * overhang * 0.35f, -overhang * 0.35f, 0f);
            }

            const int steps = 8;
            foreach (var end in new[] { -1f, 1f })
            {
                for (var i = 0; i < steps; i++)
                {
                    var stepHeight = rise / steps;
                    var stepWidth = width * (1f - (i + 0.5f) / steps);

                    var block = VisualCube("Gable", roof, stone);
                    block.localPosition = new Vector3(0f, WallHeight + stepHeight * (i + 0.5f), end * depth * 0.5f);
                    block.localScale = new Vector3(stepWidth, stepHeight, WallThickness);
                }
            }

            if (!withCross) return;

            // Over the front gable, where the chapel is first seen from the path.
            var apex = new Vector3(0f, WallHeight + rise, depth * 0.5f);
            var post = VisualCube("Cross", roof, stone);
            post.localPosition = apex + new Vector3(0f, 0.8f, 0f);
            post.localScale = new Vector3(0.22f, 1.6f, 0.22f);

            var arm = VisualCube("Cross", roof, stone);
            arm.localPosition = apex + new Vector3(0f, 1.15f, 0f);
            arm.localScale = new Vector3(0.9f, 0.22f, 0.22f);
        }

        private static Transform VisualCube(string name, Transform parent, Material material)
        {
            var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            cube.name = name;
            Object.DestroyImmediate(cube.GetComponent<BoxCollider>());
            cube.transform.SetParent(parent, false);
            cube.GetComponent<MeshRenderer>().sharedMaterial = material;
            return cube.transform;
        }

        private static void Restyle(Transform building, Material walls, Material roof)
        {
            if (building == null) return;

            foreach (var renderer in building.GetComponentsInChildren<MeshRenderer>(true))
            {
                // Barricade planks keep their wood.
                if (renderer.GetComponentInParent<Barricade>() != null) continue;

                renderer.sharedMaterial = renderer.name == "Roof" ? roof : walls;
            }
        }

        private static void DressPaths(CemeteryStyle style, Transform root, CemeteryAssets assets, Action<string, int> count)
        {
            const float slabLength = 4.2f;

            foreach (var path in style.Paths)
            {
                for (var i = 0; i + 1 < path.Length; i++)
                {
                    var from = path[i];
                    var to = path[i + 1];
                    var direction = to - from;
                    var length = direction.magnitude;
                    if (length < 0.1f) continue;

                    var yaw = Quaternion.LookRotation(direction).eulerAngles.y;
                    var slabs = Mathf.Max(1, Mathf.RoundToInt(length / slabLength));

                    for (var s = 0; s < slabs; s++)
                    {
                        var centre = Vector3.Lerp(from, to, (s + 0.5f) / slabs);

                        // Walk-through: paving is 16 cm of relief, and a collider would trip players and the NavMesh alike.
                        Place(assets.Pavement, root, centre + Vector3.down * 0.07f, yaw, 1f, false);
                        count("paving slabs", 1);
                    }
                }
            }
        }

        // ---- Graves ----------------------------------------------------------------------------------

        /// <summary>
        /// Rows of graves in a plot's own frame: slab and headstone, headstone and mound, or a tall monument, with the odd
        /// empty lot. Cells that would crowd anything in <paramref name="keepClear"/> are left empty.
        /// </summary>
        private static void DressPlot(Plot plot, Transform root, CemeteryAssets assets, Random random, KeepClear keepClear,
            KeepClear occupied, Action<string, int> count)
        {
            const float column = 2.4f;
            const float row = 4f;

            var rotation = Quaternion.Euler(0f, plot.Yaw, 0f);
            var columns = Mathf.Max(1, Mathf.FloorToInt(plot.Size.x / column));
            var rows = Mathf.Max(1, Mathf.FloorToInt(plot.Size.y / row));

            for (var r = 0; r < rows; r++)
            {
                for (var c = 0; c < columns; c++)
                {
                    var local = new Vector3((c - (columns - 1) * 0.5f) * column, 0f, (r - (rows - 1) * 0.5f) * row);
                    var centre = plot.Centre + rotation * local;
                    if (!keepClear.IsFree(centre, 1.6f)) continue;

                    var roll = random.NextDouble();
                    if (roll < 0.1) continue;

                    var yaw = plot.Yaw + Lerp(random, -3f, 3f);
                    var head = centre + rotation * new Vector3(0f, 0f, 1.45f);

                    if (roll < 0.55)
                    {
                        Place(Pick(assets.GraveSlabs, random), root, centre, yaw, 1f, true);
                        PlaceStone(Pick(assets.Headstones, random), root, head, yaw, random, 4f);
                        count("graves", 1);
                    }
                    else if (roll < 0.85)
                    {
                        Place(assets.Mound, root, centre, yaw, 1f, true);
                        PlaceStone(Pick(assets.OldStones, random), root, head, yaw, random, 9f);
                        count("graves", 1);
                    }
                    else
                    {
                        Place(Pick(assets.TallStones, random), root, centre + rotation * new Vector3(0f, 0f, 0.6f), yaw, 1f, true);
                        count("monuments", 1);
                    }

                    if (random.NextDouble() < 0.3)
                    {
                        Place(Pick(assets.Pots, random), root, centre + rotation * new Vector3(0.95f, 0f, 0.9f), yaw, 1f, false);
                        count("grave pots", 1);
                    }

                    occupied.Circle(centre, 1.4f);
                }
            }

            if (plot.Fenced) FencePlot(plot, root, assets, keepClear, count);
        }

        /// <summary>A low iron fence round a plot, one side left open in the middle so the rows can be walked into.</summary>
        private static void FencePlot(Plot plot, Transform root, CemeteryAssets assets, KeepClear keepClear, Action<string, int> count)
        {
            const float section = 1.45f;
            const float gap = 2.4f;

            var rotation = Quaternion.Euler(0f, plot.Yaw, 0f);
            var half = plot.Size * 0.5f + new Vector2(0.9f, 0.9f);

            foreach (Side side in Enum.GetValues(typeof(Side)))
            {
                var alongX = side is Side.Front or Side.Back;
                var length = alongX ? half.x * 2f : half.y * 2f;
                var sideCentre = side switch
                {
                    Side.Front => new Vector3(0f, 0f, half.y),
                    Side.Back => new Vector3(0f, 0f, -half.y),
                    Side.Left => new Vector3(-half.x, 0f, 0f),
                    _ => new Vector3(half.x, 0f, 0f)
                };

                var sections = Mathf.Max(1, Mathf.RoundToInt(length / section));
                var step = length / sections;

                for (var s = 0; s < sections; s++)
                {
                    var along = -length * 0.5f + step * (s + 0.5f);
                    if (side == plot.Opening && Mathf.Abs(along) < gap * 0.5f) continue;

                    var local = sideCentre + (alongX ? new Vector3(along, 0f, 0f) : new Vector3(0f, 0f, along));
                    var position = plot.Centre + rotation * local;

                    // A fence across a path or a doorway would close it; those stretches are left out.
                    if (!keepClear.IsFree(position, 0.4f)) continue;

                    Place(assets.FenceSection, root, position, plot.Yaw + (alongX ? 0f : 90f), step / section, true);
                    count("fence sections", 1);
                }
            }
        }

        private static void PlaceStone(GameObject stone, Transform root, Vector3 position, float yaw, Random random, float maxTilt)
        {
            var placed = Place(stone, root, position, yaw, 1f, true);

            // Old ground settles: stones lean a little, each its own way.
            placed.transform.rotation *= Quaternion.Euler(Lerp(random, -maxTilt, maxTilt), 0f, Lerp(random, -maxTilt * 0.6f, maxTilt * 0.6f));
        }

        private static void DressStatues(CemeteryStyle style, Transform root, CemeteryAssets assets, KeepClear keepClear, KeepClear occupied,
            Action<string, int> count)
        {
            foreach (var statue in style.Statues)
            {
                if (!keepClear.IsFree(statue.Position, 0.5f)) continue;

                Place(statue.Kneeling ? assets.AngelKneeling : assets.AngelStatue, root, statue.Position, statue.Yaw, 1f, true);
                occupied.Circle(statue.Position, 1.2f);
                count("angels", 1);
            }
        }

        /// <summary>Clusters of weathered headstones among the trees, the part of the cemetery nobody tends any more.</summary>
        private static void DressOldGraves(CemeteryStyle style, Transform root, CemeteryAssets assets, Random random, KeepClear keepClear,
            KeepClear occupied, Action<string, int> count)
        {
            var clusters = 0;
            for (var attempt = 0; attempt < style.OldGraveClusters * 30 && clusters < style.OldGraveClusters; attempt++)
            {
                var centre = new Vector3(Lerp(random, -44f, 44f), 0f, Lerp(random, -44f, 44f));
                if (new Vector2(centre.x, centre.z).magnitude < style.OldGraveMinRadius) continue;
                if (style.SeaBeyond && centre.z < -30f) continue;
                if (!keepClear.IsFree(centre, 3.5f) || !occupied.IsFree(centre, 3.5f)) continue;

                var yaw = Lerp(random, 0f, 360f);
                var stones = random.Next(3, 7);
                for (var s = 0; s < stones; s++)
                {
                    var offset = Quaternion.Euler(0f, yaw, 0f) * new Vector3((s % 3 - 1) * 1.6f + Lerp(random, -0.3f, 0.3f), 0f,
                        (s / 3) * 2.4f + Lerp(random, -0.3f, 0.3f));
                    PlaceStone(Pick(assets.OldStones, random), root, centre + offset, yaw + Lerp(random, -12f, 12f), random, 12f);
                }

                occupied.Circle(centre, 3.5f);
                clusters++;
                count("old headstones", stones);
            }
        }

        // ---- Edges, trees, candles, grass, rain -------------------------------------------------------

        /// <summary>An iron fence along the level's edge with a gate, in front of the invisible walls that really stop players.</summary>
        private static void DressPerimeter(CemeteryStyle style, Transform root, Transform layoutRoot, CemeteryAssets assets,
            Action<string, int> count)
        {
            var boundary = layoutRoot.Find("Boundary");
            if (boundary != null)
            {
                foreach (var renderer in boundary.GetComponentsInChildren<MeshRenderer>(true))
                {
                    // The sea wall stays: it is the shore, not the level's edge.
                    if (renderer.name != "Sea Wall" && renderer.name != "Sea") renderer.enabled = false;
                    if (renderer.name == "Sea Wall") renderer.sharedMaterial = AssetDatabase.LoadAssetAtPath<Material>($"{CemeteryMaterialFolder}/CryptStone.mat");
                }

                var sea = boundary.Find("Sea");
                if (sea != null && style.SeaBeyond)
                {
                    // Out past the fence line, so the shore does not end in a hard edge.
                    sea.localPosition = new Vector3(0f, 0.03f, -57f);
                    sea.localScale = new Vector3(10f * CemeteryGroundScale, 0.02f, 46f);
                }
            }

            const float section = 3.19f;
            var sections = Mathf.RoundToInt(FenceInset * 2f / section);
            var step = FenceInset * 2f / sections;

            foreach (var (origin, along, yaw) in new[]
                     {
                         (new Vector3(0f, 0f, FenceInset), Vector3.right, 0f),
                         (new Vector3(0f, 0f, -FenceInset), Vector3.right, 0f),
                         (new Vector3(FenceInset, 0f, 0f), Vector3.forward, 90f),
                         (new Vector3(-FenceInset, 0f, 0f), Vector3.forward, 90f)
                     })
            {
                for (var s = 0; s < sections; s++)
                {
                    var position = origin + along * (-FenceInset + step * (s + 0.5f));

                    // The shore side of the coast has its sea wall, and the gate needs its gap.
                    if (style.SeaBeyond && position.z < -34f) continue;
                    if (Vector3.Distance(position, style.Gate) < 2.2f) continue;

                    // No collider: the boundary walls behind already stop everything, and doubling them only costs.
                    Place(assets.PerimeterSection, root, position, yaw, step / section, false);
                    count("perimeter fence sections", 1);
                }
            }

            // A double gate: two leaves hinged at the gap's edges, the second mirrored.
            var gateRotation = Quaternion.Euler(0f, style.GateYaw, 0f);
            var left = Place(assets.Gate, root, style.Gate + gateRotation * new Vector3(-0.8f, 0f, 0f), style.GateYaw, 1f, false);
            var right = Place(assets.Gate, root, style.Gate + gateRotation * new Vector3(0.8f, 0f, 0f), style.GateYaw, 1f, false);
            right.transform.localScale = new Vector3(-1f, 1f, 1f);
            left.name = right.name = "Gate";

            foreach (var side in new[] { -1f, 1f })
                Place(assets.TallStones[1], root, style.Gate + gateRotation * new Vector3(1.55f * side, 0f, 0f), style.GateYaw, 0.75f, true);

            count("gates", 1);
        }

        private static void DressTrees(Layout layout, CemeteryStyle style, Transform root, CemeteryAssets assets, Random random,
            KeepClear keepClear, KeepClear occupied, Action<string, int> count)
        {
            var inside = 0;
            for (var attempt = 0; attempt < layout.TreeCount * 30 && inside < layout.TreeCount; attempt++)
            {
                var position = new Vector3(Lerp(random, -46f, 46f), 0f, Lerp(random, -46f, 46f));
                if (new Vector2(position.x, position.z).magnitude < layout.TreeInnerRadius) continue;
                if (style.SeaBeyond && position.z < -30f) continue;
                if (!keepClear.IsFree(position, 1.2f) || !occupied.IsFree(position, 1.2f)) continue;

                Place(Pick(assets.Trees, random), root, position, Lerp(random, 0f, 360f), Lerp(random, style.TreeScale.x, style.TreeScale.y), true);
                occupied.Circle(position, 1.2f);
                inside++;
            }

            // A dead treeline past the fence, so the fog has shapes in it and the level has no visible edge.
            var outside = 0;
            for (var attempt = 0; attempt < style.OuterTrees * 30 && outside < style.OuterTrees; attempt++)
            {
                var position = new Vector3(Lerp(random, -72f, 72f), 0f, Lerp(random, -72f, 72f));
                if (Mathf.Max(Mathf.Abs(position.x), Mathf.Abs(position.z)) < 53f) continue;
                if (style.SeaBeyond && position.z < -34f) continue;

                Place(Pick(assets.Trees, random), root, position, Lerp(random, 0f, 360f), Lerp(random, 0.5f, 0.9f), false);
                outside++;
            }

            count("trees", inside + outside);
        }

        /// <summary>The authored candles, and a few more left on graves. Each carries a flickering light, so they stay few.</summary>
        private static void DressCandles(CemeteryStyle style, Transform root, CemeteryAssets assets, Random random, KeepClear occupied,
            Action<string, int> count)
        {
            foreach (var position in style.Candles)
            {
                Place(assets.CandleTrio, root, position, Lerp(random, 0f, 360f), 1f, false);
                count("candles", 1);
            }

            var graves = root.Cast<Transform>().Where(child => child.name.StartsWith("Grave") && child.name.Length <= 7).ToList();
            for (var i = 0; i < style.CandlesOnGraves && graves.Count > 0; i++)
            {
                var grave = graves[random.Next(graves.Count)];
                graves.Remove(grave);

                Place(assets.Candle, root, grave.position + grave.rotation * new Vector3(-0.45f, 0.5f, -0.8f), Lerp(random, 0f, 360f), 1f, false);
                count("candles", 1);
            }
        }

        private static void DressGrass(CemeteryStyle style, Transform root, CemeteryAssets assets, Random random, KeepClear keepClear,
            Action<string, int> count)
        {
            var placed = 0;
            for (var attempt = 0; attempt < style.GrassTufts * 4 && placed < style.GrassTufts; attempt++)
            {
                var position = new Vector3(Lerp(random, -48f, 48f), 0f, Lerp(random, -48f, 48f));
                if (style.SeaBeyond && position.z < -33f) continue;

                // Grass may grow against graves and fences, but not over paths, doorways or out of a crypt floor.
                if (!keepClear.IsFree(position, 0.2f)) continue;

                Place(Pick(assets.Grass, random), root, position, Lerp(random, 0f, 360f), Lerp(random, 0.7f, 1.3f), false);
                placed++;
            }

            count("grass tufts", placed);
        }

        private static void AddRain(CemeteryAssets assets)
        {
            var weather = new GameObject(WeatherName);
            var rain = (GameObject)PrefabUtility.InstantiatePrefab(assets.Rain, weather.transform);
            rain.name = "Rain";
            weather.AddComponent<FollowPlayerCamera>();
        }

        // ---- Keep clear ------------------------------------------------------------------------------

        private static KeepClear BuildKeepClear(Layout layout, CemeteryStyle style, Transform layoutRoot, Transform[] creatureSpawns,
            Transform[] playerSpawns)
        {
            var keepClear = new KeepClear();

            // The shelter, with room round it for players to move between openings while creatures come at them.
            keepClear.Box(layout.ShelterCentre, layout.ShelterYaw, layout.ShelterSize + new Vector2(8f, 8f));

            foreach (var house in layout.Houses)
            {
                keepClear.Box(house.Centre, house.Yaw, house.Size + new Vector2(3f, 3f));

                // The ground before the door, where a crypt is entered for its scrap.
                var front = Quaternion.Euler(0f, house.Yaw, 0f) * Vector3.forward;
                keepClear.Circle(house.Centre + front * (house.Size.y * 0.5f + 2.5f), 2.5f);
            }

            // Where creatures stand to break a barricade.
            foreach (var barricade in layoutRoot.GetComponentsInChildren<Barricade>(true))
                keepClear.Circle(barricade.AttackPoint, 2.5f);

            foreach (var spawn in creatureSpawns) keepClear.Circle(spawn.position, 5f);
            foreach (var spawn in playerSpawns) keepClear.Circle(spawn.position, 2f);
            foreach (var scrap in layout.LooseScrap) keepClear.Circle(scrap, 1.8f);

            foreach (var path in style.Paths)
                for (var i = 0; i + 1 < path.Length; i++) keepClear.Corridor(path[i], path[i + 1], 3.2f);

            keepClear.Circle(style.Gate, 3f);
            return keepClear;
        }

        // ---- Verification ----------------------------------------------------------------------------

        /// <summary>
        /// Proves the dressing closed nothing off: every creature spawn reaches every barricade and every player spawn, and
        /// every player spawn reaches every crypt door.
        /// </summary>
        /// <remarks>
        /// Barricades are baked open, so this is the night with every barricade down, which is the case the dressing must
        /// never make easier for the players or impossible for the creatures.
        /// </remarks>
        private static void CheckCemeteryPaths(Transform layoutRoot, Transform[] creatureSpawns, Transform[] playerSpawns, StringBuilder report)
        {
            var surface = Object.FindAnyObjectByType<Unity.AI.Navigation.NavMeshSurface>();
            if (surface != null && surface.navMeshData != null) surface.AddData();

            var targets = layoutRoot.GetComponentsInChildren<Barricade>(true)
                .Select(barricade => (Name: barricade.name, Position: barricade.AttackPoint))
                .Concat(playerSpawns.Select(spawn => (Name: spawn.name, Position: spawn.position)))
                .ToList();

            var houses = layoutRoot.Find("Houses");
            var doors = houses != null
                ? houses.Cast<Transform>().Select(house => (Name: house.name, Position: house.position + house.forward * 4.5f)).ToList()
                : new List<(string Name, Vector3 Position)>();

            var failures = new List<string>();
            var path = new NavMeshPath();

            bool Reaches(Vector3 from, Vector3 to)
            {
                if (!NavMesh.SamplePosition(from, out var start, 3f, NavMesh.AllAreas)) return false;
                if (!NavMesh.SamplePosition(to, out var end, 3f, NavMesh.AllAreas)) return false;
                return NavMesh.CalculatePath(start.position, end.position, NavMesh.AllAreas, path) && path.status == NavMeshPathStatus.PathComplete;
            }

            foreach (var spawn in creatureSpawns)
                foreach (var target in targets)
                    if (!Reaches(spawn.position, target.Position)) failures.Add($"{spawn.name} -> {target.Name}");

            if (playerSpawns.Length > 0)
                foreach (var door in doors)
                    if (!Reaches(playerSpawns[0].position, door.Position)) failures.Add($"players -> {door.Name}");

            report.AppendLine(failures.Count == 0
                ? $"Paths checked: every creature spawn reaches all {targets.Count} barricades and player spawns; every crypt door is reachable."
                : $"WARNING: the cemetery blocks {failures.Count} route(s): {string.Join(", ", failures)}. Move the pieces in the way or rebuild.");
        }

        // ---- Placement -------------------------------------------------------------------------------

        /// <summary>
        /// Places a pack model or prefab standing on the ground, and gives a solid piece a box collider from its mesh if it
        /// has none.
        /// </summary>
        private static GameObject Place(GameObject source, Transform parent, Vector3 position, float yaw, float scale, bool solid)
        {
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(source, parent);
            instance.transform.localPosition = Vector3.zero;
            instance.transform.localRotation = Quaternion.identity;
            instance.transform.localScale = Vector3.one;

            if (solid && instance.GetComponentInChildren<Collider>(true) == null) AddMeshBox(instance, parent);
            FillMissingMaterials(instance);

            instance.transform.position = position;
            instance.transform.rotation = Quaternion.Euler(0f, yaw, 0f);
            instance.transform.localScale = Vector3.one * scale;
            return instance;
        }

        /// <summary>
        /// Gives empty material slots the piece's own material, on this instance only.
        /// </summary>
        /// <remarks>
        /// The pack's AngelStatue-LODs prefab references two materials the pack does not include, so its closest detail
        /// level draws magenta. The other levels share one stone material, which stands in without touching the pack.
        /// </remarks>
        private static void FillMissingMaterials(GameObject instance)
        {
            var renderers = instance.GetComponentsInChildren<Renderer>(true);
            var fallback = renderers.SelectMany(renderer => renderer.sharedMaterials).FirstOrDefault(material => material != null);
            if (fallback == null) return;

            foreach (var renderer in renderers)
            {
                var materials = renderer.sharedMaterials;
                if (materials.All(material => material != null)) continue;

                for (var i = 0; i < materials.Length; i++)
                {
                    if (materials[i] == null) materials[i] = fallback;
                }
                renderer.sharedMaterials = materials;
            }
        }

        /// <summary>A box round the model's mesh, at least thick enough for the NavMesh bake to see.</summary>
        private static void AddMeshBox(GameObject instance, Transform parent)
        {
            var renderers = instance.GetComponentsInChildren<MeshRenderer>(true);
            if (renderers.Length == 0) return;

            var bounds = renderers[0].bounds;
            foreach (var renderer in renderers.Skip(1)) bounds.Encapsulate(renderer.bounds);

            // The instance sits at its parent's origin, unrotated and unscaled, while this is measured.
            var box = instance.AddComponent<BoxCollider>();
            box.center = parent.InverseTransformPoint(bounds.center);
            box.size = new Vector3(Mathf.Max(bounds.size.x, 0.25f), bounds.size.y, Mathf.Max(bounds.size.z, 0.25f));
        }

        private static T Pick<T>(IReadOnlyList<T> items, Random random) => items[random.Next(items.Count)];
    }
}
