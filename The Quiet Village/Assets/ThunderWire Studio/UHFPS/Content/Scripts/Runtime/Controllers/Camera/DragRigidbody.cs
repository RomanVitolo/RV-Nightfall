using UnityEngine;
using UHFPS.Input;
using System;
using Modules.Multiplayer.Bridge;

namespace UHFPS.Runtime
{
    [RequireComponent(typeof(InteractController))]
    public class DragRigidbody : PlayerComponent, IReticleProvider
    {
        public enum HoldTypeEnum { Press, Hold }
        public enum DragTypeEnum { WeightedVelocity, FixedVelocity }

        // --------------------------------------------------
        // INSPECTOR SETTINGS
        // --------------------------------------------------
        
        public HoldTypeEnum HoldType = HoldTypeEnum.Press;
        public DragTypeEnum DragType = DragTypeEnum.WeightedVelocity;

        public ControlsContext[] ControlsContexts;
        
        public bool ShowGrabReticle = true;
        public Reticle GrabHand;
        public Reticle HoldHand;
        
        public RigidbodyInterpolation Interpolate = RigidbodyInterpolation.Interpolate;
        public CollisionDetectionMode CollisionDetection = CollisionDetectionMode.ContinuousDynamic;
        public bool FreezeRotation = false;
        
        [Tooltip("How strongly the object is pulled towards the hold point.")]
        public float DragStrength = 10f;
        [Tooltip("How strongly the object is thrown when using the throw input.")]
        public float ThrowStrength = 10f;
        [Tooltip("Rotation speed when rotating the held object.")]
        public float RotateSpeed = 1f;
        [Tooltip("Zoom speed (mouse wheel).")]
        public float ZoomSpeed = 1f;
        
        public bool HitpointOffset = true;
        public bool PlayerCollision = false;
        public bool ObjectZooming = true;
        public bool ObjectRotating = true;
        public bool ObjectThrowing = true;
        
        public MinMax DefaultZoomDistance = new MinMax(1f, 3f);
        public float DefaultMaxHoldDistance = 4f;

        // --------------------------------------------------
        // PRIVATE FIELDS
        // --------------------------------------------------

        private GameManager gameManager;
        private InteractController interactController;

        private Rigidbody heldRigidbody;
        private IDraggableObject draggableInfo;
        private GameObject heldObjectRoot;

        private GameObject raycastObject;
        
        private RigidbodyInterpolation defInterpolate;
        private CollisionDetectionMode defCollisionDetection;
        private bool defFreezeRotation;
        private bool defUseGravity;
        private bool defIsKinematic;

        private GameObject holdPoint;
        private GameObject holdRotatePoint;

        private Vector3 holdOffset;
        private float holdDistance;
        private MinMax currentZoomDistance;
        private float currentMaxHoldDistance;

        private bool isDragging;
        private bool isRotating;
        private bool isThrown;

        // --------------------------------------------------
        // UNITY LIFECYCLE
        // --------------------------------------------------

        private void Awake()
        {
            gameManager = LocalPlayerContext.ResolveGameManager(this);
            interactController = GetComponent<InteractController>();
        }

        private void Start()
        {
            foreach (var control in ControlsContexts)
                control.SubscribeGloc();
        }

        private void Update()
        {
            raycastObject = interactController.RaycastObject;

            HandleGrabDropInput();

            if (isDragging)
                HoldUpdate();
        }

        private void FixedUpdate()
        {
            if (isDragging)
                FixedHoldUpdate();
        }

        // --------------------------------------------------
        // INPUT HANDLING
        // --------------------------------------------------

        private void HandleGrabDropInput()
        {
            if (raycastObject == null && !isDragging)
                return;

            if (HoldType == HoldTypeEnum.Press)
            {
                if (InputManager.ReadButtonOnce(this, Controls.USE))
                {
                    if (!isDragging)
                        GrabObject();
                    else
                        DropObject();
                }
            }
            else if (HoldType == HoldTypeEnum.Hold)
            {
                if (InputManager.ReadButton(Controls.USE))
                {
                    if (!isDragging && !isThrown)
                        GrabObject();
                }
                else
                {
                    if (isDragging)
                        DropObject();
                    else
                        isThrown = false;
                }
            }
        }

        // --------------------------------------------------
        // GRAB / DROP / THROW
        // --------------------------------------------------

        /// <summary>
        /// Public entry for grabbing a specific object from code.
        /// </summary>
        public void GrabObject(GameObject item, bool ignoreAllowDragging = false)
        {
            if (item == null) 
                return;
            
            raycastObject = item;
            GrabObject(ignoreAllowDragging);
        }

        private void GrabObject(bool ignoreAllowDragging = false)
        {
            if (raycastObject == null)
                return;

            if (!raycastObject.TryGetComponent(out heldRigidbody))
                return;

            heldObjectRoot = heldRigidbody.gameObject;
            draggableInfo = heldObjectRoot.GetComponent<IDraggableObject>();

            if (draggableInfo == null || (!ignoreAllowDragging && !draggableInfo.AllowDragging))
                return;
            
            // Save original rigidbody settings
            defUseGravity = heldRigidbody.useGravity;
            defIsKinematic = heldRigidbody.isKinematic;
            defInterpolate = heldRigidbody.interpolation;
            defCollisionDetection = heldRigidbody.collisionDetectionMode;
            defFreezeRotation = heldRigidbody.freezeRotation;

            // Apply drag overrides
            heldRigidbody.interpolation = Interpolate;
            heldRigidbody.collisionDetectionMode = CollisionDetection;
            heldRigidbody.freezeRotation = FreezeRotation;

            Physics.IgnoreCollision(
                heldObjectRoot.GetComponent<Collider>(),
                PlayerCollider,
                !PlayerCollision
            );

            // Determine zoom and max hold distance
            currentZoomDistance = draggableInfo != null
                ? draggableInfo.ZoomDistanceValue
                : DefaultZoomDistance;

            currentMaxHoldDistance = draggableInfo != null
                ? draggableInfo.MaxHoldDistanceValue
                : DefaultMaxHoldDistance;

            // Calculate starting distance
            Vector3 hitWorldPoint = GetHitWorldPoint();
            float startDistance = Vector3.Distance(MainCamera.transform.position, hitWorldPoint);
            holdDistance = Mathf.Clamp(startDistance, currentZoomDistance.RealMin, currentZoomDistance.RealMax);

            // Create hold points
            CreateHoldPoints(hitWorldPoint);

            // Rigidbody state per drag type
            if (DragType == DragTypeEnum.FixedVelocity)
            {
                heldRigidbody.linearVelocity = Vector3.zero;
                heldRigidbody.useGravity = false;
                heldRigidbody.isKinematic = false;
            }
            else // WeightedVelocity
            {
                heldRigidbody.useGravity = true;
                heldRigidbody.isKinematic = false;
            }

            // Call drag start interfaces
            foreach (var dragStart in heldObjectRoot.GetComponentsInChildren<IOnDragStart>())
            {
                dragStart.OnDragStart();
            }

            // UI / state
            PlayerManager.PlayerItems.IsItemsUsable = false;
            interactController.EnableInteractInfo(false);
            gameManager.ShowControlsInfo(true, ControlsContexts);

            isDragging = true;
        }

        private void DropObject()
        {
            if (heldRigidbody == null || heldObjectRoot == null)
                return;

            // Restore rigidbody settings
            heldRigidbody.useGravity = defUseGravity;
            heldRigidbody.isKinematic = defIsKinematic;
            heldRigidbody.interpolation = defInterpolate;
            heldRigidbody.collisionDetectionMode = defCollisionDetection;
            heldRigidbody.freezeRotation = defFreezeRotation;

            Physics.IgnoreCollision(
                heldObjectRoot.GetComponent<Collider>(),
                PlayerCollider,
                false
            );

            if (isRotating)
            {
                LookController.SetEnabled(true);
                isRotating = false;
            }

            // Call drag end interfaces
            foreach (var dragEnd in heldObjectRoot.GetComponentsInChildren<IOnDragEnd>())
            {
                dragEnd.OnDragEnd();
            }

            // Cleanup helper objects / UI
            if (holdRotatePoint != null) Destroy(holdRotatePoint);
            if (holdPoint != null) Destroy(holdPoint);

            interactController.EnableInteractInfo(true);
            gameManager.ShowControlsInfo(false, Array.Empty<ControlsContext>());
            PlayerManager.PlayerItems.IsItemsUsable = true;

            // Reset state
            holdOffset = Vector3.zero;
            holdDistance = 0;

            heldRigidbody = null;
            draggableInfo = null;
            heldObjectRoot = null;
            isDragging = false;
        }

        private void ThrowObject()
        {
            if (heldRigidbody == null)
                return;

            heldRigidbody.AddForce(
                10f * ThrowStrength * MainCamera.transform.forward,
                ForceMode.Force
            );

            DropObject();
        }

        // --------------------------------------------------
        // HOLD UPDATE (INPUT SPACE)
        // --------------------------------------------------

        private void HoldUpdate()
        {
            if (heldRigidbody == null || heldObjectRoot == null)
                return;

            // Zoom (mouse wheel)
            if (ObjectZooming && InputManager.ReadInput(Controls.SCROLL_WHEEL, out Vector2 scroll))
            {
                holdDistance = Mathf.Clamp(
                    holdDistance + scroll.y * ZoomSpeed * 0.001f,
                    currentZoomDistance.RealMin,
                    currentZoomDistance.RealMax
                );
            }

            // Rotation
            if (ObjectRotating && InputManager.ReadButton(Controls.RELOAD))
            {
                InputManager.ReadInput(Controls.POINTER_DELTA, out Vector2 delta);
                delta = delta.normalized * RotateSpeed;

                if (DragType == DragTypeEnum.WeightedVelocity && (holdPoint != null || holdRotatePoint != null))
                {
                    Transform rotateTransform = HitpointOffset ? holdRotatePoint.transform : holdPoint.transform;
                    rotateTransform.Rotate(VirtualCamera.transform.up, delta.x, Space.World);
                    rotateTransform.Rotate(VirtualCamera.transform.right, delta.y, Space.World);
                }
                else if(DragType == DragTypeEnum.FixedVelocity && holdRotatePoint != null)
                {
                    holdRotatePoint.transform.Rotate(VirtualCamera.transform.up, delta.x, Space.World);
                    holdRotatePoint.transform.Rotate(VirtualCamera.transform.right, delta.y, Space.World);
                }

                LookController.SetEnabled(false);
                isRotating = true;
            }
            else if (isRotating)
            {
                LookController.SetEnabled(true);
                isRotating = false;
            }

            // Throw
            if (ObjectThrowing && InputManager.ReadButtonOnce("Fire", Controls.FIRE))
            {
                ThrowObject();
                isThrown = true;
            }
        }

        // --------------------------------------------------
        // HOLD UPDATE (PHYSICS SPACE)
        // --------------------------------------------------

        private void FixedHoldUpdate()
        {
            if (heldRigidbody == null || heldObjectRoot == null)
                return;

            // Target position in front of camera
            Vector3 grabPos = VirtualCamera.transform.position + VirtualCamera.transform.forward * holdDistance;

            if (HitpointOffset && holdPoint != null)
            {
                Vector3 offsetDir = holdPoint.transform.TransformDirection(holdOffset);
                grabPos -= offsetDir;
            }

            Vector3 currentPos = heldRigidbody.worldCenterOfMass;
            Vector3 targetVelocity = grabPos - currentPos;

            if (DragType == DragTypeEnum.WeightedVelocity)
            {
                holdPoint.transform.position = grabPos;
                targetVelocity.Normalize();

                float massFactor = 1f / heldRigidbody.mass;
                float distanceFactor = Mathf.Clamp01(Vector3.Distance(grabPos, currentPos));
                Transform rotateTransform = HitpointOffset ? holdRotatePoint.transform : holdPoint.transform;

                heldRigidbody.linearVelocity = Vector3.Lerp(heldRigidbody.linearVelocity, distanceFactor * DragStrength * massFactor * targetVelocity, 0.3f);
                heldRigidbody.rotation = Quaternion.Slerp(heldRigidbody.rotation, rotateTransform.rotation, 0.3f);
                heldRigidbody.angularVelocity = Vector3.zero;
            }
            else // FixedVelocity
            {
                heldRigidbody.linearVelocity = targetVelocity * DragStrength;
                heldRigidbody.rotation = Quaternion.Slerp(heldRigidbody.rotation, holdRotatePoint.transform.rotation, 0.3f);
                heldRigidbody.angularVelocity = Vector3.zero;
            }

            foreach (var dragUpdate in heldRigidbody.GetComponentsInChildren<IOnDragUpdate>())
            {
                dragUpdate.OnDragUpdate(targetVelocity);
            }
            
            // Distance fail-safe: drop if player walks too far away
            float distanceFromPlayer = Vector3.Distance(heldObjectRoot.transform.position, MainCamera.transform.position);
            if (distanceFromPlayer > currentMaxHoldDistance)
                DropObject();
        }

        // --------------------------------------------------
        // HELPER METHODS
        // --------------------------------------------------

        private Vector3 GetHitWorldPoint()
        {
            // Use raycast hitpoint if available, otherwise object position
            Vector3 localHitpoint = interactController.LocalHitpoint;
            if (localHitpoint != Vector3.zero)
            {
                return raycastObject.transform.TransformPoint(localHitpoint);
            }

            return raycastObject.transform.position;
        }

        private void CreateHoldPoints(Vector3 hitWorldPoint)
        {
            // Common hold point in front of camera
            holdPoint = new GameObject("HoldPoint");
            holdPoint.transform.SetParent(VirtualCamera.transform);
            holdPoint.transform.position = VirtualCamera.transform.position + VirtualCamera.transform.forward * holdDistance;

            if (HitpointOffset)
            {
                // Rotate around the hit point
                holdRotatePoint = new GameObject("RotatePoint");
                holdRotatePoint.transform.SetParent(holdPoint.transform);
                holdRotatePoint.transform.position = hitWorldPoint;
                holdRotatePoint.transform.rotation = heldObjectRoot.transform.rotation;

                // Store offset from object to hit point
                holdOffset = hitWorldPoint - heldObjectRoot.transform.position;
            }
            else
            {
                // Rotate whole object from its center
                holdPoint.transform.rotation = heldObjectRoot.transform.rotation;
                holdRotatePoint = holdPoint;
                holdOffset = Vector3.zero;
            }
        }

        // --------------------------------------------------
        // RETICLE PROVIDER
        // --------------------------------------------------

        public (Type, Reticle, bool) OnProvideReticle()
        {
            Reticle reticle = isDragging ? HoldHand : GrabHand;

            if (!ShowGrabReticle)
                return (null, null, false);

            // Show reticle for objects that implement IDraggableObject
            return (typeof(IDraggableObject), reticle, isDragging);
        }
    }
}