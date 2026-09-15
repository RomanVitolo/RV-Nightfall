using UnityEngine;

namespace QuietVillage.Gameplay.Survival
{
    /// <summary>
    /// Makes a night creature heard: growls while it hunts, footsteps as it moves, a scream as it rises and when it spots
    /// someone, a sound with each swing, and its death at dawn.
    /// </summary>
    /// <remarks>
    /// Presentation, run on every client like <see cref="CreatureBody"/>, and sends nothing. Footsteps follow the distance
    /// this copy actually moved, which NetworkTransform delivers, and growls are timed locally, so neither costs a message;
    /// players may hear a growl at slightly different moments, which nobody can tell. Swings, the spotting scream and death
    /// arrive through what <see cref="NightCreature"/> already replicates, so everyone hears those together.
    ///
    /// Added by <see cref="NightCreature"/> when it spawns.
    /// </remarks>
    [DisallowMultipleComponent]
    public class CreatureVoice : MonoBehaviour
    {
        // Screams at nightfall are staggered, so a pack rising together does not sound like one loud creature.
        private const float MaxRiseDelay = 2.5f;

        private CreatureSounds m_sounds;
        private AudioSource m_voice;
        private AudioSource m_feet;

        private float m_nextGrowlAt;
        private float m_nextScreamAt;
        private float m_walked;
        private Vector3 m_lastPosition;
        private bool m_dead;

        private int m_lastGrowl = -1, m_lastScream = -1, m_lastAttack = -1, m_lastDeath = -1, m_lastStep = -1;

        public void Configure(CreatureSounds sounds)
        {
            m_sounds = sounds;
            if (m_sounds == null) return;

            m_voice = CreateSource("Voice");
            m_feet = CreateSource("Feet");

            m_lastPosition = transform.position;
            m_nextGrowlAt = Time.time + Random.Range(m_sounds.GrowlInterval.x, m_sounds.GrowlInterval.y);
        }

        private AudioSource CreateSource(string sourceName)
        {
            var holder = new GameObject(sourceName).transform;
            holder.SetParent(transform, false);
            holder.localPosition = new Vector3(0f, sourceName == "Feet" ? 0.1f : 1.6f, 0f);

            var source = holder.gameObject.AddComponent<AudioSource>();
            source.playOnAwake = false;
            source.spatialBlend = 1f;
            source.rolloffMode = AudioRolloffMode.Logarithmic;
            source.minDistance = m_sounds.MinDistance;
            source.maxDistance = m_sounds.MaxDistance;
            source.dopplerLevel = 0f;
            return source;
        }

        /// <summary>A scream as it rises, a moment after the others.</summary>
        public void PlayRise()
        {
            if (m_sounds == null) return;

            Invoke(nameof(Scream), Random.Range(0f, MaxRiseDelay));
            m_nextScreamAt = Time.time + m_sounds.ScreamCooldown;
        }

        /// <summary>It has seen someone. Screams, unless it screamed only a moment ago.</summary>
        public void PlaySpotted()
        {
            if (m_sounds == null || m_dead || Time.time < m_nextScreamAt) return;

            Scream();
            m_nextScreamAt = Time.time + m_sounds.ScreamCooldown;
        }

        public void PlayAttack()
        {
            if (m_sounds == null || m_dead) return;

            Play(m_voice, CreatureSounds.Pick(m_sounds.Attacks, ref m_lastAttack), m_sounds.VoiceVolume);

            // A growl straight after a swing would talk over it.
            m_nextGrowlAt = Mathf.Max(m_nextGrowlAt, Time.time + 2f);
        }

        /// <summary>Stunned: a pained growl, pitched up so it is not mistaken for one of its idle ones.</summary>
        public void PlayHurt()
        {
            if (m_sounds == null || m_dead || m_voice == null) return;

            var clip = CreatureSounds.Pick(m_sounds.Growls, ref m_lastGrowl);
            if (clip == null) return;

            m_voice.pitch = Random.Range(1.25f, 1.4f);
            m_voice.PlayOneShot(clip, m_sounds.VoiceVolume);
            m_nextGrowlAt = Mathf.Max(m_nextGrowlAt, Time.time + 2f);
        }

        public void PlayDeath()
        {
            if (m_sounds == null || m_dead) return;

            m_dead = true;
            CancelInvoke();
            if (m_voice != null) m_voice.Stop();
            Play(m_voice, CreatureSounds.Pick(m_sounds.Deaths, ref m_lastDeath), m_sounds.VoiceVolume);
        }

        private void Scream()
        {
            if (m_dead) return;
            Play(m_voice, CreatureSounds.Pick(m_sounds.Screams, ref m_lastScream), m_sounds.VoiceVolume);
        }

        private void Update()
        {
            if (m_sounds == null || m_dead) return;

            if (Time.time >= m_nextGrowlAt)
            {
                // Not over a scream or swing still sounding.
                if (m_voice != null && !m_voice.isPlaying)
                    Play(m_voice, CreatureSounds.Pick(m_sounds.Growls, ref m_lastGrowl), m_sounds.VoiceVolume * 0.8f);

                m_nextGrowlAt = Time.time + Random.Range(m_sounds.GrowlInterval.x, m_sounds.GrowlInterval.y);
            }

            StepFootsteps();
        }

        private void StepFootsteps()
        {
            var moved = transform.position - m_lastPosition;
            moved.y = 0f;
            m_lastPosition = transform.position;

            var distance = moved.magnitude;

            // A teleport (a warp back onto the NavMesh) is not a stride.
            if (distance > 2f) return;

            var speed = Time.deltaTime > 0f ? distance / Time.deltaTime : 0f;
            if (speed < 0.3f)
            {
                m_walked = 0f;
                return;
            }

            // Longer strides when running, but not proportionally: a sprinting creature patters faster, too.
            var stride = m_sounds.StepLength * Mathf.Lerp(1f, 1.6f, Mathf.InverseLerp(1.5f, 4.5f, speed));
            m_walked += distance;
            if (m_walked < stride) return;

            m_walked -= stride;
            Play(m_feet, CreatureSounds.Pick(m_sounds.Footsteps, ref m_lastStep),
                m_sounds.FootstepVolume * Mathf.Lerp(0.8f, 1.2f, Mathf.InverseLerp(1.5f, 4.5f, speed)));
        }

        private static void Play(AudioSource source, AudioClip clip, float volume)
        {
            if (source == null || clip == null) return;

            source.pitch = Random.Range(0.92f, 1.08f);
            source.PlayOneShot(clip, volume);
        }
    }
}
