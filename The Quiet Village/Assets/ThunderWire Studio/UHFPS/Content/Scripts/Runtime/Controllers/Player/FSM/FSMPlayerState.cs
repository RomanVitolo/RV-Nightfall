using System;
using UnityEngine;
using static UHFPS.Runtime.PlayerStateMachine;
using QuietVillage.Multiplayer.Bridge;

namespace UHFPS.Runtime
{
    public class FSMPlayerState : FSMState
    {
        public struct DynamicHeightCast
        {
            public Vector3 TopHit;
            public bool IsValid;
        }

        public Transition[] Transitions { get; private set; }
        public StorableCollection StateData { get; set; }

        protected Transform cameraHolder;
        protected GameManager gameManager;
        protected PlayerStateMachine machine;
        protected CharacterController controller;
        protected PlayerItemsManager playerItems;
        protected MotionController motionController;
        protected LookController cameraLook;
        protected FootstepsSystem footstepsSystem;
        protected ControllerState controllerState;

        private Vector3 heightVelocity;
        private float lastDynamicHeight;

        /// <summary>
        /// Character controller current position.
        /// </summary>
        protected Vector3 Position
        {
            get => machine.transform.position;
            set => machine.transform.position = value;
        }

        /// <summary>
        /// Character controller center position.
        /// </summary>
        protected Vector3 CenterPosition
        {
            get => machine.ControllerCenter;
            set
            {
                Vector3 position = value;
                position -= controller.center;
                machine.transform.position = position;
            }
        }

        /// <summary>
        /// Character controller bottom position.
        /// </summary>
        protected Vector3 FeetPosition
        {
            get => machine.ControllerFeet;
            set
            {
                Vector3 position = value;
                position -= machine.ControllerFeet;
                machine.transform.position = position;
            }
        }

        /// <summary>
        /// The keyboard input movement value.
        /// </summary>
        protected Vector2 MovementInput => machine.Input;

        /// <summary>
        /// Check if you can transition to this state when the transition is disabled.
        /// </summary>
        public virtual bool CanTransitionWhenDisabled => false;

        /// <summary>
        /// Does this state represent a crouching state?
        /// Used for dynamic height adjustments.
        /// </summary>
        public virtual bool IsCrouchingState => false;

        /// <summary>
        /// The magnitude of the movement input.
        /// </summary>
        protected float InputMagnitude => machine.Input.magnitude;

        /// <summary>
        /// Check if the character controller is on the ground.
        /// </summary>
        protected bool IsGrounded => machine.IsGrounded;

        /// <summary>
        /// Check if the stamina feature is enabled in the player.
        /// </summary>
        protected bool StaminaEnabled => machine.PlayerFeatures.EnableStamina;

        /// <summary>
        /// Check if the player has died.
        /// </summary>
        protected bool IsDead => machine.IsPlayerDead;

        public FSMPlayerState(PlayerStateMachine machine)
        {
            this.machine = machine;
            // Not a Component itself, so it resolves through the state machine it drives.
            gameManager = LocalPlayerContext.ResolveGameManager(machine);
            controller = machine.Controller;
            playerItems = machine.PlayerManager.PlayerItems;
            cameraHolder = machine.PlayerManager.CameraHolder;
            motionController = machine.PlayerManager.MotionController;
            cameraLook = machine.LookController;
            footstepsSystem = machine.GetComponent<FootstepsSystem>();
            Transitions = OnGetTransitions();
        }

        /// <summary>
        /// Event when a player dies.
        /// </summary>
        public virtual void OnPlayerDeath() { }

        /// <summary>
        /// Get player state transitions.
        /// </summary>
        public virtual Transition[] OnGetTransitions()
        {
            return new Transition[0];
        }

        public override void OnStateExit()
        {
            lastDynamicHeight = 0f;
        }

        /// <summary>
        /// Sets the height and center position of the dynamic character controller and calculates the adjusted camera
        /// position based on the specified offset.
        /// </summary>
        /// <param name="controllerHeight">The desired height of the character controller, in world units. Must be a positive value.</param>
        /// <param name="cameraOffset">The offset to apply to the camera position, relative to the controller's calculated center.</param>
        /// <returns>A <see cref="Vector3"/> representing the new camera position after applying the offset and controller
        /// adjustments.</returns>
        public Vector3 SetDynamicControllerState(float controllerHeight, Vector3 cameraOffset)
        {
            float skinWidth = controller.skinWidth;
            float center = controllerHeight / 2;

            Vector3 controllerCenter = machine.ControllerOffset switch
            {
                PositionOffset.Ground => new Vector3(0, center + skinWidth, 0),
                PositionOffset.Feet => new Vector3(0, center, 0),
                PositionOffset.Center => new Vector3(0, 0, 0),
                PositionOffset.Head => new Vector3(0, -center, 0),
                _ => controller.center
            };

            controller.height = controllerHeight;
            controller.center = controllerCenter;

            Vector3 cameraTop = cameraOffset;
            cameraTop.y += center + controllerCenter.y;
            return cameraTop;
        }

        /// <summary>
        /// Set custom controller state by index.
        /// </summary>
        public void SetCustomControllerState(int index)
        {
            if (index < 0 || index > machine.CustomStates.Count)
                throw new Exception("The controller state index exceeds the number of custom states.");

            controllerState = machine.CustomStates[index];
        }

        /// <summary>
        /// Change player controller height.
        /// </summary>
        public void PlayerHeightUpdate()
        {
            float changeSpeed = machine.PlayerControllerSettings.StateChangeSmooth;
            Transform cameraHolder = machine.PlayerManager.CameraHolder;
            Vector3 cameraPos = cameraHolder.localPosition;

            // Dynamic Height Adjustment
            bool isEnabled = !machine.DynamicHeight.TriggerBased || machine.EnableDynamicHeight;
            if (IsCrouchingState && machine.DynamicHeight.EnableDynamicHeight && isEnabled)
            {
                var dynamicHeight = machine.DynamicHeight;
                Vector3 rayOrigin = machine.ControllerFeet + Vector3.up * dynamicHeight.HeightRayOffset;
                Vector3 forward = cameraLook.LookForward;

                float feetHeight = machine.ControllerFeet.y;
                float minHeight = Mathf.Infinity;
                bool hasValidRay = false;

                // Build the set of height casts based on current mode
                DynamicHeightCast[] heightCasts = BuildDynamicHeightCasts(rayOrigin, forward);

                // Get minimal height from valid rays
                foreach (DynamicHeightCast heightCast in heightCasts)
                {
                    if (!heightCast.IsValid)
                        continue;

                    float hitHeight = Mathf.Abs(heightCast.TopHit.y - feetHeight);
                    if (hitHeight < minHeight) 
                        minHeight = hitHeight;

                    hasValidRay = true;
                }

                if (hasValidRay)
                {
                    // Round height to avoid jittering and store last dynamic height
                    minHeight = (float)Math.Round(minHeight, 2);
                }

                // Check if we can get higher under the obstacle
                if (lastDynamicHeight > 0f && !Mathf.Approximately(minHeight, lastDynamicHeight))
                {
                    float upwardHeight = GetDynamicObstacleHeight(rayOrigin);
                    minHeight = Mathf.Min(minHeight, upwardHeight);
                    lastDynamicHeight = minHeight;
                }

                // Apply dynamic height or fallback to last value
                if (hasValidRay || CheckStillUnderDynamicHeightObstacle(rayOrigin))
                {
                    // If there is any valid ray, set the height to the minimal height, otherwise use the last dynamic height
                    float height = hasValidRay ? minHeight : lastDynamicHeight;

                    // Clamp height so it doesn't exceed the limits
                    float minLimit = dynamicHeight.ControllerHeight.RealMin;
                    float maxLimit = dynamicHeight.ControllerHeight.RealMax;

                    // Check if player is able to crouch to desired height
                    if (height > minLimit && height < maxLimit)
                    {
                        if (hasValidRay) lastDynamicHeight = minHeight;

                        // Subtract height offset to get the correct controller height that fits under the obstacle
                        float heightOffset = dynamicHeight.HeightTargetOffset;
                        height -= heightOffset;

                        // Set dynamic controller state
                        Vector3 cameraOffset = dynamicHeight.CameraOffset;
                        Vector3 dynamicCameraPos = SetDynamicControllerState(height, cameraOffset);
                        cameraPos = Vector3.SmoothDamp(cameraPos, dynamicCameraPos, ref heightVelocity, changeSpeed);

                        // Apply camera position
                        cameraHolder.localPosition = cameraPos;
                    }

                    return;
                }
            }

            // Fallback to normal controller state
            if (controllerState != null)
            {
                Vector3 cameraPosition = machine.SetControllerState(controllerState);
                cameraPos = Vector3.SmoothDamp(cameraPos, cameraPosition, ref heightVelocity, changeSpeed);
                machine.PlayerManager.CameraHolder.localPosition = cameraPos;
            }

            // Reset last dynamic height
            lastDynamicHeight = 0f;
        }

        /// <summary>
        /// Get player gravity force with weight.
        /// </summary>
        public float GravityForce()
        {
            float gravity = machine.PlayerControllerSettings.BaseGravity;
            float weight = machine.PlayerControllerSettings.PlayerWeight / 10f;
            return gravity - weight;
        }

        /// <summary>
        /// Apply gravity force to motion.
        /// </summary>
        public void ApplyGravity(ref Vector3 motion)
        {
            float gravityForce = GravityForce();
            motion += gravityForce * Time.deltaTime * Vector3.up;
        }

        /// <summary>
        /// Check if the surface is a sliding surface.
        /// </summary>
        public bool SlopeCast(out Vector3 normal, out float angle)
        {
            LayerMask slidingMask = machine.PlayerSliding.SlidingMask;
            float slideRayLength = machine.PlayerSliding.SlideRayLength;

            if (Physics.SphereCast(CenterPosition, controller.radius, Vector3.down, out RaycastHit hit, slideRayLength, slidingMask, QueryTriggerInteraction.Ignore))
            {
                normal = hit.normal;
                angle = Vector3.Angle(hit.normal, Vector3.up);
                return true;
            }

            normal = Vector3.zero;
            angle = 0f;
            return false;
        }

        /// <summary>
        /// Check if there is an obstacle above the player when standing up.
        /// </summary>
        public bool CheckStandObstacle()
        {
            float height = machine.StandingState.ControllerHeight + 0.1f;
            float radius = controller.radius;
            Vector3 origin = machine.ControllerFeet;
            Ray ray = new(origin, Vector3.up);

            return Physics.SphereCast(ray, radius, out _, height, machine.SurfaceMask, QueryTriggerInteraction.Ignore);
        }

        /// <summary>
        /// Build dynamic height casts based on the selected mode.
        /// </summary>
        private DynamicHeightCast[] BuildDynamicHeightCasts(Vector3 rayOrigin, Vector3 forward)
        {
            var dynamicHeight = machine.DynamicHeight;
            DynamicHeightMode mode = dynamicHeight.HeightRayMode;

            switch (mode)
            {
                case DynamicHeightMode.Frontal1Ray:
                    // Frontal 1 Ray
                    return new[] { CastDynamicHeightRay(rayOrigin, forward) };

                case DynamicHeightMode.Frontal3Rays:
                    // Frontal 3 Rays
                    return new[]
                    {
                        CastDynamicHeightRay(rayOrigin, forward),
                        CastDynamicHeightRay(rayOrigin, Quaternion.Euler(0f,  60f, 0f) * forward),
                        CastDynamicHeightRay(rayOrigin, Quaternion.Euler(0f, -60f, 0f) * forward)
                    };

                case DynamicHeightMode.AllAround6Rays:
                    // All Around 6 Rays
                    return new[]
                    {
                        CastDynamicHeightRay(rayOrigin, forward),
                        CastDynamicHeightRay(rayOrigin, Quaternion.Euler(0f,  60f,  0f) * forward),
                        CastDynamicHeightRay(rayOrigin, Quaternion.Euler(0f, -60f,  0f) * forward),
                        CastDynamicHeightRay(rayOrigin, Quaternion.Euler(0f, 120f,  0f) * forward),
                        CastDynamicHeightRay(rayOrigin, Quaternion.Euler(0f, -120f, 0f) * forward),
                        CastDynamicHeightRay(rayOrigin, Quaternion.Euler(0f, 180f, 0f) * forward)
                    };

                default:
                    // Sphere Cast
                    float radius = controller.radius + dynamicHeight.SphereCastRadiusOffset;
                    float hitHeight = GetDynamicObstacleHeight(rayOrigin, radius);

                    return new[]
                    {
                        new DynamicHeightCast
                        {
                            TopHit = new Vector3(0f, hitHeight, 0f),
                            IsValid = hitHeight > 0f
                        }
                    };
            }
        }

        /// <summary>
        /// Performs a dynamic height raycast using sphere casts in upward direction from a calculated start point.
        /// </summary>
        private DynamicHeightCast CastDynamicHeightRay(Vector3 origin, Vector3 dir)
        {
            var dynamicHeight = machine.DynamicHeight;
            Vector3? topHitPosition = null;

            Vector3 rayStart = origin + dir.normalized * dynamicHeight.HeightRayFront;
            float rayRadius = dynamicHeight.RaySphereRadius;
            float rayLength = dynamicHeight.HeightRayLength;
            LayerMask rayMask = dynamicHeight.HeightRayMask;

            // Frontal Obstacle Check
            if (Physics.Linecast(origin, rayStart, rayMask, QueryTriggerInteraction.Ignore))
            {
                return new DynamicHeightCast
                {
                    TopHit = Vector3.zero,
                    IsValid = false
                };
            }

            // Top Ray
            if (Physics.SphereCast(rayStart, rayRadius, Vector3.up, out RaycastHit topHit, rayLength, rayMask, QueryTriggerInteraction.Ignore))
            {
                topHitPosition = topHit.point;
            }

            // Return cast results
            return new DynamicHeightCast
            {
                TopHit = topHitPosition ?? Vector3.zero,
                IsValid = topHitPosition.HasValue
            };
        }

        /// <summary>
        /// Check if player is still under dynamic height obstacle.
        /// </summary>
        private bool CheckStillUnderDynamicHeightObstacle(Vector3 rayOrigin)
        {
            // No previous dynamic height set
            if (lastDynamicHeight <= 0f)
                return false;

            float feetHeight = machine.ControllerFeet.y;
            float rayRadius = controller.radius;
            float rayLength = machine.DynamicHeight.HeightRayLength;
            LayerMask rayMask = machine.DynamicHeight.HeightRayMask;

            // Top Ray
            if (Physics.SphereCast(rayOrigin, rayRadius, Vector3.up, out RaycastHit topHit, rayLength, rayMask, QueryTriggerInteraction.Ignore))
            {
                float hitHeight = Mathf.Abs(topHit.point.y - feetHeight);
                hitHeight = (float)Math.Round(hitHeight, 2);

                // Check if still under the previous dynamic height
                if (hitHeight < lastDynamicHeight + 0.1f)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Get dynamic obstacle height above the player.
        /// </summary>
        private float GetDynamicObstacleHeight(Vector3 rayOrigin, float radiusOverride = 0f)
        {
            float rayRadius = radiusOverride > 0f ? radiusOverride : controller.radius;
            float feetHeight = machine.ControllerFeet.y;
            float rayLength = machine.DynamicHeight.HeightRayLength;
            LayerMask rayMask = machine.DynamicHeight.HeightRayMask;

            // Top Ray
            if (Physics.SphereCast(rayOrigin, rayRadius, Vector3.up, out RaycastHit topHit, rayLength, rayMask, QueryTriggerInteraction.Ignore))
            {
                float hitHeight = Mathf.Abs(topHit.point.y - feetHeight);
                return (float)Math.Round(hitHeight, 2);
            }

            return 0f;
        }
    }
}