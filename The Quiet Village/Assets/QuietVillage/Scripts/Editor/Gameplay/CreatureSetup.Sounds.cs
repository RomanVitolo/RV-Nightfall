using System.IO;
using System.Linq;
using System.Text;
using QuietVillage.Gameplay.Survival;
using UnityEditor;
using UnityEngine;

namespace QuietVillage.Gameplay.EditorTools
{
    /// <summary>
    /// The shared creature sound set: UHFPS's zombie grunts and dirt footsteps, the Super Mega Creatures parasites'
    /// screams, claws and deaths, and UHFPS's wood impacts for barricades.
    /// </summary>
    /// <remarks>
    /// Created when missing and filled only while it is empty, like the catalog: once someone has edited it, re-running
    /// the setup leaves it alone. Clips that are not where expected are simply left out, and the report says how many
    /// each list got. Nothing in the packs is changed.
    /// </remarks>
    public static partial class CreatureSetup
    {
        private static readonly string SoundsPath = Path.GetDirectoryName(CatalogPath)?.Replace('\\', '/') + "/CreatureSounds.asset";

        private const string UhfpsSounds = "Assets/ThunderWire Studio/UHFPS/Content/Sounds";
        private const string Parasites = CreaturePack + "/Parasites_Pack";

        private static readonly string[] GrowlPaths = Enumerable.Range(1, 7)
            .Select(i => $"{UhfpsSounds}/Grunts/Zombie Grunts/Zombie_Grunt_{i:00}.wav").ToArray();

        private static readonly string[] ScreamPaths =
        {
            Parasites + "/Parazite_fat/Sounds/FP_Scream_Only.flac",
            Parasites + "/Parazite_slider/Sounds/Slider_Scream_only.flac",
            Parasites + "/Parazite_spider/Sounds/Spider_Scream_only.flac",
            Parasites + "/Parazite_Alfa/Sounds/Sounds_without_stand/Alphas_Roar_Single.flac"
        };

        private static readonly string[] AttackPaths =
        {
            Parasites + "/Parazite_fat/Sounds/FP_Whoosh1.flac",
            Parasites + "/Parazite_fat/Sounds/FP_Whoosh2.flac",
            Parasites + "/Parazite_slider/Sounds/Slider_Whoosh1.flac",
            Parasites + "/Parazite_slider/Sounds/Slider_Whoosh2.flac",
            Parasites + "/Parazite_Alfa/Sounds/Sounds_without_stand/Alphas_Claw_Attack.flac",
            Parasites + "/Parazite_Alfa/Sounds/Sounds_without_stand/Alphas_Claw_Attack2.flac"
        };

        private static readonly string[] DeathPaths =
        {
            Parasites + "/Parazite_Alfa/Sounds/Sounds_with_stand/Alphas_Death.flac",
            Parasites + "/Parazite_fat/Sounds/FP_Hurt1.flac",
            Parasites + "/Parazite_slider/Sounds/Slider_Hurt2.flac"
        };

        private static readonly string[] FootstepPaths = Enumerable.Range(1, 10)
            .Select(i => $"{UhfpsSounds}/Player/Footsteps/Dirt/Footstep_Dirt_{i:00}.wav").ToArray();

        private static readonly string[] BarricadeHitPaths =
        {
            UhfpsSounds + "/Impacts/WoodCracking.wav",
            Parasites + "/Parazite_fat/Sounds/FP_Hit.flac",
            Parasites + "/Parazite_slider/Sounds/Slider_Hit.flac",
            Parasites + "/Parazite_spider/Sounds/Spider_Hit.flac"
        };

        private const string BarricadeBreakPath = UhfpsSounds + "/Impacts/WoodBreak.wav";

        private static CreatureSounds EnsureSounds(StringBuilder report)
        {
            var sounds = AssetDatabase.LoadAssetAtPath<CreatureSounds>(SoundsPath);
            if (sounds == null)
            {
                sounds = ScriptableObject.CreateInstance<CreatureSounds>();
                AssetDatabase.CreateAsset(sounds, SoundsPath);
                report.AppendLine($"Created {SoundsPath}.");
            }

            var isEmpty = sounds.Growls.Length == 0 && sounds.Screams.Length == 0 && sounds.Attacks.Length == 0
                          && sounds.Deaths.Length == 0 && sounds.Footsteps.Length == 0 && sounds.BarricadeHits.Length == 0
                          && sounds.BarricadeBreak == null;
            if (!isEmpty) return sounds;

            sounds.Growls = Load(GrowlPaths);
            sounds.Screams = Load(ScreamPaths);
            sounds.Attacks = Load(AttackPaths);
            sounds.Deaths = Load(DeathPaths);
            sounds.Footsteps = Load(FootstepPaths);
            sounds.BarricadeHits = Load(BarricadeHitPaths);
            sounds.BarricadeBreak = AssetDatabase.LoadAssetAtPath<AudioClip>(BarricadeBreakPath);

            EditorUtility.SetDirty(sounds);
            AssetDatabase.SaveAssets();

            report.AppendLine($"Creature sounds seeded: {sounds.Growls.Length} growls, {sounds.Screams.Length} screams, " +
                              $"{sounds.Attacks.Length} attacks, {sounds.Deaths.Length} deaths, {sounds.Footsteps.Length} footsteps, " +
                              $"{sounds.BarricadeHits.Length} barricade hits, barricade break {(sounds.BarricadeBreak != null ? "found" : "missing")}.");
            return sounds;
        }

        private static AudioClip[] Load(string[] paths) =>
            paths.Select(AssetDatabase.LoadAssetAtPath<AudioClip>).Where(clip => clip != null).ToArray();
    }
}
