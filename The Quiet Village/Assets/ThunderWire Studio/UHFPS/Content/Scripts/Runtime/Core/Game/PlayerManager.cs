using UnityEngine;
using Unity.Cinemachine;
using Newtonsoft.Json.Linq;
using ThunderWire.Attributes;
using UHFPS.Input;
using QuietVillage.Multiplayer.Bridge;

namespace UHFPS.Runtime
{
    [InspectorHeader("Player Manager", space = false)]
    public class PlayerManager : MonoBehaviour, ISaveableCustom
    {
        [Header("Player References")]
        public Transform CameraHolder;

        [SerializeField] private Camera m_mainCamera;

        /// <summary>The camera this client renders the player through.</summary>
        /// <remarks>
        /// MULTIPLAYER PATCH: was a plain serialized field pointing at a scene camera. A prefab cannot
        /// hold a scene reference, so on a spawned player it is empty — and consumers such as
        /// <c>CutsceneModule.OnAwake</c> and <c>ReticleController.Awake</c> dereference it during
        /// Instantiate, long before anything can assign it. Resolving lazily through the scene's
        /// CinemachineBrain covers that window. Not <c>Camera.main</c>: the brain camera is untagged.
        /// </remarks>
        public Camera MainCamera
        {
            get
            {
                if (m_mainCamera == null)
                {
                    var brain = FindFirstObjectByType<CinemachineBrain>(FindObjectsInactive.Exclude);
                    if (brain != null) m_mainCamera = brain.OutputCamera;
                }

                return m_mainCamera;
            }
            set => m_mainCamera = value;
        }

        public CinemachineCamera MainVirtualCamera;

        [Header("Load Options")]
        public bool LoadSelectedItem;

        // MULTIPLAYER PATCH: the static Instance accessor is gone. It resolved "the" player, which no
        // longer exists — use LocalPlayerContext.PlayerManager for this client's player, or walk up
        // the hierarchy (as PlayerComponent does) when you are already on a player.

        private CharacterController m_PlayerCollider;
        public CharacterController PlayerCollider
        {
            get
            {
                if (m_PlayerCollider == null)
                    m_PlayerCollider = GetComponent<CharacterController>();

                return m_PlayerCollider;
            }
        }

        private PlayerStateMachine m_PlayerStateMachine;
        public PlayerStateMachine PlayerStateMachine
        {
            get
            {
                if (m_PlayerStateMachine == null)
                    m_PlayerStateMachine = GetComponent<PlayerStateMachine>();

                return m_PlayerStateMachine;
            }
        }

        private PlayerHealth m_PlayerHealth;
        public PlayerHealth PlayerHealth
        {
            get
            {
                if (m_PlayerHealth == null)
                    m_PlayerHealth = GetComponent<PlayerHealth>();

                return m_PlayerHealth;
            }
        }

        private InteractController m_InteractController;
        public InteractController InteractController
        {
            get
            {
                if (m_InteractController == null)
                    m_InteractController = GetComponentInChildren<InteractController>();

                return m_InteractController;
            }
        }

        private ReticleController m_ReticleController;
        public ReticleController ReticleController
        {
            get
            {
                if (m_ReticleController == null)
                    m_ReticleController = GetComponentInChildren<ReticleController>();

                return m_ReticleController;
            }
        }

        private LookController m_LookController;
        public LookController LookController
        {
            get
            {
                if (m_LookController == null)
                    m_LookController = GetComponentInChildren<LookController>();

                return m_LookController;
            }
        }

        private ExamineController m_ExamineController;
        public ExamineController ExamineController
        {
            get
            {
                if (m_ExamineController == null)
                    m_ExamineController = GetComponentInChildren<ExamineController>();

                return m_ExamineController;
            }
        }
        
        private DragRigidbody m_DragRigidbody;
        public DragRigidbody DragRigidbody
        {
            get
            {
                if (m_DragRigidbody == null)
                    m_DragRigidbody = GetComponentInChildren<DragRigidbody>();
                
                return m_DragRigidbody;
            }
        }

        private PlayerItemsManager m_PlayerItems;
        public PlayerItemsManager PlayerItems
        {
            get
            {
                if (m_PlayerItems == null)
                    m_PlayerItems = GetComponentInChildren<PlayerItemsManager>();

                return m_PlayerItems;
            }
        }

        private MotionController m_MotionController;
        public MotionController MotionController
        {
            get
            {
                if (m_MotionController == null)
                    m_MotionController = GetComponentInChildren<MotionController>();

                return m_MotionController;
            }
        }

        private void Start()
        {
            if (!SaveGameManager.GameWillLoad || !SaveGameManager.GameStateExist)
            {
                // transfer player rotation to look rotation
                Vector3 rotation = transform.eulerAngles;
                if(LookController.PlayerForward == LookController.ForwardStyle.LookForward)
                    transform.rotation = Quaternion.identity;
                LookController.LookRotation.x = rotation.y;
            }
        }

        private void Update()
        {
            // keep the player rotation unchanged when PlayerForward is set to LookForward
            if (LookController.PlayerForward == LookController.ForwardStyle.LookForward)
                transform.rotation = Quaternion.identity;
        }

        /// <summary>
        /// Parent player to object.
        /// </summary>
        public void ParentToObject(Transform parent, bool autoSync = true)
        {
            Physics.autoSyncTransforms = autoSync;
            LookController.ParentToObject(parent);
            transform.SetParent(parent);
        }

        /// <summary>
        /// Unparent player from object.
        /// </summary>
        public void UnparentFromObject()
        {
            Physics.autoSyncTransforms = false;
            LookController.UnparentFromObject();
            transform.SetParent(null);
        }

        /// <summary>
        /// This function is used to collect all local player data to be saved.
        /// </summary>
        public StorableCollection OnCustomSave()
        {
            StorableCollection data = new StorableCollection();
            data.Add("health", PlayerHealth.EntityHealth);
            data.Add("crouched", PlayerStateMachine.StateCrouched);

            StorableCollection playerItemsData = new StorableCollection();
            for (int i = 0; i < PlayerItems.PlayerItems.Count; i++)
            {
                var playerItem = PlayerItems.PlayerItems[i];
                var itemData = (playerItem as ISaveableCustom).OnCustomSave();
                playerItemsData.Add("playerItem_" + i, itemData);
            }

            if(LoadSelectedItem) data.Add("selectedItem", PlayerItems.CurrentItemIndex);
            data.Add("playerItems", playerItemsData);
            return data;
        }

        /// <summary>
        /// This function is used to load all stored local player data.
        /// </summary>
        public void OnCustomLoad(JToken data)
        {
            PlayerHealth.StartHealth = data["health"].ToObject<uint>();
            PlayerHealth.InitHealth();

            bool crouched = data["crouched"].ToObject<bool>();
            if (crouched)
            {
                PlayerStateMachine.ChangeState(PlayerStateMachine.CROUCH_STATE, true);
                if (PlayerStateMachine.PlayerFeatures.CrouchToggle)
                {
                    // set crouch toggle button to true so the player won't stand up immediately after loading
                    InputManager.SetButtonToggle("Crouch", Controls.CROUCH, true);
                }
            }
            
            for (int i = 0; i < PlayerItems.PlayerItems.Count; i++)
            {
                var playerItem = PlayerItems.PlayerItems[i];
                var itemData = data["playerItems"]["playerItem_" + i];
                (playerItem as ISaveableCustom).OnCustomLoad(itemData);
            }

            if (LoadSelectedItem)
            {
                int itemIndex = (int)data["selectedItem"];
                if(itemIndex != -1) PlayerItems.ActivateItem(itemIndex);
            }
        }
    }
}