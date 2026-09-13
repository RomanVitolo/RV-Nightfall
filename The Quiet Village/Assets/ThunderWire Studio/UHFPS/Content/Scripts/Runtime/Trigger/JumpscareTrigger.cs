using System.Collections;
using UnityEngine;
using UnityEngine.Events;
using Newtonsoft.Json.Linq;
using ThunderWire.Attributes;
using UHFPS.Tools;
using QuietVillage.Multiplayer.Bridge;

namespace UHFPS.Runtime
{
    [Docs("https://docs.twgamesdev.com/uhfps/guides/jumpscares")]
    public class JumpscareTrigger : MonoBehaviour, ISaveable
    {
        public enum JumpscareTypeEnum { Direct, Indirect, Audio }
        public enum DirectTypeEnum { Image, Model }
        public enum TriggerTypeEnum { Event, TriggerEnter, TriggerExit }

        public JumpscareTypeEnum JumpscareType = JumpscareTypeEnum.Direct;
        public DirectTypeEnum DirectType = DirectTypeEnum.Image;
        public TriggerTypeEnum TriggerType = TriggerTypeEnum.Event;

        public Sprite JumpscareImage;
        public string JumpscareModelID = "scare_zombie";

        public SoundClip JumpscareSound;

        public Animator Animator;
        public string AnimatorStateName = "Jumpscare";
        public string AnimatorTrigger = "Jumpscare";

        public bool InfluenceFear;
        [Range(0f, 1f)] public float TentaclesIntensity = 0f;
        [Range(0.1f, 3f)] public float TentaclesSpeed = 1f;
        [Range(0f, 1f)] public float VignetteStrength = 0f;

        public bool LookAtJumpscare;
        [Tooltip("Target transform to look at during the jumpscare.")]
        public Transform LookAtTarget;
        [Tooltip("Duration of the look at effect during the jumpscare.")]
        public float LookAtDuration;
        [Tooltip("If enabled, the player will be locked during the jumpscare look at.")]
        public bool LockPlayer;
        [Tooltip("If enabled, the jumpscare will end when TriggerJumpscareEnded function is called.")]
        public bool EndJumpscareManually;
        [Tooltip("Delay in seconds before OnJumpscareEnded is invoked.")]
        [Min(0f)] public float JumpscareEndEventDelay;

        public bool InfluenceWobble;
        public float WobbleAmplitudeGain = 1f;
        public float WobbleFrequencyGain = 1f;
        public float WobbleDuration = 0.2f;

        public float DirectDuration = 1f;
        public float FearDuration = 1f;

        public UnityEvent TriggerEnter;
        public UnityEvent TriggerExit;
        public UnityEvent OnJumpscareStarted;
        public UnityEvent OnJumpscareEnded;

        private bool jumpscareStarted;
        private bool jumpscareEnded;
        private bool triggerEntered;

        // MULTIPLAYER PATCH: resolved on use instead of cached in Awake. The manager lives on the player prefab, which
        // Netcode spawns after this Awakes, and Singleton lookup could return a teammate's disabled copy anyway.
        private JumpscareManager jumpscareManager => LocalPlayerContext.JumpscareManager;

        // --------------------------------------------------------------------
        // UNITY METHODS
        // --------------------------------------------------------------------

        private void OnTriggerEnter(Collider other)
        {
            if (TriggerType == TriggerTypeEnum.Event)
                return;

            // MULTIPLAYER PATCH: jumpscares are per-player, so only this client's own body sets them off here.
            if (!other.CompareTag("Player") || !LocalPlayerContext.IsLocalPlayer(other))
                return;

            if (jumpscareStarted || triggerEntered)
                return;

            TriggerEnter?.Invoke();

            if (TriggerType == TriggerTypeEnum.TriggerEnter)
            {
                TriggerJumpscare();
            }
            else if (TriggerType == TriggerTypeEnum.TriggerExit)
            {
                triggerEntered = true;
            }
        }

        private void OnTriggerExit(Collider other)
        {
            if (TriggerType == TriggerTypeEnum.Event)
                return;

            if (!other.CompareTag("Player") || !LocalPlayerContext.IsLocalPlayer(other))
                return;

            if (jumpscareStarted || !triggerEntered)
                return;

            TriggerExit?.Invoke();

            if (TriggerType == TriggerTypeEnum.TriggerExit)
            {
                TriggerJumpscare();
            }
        }

        // --------------------------------------------------------------------
        // PUBLIC API
        // --------------------------------------------------------------------

        /// <summary>
        /// Initiates the jumpscare sequence, triggering associated visual, audio, and gameplay effects.
        /// </summary>
        public void TriggerJumpscare()
        {
            if (jumpscareStarted)
                return;

            // MULTIPLAYER PATCH: an Event jumpscare can fire before this client's player has spawned. Left unstarted,
            // so the event can play it again once there is a screen to play it on.
            if (jumpscareManager == null)
                return;

            jumpscareStarted = true;
            OnJumpscareStarted?.Invoke();

            // Indirect jumpscare starts an animation.
            if (JumpscareType == JumpscareTypeEnum.Indirect && Animator != null)
            {
                Animator.SetTrigger(AnimatorTrigger);
            }

            // Start effects + auto-end handling (if EndJumpscareManually == false).
            jumpscareManager.StartJumpscareEffect(this);

            // Play audio (manager will use clip length to decide when to end, if needed).
            GameTools.PlayOneShot2D(transform.position, JumpscareSound, "Jumpscare Sound");
        }

        /// <summary>
        /// Signals to end the jumpscare effect when manual ending is enabled.
        /// </summary>
        public void TriggerJumpscareEnded()
        {
            if (!EndJumpscareManually || jumpscareManager == null)
                return;

            jumpscareManager.EndJumpscareEffect();
            HandleJumpscareEnded();
        }

        /// <summary>
        /// Called by the JumpscareManager when an auto jumpscare ends.
        /// </summary>
        public void HandleAutoJumpscareEnded()
        {
            HandleJumpscareEnded();
        }

        private void HandleJumpscareEnded()
        {
            if (jumpscareEnded)
                return;

            jumpscareEnded = true;

            if (JumpscareEndEventDelay > 0f)
            {
                StartCoroutine(InvokeEndEventAfterDelay(JumpscareEndEventDelay));
                return;
            }

            OnJumpscareEnded?.Invoke();
        }

        private IEnumerator InvokeEndEventAfterDelay(float delay)
        {
            yield return new WaitForSeconds(delay);
            OnJumpscareEnded?.Invoke();
        }

        // --------------------------------------------------------------------
        // SAVE/LOAD METHODS
        // --------------------------------------------------------------------
        public StorableCollection OnSave()
        {
            return new StorableCollection()
            {
                { nameof(jumpscareStarted), jumpscareStarted }
            };
        }

        public void OnLoad(JToken data)
        {
            jumpscareStarted = (bool)data[nameof(jumpscareStarted)];
        }
    }
}
