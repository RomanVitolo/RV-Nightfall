using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using QuietVillage.Gameplay.Survival;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;
using Object = UnityEngine.Object;

namespace QuietVillage.Gameplay.EditorTools
{
    /// <summary>
    /// Turns the creature packs into bodies for the night creature: a shared state machine, one override per body, and
    /// the catalog the director picks from.
    /// </summary>
    /// <remarks>
    /// Every body plays <c>CreatureBase.controller</c> (locomotion, attacks, entrance, death) with its own clips swapped in
    /// through an <see cref="AnimatorOverrideController"/>. That is what lets Generic creatures from Super Mega Creatures,
    /// each on its own skeleton, and the Humanoid Horror Maiden share one behaviour.
    ///
    /// Seeding runs only on an empty catalog. It reads each pack prefab's own controller for clips and matches them by
    /// name (Idle, Walk, Run, Attack, Death/Dead, Shout); a prefab without an idle, a way to move and an attack is left
    /// out. A few humanoid-sized creatures start enabled; the rest are listed but disabled, to try with F9 in game or
    /// Only One. After that the catalog is design data: edit clips, speeds, scale or enabled flags in its Inspector, then
    /// re-run to rebuild the overrides. Re-running never re-seeds or changes those values.
    ///
    /// Vendor assets are never modified. Gait clips that do not loop in their import, and death clips that do, are copied
    /// into <c>Art/Animations/Creatures/Loops</c> with looping corrected; see <see cref="WithLooping"/>.
    /// </remarks>
    public static partial class CreatureSetup
    {
        private const string CatalogPath = ProjectPaths.CreatureCatalog;
        private const string AnimationFolder = ProjectPaths.CreatureAnimations;
        private const string BaseControllerPath = AnimationFolder + "/CreatureBase.controller";
        private const string PlaceholderFolder = AnimationFolder + "/Placeholders";
        private const string OverrideFolder = AnimationFolder + "/Overrides";
        private const string LoopFolder = AnimationFolder + "/Loops";
        private const string CreaturePrefabPath = ProjectPaths.CreaturePrefabs + "/NightCreature.prefab";

        private const string CreaturePack = "Assets/Super_Mega_Creatures_Pack";
        private const string MaidenPack = "Assets/HorrorMaiden";

        private const string SharedHumanoidController =
            CreaturePack + "/Animations_For_Humanoid/Set_01/Animations/Controller_Set_01.controller";

        // Beyond this a body towers through doorways and barricade openings; below the other, it is a toy.
        private const float TallestHeight = 3f;
        private const float TallTarget = 2.4f;
        private const float ShortestHeight = 1f;
        private const float ShortTarget = 1.6f;

        private const float DefaultWalkClipSpeed = 1.3f;
        private const float DefaultRunClipSpeed = 3.6f;

        private enum Slot { Idle, Walk, Run, Attack1, Attack2, Attack3, Death, Entrance }

        // Enabled on first seeding: roughly human-sized, and read as something that hunts people in a village.
        private static readonly string[] EnabledByDefault =
        {
            "maiden_", "creature_humanoid_", "monster_0", "creature_mutant", "creepy", "hunter"
        };

        [MenuItem("Tools/Quiet Village/Creatures/Set Up Creatures")]
        private static void RunFromMenu()
        {
            if (EditorApplication.isPlaying || EditorApplication.isPlayingOrWillChangePlaymode)
            {
                EditorUtility.DisplayDialog("Set Up Creatures", "Exit Play Mode first.", "OK");
                return;
            }

            Execute(applyZombieFemale: false);
        }

        [MenuItem("Tools/Quiet Village/Creatures/Select Creature Catalog")]
        private static void SelectCatalog()
        {
            var catalog = AssetDatabase.LoadAssetAtPath<CreatureCatalog>(CatalogPath);
            if (catalog == null)
            {
                EditorUtility.DisplayDialog("Creature Catalog",
                    "No catalog yet. Run Tools > Quiet Village > Creatures > Set Up Creatures.", "OK");
                return;
            }

            Selection.activeObject = catalog;
            EditorGUIUtility.PingObject(catalog);
        }

        /// <summary>Entry point for <c>-executeMethod</c>, so this can run headlessly.</summary>
        public static void RunFromCommandLine()
        {
            var succeeded = Execute(applyZombieFemale: false);

            if (Application.isBatchMode) EditorApplication.Exit(succeeded ? 0 : 1);
        }

        /// <param name="applyZombieFemale">
        /// Also dress the humanoid zombies in the Zombie Female animation set, replacing their clips; see
        /// <see cref="ApplyZombieFemaleSet"/>. Always done for a freshly seeded catalog.
        /// </param>
        private static bool Execute(bool applyZombieFemale)
        {
            var report = new StringBuilder();

            EnsureFolder(PlaceholderFolder);
            EnsureFolder(OverrideFolder);
            EnsureFolder(LoopFolder);

            var placeholders = EnsurePlaceholders();
            var baseController = EnsureBaseController(placeholders, report);
            if (baseController == null)
            {
                Debug.LogError($"Set Up Creatures aborted.\n{report}");
                return false;
            }

            var catalog = AssetDatabase.LoadAssetAtPath<CreatureCatalog>(CatalogPath);
            if (catalog == null)
            {
                catalog = ScriptableObject.CreateInstance<CreatureCatalog>();
                AssetDatabase.CreateAsset(catalog, CatalogPath);
                report.AppendLine($"Created {CatalogPath}.");
            }

            var bodies = catalog.EditableBodies;
            if (bodies.Count == 0)
            {
                Seed(bodies, report);
                applyZombieFemale = true;
            }

            if (applyZombieFemale) ApplyZombieFemaleSet(bodies, report);

            var usable = 0;
            var enabled = 0;
            foreach (var body in bodies)
            {
                if (body == null) continue;

                if (!CompleteBody(body, baseController, placeholders, report)) continue;

                usable++;
                if (body.Enabled) enabled++;
            }

            EditorUtility.SetDirty(catalog);
            AssetDatabase.SaveAssets();

            var sounds = EnsureSounds(report);

            if (!AssignToCreaturePrefab(catalog, sounds, report))
            {
                Debug.LogError($"Set Up Creatures aborted.\n{report}");
                return false;
            }

            Debug.Log($"Set Up Creatures complete: {usable} usable bodies, {enabled} enabled.\n{report}");
            return usable > 0;
        }

        // ---- Shared controller -----------------------------------------------------------------------

        private static Dictionary<Slot, AnimationClip> EnsurePlaceholders()
        {
            var clips = new Dictionary<Slot, AnimationClip>();

            foreach (Slot slot in Enum.GetValues(typeof(Slot)))
            {
                var path = $"{PlaceholderFolder}/Placeholder_{slot}.anim";
                var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(path);

                if (clip == null)
                {
                    clip = new AnimationClip { name = $"Placeholder_{slot}" };
                    AssetDatabase.CreateAsset(clip, path);
                }

                clips[slot] = clip;
            }

            return clips;
        }

        /// <summary>
        /// The one state machine every body plays. Created once, then left alone like the Worker's: transition timings
        /// are animation tuning. Delete it to regenerate.
        /// </summary>
        private static AnimatorController EnsureBaseController(Dictionary<Slot, AnimationClip> clips, StringBuilder report)
        {
            var existing = AssetDatabase.LoadAssetAtPath<AnimatorController>(BaseControllerPath);
            if (existing != null)
            {
                var names = new HashSet<string>(existing.parameters.Select(parameter => parameter.name));
                var required = new[]
                {
                    CreatureBody.MoveBlendParameter, CreatureBody.MoveTimeScaleParameter, CreatureBody.AttackParameter,
                    CreatureBody.AttackVariantParameter, CreatureBody.EntranceParameter, CreatureBody.DeadParameter
                };

                var missing = required.Where(name => !names.Contains(name)).ToList();
                if (missing.Count == 0) return existing;

                report.AppendLine($"FAILED: {BaseControllerPath} lacks {string.Join(", ", missing)}. Delete it to regenerate.");
                return null;
            }

            var controller = AnimatorController.CreateAnimatorControllerAtPath(BaseControllerPath);

            controller.AddParameter(CreatureBody.MoveBlendParameter, AnimatorControllerParameterType.Float);
            controller.AddParameter(new AnimatorControllerParameter
            {
                name = CreatureBody.MoveTimeScaleParameter,
                type = AnimatorControllerParameterType.Float,
                defaultFloat = 1f
            });
            controller.AddParameter(CreatureBody.AttackParameter, AnimatorControllerParameterType.Trigger);
            controller.AddParameter(CreatureBody.AttackVariantParameter, AnimatorControllerParameterType.Float);
            controller.AddParameter(CreatureBody.EntranceParameter, AnimatorControllerParameterType.Trigger);
            controller.AddParameter(CreatureBody.DeadParameter, AnimatorControllerParameterType.Bool);

            var machine = controller.layers[0].stateMachine;

            var locomotion = controller.CreateBlendTreeInController("Locomotion", out var moveTree, 0);
            moveTree.blendType = BlendTreeType.Simple1D;
            moveTree.blendParameter = CreatureBody.MoveBlendParameter;
            moveTree.useAutomaticThresholds = false;
            moveTree.AddChild(clips[Slot.Idle], CreatureBody.IdleBlend);
            moveTree.AddChild(clips[Slot.Walk], CreatureBody.WalkBlend);
            moveTree.AddChild(clips[Slot.Run], CreatureBody.RunBlend);
            locomotion.speedParameter = CreatureBody.MoveTimeScaleParameter;
            locomotion.speedParameterActive = true;
            machine.defaultState = locomotion;

            var attack = controller.CreateBlendTreeInController("Attack", out var attackTree, 0);
            attackTree.blendType = BlendTreeType.Simple1D;
            attackTree.blendParameter = CreatureBody.AttackVariantParameter;
            attackTree.useAutomaticThresholds = false;
            attackTree.AddChild(clips[Slot.Attack1], 0f);
            attackTree.AddChild(clips[Slot.Attack2], 1f);
            attackTree.AddChild(clips[Slot.Attack3], 2f);

            var entrance = machine.AddState("Entrance");
            entrance.motion = clips[Slot.Entrance];

            var dead = machine.AddState("Dead");
            dead.motion = clips[Slot.Death];

            // Death first, from anywhere, so it is evaluated before a swing that was triggered the same frame.
            var die = machine.AddAnyStateTransition(dead);
            die.hasExitTime = false;
            die.duration = 0.15f;
            die.canTransitionToSelf = false;
            die.AddCondition(AnimatorConditionMode.If, 0f, CreatureBody.DeadParameter);

            // A fresh swing restarts: the host decides the rhythm, and one arriving mid-swing is a new blow.
            var swing = machine.AddAnyStateTransition(attack);
            swing.hasExitTime = false;
            swing.duration = 0.08f;
            swing.canTransitionToSelf = true;
            swing.AddCondition(AnimatorConditionMode.If, 0f, CreatureBody.AttackParameter);
            swing.AddCondition(AnimatorConditionMode.IfNot, 0f, CreatureBody.DeadParameter);

            var swingDone = attack.AddTransition(locomotion);
            swingDone.hasExitTime = true;
            swingDone.exitTime = 0.9f;
            swingDone.duration = 0.2f;

            var arrive = machine.AddAnyStateTransition(entrance);
            arrive.hasExitTime = false;
            arrive.duration = 0.05f;
            arrive.canTransitionToSelf = false;
            arrive.AddCondition(AnimatorConditionMode.If, 0f, CreatureBody.EntranceParameter);
            arrive.AddCondition(AnimatorConditionMode.IfNot, 0f, CreatureBody.DeadParameter);

            var arrived = entrance.AddTransition(locomotion);
            arrived.hasExitTime = true;
            arrived.exitTime = 0.95f;
            arrived.duration = 0.25f;

            EditorUtility.SetDirty(controller);
            AssetDatabase.SaveAssets();
            report.AppendLine($"Created {BaseControllerPath}.");
            return controller;
        }

        // ---- Seeding ---------------------------------------------------------------------------------

        private static void Seed(List<CreatureCatalog.Body> bodies, StringBuilder report)
        {
            var ids = new HashSet<string>();

            SeedMaiden(bodies, ids, "maiden_zombie_1", "Horror Maiden (Zombie 1)", "UnderwearBunHair", "1", "01",
                "MoveFastTwitchyWalkForward", "", report);
            SeedMaiden(bodies, ids, "maiden_zombie_2", "Horror Maiden (Zombie 2)", "WetDressLongHair", "2", "02",
                "MoveWalkJitteryBigStepsForward", "AltIdle01_", report);

            var seeded = 0;
            var skipped = new List<string>();

            foreach (var guid in AssetDatabase.FindAssets("t:Prefab", new[] { CreaturePack }).OrderBy(AssetDatabase.GUIDToAssetPath))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);

                // Swimmers have no ground gait, and would hunt on land looking like fish.
                if (path.IndexOf("Underwater", StringComparison.OrdinalIgnoreCase) >= 0) continue;

                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (prefab == null || prefab.GetComponentInChildren<SkinnedMeshRenderer>(true) == null) continue;

                var clips = ClipsOf(prefab);
                var borrowed = false;

                // Some humanoid creatures ship pointing at controllers missing from the pack. They are rigged for the
                // pack's shared humanoid set, which the Creatures_10 bodies play, so they borrow it.
                if (clips.Count == 0 && path.IndexOf("Humanoid", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    var shared = AssetDatabase.LoadAssetAtPath<RuntimeAnimatorController>(SharedHumanoidController);
                    if (shared != null)
                    {
                        clips = shared.animationClips.Where(clip => clip != null).Distinct().ToList();
                        borrowed = clips.Count > 0;
                    }
                }

                if (clips.Count == 0)
                {
                    skipped.Add(prefab.name);
                    continue;
                }

                var body = new CreatureCatalog.Body
                {
                    Id = UniqueId(Slugify(prefab.name), ids),
                    DisplayName = prefab.name.Replace('_', ' '),
                    Prefab = prefab,
                    Idle = Best(clips, IsIdle),
                    Walk = Best(clips, IsWalk),
                    Run = Best(clips, IsRun),
                    Attacks = clips.Where(clip => Matches(KeyOf(clip), "attack")).OrderBy(KeyOf).Take(CreatureBody.AttackVariants).ToList(),
                    Death = Best(clips, IsDeath),
                    Entrance = Best(clips, IsEntrance)
                };

                if (body.Idle == null || (body.Walk == null && body.Run == null) || body.Attacks.Count == 0)
                {
                    skipped.Add(prefab.name);
                    continue;
                }

                // A creature that only runs still needs something at walking pace.
                body.Walk ??= body.Run;
                body.MoveSpeed = body.Run != null && body.Run != body.Walk ? 3.4f : 2.2f;
                body.Enabled = EnabledByDefault.Any(prefix => body.Id.StartsWith(prefix, StringComparison.Ordinal));

                DeriveTimings(body);
                FitScale(body, report);
                bodies.Add(body);
                seeded++;

                if (borrowed)
                    report.AppendLine($"  {body.Id}: its own controller is missing from the pack; given the shared " +
                                      "humanoid set. If it stands in a T-pose in game, its rig does not match.");
            }

            report.AppendLine($"Seeded {seeded} creature bodies from {CreaturePack}. Enabled: " +
                              string.Join(", ", bodies.Where(body => body.Enabled).Select(body => body.Id)) + ".");
            if (skipped.Count > 0)
                report.AppendLine($"  Left out (no controller, or no idle, movement and attack clips): {string.Join(", ", skipped)}.");
        }

        private static void SeedMaiden(List<CreatureCatalog.Body> bodies, HashSet<string> ids, string id, string displayName,
            string prefabName, string zombieNumber, string neckNumber, string run, string attackPrefix, StringBuilder report)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>($"{MaidenPack}/Prefabs/{prefabName}.prefab");
            if (prefab == null)
            {
                report.AppendLine($"WARNING: {MaidenPack}/Prefabs/{prefabName}.prefab not found; {displayName} left out.");
                return;
            }

            var body = new CreatureCatalog.Body
            {
                Id = UniqueId(id, ids),
                DisplayName = displayName,
                Prefab = prefab,
                Idle = MaidenClip($"Idle_BrokenNeck{neckNumber}"),
                Walk = MaidenClip($"MoveBrokenNeck{neckNumber}WalkForward"),
                Run = MaidenClip(run),
                Attacks = new List<AnimationClip>
                {
                    MaidenClip($"{attackPrefix}AttackInPlace01"),
                    MaidenClip($"{attackPrefix}AttackInPlace02"),
                    MaidenClip($"{attackPrefix}AttackInPlace03")
                }.Where(clip => clip != null).ToList(),
                Death = MaidenClip($"Idle_Death{neckNumber}"),
                Entrance = MaidenClip($"Idle_RiseOutOfGround{neckNumber}"),
                MoveSpeed = 3f,
                MaterialSwaps = new List<CreatureCatalog.MaterialSwap>
                {
                    new() { NameContains = "Body", Replacement = MaidenMaterial("Body", $"ZombieBody {zombieNumber}") },
                    new() { NameContains = "Head", Replacement = MaidenMaterial("Head", $"ZombieHead {zombieNumber}") }
                }
            };

            body.MaterialSwaps.RemoveAll(swap => swap.Replacement == null);
            body.Enabled = true;

            DeriveTimings(body);
            FitScale(body, report);
            bodies.Add(body);
        }

        private static AnimationClip MaidenClip(string name) =>
            FirstClipAt($"{MaidenPack}/Art/Animations/HorrorMaiden_{name}.FBX");

        private static Material MaidenMaterial(string folder, string name) =>
            AssetDatabase.LoadAssetAtPath<Material>($"{MaidenPack}/Art/Materials/{folder}/{name}.mat");

        // ---- Clip matching ---------------------------------------------------------------------------

        /// <summary>Every clip the prefab's own controller plays, which is how each pack pairs a model with its set.</summary>
        private static List<AnimationClip> ClipsOf(GameObject prefab)
        {
            var animator = prefab.GetComponentInChildren<Animator>(true);
            var controller = animator != null ? animator.runtimeAnimatorController : null;
            if (controller == null) return new List<AnimationClip>();

            return controller.animationClips.Where(clip => clip != null).Distinct().ToList();
        }

        /// <summary>A clip's matching name: the file part after '@' for pack animations, otherwise the clip's own name.</summary>
        private static string KeyOf(AnimationClip clip)
        {
            var file = Path.GetFileNameWithoutExtension(AssetDatabase.GetAssetPath(clip)) ?? string.Empty;
            var at = file.LastIndexOf('@');
            var key = at >= 0 ? file.Substring(at + 1) : clip.name;
            return key.ToLowerInvariant().Replace(" ", string.Empty);
        }

        // Directional, sitting, root-motion and alternate versions of a clip, none of which is the plain gait.
        private static readonly string[] Variations =
        {
            "back", "left", "right", "root", "mec", "sit", "ceiling", "celing", "other", "oher", "preparation", "fire",
            "combo", "turn", "rotation"
        };

        private static bool Matches(string key, string word) =>
            key.Contains(word) && !Variations.Any(key.Contains);

        private static bool IsIdle(AnimationClip clip) => Matches(KeyOf(clip), "idle");
        private static bool IsWalk(AnimationClip clip) => Matches(KeyOf(clip), "walk");
        private static bool IsRun(AnimationClip clip) => Matches(KeyOf(clip), "run");
        private static bool IsDeath(AnimationClip clip) => Matches(KeyOf(clip), "death") || Matches(KeyOf(clip), "dead");

        private static bool IsEntrance(AnimationClip clip)
        {
            var key = KeyOf(clip);
            return Matches(key, "shout") || Matches(key, "emergence") || Matches(key, "anger");
        }

        /// <summary>The shortest-named match, which is the plain version: "idle" over "idle_2".</summary>
        private static AnimationClip Best(List<AnimationClip> clips, Func<AnimationClip, bool> match) =>
            clips.Where(match).OrderBy(clip => KeyOf(clip).Length).ThenBy(KeyOf).FirstOrDefault();

        private static AnimationClip FirstClipAt(string path) =>
            AssetDatabase.LoadAllAssetRepresentationsAtPath(path)
                .OfType<AnimationClip>()
                .FirstOrDefault(clip => !clip.name.StartsWith("__preview__", StringComparison.Ordinal));

        // ---- Completing bodies -----------------------------------------------------------------------

        private static bool CompleteBody(CreatureCatalog.Body body, AnimatorController baseController,
            Dictionary<Slot, AnimationClip> placeholders, StringBuilder report)
        {
            var label = string.IsNullOrEmpty(body.Id) ? body.DisplayName : body.Id;

            if (body.Prefab == null || body.Idle == null || body.Walk == null || body.Attacks == null
                || body.Attacks.All(clip => clip == null))
            {
                body.Controller = null;
                report.AppendLine($"SKIPPED '{label}': needs a prefab, idle, walk and at least one attack clip.");
                return false;
            }

            // Structural rather than tuning: without an entrance clip the trigger would play the idle as one.
            body.HasEntrance = body.Entrance != null;

            var attacks = body.Attacks.Where(clip => clip != null).ToList();
            body.Controller = BuildOverride(body, attacks, baseController, placeholders, label, report);
            return body.Controller != null;
        }

        /// <summary>
        /// First guesses at speeds and timings, read from the clips. Seeding only: afterwards these are tuning.
        /// </summary>
        private static void DeriveTimings(CreatureCatalog.Body body)
        {
            body.WalkClipSpeed = GroundSpeed(body.Walk, DefaultWalkClipSpeed);
            body.RunClipSpeed = body.Run != null && body.Run != body.Walk
                ? Mathf.Max(body.WalkClipSpeed + 0.5f, GroundSpeed(body.Run, DefaultRunClipSpeed))
                : body.WalkClipSpeed * 1.6f;

            var firstAttack = body.Attacks.FirstOrDefault(clip => clip != null);
            if (firstAttack != null)
            {
                // Most swings connect a little before halfway through.
                body.AttackHitDelay = Mathf.Clamp(firstAttack.length * 0.45f, 0.15f, 1.2f);
                body.AttackDuration = Mathf.Clamp(firstAttack.length, 0.5f, 1.8f);
            }

            body.DeathDuration = body.Death != null ? Mathf.Min(body.Death.length + 0.5f, 6f) : 0f;
        }

        private static AnimatorOverrideController BuildOverride(CreatureCatalog.Body body, List<AnimationClip> attacks,
            AnimatorController baseController, Dictionary<Slot, AnimationClip> placeholders, string label, StringBuilder report)
        {
            var path = $"{OverrideFolder}/{Slugify(label)}.overrideController";
            var controller = AssetDatabase.LoadAssetAtPath<AnimatorOverrideController>(path);
            var created = controller == null;

            if (created) controller = new AnimatorOverrideController(baseController) { name = Slugify(label) };
            else controller.runtimeAnimatorController = baseController;

            controller[placeholders[Slot.Idle]] = WithLooping(body.Idle, true, report);
            controller[placeholders[Slot.Walk]] = WithLooping(body.Walk, true, report);
            controller[placeholders[Slot.Run]] = WithLooping(body.Run != null ? body.Run : body.Walk, true, report);
            controller[placeholders[Slot.Attack1]] = attacks[0];
            controller[placeholders[Slot.Attack2]] = attacks[1 % attacks.Count];
            controller[placeholders[Slot.Attack3]] = attacks[2 % attacks.Count];
            controller[placeholders[Slot.Death]] = body.Death != null ? WithLooping(body.Death, false, report) : body.Idle;
            controller[placeholders[Slot.Entrance]] = body.Entrance != null ? body.Entrance : body.Idle;

            if (created) AssetDatabase.CreateAsset(controller, path);
            else EditorUtility.SetDirty(controller);

            return controller;
        }

        /// <summary>
        /// A clip that loops: the clip itself if its import already loops, otherwise a looping copy in this project.
        /// </summary>
        /// <remarks>
        /// The creature pack imports every clip with Unity's defaults, which do not loop, so a walk would play once and
        /// freeze. Turning looping on in the pack's own import settings is not safe: it turns the default take into an
        /// explicit clip with a new internal id, which breaks every reference the pack's controllers and demo scenes hold.
        /// A copy leaves the pack exactly as it shipped. Copies are made once and reused; delete the Loops folder to
        /// remake them.
        /// </remarks>
        /// <param name="loop">
        /// True for gaits. False for a death: the Zombie Female set marks every clip as looping, and a death that loops
        /// dies again and again until the creature is removed.
        /// </param>
        private static AnimationClip WithLooping(AnimationClip clip, bool loop, StringBuilder report)
        {
            if (clip == null || clip.isLooping == loop) return clip;

            var source = AssetDatabase.GetAssetPath(clip);
            var name = Slugify($"{Path.GetFileNameWithoutExtension(source)}_{clip.name}") + (loop ? string.Empty : "_once");
            var path = $"{LoopFolder}/{name}.anim";

            var copy = AssetDatabase.LoadAssetAtPath<AnimationClip>(path);
            if (copy != null) return copy;

            copy = Object.Instantiate(clip);
            copy.name = name;

            var settings = AnimationUtility.GetAnimationClipSettings(copy);
            settings.loopTime = loop;
            AnimationUtility.SetAnimationClipSettings(copy, settings);

            AssetDatabase.CreateAsset(copy, path);
            report.AppendLine($"  {(loop ? "Looping" : "Non-looping")} copy: {path}.");
            return copy;
        }

        /// <summary>How fast a clip travels over the ground, or a default for clips authored in place.</summary>
        private static float GroundSpeed(AnimationClip clip, float fallback)
        {
            var velocity = clip.averageSpeed;
            var speed = new Vector3(velocity.x, 0f, velocity.z).magnitude;
            return speed > 0.2f ? speed : fallback;
        }

        /// <summary>Scales a body that would tower through doorways, or shrink to a toy, towards human size.</summary>
        private static void FitScale(CreatureCatalog.Body body, StringBuilder report)
        {
            var scene = EditorSceneManager.NewPreviewScene();
            try
            {
                var instance = (GameObject)PrefabUtility.InstantiatePrefab(body.Prefab, scene);
                var renderers = instance.GetComponentsInChildren<Renderer>(true);
                if (renderers.Length == 0) return;

                var bounds = renderers[0].bounds;
                for (var i = 1; i < renderers.Length; i++) bounds.Encapsulate(renderers[i].bounds);

                var height = bounds.size.y;
                if (height > TallestHeight) body.Scale = TallTarget / height;
                else if (height > 0.01f && height < ShortestHeight) body.Scale = ShortTarget / height;

                if (!Mathf.Approximately(body.Scale, 1f))
                    report.AppendLine($"  {body.Id}: {height:0.00} m tall, scaled x{body.Scale:0.00}.");
            }
            finally
            {
                EditorSceneManager.ClosePreviewScene(scene);
            }
        }

        // ---- Creature prefab -------------------------------------------------------------------------

        private static bool AssignToCreaturePrefab(CreatureCatalog catalog, CreatureSounds sounds, StringBuilder report)
        {
            if (AssetDatabase.LoadAssetAtPath<GameObject>(CreaturePrefabPath) == null)
            {
                report.AppendLine($"FAILED: no {CreaturePrefabPath}. Build a greybox settlement first " +
                                  "(Tools > Quiet Village > Build Greybox), which creates it.");
                return false;
            }

            var root = PrefabUtility.LoadPrefabContents(CreaturePrefabPath);
            try
            {
                var creature = root.GetComponent<NightCreature>();
                if (creature == null)
                {
                    report.AppendLine($"FAILED: {CreaturePrefabPath} has no NightCreature.");
                    return false;
                }

                var serialized = new SerializedObject(creature);
                serialized.FindProperty("m_catalog").objectReferenceValue = catalog;

                // Only when unset, so a hand-picked sound set on the prefab is kept.
                var soundsProperty = serialized.FindProperty("m_sounds");
                if (soundsProperty.objectReferenceValue == null) soundsProperty.objectReferenceValue = sounds;

                serialized.ApplyModifiedPropertiesWithoutUndo();

                PrefabUtility.SaveAsPrefabAsset(root, CreaturePrefabPath);
                report.AppendLine("NightCreature prefab now dresses itself from the creature catalog.");
                return true;
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
        }

        // ---- Helpers ---------------------------------------------------------------------------------

        private static string Slugify(string name)
        {
            var builder = new StringBuilder();
            foreach (var character in name.ToLowerInvariant())
                builder.Append(char.IsLetterOrDigit(character) ? character : '_');

            return builder.ToString().Trim('_');
        }

        private static string UniqueId(string id, HashSet<string> ids)
        {
            var unique = id;
            for (var n = 2; !ids.Add(unique); n++) unique = $"{id}_{n}";
            return unique;
        }

        private static void EnsureFolder(string path)
        {
            if (string.IsNullOrEmpty(path) || AssetDatabase.IsValidFolder(path)) return;

            var parent = Path.GetDirectoryName(path)?.Replace('\\', '/');
            EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, Path.GetFileName(path));
        }
    }
}
