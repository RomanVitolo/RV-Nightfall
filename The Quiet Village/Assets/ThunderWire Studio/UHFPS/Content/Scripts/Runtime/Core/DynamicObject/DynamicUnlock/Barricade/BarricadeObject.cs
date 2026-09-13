using System.Collections;
using UnityEngine;
using UnityEngine.Events;
using Newtonsoft.Json.Linq;
using UHFPS.Tools;
using QuietVillage.Multiplayer.Bridge;

namespace UHFPS.Runtime
{
    public class BarricadeObject : MonoBehaviour, ISaveable, IInteractStart, IInteractStop, IInteractDrag, IInteractTimed, IInteractIconSource, IDraggableObject
    {
        public enum EBreakStyle { Instant, Timed, Drag }

        /// <summary> Indicates whether the barricade object has been unblocked. </summary>
        public bool IsUnblocked { get; private set; }

        /// <summary> Indicates whether the barricade object can be dragged by the player. </summary>
        public bool AllowDragging => BreakStyle == EBreakStyle.Drag 
            && canGrabObject && IsUnblocked && KeepObjectInHandAfterUnblock;
        
        // --------------------------------------------------
        // VARIABLES
        // --------------------------------------------------

        public EBreakStyle BreakStyle = EBreakStyle.Timed;

        [Tooltip("Layer to assign to the barricade object once unblocked.")]
        public Layer UnblockedLayer = 0;
        [Tooltip("Defines the strength of the pull force applied when unblocking the barricade object.")]
        public float PullStrength = 1f;

        // --------------------------------------------------
        // TIMED INTERACT SETTINGS
        // --------------------------------------------------

        [field: SerializeField, Tooltip("Defines the time required to unblock the barricade object.")]
        public float InteractTime { get; set; }

        // --------------------------------------------------
        // DRAG INTERACT SETTINGS
        // --------------------------------------------------

        [Tooltip("Defines the amount of work required to unblock the barricade object by dragging.")]
        public MinMax RequiredPullWork = new(1f, 3f);
        [Tooltip("Defines how much effort is needed to pull the barricade object.")]
        public MinMax PullEffort = new(0f, 1f);
        [Tooltip("Defines the strength of the directional pull force applied when unblocking the barricade object by dragging.")]
        public MinMax DragPullStrength = new(1f, 2f);
        [Tooltip("Defines how the player look movement is resisted based on pull effort.")]
        public AnimationCurve PullResistance = AnimationCurve.Linear(0f, 1f, 1f, 0f);
        [Tooltip("Defines how quickly the player look returns to original position when drag is minimal.")]
        public float ReturnSmoothTime = 0.1f;
        [Tooltip("Defines the multiplier applied to the look resistance.")]
        [Range(0f, 1f)] public float ResistanceMultiplier = 1f;
        [Tooltip("Defines the threshold for returning the player look to original position.")]
        [Range(0f, 1f)] public float ReturnThreshold = 0.1f;
        [Tooltip("If enabled, the drag point will be used as the force application point.")]
        public bool UseDragPointForForce = false;
        [Tooltip("If enabled, the pull force will be summed with the drag direction when unblocking by dragging.")]
        public bool SumPullForceWithDirection = true;
        [Tooltip("If enabled, the barricade object will ignore collisions with the player after being unblocked.")]
        public bool IgnorePlayerCollision = true;
        
        // --------------------------------------------------
        // IDraggableObject
        // --------------------------------------------------
        
        [Tooltip("If enabled, the barricade object will remain in the player hand after being unblocked, allowing it to be thrown.")]
        public bool KeepObjectInHandAfterUnblock = false;
        [field: SerializeField] public float MaxHoldDistanceValue { get; set; }
        [field: SerializeField] public MinMax ZoomDistanceValue { get; set; }
        
        // --------------------------------------------------
        // CUSTOM INTERACT ICON
        // --------------------------------------------------

        public bool UseCustomInteractIcon = false;
        public bool DisableReticleWhileHolding = true;
        public Sprite HoldIcon;
        public Vector2 HoldSize;

        // --------------------------------------------------
        // SOUND SETTINGS
        // --------------------------------------------------

        [Tooltip("Audio clips played when the barricade object is unblocked.")]
        public AudioClip[] BreakSounds;
        [Range(0f, 1f)] public float BreakSoundVolume = 1f;

        [Tooltip("Audio source that plays cracking sounds while unblocking the barricade object.")]
        public AudioSource CrackingAudioSource;
        [Tooltip("Audio clips played as cracking sounds while unblocking the barricade object.")]
        public AudioClip[] CrackingSounds;
        [Tooltip("Minimum time before switching to a new cracking sound.")]
        public float MinCrackingTime = 1f;
        [Range(0f, 1f)] public float CrackingMaxVolume = 0.5f;

        // --------------------------------------------------
        // EVENTS
        // --------------------------------------------------

        public UnityEvent OnUnblocked;

        // --------------------------------------------------

        public bool NoInteract => IsUnblocked || BreakStyle == EBreakStyle.Instant;
        public float RequiredPullWorkValue => requiredPullWork;
        public float AccumulatedPullWorkValue => accumulatedPullWork;
        public bool IsHolding { get; private set; }

        private PlayerManager PlayerManager
        {
            get
            {
                if (playerManager == null && (LocalPlayerContext.Presence != null))
                    playerManager = LocalPlayerContext.Presence.PlayerManager;

                return playerManager;
            }
        }

        private LookController LookController => PlayerManager.LookController;

        private InteractIconModule interactIcon;
        private PlayerManager playerManager;
        private Rigidbody _rigidbody;

        private bool canGrabObject;
        private bool hasLookOriginal;
        private float originalMultiplierX;
        private float originalMultiplierY;

        private float crackingTime;
        private int lastCrackingSoundIndex = 0;

        private float requiredPullWork;
        private float accumulatedPullWork;
        private Vector2 originalLookRotation;
        private Vector3 dragPoint;

        // --------------------------------------------------

        private void Awake()
        {
            _rigidbody = GetComponent<Rigidbody>();
            interactIcon = GameManager.Module<InteractIconModule>();

            if (BreakStyle == EBreakStyle.Drag && !SaveGameManager.GameWillLoad)
            {
                // Set initial required pull work
                requiredPullWork = RequiredPullWork.Random();
            }

            if (CrackingAudioSource != null)
            {
                // Setup cracking audio source properties
                CrackingAudioSource.clip = CrackingSounds[0];
                CrackingAudioSource.playOnAwake = false;
                CrackingAudioSource.loop = true;
                CrackingAudioSource.volume = 0f;
            }
        }
        
        // --------------------------------------------------
        // CUSTOM INTERACT ICON
        // --------------------------------------------------

        public InteractIconData GetInteractIconData()
        {
            return new InteractIconData
            {
                IconPosition = dragPoint,
                Sprite = HoldIcon,
                Size = HoldSize
            };
        }

        // --------------------------------------------------
        // API METHODS
        // --------------------------------------------------

        public void InteractTimed()
        {
            if (IsUnblocked || BreakStyle != EBreakStyle.Timed)
                return;

            PullBarricadeObject(Vector3.zero);
            IsUnblocked = true;
        }

        public void InteractStart()
        {
            if (IsUnblocked || BreakStyle != EBreakStyle.Instant)
                return;

            PullBarricadeObject(Vector3.zero);
            IsUnblocked = true;
        }

        public void InteractStop()
        {
            if (UseCustomInteractIcon && interactIcon != null && BreakStyle == EBreakStyle.Drag)
            {
                interactIcon.DestroyInteractIcon(this);
                PlayerManager.InteractController.EnableInteractInfo(true);

                if (DisableReticleWhileHolding)
                {
                    PlayerManager.ReticleController.EnableReticle(true);
                }
            }

            // Stop cracking audio
            if (CrackingAudioSource != null)
                CrackingAudioSource.Stop();

            RestoreOriginalLook();
            LookController.ResetCustomLerp();
            dragPoint = Vector3.zero;
            IsHolding = false;
        }

        /// <summary>
        /// Called every frame while the player is holding USE and dragging on this object.
        /// </summary>
        /// <param name="point">World point where the player is interacting.</param>
        /// <param name="direction">Direction vector of the drag movement.</param>
        public void InteractDrag(Vector3 point, Vector3 direction)
        {
            dragPoint = point;
            if (IsUnblocked || BreakStyle != EBreakStyle.Drag)
                return;

            if (!hasLookOriginal)
            {
                if (UseCustomInteractIcon && interactIcon != null)
                {
                    interactIcon.ShowInteractIcon(this);
                    PlayerManager.InteractController.EnableInteractInfo(false);

                    if (DisableReticleWhileHolding)
                    {
                        PlayerManager.ReticleController.EnableReticle(false);
                    }
                }

                originalMultiplierX = LookController.MultiplierX;
                originalMultiplierY = LookController.MultiplierY;
                
                LookController.SetStartingLook();
                originalLookRotation = LookController.LookRotation;

                hasLookOriginal = true;
            }

            float pullEffort = direction.magnitude;             // How hard the player is pulling
            float pullValue = PullEffort.Weight(pullEffort);    // Normalized pull value (1-0)
            pullValue = Mathf.Clamp01(pullValue);

            // Resist look movement based on pull effort
            float resistanceValue = PullResistance.Evaluate(pullValue);
            resistanceValue *= ResistanceMultiplier;

            // Current input and current look
            Vector2 dragDelta = LookController.DeltaInput.normalized;
            Vector2 currentLook = LookController.LookRotation;
            Vector2 fromStart = currentLook - originalLookRotation;

            // Determine if the player is trying to move back toward the starting look
            // dot > 0  -> moving further away from starting look
            // dot < 0  -> moving back towards starting look
            float dot = Vector2.Dot(dragDelta, fromStart);
            bool returningToStart = dot < 0f;

            // Apply resistance only when moving further away from the starting look.
            float appliedResistance = returningToStart ? 1f : resistanceValue;

            LookController.MultiplierX = originalMultiplierX * appliedResistance;
            LookController.MultiplierY = originalMultiplierY * appliedResistance;

            // Return look to original position if drag is minimal
            if (Mathf.Abs(dragDelta.magnitude) < ReturnThreshold)
            {
                LookController.LerpTowardsStartingLook(ReturnSmoothTime);
            }

            // Accumulate pull work
            accumulatedPullWork += Time.deltaTime * (1f - pullValue);

            // Update cracking audio
            if (CrackingAudioSource != null && CrackingSounds.Length > 0)
            {
                float targetVolume = CrackingMaxVolume * (1f - pullValue);
                CrackingAudioSource.volume = targetVolume;

                if (!CrackingAudioSource.isPlaying)
                    CrackingAudioSource.Play();

                if (targetVolume > 0.01f)
                    crackingTime += Time.deltaTime;

                if (crackingTime >= MinCrackingTime && targetVolume <= 0.01f)
                {
                    int newSoundIndex = GameTools.RandomUnique(0, CrackingSounds.Length, lastCrackingSoundIndex);
                    if (newSoundIndex != lastCrackingSoundIndex)
                    {
                        CrackingAudioSource.clip = CrackingSounds[newSoundIndex];
                        lastCrackingSoundIndex = newSoundIndex;
                        crackingTime = 0f;
                    }
                }
            }

            // Check if enough pull work has been done to unblock
            if (accumulatedPullWork >= requiredPullWork)
            {
                float normalPullValue = Mathf.Abs(1 - pullValue);
                float strength = DragPullStrength.Lerp(normalPullValue);
                Vector3 addForce = direction.normalized * strength;
                PullBarricadeObject(addForce);
            }

            IsHolding = true;
        }

        // --------------------------------------------------
        // INTERNAL METHODS
        // --------------------------------------------------

        private void PullBarricadeObject(Vector3 addForce)
        {
            // Called when the barricade object is unblocked, either by interaction or by dragging.
            
            if (IsUnblocked) return;
            IsUnblocked = true;

            bool keepInHand = BreakStyle == EBreakStyle.Drag && KeepObjectInHandAfterUnblock;
            if (_rigidbody != null && !keepInHand)
            {
                Vector3 cameraPos = PlayerManager.MainVirtualCamera.transform.position;
                Vector3 objectPos = transform.position;

                Vector3 pullDirection = (cameraPos - objectPos).normalized;
                Vector3 pullForce = pullDirection * PullStrength;
                Vector3 finalForce = SumPullForceWithDirection ? pullForce + addForce
                    : addForce != Vector3.zero ? addForce : pullForce;

                Debug.DrawRay(objectPos, pullDirection, Color.red, 5f);
                Debug.DrawRay(objectPos, finalForce, Color.green, 5f);

                _rigidbody.isKinematic = false;
                _rigidbody.useGravity = true;

                Vector3 forcePoint = UseDragPointForForce ? dragPoint : objectPos;

                StopAllCoroutines();
                StartCoroutine(ApplyPullForce(forcePoint, finalForce));
                dragPoint = Vector3.zero;
            }

            // Audio
            if (BreakSounds != null && BreakSounds.Length > 0)
            {
                int randomIndex = Random.Range(0, BreakSounds.Length);
                AudioSource.PlayClipAtPoint(BreakSounds[randomIndex], transform.position, BreakSoundVolume);
            }
            
            // Change layer & unparent
            gameObject.layer = UnblockedLayer;
            gameObject.transform.parent = null;
            
            // Ignore player collision
            IgnorePlayerCollisions();

            if (BreakStyle == EBreakStyle.Drag)
            {
                RestoreOriginalLook();
                LookController.ResetCustomLerp();

                if (UseCustomInteractIcon && interactIcon != null)
                {
                    interactIcon.DestroyInteractIcon(this);
                    PlayerManager.InteractController.EnableInteractInfo(true);

                    if (DisableReticleWhileHolding)
                    {
                        PlayerManager.ReticleController.EnableReticle(true);
                    }
                }
                
                if (_rigidbody != null && KeepObjectInHandAfterUnblock)
                {
                    _rigidbody.isKinematic = false;
                    _rigidbody.useGravity = true;
                    dragPoint = Vector3.zero;
                
                    // Grab object with DragRigidbody module
                    PlayerManager.DragRigidbody.GrabObject(gameObject, true);
                }
            }
            
            // Stop cracking audio
            if (CrackingAudioSource != null) 
                CrackingAudioSource.Stop();
            
            OnUnblocked?.Invoke();
            canGrabObject = true;
            IsHolding = false;
        }

        IEnumerator ApplyPullForce(Vector3 point, Vector3 force)
        {
            yield return new WaitForEndOfFrame();
            _rigidbody.AddForceAtPosition(force, point, ForceMode.Impulse);
        }

        private void RestoreOriginalLook()
        {
            if (LookController == null || !hasLookOriginal)
                return;

            LookController.MultiplierX = originalMultiplierX;
            LookController.MultiplierY = originalMultiplierY;
            hasLookOriginal = false;
        }

        private void IgnorePlayerCollisions()
        {
            if (!IgnorePlayerCollision)
                return;
            
            Collider playerCollider = PlayerManager.PlayerCollider;
            Collider[] objectColliders = GetComponentsInChildren<Collider>();

            foreach (Collider col in objectColliders)
            {
                Physics.IgnoreCollision(col, playerCollider, true);
            }
        }

        // --------------------------------------------------
        // SAVE/LOAD
        // --------------------------------------------------

        public StorableCollection OnSave()
        {
            StorableCollection saveables = new();
            saveables.Add("isUnblocked", IsUnblocked);
            saveables.Add("requiredPullWork", requiredPullWork);
            saveables.Add("accumulatedPullWork", accumulatedPullWork);

            saveables.StoreTransform(transform);
            saveables.StoreRigidbody(_rigidbody);
            return saveables;
        }

        public void OnLoad(JToken data)
        {
            IsUnblocked = (bool)data["isUnblocked"];
            requiredPullWork = (float)data["requiredPullWork"];
            accumulatedPullWork = (float)data["accumulatedPullWork"];

            if (IsUnblocked)
            {
                transform.SetParent(null);
                IgnorePlayerCollisions();
            }

            data.LoadTransform(transform);
            data.LoadRigidbody(_rigidbody);
        }
    }
}