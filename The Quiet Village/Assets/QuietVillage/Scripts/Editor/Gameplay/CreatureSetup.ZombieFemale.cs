using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using QuietVillage.Gameplay.Survival;
using UnityEditor;
using UnityEngine;

namespace QuietVillage.Gameplay.EditorTools
{
    /// <summary>
    /// Dresses every humanoid zombie in the Zombie Female animation set, and adds the set's own model as a creature.
    /// </summary>
    /// <remarks>
    /// The set is Humanoid, so it only plays on bodies with a Humanoid Avatar:
    /// <list type="bullet">
    /// <item>the Horror Maidens, already Humanoid;</item>
    /// <item>the set's own Female Zombie, added here as a body;</item>
    /// <item>Creature Humanoid 01–05 and Monster 01–05. Their pack imports them as Generic, but they are rigged to the
    /// UE Mannequin skeleton, which Unity maps to Humanoid on its own. Each gets a Humanoid Avatar from a copy of its model
    /// in <c>Creatures/Rigs</c>, set as the body's Avatar, so the pack itself is untouched.</item>
    /// </list>
    /// Creepy, Hunter and Mutant are not human-shaped and keep their own clips.
    ///
    /// Clips are spread across the zombies rather than shared, so a horde does not move in lockstep: each body takes the
    /// next idle, walk, run, attack set, death and entrance in turn. Applying replaces those bodies' clips and re-derives
    /// their speeds and timings, so run it deliberately: <c>Tools > Quiet Village > Creatures > Apply Zombie Female
    /// Animations</c>. It is idempotent.
    /// </remarks>
    public static partial class CreatureSetup
    {
        private const string ZombieFemalePack = "Assets/ZombieFemaleAnimations";
        private const string ZombieFemaleClips = ZombieFemalePack + "/Art/Animations/ZombieFemale_";
        private const string ZombieFemalePrefab = ZombieFemalePack + "/Prefabs/FemaleZombie.prefab";
        private const string ZombieFemaleId = "zombie_female";
        private const string RigFolder = AnimationFolder + "/Rigs";

        // Humanoid bodies that take the set, in the order clips are handed out.
        private static readonly string[] ZombieTargets =
        {
            "maiden_zombie_1", "maiden_zombie_2", ZombieFemaleId,
            "creature_humanoid_01", "creature_humanoid_02", "creature_humanoid_03", "creature_humanoid_04",
            "creature_humanoid_05", "monster_01", "monster_02", "monster_03", "monster_04", "monster_05"
        };

        private static readonly string[] ZombieIdles = { "Idle01", "Idle02", "Idle03", "Idle04", "Idle05" };

        private static readonly string[] ZombieWalks =
            { "Walk01Forward", "Walk02Forward", "Walk03Forward", "Walk04Forward", "Walk05Forward", "Walk06Forward" };

        // Two jogs, then four sprints. Sprinters also hunt faster; see ZombieMoveSpeed.
        private static readonly string[] ZombieRuns =
        {
            "Run01Forward", "Run02Forward", "RunSprintForward01", "RunSprintForward02", "RunSprintForward03",
            "RunSprintForward04"
        };

        // In place and combos only. The lunges and forward attacks carry the body forward, and with root motion off they
        // would reach out and snap back.
        private static readonly string[][] ZombieAttacks =
        {
            new[] { "AttackInPlace01", "AttackInPlace02", "AttackInPlace03" },
            new[] { "AttackInPlace04", "AttackInPlace05", "AttackInPlace06" },
            new[] { "AttackCombo01", "AttackCombo02", "AttackCombo03" }
        };

        private static readonly string[] ZombieDeaths = { "Death01", "Death02", "Death03" };

        private static readonly string[] ZombieEntrances = { "IdleScreams01", "IdleScreams02", "IdleScreams03", "IdleReactToSound" };

        private const float ZombieJogSpeed = 3.2f;
        private const float ZombieSprintSpeed = 3.8f;

        [MenuItem("Tools/Quiet Village/Creatures/Apply Zombie Female Animations")]
        private static void ApplyZombieFemaleFromMenu()
        {
            if (EditorApplication.isPlaying || EditorApplication.isPlayingOrWillChangePlaymode)
            {
                EditorUtility.DisplayDialog("Apply Zombie Female Animations", "Exit Play Mode first.", "OK");
                return;
            }

            if (!EditorUtility.DisplayDialog("Apply Zombie Female Animations",
                    "Replaces the clips, speeds and attack timings of the Horror Maidens, Creature Humanoid 01–05 and " +
                    "Monster 01–05, and adds the Female Zombie. Other creatures are left as they are.", "Apply", "Cancel"))
                return;

            Execute(applyZombieFemale: true);
        }

        /// <summary>Command-line form of <see cref="ApplyZombieFemaleFromMenu"/>.</summary>
        public static void ApplyZombieFemaleFromCommandLine()
        {
            var succeeded = Execute(applyZombieFemale: true);

            if (Application.isBatchMode) EditorApplication.Exit(succeeded ? 0 : 1);
        }

        private static void ApplyZombieFemaleSet(List<CreatureCatalog.Body> bodies, StringBuilder report)
        {
            if (!AssetDatabase.IsValidFolder(ZombieFemalePack))
            {
                report.AppendLine($"Zombie Female animations not applied: {ZombieFemalePack} is not in the project.");
                return;
            }

            EnsureFolder(RigFolder);
            EnsureZombieFemaleBody(bodies, report);

            var applied = new List<string>();
            var unmapped = new List<(CreatureCatalog.Body Body, int Index)>();

            for (var i = 0; i < ZombieTargets.Length; i++)
            {
                var body = bodies.FirstOrDefault(candidate => candidate != null && candidate.Id == ZombieTargets[i]);
                if (body == null || body.Prefab == null)
                {
                    report.AppendLine($"  Zombie Female: no body '{ZombieTargets[i]}' in the catalog; skipped.");
                    continue;
                }

                if (EnsureHumanoidAvatar(body, report)) ApplyZombieClips(body, i, applied, report);
                else unmapped.Add((body, i));
            }

            // Unity's automatic mapping guesses from the bind pose and gives up on some models it maps fine by name. A body
            // on the same skeleton that did map lends its bone names.
            var template = bodies
                .Where(body => body != null && body.Id.StartsWith("monster_") || body != null && body.Id.StartsWith("creature_humanoid_"))
                .Select(body => body.Avatar)
                .FirstOrDefault(avatar => avatar != null && avatar.isHuman && avatar.isValid);

            foreach (var (body, index) in unmapped)
            {
                if (template != null && MapLikeTemplate(body, template, report)) ApplyZombieClips(body, index, applied, report);
                else report.AppendLine($"  Zombie Female: '{body.Id}' could not be made Humanoid; kept its clips.");
            }

            report.AppendLine($"Zombie Female animations on {applied.Count} bodies: {string.Join(", ", applied)}.");
        }

        private static void ApplyZombieClips(CreatureCatalog.Body body, int i, List<string> applied, StringBuilder report)
        {
            var idle = ZombieClip(ZombieIdles[i % ZombieIdles.Length], report);
            var walk = ZombieClip(ZombieWalks[i % ZombieWalks.Length], report);
            var runName = ZombieRuns[i % ZombieRuns.Length];
            var run = ZombieClip(runName, report);
            var attacks = ZombieAttacks[i % ZombieAttacks.Length].Select(name => ZombieClip(name, report))
                .Where(clip => clip != null).ToList();

            if (idle == null || walk == null || attacks.Count == 0)
            {
                report.AppendLine($"  Zombie Female: '{body.Id}' kept its clips; the set is missing some it needs.");
                return;
            }

            body.Idle = idle;
            body.Walk = walk;
            body.Run = run;
            body.Attacks = attacks;
            body.Death = ZombieClip(ZombieDeaths[i % ZombieDeaths.Length], report);
            body.Entrance = ZombieClip(ZombieEntrances[i % ZombieEntrances.Length], report);
            body.MoveSpeed = runName.StartsWith("RunSprint") ? ZombieSprintSpeed : ZombieJogSpeed;
            body.Enabled = true;

            DeriveTimings(body);
            applied.Add($"{body.Id} ({ZombieWalks[i % ZombieWalks.Length]}, {runName})");
        }

        private static void EnsureZombieFemaleBody(List<CreatureCatalog.Body> bodies, StringBuilder report)
        {
            if (bodies.Any(body => body != null && body.Id == ZombieFemaleId)) return;

            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(ZombieFemalePrefab);
            if (prefab == null)
            {
                report.AppendLine($"  Zombie Female: {ZombieFemalePrefab} not found; the Female Zombie body was not added.");
                return;
            }

            var body = new CreatureCatalog.Body { Id = ZombieFemaleId, DisplayName = "Female Zombie", Prefab = prefab };
            FitScale(body, report);
            bodies.Add(body);
            report.AppendLine("  Added the Female Zombie body.");
        }

        /// <summary>
        /// Makes sure a body's rig can play Humanoid clips, building a Humanoid Avatar from a copy of its model if the
        /// model is Generic.
        /// </summary>
        /// <returns><c>false</c> if no Humanoid Avatar could be had; the body then keeps its clips.</returns>
        private static bool EnsureHumanoidAvatar(CreatureCatalog.Body body, StringBuilder report)
        {
            if (body.Avatar != null && body.Avatar.isHuman && body.Avatar.isValid) return true;

            var animator = body.Prefab.GetComponentInChildren<Animator>(true);
            if (animator != null && animator.avatar != null && animator.avatar.isHuman && animator.avatar.isValid)
            {
                body.Avatar = null;
                return true;
            }

            var skinned = body.Prefab.GetComponentInChildren<SkinnedMeshRenderer>(true);
            var modelPath = skinned != null && skinned.sharedMesh != null ? AssetDatabase.GetAssetPath(skinned.sharedMesh) : null;
            if (string.IsNullOrEmpty(modelPath) || AssetImporter.GetAtPath(modelPath) is not ModelImporter source)
            {
                report.AppendLine($"  Zombie Female: '{body.Id}' has no model to build a Humanoid Avatar from; kept its clips.");
                return false;
            }

            // A model already imported as Humanoid lends its own Avatar; no copy needed.
            if (source.animationType == ModelImporterAnimationType.Human)
            {
                var own = AssetDatabase.LoadAllAssetRepresentationsAtPath(modelPath).OfType<Avatar>()
                    .FirstOrDefault(candidate => candidate.isHuman && candidate.isValid);
                if (own != null)
                {
                    body.Avatar = own;
                    return true;
                }
            }

            var copyPath = $"{RigFolder}/{Path.GetFileName(modelPath)}";
            if (AssetImporter.GetAtPath(copyPath) == null && !AssetDatabase.CopyAsset(modelPath, copyPath))
            {
                report.AppendLine($"  Zombie Female: could not copy {modelPath}; '{body.Id}' kept its clips.");
                return false;
            }

            if (AssetImporter.GetAtPath(copyPath) is not ModelImporter importer) return false;

            // Same scale as the pack's import, so the Avatar's proportions match the transforms it will drive.
            var changed = importer.animationType != ModelImporterAnimationType.Human
                          || importer.avatarSetup != ModelImporterAvatarSetup.CreateFromThisModel
                          || importer.importAnimation
                          || !Mathf.Approximately(importer.globalScale, source.globalScale)
                          || importer.useFileUnits != source.useFileUnits
                          || importer.materialImportMode != ModelImporterMaterialImportMode.None;

            if (changed)
            {
                importer.animationType = ModelImporterAnimationType.Human;
                importer.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel;
                importer.importAnimation = false;
                importer.globalScale = source.globalScale;
                importer.useFileUnits = source.useFileUnits;
                importer.materialImportMode = ModelImporterMaterialImportMode.None;
                importer.SaveAndReimport();
            }

            var avatar = AssetDatabase.LoadAllAssetRepresentationsAtPath(copyPath).OfType<Avatar>().FirstOrDefault();
            if (avatar == null || !avatar.isHuman || !avatar.isValid) return false;

            body.Avatar = avatar;
            report.AppendLine($"  {body.Id}: Humanoid Avatar from {copyPath}.");
            return true;
        }

        /// <summary>
        /// Maps a body's rig copy to Humanoid with another Avatar's bone names, and its own bones' rest pose.
        /// </summary>
        /// <remarks>
        /// Only the name-to-bone mapping is borrowed. The skeleton is read from this model's own transforms, so the Avatar
        /// fits its proportions rather than the template's. Bones the template maps that this model lacks are dropped; if a
        /// required one is among them, the import still fails and the body keeps its clips.
        /// </remarks>
        private static bool MapLikeTemplate(CreatureCatalog.Body body, Avatar template, StringBuilder report)
        {
            var skinned = body.Prefab.GetComponentInChildren<SkinnedMeshRenderer>(true);
            var modelPath = skinned != null && skinned.sharedMesh != null ? AssetDatabase.GetAssetPath(skinned.sharedMesh) : null;
            if (string.IsNullOrEmpty(modelPath)) return false;

            var copyPath = $"{RigFolder}/{Path.GetFileName(modelPath)}";
            var model = AssetDatabase.LoadAssetAtPath<GameObject>(copyPath);
            if (model == null || AssetImporter.GetAtPath(copyPath) is not ModelImporter importer) return false;

            var transforms = model.GetComponentsInChildren<Transform>(true);
            var names = new HashSet<string>(transforms.Select(transform => transform.name));

            var description = template.humanDescription;
            description.human = description.human.Where(bone => names.Contains(bone.boneName)).ToArray();
            description.skeleton = transforms.Select(transform => new SkeletonBone
            {
                name = transform.name,
                position = transform.localPosition,
                rotation = transform.localRotation,
                scale = transform.localScale
            }).ToArray();

            importer.animationType = ModelImporterAnimationType.Human;
            importer.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel;
            importer.humanDescription = description;
            importer.SaveAndReimport();

            var avatar = AssetDatabase.LoadAllAssetRepresentationsAtPath(copyPath).OfType<Avatar>().FirstOrDefault();
            if (avatar == null || !avatar.isHuman || !avatar.isValid) return false;

            body.Avatar = avatar;
            report.AppendLine($"  {body.Id}: Humanoid Avatar from {copyPath}, mapped by bone name like {template.name}.");
            return true;
        }

        private static AnimationClip ZombieClip(string name, StringBuilder report)
        {
            var clip = FirstClipAt($"{ZombieFemaleClips}{name}.FBX");
            if (clip == null) report.AppendLine($"  Zombie Female: clip '{name}' not found.");
            return clip;
        }
    }
}
