using UnityEngine;

namespace QuietVillage.Gameplay.Survival
{
    /// <summary>
    /// What a night creature sounds like: its growls, screams, swings, footsteps and death, and how far they carry.
    /// </summary>
    /// <remarks>
    /// One shared set is the default, on the creature prefab; a body in the <see cref="CreatureCatalog"/> may bring its
    /// own. Built by <c>Tools > Quiet Village > Creatures > Set Up Creatures</c> from UHFPS's zombie grunts and footsteps
    /// and the Super Mega Creatures pack's parasite screams and claws, only while it is empty; after that it is design
    /// data. Each list is picked from at random, never the same clip twice running.
    /// </remarks>
    [CreateAssetMenu(fileName = "CreatureSounds", menuName = "Quiet Village/Creature Sounds")]
    public class CreatureSounds : ScriptableObject
    {
        [Header("Voice")]
        [Tooltip("Low growls while it hunts, now and then.")]
        public AudioClip[] Growls = System.Array.Empty<AudioClip>();

        [Tooltip("Seconds between growls, picked at random in this range.")]
        public Vector2 GrowlInterval = new(4f, 10f);

        [Tooltip("As it rises at nightfall, and when it first catches sight of a player.")]
        public AudioClip[] Screams = System.Array.Empty<AudioClip>();

        [Tooltip("Seconds before it will scream at a player again.")]
        [Min(0f)] public float ScreamCooldown = 14f;

        [Tooltip("Played with every swing, at a player or a barricade.")]
        public AudioClip[] Attacks = System.Array.Empty<AudioClip>();

        [Tooltip("When dawn kills it.")]
        public AudioClip[] Deaths = System.Array.Empty<AudioClip>();

        [Header("Footsteps")]
        public AudioClip[] Footsteps = System.Array.Empty<AudioClip>();

        [Tooltip("Metres between footsteps at walking pace; running shortens it.")]
        [Min(0.2f)] public float StepLength = 0.9f;

        [Header("Barricades")]
        [Tooltip("A barricade taking a hit.")]
        public AudioClip[] BarricadeHits = System.Array.Empty<AudioClip>();

        [Tooltip("A barricade giving way.")]
        public AudioClip BarricadeBreak;

        [Header("Mix")]
        [Range(0f, 1f)] public float VoiceVolume = 0.9f;
        [Range(0f, 1f)] public float FootstepVolume = 0.45f;
        [Range(0f, 1f)] public float BarricadeVolume = 0.9f;

        [Tooltip("Within this distance a sound is at full volume.")]
        [Min(0.1f)] public float MinDistance = 2f;

        [Tooltip("Beyond this distance it cannot be heard. Far enough to hear one coming before it is seen in the fog.")]
        [Min(1f)] public float MaxDistance = 40f;

        /// <summary>A random clip from a list, avoiding <paramref name="last"/> when there is a choice.</summary>
        public static AudioClip Pick(AudioClip[] clips, ref int last)
        {
            if (clips == null || clips.Length == 0) return null;
            if (clips.Length == 1) return clips[0];

            int index;
            if (last < 0 || last >= clips.Length) index = Random.Range(0, clips.Length);
            else
            {
                // One of the others: pick from one fewer, then step over the last.
                index = Random.Range(0, clips.Length - 1);
                if (index >= last) index++;
            }

            last = index;
            return clips[index];
        }
    }
}
