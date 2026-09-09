using UnityEngine;
using UnityEngine.Events;
using UHFPS.Input;
using UHFPS.Tools;
using ThunderWire.Attributes;
using Newtonsoft.Json.Linq;
using Modules.Multiplayer.Bridge;

namespace UHFPS.Runtime
{
    public enum DynamicSoundType { Open, Close, Locked, Unlock }

    [Docs("https://docs.twgamesdev.com/uhfps/guides/dynamic-objects")]
    public class DynamicObject : MonoBehaviour, IInteractStartPlayer, IInteractHold, IInteractStop, ISaveable
    {
        public enum DynamicType { Openable, Pullable, Switchable, Rotable }
        public enum TransformType { Local, Global }
        public enum InteractType { Dynamic, Mouse, Animation }
        public enum DynamicStatus { Normal, Locked }
        public enum StatusChange { InventoryItem, CustomScript, Manual }

        // enums
        public DynamicType dynamicType = DynamicType.Openable;
        public TransformType transformType = TransformType.Local;
        public DynamicStatus dynamicStatus = DynamicStatus.Normal;
        public InteractType interactType = InteractType.Dynamic;
        public StatusChange statusChange = StatusChange.InventoryItem;

        // general
        public Transform target;
        public AudioSource audioSource;
        public Animator animator;
        public HingeJoint joint;
        public new Rigidbody rigidbody;
        public Inventory inventory;
        public GameManager gameManager;

        // MULTIPLAYER PATCH: set while waiting for the networked local player to spawn.
        private bool pendingIgnorePlayerCollider;

        // items
        public InterfaceReference<IDynamicUnlock> unlockScript;
        public InterfaceReference<IDynamicBarricade> barricadeScript;

        public ItemGuid unlockItem;
        public GString lockedText;
        public GString jammedText;

        public bool keepUnlockItem;
        public bool showLockedText;
        public bool showJammedText;
        public bool isBarricaded;

        public Collider[] ignoreColliders;
        public bool ignorePlayerCollider;

        public string useTrigger1 = "Open";
        public string useTrigger2 = "Close";
        public string useTrigger3 = "OpenSide";

        // dynamic types
        public DynamicOpenable openable = new();
        public DynamicPullable pullable = new();
        public DynamicSwitchable switchable = new();
        public DynamicRotable rotable = new();

        // sounds
        public SoundClip useSound1;
        public SoundClip useSound2;
        public SoundClip lockedSound;
        public SoundClip unlockSound;

        // events
        public UnityEvent useEvent1;
        public UnityEvent useEvent2;
        public UnityEvent<float> onValueChange;
        public UnityEvent lockedEvent;
        public UnityEvent unlockedEvent;

        // hidden variables
        public bool lockPlayer;
        public bool isInteractLocked;

        public DynamicObjectType CurrentDynamic
        {
            get => dynamicType switch
            {
                DynamicType.Openable => openable,
                DynamicType.Pullable => pullable,
                DynamicType.Switchable => switchable,
                DynamicType.Rotable => rotable,
                _ => null,
            };
        }

        public bool IsOpened => CurrentDynamic.IsOpened;
        public bool IsHolding => CurrentDynamic.IsHolding;
        public bool IsLocked { get; private set; }
        public bool IsJammed { get; private set; }

        // --------------------------------------------------
        // UNITY METHODS
        // --------------------------------------------------

        private void OnValidate()
        {
            openable.DynamicObject = this;
            pullable.DynamicObject = this;
            switchable.DynamicObject = this;
            rotable.DynamicObject = this;
        }

        private void Awake()
        {
            inventory = LocalPlayerContext.Inventory;
            gameManager = LocalPlayerContext.GameManager;

            if (dynamicStatus == DynamicStatus.Locked && !SaveGameManager.GameActuallyLoad)
            {
                // Main value for defining object as locked.
                IsLocked = true;
            }

            CurrentDynamic?.OnDynamicInit();

            lockedText.SubscribeGloc();
            jammedText.SubscribeGloc();
        }

        private void Start()
        {
            if (interactType == InteractType.Mouse)
            {
                Collider collider = GetComponent<Collider>();
                foreach (var col in ignoreColliders)
                {
                    Physics.IgnoreCollision(collider, col);
                }

                if (rigidbody != null && IsLocked)
                {
                    // Prevent opening dynamic object by dropping physics object on it
                    rigidbody.isKinematic = true;
                }
            }

            if (dynamicType == DynamicType.Pullable && ignorePlayerCollider)
            {
                // MULTIPLAYER PATCH: deferred — Netcode spawns the player after scene load, so the
                // collision pairing is retried from Update until the player exists.
                pendingIgnorePlayerCollider = !TryIgnorePlayerCollision();
            }
        }

        /// <summary>
        /// Stops the pullable object from colliding with the local player.
        /// </summary>
        /// <remarks>MULTIPLAYER PATCH. Returns false while no local player is spawned yet.</remarks>
        private bool TryIgnorePlayerCollision()
        {
            var localPlayer = LocalPlayerContext.Player;
            if (localPlayer == null) return false;

            Collider player = localPlayer.GetComponent<CharacterController>();
            Collider collider = GetComponent<Collider>();
            if (player == null || collider == null) return false;

            Physics.IgnoreCollision(player, collider);
            return true;
        }

        private void Update()
        {
            // MULTIPLAYER PATCH: one-shot retry, clears itself as soon as the player is available.
            if (pendingIgnorePlayerCollider) pendingIgnorePlayerCollider = !TryIgnorePlayerCollision();

            if (!IsLocked) CurrentDynamic?.OnDynamicUpdate();
            
            if (isBarricaded && barricadeScript.HasValue)
            {
                bool isCurrentlyJammed = barricadeScript.Value.CheckBarricaded();
                if (!isCurrentlyJammed && IsJammed)
                {
                    // Call unjammed event once
                    unlockedEvent?.Invoke();
                }
            
                IsJammed = isCurrentlyJammed;
            }
        }

        // --------------------------------------------------
        // PUBLIC API
        // --------------------------------------------------

        public void InteractStartPlayer(GameObject player)
        {
            if (IsJammed && showJammedText)
            {
                gameManager.ShowHintMessage(jammedText, 3f);
                return;
            }
            
            if (isInteractLocked || IsJammed) 
                return;
            
            PlayerManager playerManager = player.GetComponent<PlayerManager>();
            CurrentDynamic?.OnDynamicStart(playerManager);
        }

        public void InteractHold(Vector3 point)
        {
            Vector2 delta = InputManager.ReadInput<Vector2>(Controls.POINTER_DELTA);
            if (!IsLocked) CurrentDynamic?.OnDynamicHold(delta);
        }

        public void InteractStop()
        {
            if (IsLocked || IsJammed)
                return;
            
            CurrentDynamic?.OnDynamicEnd();
        }

        /// <summary>
        /// Set dynamic object locked status.
        /// </summary>
        public void SetLockedStatus(bool locked)
        {
            IsLocked = locked;
            if (!locked) isInteractLocked = false;
            
            if (rigidbody != null)
            {
                // Prevent opening dynamic object by dropping physics object on it
                rigidbody.isKinematic = locked;
            }
        }

        /// <summary>
        /// Set dynamic object open state.
        /// </summary>
        /// <remarks>
        /// The dynamic object opens as if you were interacting with it. If the dynamic interaction type is mouse, nothing happens.
        /// <br>This function is good for calling from an event.</br>
        /// </remarks>
        public void SetOpenState()
        {
            if (interactType == InteractType.Mouse || IsLocked || IsJammed)
                return;

            CurrentDynamic?.OnDynamicOpen();
        }

        /// <summary>
        /// Set dynamic object close state.
        /// </summary>
        /// <remarks>
        /// The dynamic object opens as if you were interacting with it. If the dynamic interaction type is mouse, nothing happens.
        /// <br>This function is good for calling from an event.</br>
        /// </remarks>
        public void SetCloseState()
        {
            if (interactType == InteractType.Mouse || IsLocked || IsJammed)
                return;

            CurrentDynamic?.OnDynamicClose();
        }

        /// <summary>
        /// Play Dynamic Object Sound.
        /// </summary>
        public void PlaySound(DynamicSoundType soundType)
        {
            switch (soundType)
            {
                case DynamicSoundType.Open: GameTools.PlayOneShot3D(target.position, useSound1, "Open Sound"); break;
                case DynamicSoundType.Close: GameTools.PlayOneShot3D(target.position, useSound2, "Close Sound"); break;
                case DynamicSoundType.Locked: GameTools.PlayOneShot3D(target.position, lockedSound, "Locked Sound"); break;
                case DynamicSoundType.Unlock: GameTools.PlayOneShot3D(target.position, unlockSound, "Unlock Sound"); break;
            }
        }

        // --------------------------------------------------
        // UNLOCK METHODS
        // --------------------------------------------------

        /// <summary>
        /// Try to unlock the dynamic object.
        /// </summary>
        public bool TryUnlock()
        {
            if (statusChange == StatusChange.InventoryItem)
            {
                if (unlockItem.InInventory)
                {
                    if (!keepUnlockItem)
                        inventory.RemoveItem(unlockItem);

                    SetLockedStatus(false);
                    unlockedEvent?.Invoke();
                    PlaySound(DynamicSoundType.Unlock);
                    return true;
                }
                else
                {
                    lockedEvent?.Invoke();
                    PlaySound(DynamicSoundType.Locked);
                    CurrentDynamic.OnDynamicLocked();

                    if (showLockedText)
                        gameManager.ShowHintMessage(lockedText, 3f);
                }
            }
            else if (statusChange == StatusChange.CustomScript && unlockScript != null)
            {
                if (unlockScript.HasValue)
                {
                    IDynamicUnlock dynamicUnlock = unlockScript.Value;
                    dynamicUnlock.OnTryUnlock(this);
                }
            }
            else
            {
                lockedEvent?.Invoke();
                PlaySound(DynamicSoundType.Locked);
                CurrentDynamic.OnDynamicLocked();

                if (showLockedText)
                    gameManager.ShowHintMessage(lockedText, 3f);
            }

            return false;
        }

        /// <summary>
        /// The result of using the custom unlock script function.
        /// <br>Call this function after using the <see cref="TryUnlock"/> function.</br>
        /// </summary>
        public void TryUnlockResult(bool unlocked)
        {
            if (unlocked)
            {
                unlockedEvent?.Invoke();
                PlaySound(DynamicSoundType.Unlock);
            }
            else
            {
                lockedEvent?.Invoke();
                PlaySound(DynamicSoundType.Locked);
            }

            SetLockedStatus(!unlocked);
        }

        // --------------------------------------------------
        // GIZMOS
        // --------------------------------------------------

        private void OnDrawGizmosSelected()
        {
            if (CurrentDynamic.ShowGizmos)
                CurrentDynamic?.OnDrawGizmos();
        }

        // --------------------------------------------------
        // SAVE/LOAD METHODS
        // --------------------------------------------------

        public StorableCollection OnSave()
        {
            StorableCollection saveableBuffer = new();

            switch (dynamicType)
            {
                case DynamicType.Openable:
                    saveableBuffer = openable.OnSave();
                    break;
                case DynamicType.Pullable:
                    saveableBuffer = pullable.OnSave();
                    break;
                case DynamicType.Switchable:
                    saveableBuffer = switchable.OnSave();
                    break;
                case DynamicType.Rotable:
                    saveableBuffer = rotable.OnSave();
                    break;
                default:
                    break;
            }

            saveableBuffer.Add("isLocked", IsLocked);
            return saveableBuffer;
        }

        public void OnLoad(JToken data)
        {
            switch (dynamicType)
            {
                case DynamicType.Openable:
                    openable.OnLoad(data);
                    break;
                case DynamicType.Pullable:
                    pullable.OnLoad(data);
                    break;
                case DynamicType.Switchable:
                    switchable.OnLoad(data);
                    break;
                case DynamicType.Rotable:
                    rotable.OnLoad(data);
                    break;
            }

            IsLocked = (bool)data["isLocked"];
        }
    }
}