using UnityEngine;

namespace QuietVillage.Gameplay.Survival
{
    /// <summary>
    /// Sounds the survival tools make, synthesized once in code: a church bell, a flare's crackle, holy water's hiss, a
    /// trap snapping shut and a healing chime.
    /// </summary>
    /// <remarks>
    /// The project's packs have no bell, flare or trap sounds, and these only need to be recognisable, so they are built
    /// from sine partials and shaped noise rather than added as assets. Swap any of them for a recorded clip by assigning
    /// one where it is played. Each clip is generated on first use and kept for the session.
    /// </remarks>
    public static class SynthSounds
    {
        private const int SampleRate = 44100;

        private static AudioClip s_bell, s_crackle, s_hiss, s_snap, s_chime;

        /// <summary>A large bell struck once: inharmonic partials, the higher ones dying first. Four seconds.</summary>
        public static AudioClip Bell => s_bell ??= Build("Bell", 4f, (t, _) =>
        {
            const float fundamental = 196f;
            float[] ratios = { 0.5f, 1f, 1.183f, 1.506f, 2f, 2.514f, 2.662f, 3.011f };
            float[] decays = { 0.9f, 0.7f, 1.1f, 1.4f, 1.8f, 2.4f, 2.8f, 3.4f };

            var sample = 0f;
            for (var i = 0; i < ratios.Length; i++)
                sample += Mathf.Sin(2f * Mathf.PI * fundamental * ratios[i] * t) * Mathf.Exp(-decays[i] * t) / (1f + i * 0.35f);

            // The strike itself: a short metallic click.
            sample += Mathf.Sin(2f * Mathf.PI * 1800f * t) * Mathf.Exp(-60f * t) * 0.6f;
            return sample * 0.35f;
        });

        /// <summary>A burning flare: sparse pops over a soft roar. One second, meant to loop.</summary>
        public static AudioClip Crackle => s_crackle ??= Build("Crackle", 1f, (t, random) =>
        {
            var roar = (float)(random.NextDouble() * 2.0 - 1.0) * 0.08f;
            var pop = random.NextDouble() < 0.0015 ? (float)(random.NextDouble() * 2.0 - 1.0) * 0.9f : 0f;
            return roar + pop;
        }, smoothing: 0.35f);

        /// <summary>Holy water on something unholy: a burst of hiss that fades. One second.</summary>
        public static AudioClip Hiss => s_hiss ??= Build("Hiss", 1f, (t, random) =>
            (float)(random.NextDouble() * 2.0 - 1.0) * Mathf.Exp(-3f * t) * 0.7f, smoothing: 0.6f);

        /// <summary>A trap snapping shut: a hard knock and a short metal ring. Half a second.</summary>
        public static AudioClip Snap => s_snap ??= Build("Snap", 0.5f, (t, random) =>
        {
            var knock = (float)(random.NextDouble() * 2.0 - 1.0) * Mathf.Exp(-80f * t);
            var ring = (Mathf.Sin(2f * Mathf.PI * 920f * t) + Mathf.Sin(2f * Mathf.PI * 1370f * t)) * Mathf.Exp(-12f * t) * 0.3f;
            return (knock + ring) * 0.8f;
        });

        /// <summary>A healing chime: two soft rising tones. One second.</summary>
        public static AudioClip Chime => s_chime ??= Build("Chime", 1f, (t, _) =>
        {
            var first = Mathf.Sin(2f * Mathf.PI * 660f * t) * Mathf.Exp(-4f * t);
            var second = t > 0.18f ? Mathf.Sin(2f * Mathf.PI * 880f * (t - 0.18f)) * Mathf.Exp(-4f * (t - 0.18f)) : 0f;
            return (first + second) * 0.35f;
        });

        /// <summary>Plays a clip once, in the world, heard up to <paramref name="maxDistance"/> away.</summary>
        public static void PlayAt(Vector3 position, AudioClip clip, float maxDistance, float volume = 1f)
        {
            if (clip == null) return;

            var source = UHFPS.Tools.GameTools.PlayOneShot3D(position, clip, maxDistance, volume, clip.name);
            if (source == null) return;

            source.rolloffMode = AudioRolloffMode.Logarithmic;
            source.minDistance = Mathf.Min(3f, maxDistance * 0.1f);
        }

        private static AudioClip Build(string name, float seconds, System.Func<float, System.Random, float> wave, float smoothing = 0f)
        {
            var count = Mathf.CeilToInt(seconds * SampleRate);
            var samples = new float[count];

            // Fixed seed: every machine builds the same sound.
            var random = new System.Random(7919 * name.Length + name[0]);
            var previous = 0f;
            for (var i = 0; i < count; i++)
            {
                var sample = wave((float)i / SampleRate, random);

                // A one-pole low-pass, which turns raw white noise into something closer to fire or steam.
                if (smoothing > 0f) sample = previous = Mathf.Lerp(sample, previous, smoothing);

                samples[i] = Mathf.Clamp(sample, -1f, 1f);
            }

            var clip = AudioClip.Create(name, count, 1, SampleRate, false);
            clip.SetData(samples, 0);
            return clip;
        }
    }
}
