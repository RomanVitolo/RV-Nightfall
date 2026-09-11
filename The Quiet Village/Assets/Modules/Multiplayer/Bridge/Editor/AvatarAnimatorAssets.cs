using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace Modules.Multiplayer.Bridge.EditorTools
{
    /// <summary>
    /// Authors the humanoid controller that animates a player's third-person body.
    /// </summary>
    /// <remarks>
    /// Clips are copies of the Sci-Fi pack's Defender ground set, made here with
    /// <c>AssetDatabase.CopyAsset</c> and re-imported as Humanoid. The pack's originals stay Generic
    /// because its own demo characters depend on that; the copies share the Worker's UE-Mannequin
    /// skeleton, so Humanoid auto-mapping needs no manual bones.
    ///
    /// Deliberately left out of the pack: the Fly set (UHFPS players cannot fly), <c>Weapon@Rotation</c>
    /// (animates a prop, not a body), and the <c>Rotate_90_*</c> aim poses (authored around the Defender's
    /// rifle — <see cref="AvatarAim"/> covers aim with IK instead). Of the weapon clips only the
    /// shortest of each kind are used: <c>Shot_01</c> (0.5 s) matches one UHFPS trigger pull, where the
    /// 1.2 s and 3.8 s shots would still be firing long after a single round; <c>Attack_04/05</c> run 4 s,
    /// far longer than an axe or knife swing. The Worker holds no third-person weapon, so these mime one.
    ///
    /// Every parameter name comes from <see cref="PlayerLocomotionSync"/> and <see cref="PlayerActionSync"/>,
    /// the components that drive them. Those names are the whole contract, and a mismatch fails silently.
    ///
    /// Import settings are enforced on every run. The controller is created once and then left alone,
    /// like the spawn points: thresholds and transition timings are animation tuning, which a setup tool
    /// should not silently revert. Delete the asset to regenerate it.
    /// </remarks>
    public static class AvatarAnimatorAssets
    {
        private const string SourceFolder = "Assets/Sci_Fi_Super_Pack/Animations/Animations_Ground";
        private const string Folder = "Assets/Modules/Multiplayer/Art/Animations/Worker";

        /// <summary>Where the generated controller lives.</summary>
        public const string ControllerPath = Folder + "/WorkerAnimator.controller";

        private const string UpperBodyMaskPath = Folder + "/WorkerUpperBody.mask";

        // Matches PlayerActionSync's default idle threshold: a fidget may only start from standing still.
        private const float StillSpeed = 0.1f;

        // Below this, a clip's root barely travels — it was authored in place, and there is no stride
        // speed to match playback against.
        private const float InPlaceSpeedThreshold = 0.1f;

        /// <summary>How a clip's root motion is split between the pose and the (discarded) root.</summary>
        /// <remarks>
        /// Root motion is always off on the avatar — the CharacterController moves the player and
        /// NetworkTransform replicates it — so anything not baked into the pose is simply thrown away.
        /// </remarks>
        private enum RootMode
        {
            /// <summary>Loops: height kept (feet-based), travel discarded so the body stays on its collider.</summary>
            InPlace,

            /// <summary>Jump: height discarded too, since the CharacterController already lifts the player.</summary>
            Airborne,

            /// <summary>One-shots such as falling dead: everything baked, so they play exactly as authored.</summary>
            AsAuthored
        }

        private readonly struct ClipSpec
        {
            public readonly string Source;
            public readonly string ClipName;
            public readonly bool Loop;
            public readonly RootMode Root;

            public ClipSpec(string source, string clipName, bool loop, RootMode root)
            {
                Source = source;
                ClipName = clipName;
                Loop = loop;
                Root = root;
            }

            public string FileName => $"Defender@{Source}.FBX";
        }

        private static readonly ClipSpec Idle = new("Idle_01", "Worker_Idle", true, RootMode.InPlace);

        private static readonly ClipSpec[] IdleFidgets =
        {
            new("Idle_02", "Worker_Idle_Fidget_01", false, RootMode.InPlace),
            new("Idle_03", "Worker_Idle_Fidget_02", false, RootMode.InPlace)
        };

        private static readonly ClipSpec Walk = new("Walk", "Worker_Walk", true, RootMode.InPlace);
        private static readonly ClipSpec WalkBack = new("Walk_Back", "Worker_Walk_Back", true, RootMode.InPlace);
        private static readonly ClipSpec WalkLeft = new("Walk_Left", "Worker_Walk_Left", true, RootMode.InPlace);
        private static readonly ClipSpec WalkRight = new("Walk_Right", "Worker_Walk_Right", true, RootMode.InPlace);

        private static readonly ClipSpec Run = new("Run", "Worker_Run", true, RootMode.InPlace);
        private static readonly ClipSpec RunFast = new("Run_Fast", "Worker_Sprint", true, RootMode.InPlace);
        private static readonly ClipSpec RunBack = new("Run_Back", "Worker_Run_Back", true, RootMode.InPlace);
        private static readonly ClipSpec RunLeft = new("Run_Left", "Worker_Run_Left", true, RootMode.InPlace);
        private static readonly ClipSpec RunRight = new("Run_Right", "Worker_Run_Right", true, RootMode.InPlace);

        private static readonly ClipSpec Jump = new("Jump", "Worker_Jump", false, RootMode.Airborne);

        private static readonly ClipSpec[] Hits =
        {
            new("Get_Hit", "Worker_Hit_01", false, RootMode.AsAuthored),
            new("Get_Hit_02", "Worker_Hit_02", false, RootMode.AsAuthored)
        };

        private static readonly ClipSpec[] Deaths =
        {
            new("Dead_01", "Worker_Death_01", false, RootMode.AsAuthored),
            new("Dead_02", "Worker_Death_02", false, RootMode.AsAuthored)
        };

        private static readonly ClipSpec Shoot = new("Shot_01", "Worker_Shoot", false, RootMode.AsAuthored);
        private static readonly ClipSpec Reload = new("Reload", "Worker_Reload", false, RootMode.AsAuthored);

        private static readonly ClipSpec[] Attacks =
        {
            new("Attack_01", "Worker_Attack_01", false, RootMode.AsAuthored),
            new("Attack_02", "Worker_Attack_02", false, RootMode.AsAuthored),
            new("Attack_03", "Worker_Attack_03", false, RootMode.AsAuthored)
        };

        /// <summary>Returns the avatar controller, creating it and its clips on first use.</summary>
        /// <param name="walkSpeed">UHFPS walk speed in m/s; where the walk ring sits in the blend space.</param>
        /// <param name="runSpeed">UHFPS run speed in m/s; where the run ring sits in the blend space.</param>
        /// <param name="report">Receives progress and failures.</param>
        /// <returns>The controller, or <c>null</c> if a clip or the controller could not be prepared.</returns>
        public static RuntimeAnimatorController BuildOrLoad(float walkSpeed, float runSpeed, StringBuilder report)
        {
            if (Hits.Length != PlayerActionSync.HitVariants
                || Deaths.Length != PlayerActionSync.DeathVariants
                || IdleFidgets.Length != PlayerActionSync.IdleVariants
                || Attacks.Length != PlayerActionSync.AttackVariants)
            {
                report.AppendLine(
                    "FAILED: variant clip counts disagree with PlayerActionSync's *Variants constants; " +
                    "the sync would select clips that do not exist.");
                return null;
            }

            var clips = new Dictionary<string, AnimationClip>();
            var allSpecs = new List<ClipSpec>
            {
                Idle, Walk, WalkBack, WalkLeft, WalkRight, Run, RunFast, RunBack, RunLeft, RunRight, Jump,
                Shoot, Reload
            };
            allSpecs.AddRange(IdleFidgets);
            allSpecs.AddRange(Hits);
            allSpecs.AddRange(Deaths);
            allSpecs.AddRange(Attacks);

            foreach (var spec in allSpecs)
            {
                var clip = ImportClip(spec, report);
                if (clip == null) return null;

                clips[spec.ClipName] = clip;
            }

            report.AppendLine($"{clips.Count} Humanoid clips ready in {Folder}.");

            var existing = AssetDatabase.LoadAssetAtPath<AnimatorController>(ControllerPath);
            if (existing != null)
            {
                if (!HasRequiredParameters(existing, report)) return null;

                report.AppendLine($"Reusing existing {ControllerPath}; left as authored.");
                return existing;
            }

            if (walkSpeed <= 0f || runSpeed <= walkSpeed)
            {
                report.AppendLine(
                    $"FAILED: walk {walkSpeed} m/s and run {runSpeed} m/s cannot order a blend space.");
                return null;
            }

            var mask = GetOrCreateUpperBodyMask(report);
            var controller = CreateController(clips, mask, walkSpeed, runSpeed, report);
            report.AppendLine($"Created {ControllerPath} (walk ring {walkSpeed}, run ring {runSpeed} m/s).");
            return controller;
        }

        private static AnimationClip ImportClip(ClipSpec spec, StringBuilder report)
        {
            var path = $"{Folder}/{spec.FileName}";

            if (AssetImporter.GetAtPath(path) == null)
            {
                var sourcePath = $"{SourceFolder}/{spec.FileName}";
                if (!AssetDatabase.CopyAsset(sourcePath, path))
                {
                    report.AppendLine($"FAILED: could not copy {sourcePath} to {path}.");
                    return null;
                }

                report.AppendLine($"Copied {spec.FileName} from the Sci-Fi pack.");
            }

            if (AssetImporter.GetAtPath(path) is not ModelImporter importer)
            {
                report.AppendLine($"FAILED: {path} is not a model.");
                return null;
            }

            // Reimport only on change, so re-running the tool does not churn every FBX each time.
            if (importer.animationType != ModelImporterAnimationType.Human
                || importer.avatarSetup != ModelImporterAvatarSetup.CreateFromThisModel)
            {
                importer.animationType = ModelImporterAnimationType.Human;
                importer.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel;
                importer.SaveAndReimport();
            }

            // clipAnimations stays empty until clips are customised; the defaults describe the takes.
            var takes = importer.clipAnimations;
            if (takes == null || takes.Length == 0) takes = importer.defaultClipAnimations;

            if (takes == null || takes.Length == 0)
            {
                report.AppendLine($"FAILED: {path} contains no animation take.");
                return null;
            }

            // Each Defender file holds a single take.
            var take = takes[0];
            if (ApplySpec(take, spec))
            {
                importer.clipAnimations = new[] { take };
                importer.SaveAndReimport();
            }

            Avatar avatar = null;
            AnimationClip result = null;

            foreach (var asset in AssetDatabase.LoadAllAssetRepresentationsAtPath(path))
            {
                if (asset is Avatar foundAvatar) avatar = foundAvatar;
                else if (asset is AnimationClip foundClip && foundClip.name == spec.ClipName) result = foundClip;
            }

            if (avatar == null || !avatar.isValid || !avatar.isHuman)
            {
                report.AppendLine(
                    $"FAILED: Humanoid mapping failed for {path}. Open its Rig tab and Configure the Avatar.");
                return null;
            }

            if (result == null)
            {
                report.AppendLine($"FAILED: clip '{spec.ClipName}' not found in {path} after import.");
                return null;
            }

            return result;
        }

        /// <summary>Brings one clip's import settings in line with its spec.</summary>
        /// <returns><c>true</c> if anything changed, so the caller knows to reimport.</returns>
        private static bool ApplySpec(ModelImporterClipAnimation take, ClipSpec spec)
        {
            // Rotation is always baked, so a loop cannot drift the facing PlayerLocomotionSync sets.
            const bool bakeRotation = true;
            var bakeHeight = spec.Root != RootMode.Airborne;
            var heightFromFeet = spec.Root == RootMode.InPlace;
            var bakeTravel = spec.Root == RootMode.AsAuthored;

            var upToDate = take.name == spec.ClipName
                           && take.loopTime == spec.Loop
                           && take.lockRootRotation == bakeRotation
                           && take.keepOriginalOrientation
                           && take.lockRootHeightY == bakeHeight
                           && take.heightFromFeet == heightFromFeet
                           && take.keepOriginalPositionY == !heightFromFeet
                           && take.lockRootPositionXZ == bakeTravel
                           && take.keepOriginalPositionXZ == bakeTravel;

            if (upToDate) return false;

            take.name = spec.ClipName;
            take.loopTime = spec.Loop;
            take.lockRootRotation = bakeRotation;
            take.keepOriginalOrientation = true;
            take.lockRootHeightY = bakeHeight;

            // The two "based upon" flags for height are mutually exclusive; both false means centre of mass.
            take.heightFromFeet = heightFromFeet;
            take.keepOriginalPositionY = !heightFromFeet;
            take.lockRootPositionXZ = bakeTravel;
            take.keepOriginalPositionXZ = bakeTravel;
            return true;
        }

        /// <summary>Spine, head and arms: what a hit reaction may take over while the legs keep walking.</summary>
        private static AvatarMask GetOrCreateUpperBodyMask(StringBuilder report)
        {
            var mask = AssetDatabase.LoadAssetAtPath<AvatarMask>(UpperBodyMaskPath);
            if (mask != null) return mask;

            mask = new AvatarMask();
            for (var part = AvatarMaskBodyPart.Root; part < AvatarMaskBodyPart.LastBodyPart; part++)
            {
                mask.SetHumanoidBodyPartActive(part, IsUpperBody(part));
            }

            AssetDatabase.CreateAsset(mask, UpperBodyMaskPath);
            report.AppendLine($"Created {UpperBodyMaskPath}.");
            return mask;
        }

        private static bool IsUpperBody(AvatarMaskBodyPart part)
        {
            switch (part)
            {
                case AvatarMaskBodyPart.Body:
                case AvatarMaskBodyPart.Head:
                case AvatarMaskBodyPart.LeftArm:
                case AvatarMaskBodyPart.RightArm:
                case AvatarMaskBodyPart.LeftFingers:
                case AvatarMaskBodyPart.RightFingers:
                case AvatarMaskBodyPart.LeftHandIK:
                case AvatarMaskBodyPart.RightHandIK:
                    return true;
                default:
                    return false;
            }
        }

        private static AnimatorController CreateController(
            IReadOnlyDictionary<string, AnimationClip> clips, AvatarMask upperBody,
            float walkSpeed, float runSpeed, StringBuilder report)
        {
            var controller = AnimatorController.CreateAnimatorControllerAtPath(ControllerPath);
            AddParameters(controller);

            // Base layer: the whole body. IK pass on, because AvatarAim's look-at runs there.
            var baseMachine = controller.layers[0].stateMachine;

            var locomotion = controller.CreateBlendTreeInController("Locomotion", out var moveTree, 0);
            BuildLocomotionTree(moveTree, clips, walkSpeed, runSpeed, report);

            var fidget = controller.CreateBlendTreeInController("Fidget", out var fidgetTree, 0);
            BuildVariantTree(fidgetTree, PlayerActionSync.IdleVariantParameter, IdleFidgets, clips);

            var dead = controller.CreateBlendTreeInController(PlayerActionSync.DeadStateName, out var deathTree, 0);
            BuildVariantTree(deathTree, PlayerActionSync.DeathVariantParameter, Deaths, clips);

            var airborne = baseMachine.AddState("Airborne");
            airborne.motion = clips[Jump.ClipName];

            baseMachine.defaultState = locomotion;

            // Death pre-empts everything, from wherever the body is. Added first so it is evaluated first.
            var die = baseMachine.AddAnyStateTransition(dead);
            ConfigureTransition(die, 0.1f);
            die.canTransitionToSelf = false;
            die.AddCondition(AnimatorConditionMode.If, 0f, PlayerActionSync.DeadParameter);

            var revive = dead.AddTransition(locomotion);
            ConfigureTransition(revive, 0.3f);
            revive.AddCondition(AnimatorConditionMode.IfNot, 0f, PlayerActionSync.DeadParameter);

            // From any living state, so a fidget or a landing that bounces is left cleanly.
            var takeOff = baseMachine.AddAnyStateTransition(airborne);
            ConfigureTransition(takeOff, 0.1f);
            takeOff.canTransitionToSelf = false;
            takeOff.AddCondition(AnimatorConditionMode.IfNot, 0f, PlayerLocomotionSync.GroundedParameter);
            takeOff.AddCondition(AnimatorConditionMode.IfNot, 0f, PlayerActionSync.DeadParameter);

            var land = airborne.AddTransition(locomotion);
            ConfigureTransition(land, 0.15f);
            land.AddCondition(AnimatorConditionMode.If, 0f, PlayerLocomotionSync.GroundedParameter);

            var startFidget = locomotion.AddTransition(fidget);
            ConfigureTransition(startFidget, 0.25f);
            startFidget.AddCondition(AnimatorConditionMode.If, 0f, PlayerActionSync.FidgetParameter);
            startFidget.AddCondition(AnimatorConditionMode.Less, StillSpeed, PlayerLocomotionSync.SpeedParameter);

            var fidgetDone = fidget.AddTransition(locomotion);
            fidgetDone.hasExitTime = true;
            fidgetDone.exitTime = 0.95f;
            fidgetDone.duration = 0.25f;

            // Moving cuts a fidget short; waiting for it to finish would slide an idling body across the floor.
            var fidgetInterrupted = fidget.AddTransition(locomotion);
            ConfigureTransition(fidgetInterrupted, 0.2f);
            fidgetInterrupted.AddCondition(AnimatorConditionMode.Greater, StillSpeed, PlayerLocomotionSync.SpeedParameter);

            // Actions layer: upper body only, so a hit reaction does not stop the legs mid-stride.
            controller.AddLayer("Actions");
            var layers = controller.layers;
            layers[0].iKPass = true;
            layers[1].avatarMask = upperBody;
            layers[1].blendingMode = AnimatorLayerBlendingMode.Override;
            layers[1].defaultWeight = 1f;
            controller.layers = layers;

            var actions = controller.layers[1].stateMachine;

            // No motion, so the layer contributes nothing and the base layer shows through.
            var empty = actions.AddState("Empty");
            actions.defaultState = empty;

            var hit = controller.CreateBlendTreeInController("Hit", out var hitTree, 1);
            BuildVariantTree(hitTree, PlayerActionSync.HitVariantParameter, Hits, clips);

            // A second hit restarts the reaction rather than being swallowed by the first.
            AddActionTransitions(actions, hit, empty, PlayerActionSync.HitParameter, restartable: true);

            // Item actions share the upper body with hit reactions, so a player keeps walking while firing.
            var shoot = actions.AddState("Shoot");
            shoot.motion = clips[Shoot.ClipName];
            AddActionTransitions(actions, shoot, empty, PlayerActionSync.ShootParameter, restartable: true);

            // Not restartable: UHFPS ignores fire and reload input mid-reload, so a second trigger here
            // could only be a duplicate, and restarting would visibly hitch the arms.
            var reload = actions.AddState("Reload");
            reload.motion = clips[Reload.ClipName];
            AddActionTransitions(actions, reload, empty, PlayerActionSync.ReloadParameter, restartable: false);

            var attack = controller.CreateBlendTreeInController("Attack", out var attackTree, 1);
            BuildVariantTree(attackTree, PlayerActionSync.AttackVariantParameter, Attacks, clips);
            AddActionTransitions(actions, attack, empty, PlayerActionSync.AttackParameter, restartable: true);

            // A reaction still playing must not hold the upper body upright over a falling corpse.
            var clearOnDeath = actions.AddAnyStateTransition(empty);
            ConfigureTransition(clearOnDeath, 0.2f);
            clearOnDeath.canTransitionToSelf = false;
            clearOnDeath.AddCondition(AnimatorConditionMode.If, 0f, PlayerActionSync.DeadParameter);

            EditorUtility.SetDirty(controller);
            AssetDatabase.SaveAssets();
            return controller;
        }

        private static void AddParameters(AnimatorController controller)
        {
            controller.AddParameter(PlayerLocomotionSync.MoveXParameter, AnimatorControllerParameterType.Float);
            controller.AddParameter(PlayerLocomotionSync.MoveZParameter, AnimatorControllerParameterType.Float);
            controller.AddParameter(PlayerLocomotionSync.SpeedParameter, AnimatorControllerParameterType.Float);

            // Grounded by default, so a freshly spawned body does not flash through the air state before
            // its first replicated value arrives.
            controller.AddParameter(new AnimatorControllerParameter
            {
                name = PlayerLocomotionSync.GroundedParameter,
                type = AnimatorControllerParameterType.Bool,
                defaultBool = true
            });

            controller.AddParameter(PlayerActionSync.DeadParameter, AnimatorControllerParameterType.Bool);
            controller.AddParameter(PlayerActionSync.DeathVariantParameter, AnimatorControllerParameterType.Float);
            controller.AddParameter(PlayerActionSync.HitParameter, AnimatorControllerParameterType.Trigger);
            controller.AddParameter(PlayerActionSync.HitVariantParameter, AnimatorControllerParameterType.Float);
            controller.AddParameter(PlayerActionSync.FidgetParameter, AnimatorControllerParameterType.Trigger);
            controller.AddParameter(PlayerActionSync.IdleVariantParameter, AnimatorControllerParameterType.Float);
            controller.AddParameter(PlayerActionSync.ShootParameter, AnimatorControllerParameterType.Trigger);
            controller.AddParameter(PlayerActionSync.ReloadParameter, AnimatorControllerParameterType.Trigger);
            controller.AddParameter(PlayerActionSync.AttackParameter, AnimatorControllerParameterType.Trigger);
            controller.AddParameter(PlayerActionSync.AttackVariantParameter, AnimatorControllerParameterType.Float);
        }

        /// <summary>
        /// Eight-way movement in the body's own frame: idle at the centre, a walk ring and a run ring.
        /// </summary>
        /// <remarks>
        /// Rings sit at UHFPS's real walk and run speeds, because the replicated velocity is in m/s. Forward
        /// has an extra point: the pack's jog between the rings and its sprint on the run ring, since the
        /// jog alone looks sluggish at full run speed. Crouching lands between idle and the walk ring,
        /// which is the closest the pack gets — it has no crouch clips.
        /// </remarks>
        private static void BuildLocomotionTree(
            BlendTree tree, IReadOnlyDictionary<string, AnimationClip> clips,
            float walkSpeed, float runSpeed, StringBuilder report)
        {
            tree.blendType = BlendTreeType.FreeformDirectional2D;
            tree.blendParameter = PlayerLocomotionSync.MoveXParameter;
            tree.blendParameterY = PlayerLocomotionSync.MoveZParameter;

            var jogSpeed = (walkSpeed + runSpeed) * 0.5f;

            tree.AddChild(clips[Idle.ClipName], Vector2.zero);

            tree.AddChild(clips[Walk.ClipName], new Vector2(0f, walkSpeed));
            tree.AddChild(clips[WalkBack.ClipName], new Vector2(0f, -walkSpeed));
            tree.AddChild(clips[WalkLeft.ClipName], new Vector2(-walkSpeed, 0f));
            tree.AddChild(clips[WalkRight.ClipName], new Vector2(walkSpeed, 0f));

            tree.AddChild(clips[Run.ClipName], new Vector2(0f, jogSpeed));
            tree.AddChild(clips[RunFast.ClipName], new Vector2(0f, runSpeed));
            tree.AddChild(clips[RunBack.ClipName], new Vector2(0f, -runSpeed));
            tree.AddChild(clips[RunLeft.ClipName], new Vector2(-runSpeed, 0f));
            tree.AddChild(clips[RunRight.ClipName], new Vector2(runSpeed, 0f));

            MatchPlaybackToSpeed(tree, report);
        }

        /// <summary>
        /// A 1D tree used as a selector: the sync writes whole numbers, so exactly one clip plays.
        /// </summary>
        private static void BuildVariantTree(
            BlendTree tree, string parameter, ClipSpec[] variants, IReadOnlyDictionary<string, AnimationClip> clips)
        {
            tree.blendType = BlendTreeType.Simple1D;
            tree.blendParameter = parameter;
            tree.useAutomaticThresholds = false;

            for (var i = 0; i < variants.Length; i++) tree.AddChild(clips[variants[i].ClipName], i);
        }

        /// <summary>Enters an upper-body action on its trigger (never while dead) and returns to Empty after it.</summary>
        /// <param name="restartable">Whether a fresh trigger mid-action starts it over, as rapid fire should.</param>
        private static void AddActionTransitions(
            AnimatorStateMachine layer, AnimatorState action, AnimatorState empty, string trigger, bool restartable)
        {
            var enter = layer.AddAnyStateTransition(action);
            ConfigureTransition(enter, 0.05f);
            enter.canTransitionToSelf = restartable;
            enter.AddCondition(AnimatorConditionMode.If, 0f, trigger);
            enter.AddCondition(AnimatorConditionMode.IfNot, 0f, PlayerActionSync.DeadParameter);

            var done = action.AddTransition(empty);
            done.hasExitTime = true;
            done.exitTime = 0.9f;
            done.duration = 0.2f;
        }

        private static void ConfigureTransition(AnimatorStateTransition transition, float duration)
        {
            transition.hasExitTime = false;
            transition.duration = duration;
        }

        /// <summary>
        /// Speeds each moving clip up or down so its stride matches where it sits in the blend space.
        /// </summary>
        /// <remarks>
        /// Positions come from UHFPS's real speeds, not the clips' own, so without this feet slide whenever
        /// the two disagree. Clamped because a large correction reads as fast-forward, which is worse than
        /// a little sliding. Clips authored in place carry no stride speed and are left at 1x.
        /// </remarks>
        private static void MatchPlaybackToSpeed(BlendTree tree, StringBuilder report)
        {
            var children = tree.children;
            var inPlace = 0;

            for (var i = 0; i < children.Length; i++)
            {
                var target = children[i].position.magnitude;
                if (target <= 0f || children[i].motion is not AnimationClip clip) continue;

                var velocity = clip.averageSpeed;
                var clipSpeed = new Vector3(velocity.x, 0f, velocity.z).magnitude;

                if (clipSpeed < InPlaceSpeedThreshold)
                {
                    inPlace++;
                    continue;
                }

                children[i].timeScale = Mathf.Clamp(target / clipSpeed, 0.5f, 2f);
                report.AppendLine(
                    $"  {clip.name}: stride {clipSpeed:0.00} m/s at {target:0.00} m/s " +
                    $"-> playback x{children[i].timeScale:0.00}.");
            }

            tree.children = children;
            if (inPlace > 0) report.AppendLine($"  {inPlace} locomotion clips authored in place; playback left at 1x.");
        }

        private static bool HasRequiredParameters(AnimatorController controller, StringBuilder report)
        {
            var present = new HashSet<string>();
            foreach (var parameter in controller.parameters) present.Add(parameter.name);

            var missing = new List<string>();
            foreach (var required in new[]
                     {
                         PlayerLocomotionSync.MoveXParameter,
                         PlayerLocomotionSync.MoveZParameter,
                         PlayerLocomotionSync.SpeedParameter,
                         PlayerLocomotionSync.GroundedParameter,
                         PlayerActionSync.DeadParameter,
                         PlayerActionSync.DeathVariantParameter,
                         PlayerActionSync.HitParameter,
                         PlayerActionSync.HitVariantParameter,
                         PlayerActionSync.FidgetParameter,
                         PlayerActionSync.IdleVariantParameter,
                         PlayerActionSync.ShootParameter,
                         PlayerActionSync.ReloadParameter,
                         PlayerActionSync.AttackParameter,
                         PlayerActionSync.AttackVariantParameter
                     })
            {
                if (!present.Contains(required)) missing.Add(required);
            }

            if (missing.Count == 0) return true;

            report.AppendLine(
                $"FAILED: {ControllerPath} lacks parameters {string.Join(", ", missing)}. Delete it to regenerate.");
            return false;
        }
    }
}
