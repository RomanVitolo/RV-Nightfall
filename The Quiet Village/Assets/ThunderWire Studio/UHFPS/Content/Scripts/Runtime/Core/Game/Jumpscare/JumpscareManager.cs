using System.Collections;
using UnityEngine;
using UnityEngine.UI;
using UHFPS.Rendering;
using ThunderWire.Attributes;
using static UHFPS.Runtime.JumpscareTrigger;

namespace UHFPS.Runtime
{
    [InspectorHeader("Jumpscare Manager")]
    [Docs("https://docs.twgamesdev.com/uhfps/guides/jumpscares")]
    public class JumpscareManager : Singleton<JumpscareManager>
    {
        public Image DirectImage;

        [Header("Direct Jumpscare Settings")]
        [Range(1f, 2f)] public float ImageMaxScale = 2f;
        [Range(0f, 1f)] public float ImageScaleTime = 0.5f;

        [Header("Fear Effect Settings")]
        [Range(0f, 1f)] public float FearIntensityDuration = 0.2f;
        [Range(0f, 1f)] public float FearSpeedDuration = 0.2f;

        [Header("Tentacles Default Settings")]
        [Range(0.1f, 3f)] public float TentaclesDefaultSpeed = 1f;
        [Range(-0.2f, 0.2f)] public float TentaclesDefaultPosition = 0f;

        [Header("Tentacles Animation Settings")]
        public float TentaclesMoveSpeed = 1f;
        public float TentaclesAnimationSpeed = 1f;
        public float TentaclesFadeInSpeed = 1f;
        public float TentaclesFadeOutSpeed = 1f;

        [Header("Camera Wobble Settings")]
        public float WobbleLossRate = 0.5f;

        private PlayerPresenceManager playerPresence;
        private GameManager gameManager;

        private PlayerManager playerManager;
        private LookController lookController;
        private JumpscareDirect jumpscareDirect;

        private WobbleMotion wobbleMotion;
        private FearTentacles fearTentacles;

        private GameObject directModel;
        private bool isDirectJumpscare;
        private float directDuration;
        private float directTimer;

        private bool isPlayerLocked;

        private bool influenceFear;
        private bool tentaclesFaded;
        private bool showTentacles;
        private float fearDuration;
        private float fearTimer;

        private Coroutine autoEndRoutine;

        // --------------------------------------------------------------------
        // UNITY METHODS
        // --------------------------------------------------------------------
        private void Awake()
        {
            playerPresence = GetComponent<PlayerPresenceManager>();
            gameManager = GetComponent<GameManager>();

            playerManager = playerPresence.PlayerManager;
            lookController = playerPresence.LookController;

            jumpscareDirect = playerManager.GetComponent<JumpscareDirect>();
            fearTentacles = gameManager.GetStack<FearTentacles>();
        }

        private void Start()
        {
            wobbleMotion = playerManager.MotionController.GetDefaultMotion<WobbleMotion>();
        }

        private void Update()
        {
            if (isDirectJumpscare)
                UpdateDirectJumpscare();

            if (influenceFear && showTentacles)
                UpdateFearTentacles();
        }

        // --------------------------------------------------------------------
        // PUBLIC API
        // --------------------------------------------------------------------

        /// <summary>
        /// Start Jumpscare Effect based on the provided JumpscareTrigger settings.
        /// </summary>
        public void StartJumpscareEffect(JumpscareTrigger jumpscare)
        {
            // Stop any previous auto-end routine (prevents overlap).
            if (autoEndRoutine != null)
            {
                StopCoroutine(autoEndRoutine);
                autoEndRoutine = null;
            }

            // Camera Wobble
            if (jumpscare.InfluenceWobble)
            {
                wobbleMotion.ApplyWobble(
                    jumpscare.WobbleAmplitudeGain,
                    jumpscare.WobbleFrequencyGain,
                    jumpscare.WobbleDuration
                );
            }

            // Direct Jumpscare
            if (jumpscare.JumpscareType == JumpscareTypeEnum.Direct)
            {
                StartDirectJumpscare(jumpscare);
            }
            // Indirect / Audio LookAt
            else if ((jumpscare.JumpscareType == JumpscareTypeEnum.Indirect || jumpscare.JumpscareType == JumpscareTypeEnum.Audio) && jumpscare.LookAtJumpscare)
            {
                isPlayerLocked = jumpscare.LockPlayer;

                lookController.LerpRotation(jumpscare.LookAtTarget, jumpscare.LookAtDuration, isPlayerLocked);

                if (isPlayerLocked)
                    gameManager.FreezePlayer(true);
            }

            // Fear / Tentacles
            if (jumpscare.InfluenceFear)
            {
                fearTentacles.TentaclesPosition.value = Mathf.Lerp(0f, 0.2f, jumpscare.TentaclesIntensity);
                fearTentacles.TentaclesSpeed.value = jumpscare.TentaclesSpeed;
                fearTentacles.VignetteStrength.value = jumpscare.VignetteStrength;

                fearDuration = jumpscare.FearDuration;
                fearTimer = jumpscare.FearDuration;

                tentaclesFaded = false;
                showTentacles = true;
                influenceFear = true;
            }

            // Auto End (EndJumpscareManually disabled)
            if (!jumpscare.EndJumpscareManually)
            {
                switch (jumpscare.JumpscareType)
                {
                    case JumpscareTypeEnum.Direct:
                        autoEndRoutine = StartCoroutine(AutoEndDirectJumpscare(jumpscare));
                        break;

                    case JumpscareTypeEnum.Audio:
                        autoEndRoutine = StartCoroutine(AutoEndAudioJumpscare(jumpscare));
                        break;

                    case JumpscareTypeEnum.Indirect:
                        autoEndRoutine = StartCoroutine(AutoEndIndirectJumpscare(jumpscare));
                        break;
                }
            }
        }

        /// <summary>
        /// End the current Jumpscare Effect.
        /// </summary>
        public void EndJumpscareEffect()
        {
            // Only unlock if we actually locked the player.
            if (!isPlayerLocked)
                return;

            gameManager.FreezePlayer(false);
            lookController.LookLocked = false;
            isPlayerLocked = false;
        }

        // --------------------------------------------------------------------
        // INTERNAL METHODS
        // --------------------------------------------------------------------

        private void StartDirectJumpscare(JumpscareTrigger jumpscare)
        {
            if (jumpscare.DirectType == DirectTypeEnum.Image)
            {
                DirectImage.sprite = jumpscare.JumpscareImage;
                DirectImage.gameObject.SetActive(true);

                directDuration = jumpscare.DirectDuration;
                directTimer = jumpscare.DirectDuration;
                isDirectJumpscare = true;
            }
            else if (jumpscare.DirectType == DirectTypeEnum.Model)
            {
                jumpscareDirect.ShowDirectJumpscare(jumpscare.JumpscareModelID, jumpscare.DirectDuration);
            }
        }

        private void UpdateDirectJumpscare()
        {
            directTimer -= Time.deltaTime;

            if (directTimer <= 0f)
            {
                if (directModel != null)
                {
                    directModel.SetActive(false);
                    directModel = null;
                }
                else
                {
                    DirectImage.gameObject.SetActive(false);
                    DirectImage.rectTransform.localScale = Vector3.one;
                }

                isDirectJumpscare = false;
                return;
            }

            // Scale only for image direct jumpscare.
            if (directModel == null && DirectImage != null)
            {
                float scaleWindow = directDuration * ImageScaleTime;
                float t = Mathf.InverseLerp(directDuration - scaleWindow, 0f, directTimer);
                float scale = Mathf.Lerp(1f, ImageMaxScale, t);
                DirectImage.rectTransform.localScale = Vector3.one * scale;
            }
        }

        private void UpdateFearTentacles()
        {
            // Fade in until fully visible.
            if (fearTentacles.EffectFade.value < 1f && !tentaclesFaded)
            {
                float fade = fearTentacles.EffectFade.value;
                fearTentacles.EffectFade.value = Mathf.MoveTowards(fade, 1f, Time.deltaTime * TentaclesFadeInSpeed);
                return;
            }

            // Animate back to defaults during the fear window.
            if (fearTimer > 0f)
            {
                float fearSpeedOffset = fearDuration - fearDuration * FearSpeedDuration;
                float fearIntensityOffset = fearDuration - fearDuration * FearIntensityDuration;

                fearTimer -= Time.deltaTime;

                if (fearTimer <= fearSpeedOffset)
                {
                    float speed = fearTentacles.TentaclesSpeed.value;
                    fearTentacles.TentaclesSpeed.value = Mathf.Lerp(speed, TentaclesDefaultSpeed, Time.deltaTime * TentaclesAnimationSpeed);
                }

                if (fearTimer <= fearIntensityOffset)
                {
                    float position = fearTentacles.TentaclesPosition.value;
                    fearTentacles.TentaclesPosition.value = Mathf.Lerp(position, TentaclesDefaultPosition, Time.deltaTime * TentaclesMoveSpeed);
                }

                tentaclesFaded = true;
                return;
            }

            // Fade out once animation is done.
            if (tentaclesFaded)
            {
                if (fearTentacles.EffectFade.value > 0f)
                {
                    float fade = fearTentacles.EffectFade.value;
                    fearTentacles.EffectFade.value = Mathf.MoveTowards(fade, 0f, Time.deltaTime * TentaclesFadeOutSpeed);
                }
                else
                {
                    fearTimer = 0f;
                    fearDuration = 0f;

                    fearTentacles.EffectFade.value = 0f;

                    tentaclesFaded = false;
                    showTentacles = false;
                    influenceFear = false;
                }
            }
        }

        // --------------------------------------------------------------------
        // IENUMERATORS
        // --------------------------------------------------------------------

        private IEnumerator AutoEndDirectJumpscare(JumpscareTrigger jumpscare)
        {
            if (jumpscare.DirectDuration > 0f)
                yield return new WaitForSeconds(jumpscare.DirectDuration);

            EndAndNotify(jumpscare);
        }

        private IEnumerator AutoEndAudioJumpscare(JumpscareTrigger jumpscare)
        {
            float audioLength = GetSoundLength(jumpscare);

            if (audioLength <= 0f)
            {
                EndAndNotify(jumpscare);
                yield break;
            }

            yield return new WaitForSeconds(audioLength);
            EndAndNotify(jumpscare);
        }

        private IEnumerator AutoEndIndirectJumpscare(JumpscareTrigger jumpscare)
        {
            if (jumpscare.Animator == null || string.IsNullOrEmpty(jumpscare.AnimatorStateName))
            {
                EndAndNotify(jumpscare);
                yield break;
            }

            yield return new WaitForAnimatorClip(jumpscare.Animator, jumpscare.AnimatorStateName);
            EndAndNotify(jumpscare);
        }

        private void EndAndNotify(JumpscareTrigger jumpscare)
        {
            EndJumpscareEffect();
            jumpscare.HandleAutoJumpscareEnded();
            autoEndRoutine = null;
        }

        private float GetSoundLength(JumpscareTrigger jumpscare)
        {
            if (jumpscare == null || jumpscare.JumpscareSound == null)
                return 0f;

            var clip = jumpscare.JumpscareSound.audioClip;
            return clip != null ? clip.length : 0f;
        }
    }
}
