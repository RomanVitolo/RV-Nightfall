using UnityEngine;
using UHFPS.Tools;
using UHFPS.Scriptable;
using Newtonsoft.Json.Linq;
using QuietVillage.Multiplayer.Bridge;

namespace UHFPS.Runtime
{
    public abstract class PlayerItemBehaviour : MonoBehaviour, ISaveableCustom
    {
        // --------------------------------------------------
        // Private References
        // --------------------------------------------------
        
        private Animator animator;
        private PlayerManager playerManager;
        private PlayerStateMachine playerStateMachine;
        private LookController lookController;
        private ExamineController examineController;
        private MotionController motionController;
        private Vector3 wallHitVel;

        private Transform motionTransform;
        private Quaternion defaultMotionRot;
        private Vector3 defaultMotionPos;

        // --------------------------------------------------
        // Toggles
        // --------------------------------------------------
        
        public bool EnableWallDetection = true;
        public bool EnableMotionPreset = true;
        public bool EnableExternalMotion = false;
        public bool EnableHintControls = false;

        // --------------------------------------------------
        // Wall Detection
        // --------------------------------------------------
        
        public Transform WallHitTransform;
        public LayerMask WallHitMask;
        public float WallHitRayDistance = 0.5f;
        public float WallHitRayRadius = 0.3f;
        public float WallHitAmount = 1f;
        public float WallHitTime = 0.2f;
        public Vector3 WallHitRayOffset;
        public bool ShowRayGizmos = true;

        // --------------------------------------------------
        // Item Motion
        // --------------------------------------------------
        
        public MotionBlender MotionBlender = new();
        public MotionPreset MotionPreset;
        public ExternalMotions ExternalMotions = new();

        // --------------------------------------------------
        // Control Hints
        // --------------------------------------------------
        
        public ControlInfo[] HintControls;
        
        // --------------------------------------------------
        // Properties
        // --------------------------------------------------
        
        /// <summary>
        /// The pivot point of the item object that will be used for the motion preset effects.
        /// </summary>
        [field: SerializeField]
        public Transform MotionPivot { get; set; }

        /// <summary>
        /// The object of the item which will be enabled or disabled, usually a child object.
        /// </summary>
        [field: SerializeField]
        public GameObject ItemObject { get; set; }

        /// <summary>
        /// Root player transform.
        /// </summary>
        public Transform PlayerRoot => PlayerManager.transform;

        public Vector3 LookForward2D => LookController.LookForward2D;
        public Vector3 LookForward => LookController.LookForward;
        public Vector3 LookCross => LookController.LookCross;

        // --------------------------------------------------
        // Proxy References
        // --------------------------------------------------
        
        #region Proxy References
        /// <summary>
        /// Ray going from main camera to forward.
        /// </summary>
        public Ray CameraRay
        {
            get
            {
                Transform mainCamera = PlayerManager.MainCamera.transform;
                return new Ray(mainCamera.position, mainCamera.forward);
            }
        }

        /// <summary>
        /// Animator component of the Item Object.
        /// </summary>
        public Animator Animator
        {
            get
            {
                if(animator == null)
                    animator = ItemObject.GetComponentInChildren<Animator>();

                return animator;
            }
        }

        /// <summary>
        /// PlayerManager component.
        /// </summary>
        public PlayerManager PlayerManager
        {
            get
            {
                if (playerManager == null)
                    playerManager = GetComponentInParent<PlayerManager>();

                return playerManager;
            }
        }

        /// <summary>
        /// PlayerStateMachine component.
        /// </summary>
        public PlayerStateMachine PlayerStateMachine
        {
            get
            {
                if (playerStateMachine == null)
                    playerStateMachine = PlayerManager.GetComponent<PlayerStateMachine>();

                return playerStateMachine;
            }
        }

        /// <summary>
        /// LookController component.
        /// </summary>
        public LookController LookController
        {
            get
            {
                if (lookController == null)
                    lookController = PlayerManager.GetComponentInChildren<LookController>();

                return lookController;
            }
        }

        /// <summary>
        /// ExamineController component.
        /// </summary>
        public ExamineController ExamineController
        {
            get
            {
                if (examineController == null)
                    examineController = PlayerManager.GetComponentInChildren<ExamineController>();

                return examineController;
            }
        }

        /// <summary>
        /// MotionController component.
        /// </summary>
        public MotionController CameraMotions
        {
            get
            {
                if (motionController == null)
                    motionController = PlayerManager.GetComponentInChildren<MotionController>();

                return motionController;
            }
        }

        /// <summary>
        /// PlayerItemsManager component.
        /// </summary>
        public PlayerItemsManager PlayerItems
        {
            get => PlayerManager.PlayerItems;
        }
        #endregion

        // --------------------------------------------------
        // Equipment Properties
        // --------------------------------------------------
        
        /// <summary>
        /// Check if the item is interactive. False, for example when the inventory is opened, object is dragged etc.
        /// </summary>
        public bool CanInteract => PlayerItems.CanInteract;

        /// <summary>
        /// The name of the item that will be displayed in the list.
        /// </summary>
        public virtual string Name => "Item";

        /// <summary>
        /// Check whether the item can be switched.
        /// </summary>
        public virtual bool IsBusy() => false;

        /// <summary>
        /// Check whether the item is equipped.
        /// </summary>
        public virtual bool IsEquipped() => ItemObject.activeSelf;

        /// <summary>
        /// Check whether the item can be combined in inventory.
        /// </summary>
        public virtual bool CanCombine() => false;

        // MULTIPLAYER PATCH: UHFPS items fire, swing and reload inside their own update logic and
        // expose nothing a third-person body could follow, so remote players saw no reaction at all.
        // This is the single seam the bridge's PlayerActionSync listens to. Items raise it on the same
        // line that triggers their first-person animation, i.e. only once UHFPS has committed to the
        // action — never for a dry click on an empty magazine or a swing still on cooldown.
        public enum ItemAction { Shoot, Reload, Attack }

        /// <summary>Raised on the owning client when this item performs an action other players should see.</summary>
        public event System.Action<ItemAction> ActionPerformed;

        protected void RaiseActionPerformed(ItemAction action) => ActionPerformed?.Invoke(action);

        // --------------------------------------------------
        // Unity Methods
        // --------------------------------------------------
        
        public virtual void Reset()
        {
            if (WallHitTransform == null)
                WallHitTransform = transform;
        }

        public virtual void Start()
        {
            if (WallHitTransform == null)
                WallHitTransform = transform;

            if (EnableMotionPreset && MotionPreset != null)
            {
                motionTransform = MotionPivot != null ? MotionPivot : CameraMotions.HandsMotionTransform;
                defaultMotionRot = motionTransform.localRotation;
                defaultMotionPos = motionTransform.localPosition;
                MotionBlender.Init(MotionPreset, motionTransform, PlayerStateMachine);
            }

            if (EnableExternalMotion) 
                ExternalMotions.Init(CameraMotions);
            
            foreach (var item in HintControls)
            {
                item.Text.SubscribeGloc();
            }
        }

        public virtual void OnDestroy()
        {
            if (EnableMotionPreset && MotionPreset != null && MotionBlender != null)
                MotionBlender.Dispose();
        }

        private void Update()
        {
            if (IsEquipped())
            {
                if (EnableWallDetection)
                {
                    Vector3 forward = PlayerItems.transform.forward;
                    Vector3 origin = PlayerItems.transform.TransformPoint(WallHitRayOffset);

                    if (Physics.SphereCast(origin, WallHitRayRadius, forward, out RaycastHit hit, WallHitRayDistance, WallHitMask, QueryTriggerInteraction.Ignore))
                        OnItemBlocked(hit.distance, true);
                    else
                        OnItemBlocked(0f, false);
                }

                if (EnableMotionPreset && MotionPreset != null && motionTransform != null)
                {
                    MotionBlender.BlendMotions(Time.deltaTime, out var position, out var rotation);
                    Vector3 newPosition = defaultMotionPos + position;
                    Quaternion newRotation = defaultMotionRot * rotation;
                    motionTransform.SetLocalPositionAndRotation(newPosition, newRotation);
                }
            }

            OnUpdate();
        }
        
        // --------------------------------------------------
        // Public API
        // --------------------------------------------------

        /// <summary>
        /// Apply an external camera motion effect, such as an wobble or impact.
        /// </summary>
        public void ApplyEffect(string eventID)
        {
            if (!EnableExternalMotion || ExternalMotions == null)
                return;

            ExternalMotions.ApplyEffect(eventID);
        }

        /// <summary>
        /// Will be called when the ray going from the camera hits the wall to prevent the player item from being clipped.
        /// </summary>
        public virtual void OnItemBlocked(float hitDistance, bool blocked)
        {
            float value = GameTools.Remap(0f, WallHitRayDistance, 0f, 1f, hitDistance);
            Vector3 backward = Vector3.back * WallHitAmount;
            Vector3 result = Vector3.Lerp(backward, Vector3.zero, blocked ? value : 1f);
            WallHitTransform.localPosition = Vector3.SmoothDamp(WallHitTransform.localPosition, result, ref wallHitVel, WallHitTime);
        }
        
        /// <summary>
        /// Enables the controls info for the item at specified indexes.
        /// </summary>
        public void EnableControlsInfo(params int[] indexes)
        {
            if (HintControls.Length == 0 || indexes == null || indexes.Length == 0)
                return;

            foreach (var index in indexes)
            {
                if (index < 0 || index >= HintControls.Length)
                    continue;

                var control = HintControls[index];
                if (string.IsNullOrEmpty(control.Input.ActionName))
                    continue;
                if (control.IsEnabled)
                    continue;

                control.IsEnabled = true;
            }

            LocalPlayerContext.ResolveGameManager(this).UpdateHintControls();
        }

        /// <summary>
        /// Disables the controls info for the item at specified indexes.
        /// </summary>
        public void DisableControlsInfo(params int[] indexes)
        {
            if (HintControls.Length == 0 || indexes == null || indexes.Length == 0)
                return;

            foreach (var index in indexes)
            {
                if (index < 0 || index >= HintControls.Length)
                    continue;

                var control = HintControls[index];
                if (string.IsNullOrEmpty(control.Input.ActionName))
                    continue;
                if (!control.IsEnabled)
                    continue;

                control.IsEnabled = false;
            }

            LocalPlayerContext.ResolveGameManager(this).UpdateHintControls();
        }

        // --------------------------------------------------
        // Manager Events
        // --------------------------------------------------

        public void OnSelect()
        {
            if (EnableHintControls)
                LocalPlayerContext.ResolveGameManager(this).ShowHintControls(HintControls);
            
            OnItemSelect();
        }

        public void OnDeselect()
        {
            if (EnableHintControls)
                LocalPlayerContext.ResolveGameManager(this).HideHintControls();
            
            OnItemDeselect();
        }

        public void OnActivate()
        {
            foreach (var item in HintControls)
            {
                item.Text.SubscribeGloc();
            }
            
            if (EnableHintControls)
                LocalPlayerContext.ResolveGameManager(this).ShowHintControls(HintControls);
            
            OnItemActivate();
        }

        public void OnDeactivate()
        {
            if (EnableHintControls)
                LocalPlayerContext.ResolveGameManager(this).HideHintControls();
            
            OnItemDeactivate();
        }
        
        // --------------------------------------------------
        // Equipment Events
        // --------------------------------------------------

        /// <summary>
        /// Will be called every frame like the classic Update() function. 
        /// </summary>
        public virtual void OnUpdate() { }

        /// <summary>
        /// Will be called when a combinable item is combined with this inventory item.
        /// </summary>
        public virtual void OnItemCombine(InventoryItem combineItem) { }

        /// <summary>
        /// Will be called when PlayerItemsManager selects an item.
        /// </summary>
        public abstract void OnItemSelect();

        /// <summary>
        /// Will be called when PlayerItemsManager deselects an item.
        /// </summary>
        public abstract void OnItemDeselect();

        /// <summary>
        /// Will be called when PlayerItemsManager activates an item.
        /// </summary>
        public abstract void OnItemActivate();

        /// <summary>
        /// Will be called when PlayerItemsManager deactivates an item.
        /// </summary>
        public abstract void OnItemDeactivate();
        
        // --------------------------------------------------
        // Saving/Loading
        // --------------------------------------------------

        public virtual StorableCollection OnCustomSave()
        {
            return new();
        }

        public virtual void OnCustomLoad(JToken data) { }
        
        // --------------------------------------------------
        // Gizmos
        // --------------------------------------------------
        
        public virtual void OnDrawGizmosSelected()
        {
            bool selected = false;

#if UNITY_EDITOR
            selected = UnityEditor.Selection.activeGameObject == gameObject;
#endif

            if (ShowRayGizmos && EnableWallDetection && selected)
            {
                Vector3 forward = PlayerItems.transform.forward;
                Vector3 origin = PlayerItems.transform.TransformPoint(WallHitRayOffset);
                Vector3 p2 = origin + forward * WallHitRayDistance;

                Gizmos.color = Color.yellow;
                GizmosE.DrawWireCapsule(origin, p2, WallHitRayRadius);
            }
        }
    }
}