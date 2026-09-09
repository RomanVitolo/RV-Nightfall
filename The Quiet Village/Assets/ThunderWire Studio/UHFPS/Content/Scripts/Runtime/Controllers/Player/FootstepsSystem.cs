using UnityEngine;
using UHFPS.Scriptable;
using UHFPS.Tools;
using static UHFPS.Scriptable.SurfaceDefinitionSet;

namespace UHFPS.Runtime
{
    [RequireComponent(typeof(AudioSource))]
    public class FootstepsSystem : PlayerComponent
    {
        public enum FootstepStyleEnum { Timed, HeadBob, Animation }
        public enum StepState { None, Crouch, Walk, Run, Land }

        public SurfaceDefinitionSet SurfaceDefinitionSet;
        public FootstepStyleEnum FootstepStyle;
        public SurfaceDetection SurfaceDetection;
        public LayerMask FootstepsMask;

        public float StepPlayerVelocity = 0.1f;
        public float JumpStepAirTime = 0.1f;

        public bool EnableCrouchSteps = true;
        public bool EnableWalkSteps = true;
        public bool EnableRunSteps = true;
        public bool EnableLandSteps = true;

        public float CrouchStepTime = 1f;
        public float WalkStepTime = 1f;
        public float RunStepTime = 1f;
        public float LandStepTime = 1f;

        [Range(-1f, 1f)]
        public float HeadBobStepWave = -0.9f;

        [Range(0, 1)] public float CrouchingVolume = 1f;
        [Range(0, 1)] public float WalkingVolume = 1f;
        [Range(0, 1)] public float RunningVolume = 1f;
        [Range(0, 1)] public float LandVolume = 1f;

        public SurfaceDefinition CurrentSurface;

        private AudioSource audioSource;
        private Collider surfaceUnder;

        private int lastStep;
        private int lastLandStep;

        private float stepTime;
        private bool waveStep;

        private float airTime;
        private bool wasInAir;

        // --------------------------------------------------------------------
        // UNITY METHODS
        // --------------------------------------------------------------------

        private void Awake()
        {
            audioSource = GetComponent<AudioSource>();
        }

        private void OnControllerColliderHit(ControllerColliderHit hit)
        {
            surfaceUnder = FootstepsMask.CompareLayer(hit.gameObject.layer)
                ? hit.collider : null;
        }

        private void Update()
        {
            if (!isEnabled)
                return;

            if (stepTime > 0f)
                stepTime -= Time.deltaTime;

            if (PlayerStateMachine.StateGrounded)
            {
                if (surfaceUnder != null)
                {
                    CurrentSurface = SurfaceDefinitionSet.GetSurface(surfaceUnder.gameObject, transform.position, SurfaceDetection);
                    if (FootstepStyle != FootstepStyleEnum.Animation && CurrentSurface != null)
                        EvaluateFootsteps(CurrentSurface);
                }
            }
            else
            {
                airTime += Time.deltaTime;
                wasInAir = true;
            }
        }

        // --------------------------------------------------------------------
        // PUBLIC API (ANIMATION EVENTS)
        // --------------------------------------------------------------------

        public void PlayFootstep(StepState state)
        {
            if (surfaceUnder == null || !IsStepEnabled(state))
                return;

            CurrentSurface = SurfaceDefinitionSet.GetSurface(surfaceUnder.gameObject, transform.position, SurfaceDetection);
            if (CurrentSurface != null) 
                PlayFootstep(CurrentSurface, state);
        }

        public void PlayFootstep(bool runningStep)
        {
            PlayFootstep(runningStep ? StepState.Run : StepState.Walk);
        }

        public void PlayLandSteps()
        {
            PlayFootstep(StepState.Land);
        }

        // --------------------------------------------------------------------
        // FOOTSTEP EVALUATION
        // --------------------------------------------------------------------

        private void EvaluateFootsteps(SurfaceDefinition surface)
        {
            float playerVelocity = PlayerCollider.velocity.magnitude;

            if (FootstepStyle == FootstepStyleEnum.Timed)
            {
                // -------------------------------------------------------------
                // LANDING STEP
                // -------------------------------------------------------------
                if (wasInAir)
                {
                    if (airTime >= LandStepTime && IsStepEnabled(StepState.Land))
                        PlayFootstep(surface, StepState.Land);

                    airTime = 0f;
                    wasInAir = false;
                    return;
                }

                // -------------------------------------------------------------
                // MOVEMENT STEP
                // -------------------------------------------------------------
                StepState moveState = GetMovementStepState();

                if (moveState != StepState.None && IsStepEnabled(moveState) && playerVelocity > StepPlayerVelocity && stepTime <= 0f)
                {
                    PlayFootstep(surface, moveState);
                    stepTime = GetStepInterval(moveState);
                }
            }
            else if (FootstepStyle == FootstepStyleEnum.HeadBob)
            {
                // -------------------------------------------------------------
                // LANDING STEP
                // -------------------------------------------------------------
                if (wasInAir)
                {
                    if (airTime >= LandStepTime && IsStepEnabled(StepState.Land))
                        PlayFootstep(surface, StepState.Land);

                    airTime = 0f;
                    wasInAir = false;
                    return;
                }

                // -------------------------------------------------------------
                // HEADBOB MOVEMENT STEP
                // -------------------------------------------------------------
                StepState moveState = GetMovementStepState();
                if (moveState == StepState.None || !IsStepEnabled(moveState))
                    return;

                if (playerVelocity > StepPlayerVelocity)
                {
                    float yWave = PlayerManager.MotionController.BobWave;

                    if (yWave < HeadBobStepWave && !waveStep)
                    {
                        PlayFootstep(surface, moveState);
                        waveStep = true;
                    }
                    else if (yWave > HeadBobStepWave && waveStep)
                    {
                        waveStep = false;
                    }
                }
            }
        }

        // --------------------------------------------------------------------
        // STATE RESOLUTION
        // --------------------------------------------------------------------

        private StepState GetMovementStepState()
        {
            if (PlayerStateMachine.IsCurrent(PlayerStateMachine.CROUCH_STATE))
                return StepState.Crouch;

            if (PlayerStateMachine.IsCurrent(PlayerStateMachine.RUN_STATE))
                return StepState.Run;

            if (PlayerStateMachine.IsCurrent(PlayerStateMachine.WALK_STATE))
                return StepState.Walk;

            return StepState.None;
        }

        private bool IsStepEnabled(StepState state)
        {
            return state switch
            {
                StepState.Crouch => EnableCrouchSteps,
                StepState.Walk => EnableWalkSteps,
                StepState.Run => EnableRunSteps,
                StepState.Land => EnableLandSteps,
                _ => false,
            };
        }

        private float GetStepInterval(StepState state)
        {
            return state switch
            {
                StepState.Crouch => CrouchStepTime,
                StepState.Walk => WalkStepTime,
                StepState.Run => RunStepTime,
                _ => 0f,
            };
        }

        private float GetMovementVolumeScale(StepState state)
        {
            return state switch
            {
                StepState.Crouch => CrouchingVolume,
                StepState.Walk => WalkingVolume,
                StepState.Run => RunningVolume,
                _ => 0f,
            };
        }

        // --------------------------------------------------------------------
        // AUDIO PLAYBACK
        // --------------------------------------------------------------------

        private void PlayFootstep(SurfaceDefinition surface, StepState state)
        {
            if (surface == null)
                return;

            bool isLand = state == StepState.Land;

            if (!isLand)
            {
                if (!IsStepEnabled(state) || surface.SurfaceFootsteps.Count <= 0)
                    return;

                lastStep = GameTools.RandomUnique(0, surface.SurfaceFootsteps.Count, lastStep);
                AudioClip footstep = surface.SurfaceFootsteps[lastStep];

                float volumeScale = GetMovementVolumeScale(state) * surface.FootstepsVolume;
                audioSource.PlayOneShot(footstep, volumeScale);
            }
            else
            {
                if (!EnableLandSteps || surface.SurfaceLandSteps.Count <= 0)
                    return;

                lastLandStep = GameTools.RandomUnique(0, surface.SurfaceLandSteps.Count, lastLandStep);
                AudioClip landStep = surface.SurfaceLandSteps[lastLandStep];

                float volumeScale = LandVolume * surface.LandStepsVolume;
                audioSource.PlayOneShot(landStep, volumeScale);
            }
        }
    }
}
