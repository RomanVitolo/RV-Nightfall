using System;
using UnityEngine;
using UHFPS.Tools;

namespace UHFPS.Runtime
{
    [RequireComponent(typeof(AudioSource))]
    public class AnimationSoundEvent : MonoBehaviour
    {
        [Serializable]
        public struct SoundEvent
        {
            public string Name;
            public SoundClip Sound;
        }

        public SoundEvent[] SoundEvents;
        private AudioSource audioSource;

        private void Awake()
        {
            audioSource = GetComponent<AudioSource>();
        }

        /// <summary>
        /// MULTIPLAYER PATCH: raised for each sound an animation plays here. An item's animation is the only thing
        /// that knows about its reload, and it runs on its owner's client alone, so the bridge forwards this and
        /// plays the same sound on that player's body for everyone else.
        /// </summary>
        public event Action<string> SoundPlayed;

        public void PlaySound(string name)
        {
            foreach (var sound in SoundEvents)
            {
                if(sound.Name == name)
                {
                    audioSource.PlayOneShotSoundClip(sound.Sound);
                    SoundPlayed?.Invoke(name);
                    break;
                }
            }
        }

        /// <summary>MULTIPLAYER PATCH: the clip behind a name, so another client can play it at the right body.</summary>
        public SoundClip GetSound(string name)
        {
            foreach (var sound in SoundEvents)
            {
                if (sound.Name == name) return sound.Sound;
            }

            return null;
        }
    }
}