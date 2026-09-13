using UnityEngine;
using UHFPS.Input;
using UHFPS.Tools;
using ThunderWire.Attributes;
using static UHFPS.Runtime.InteractableItem;
using QuietVillage.Multiplayer.Bridge;

namespace UHFPS.Runtime
{
    [InspectorHeader("Interact Controller")]
    public class InteractController : PlayerComponent
    {
        [Header("Raycast")]
        public float RaycastRange = 3f;
        public float HoldDistance = 4f;
        public LayerMask CullLayers;
        public Layer InteractLayer;

        [Header("Settings")]
        public bool ShowLootedText;
        public bool ShowDefaultPickupIcon;
        public Sprite DefaultPickupIcon;

        [Header("Interact Settings")]
        public InputReference UseAction;
        public InputReference ExamineAction;

        [Header("Interact Texts")]
        public GString InteractText;
        public GString ExamineText;
        public GString LootText;

        public GameObject RaycastObject => raycastObject;
        public Vector3 LocalHitpoint => localHitpoint;

        private Inventory inventory;
        private GameManager gameManager;
        private PlayerStateMachine player;
        private ReticleController reticle;

        private GameObject raycastObject;
        private GameObject lastRaycastObject;

        private GameObject interactableObject;
        private Transform holdPointObject;

        private bool isPressed;
        private bool isHolding;
        private bool isDynamic;
        private bool isTimed;
        private bool showInteractInfo = true;

        private IInteractTimed timedInteract;
        private float reqHoldTime;
        private float holdTime;

        private Vector3 localHitpoint;

        private void Awake()
        {
            inventory = LocalPlayerContext.ResolveInventory(this);
            gameManager = LocalPlayerContext.ResolveGameManager(this);
            player = PlayerCollider.GetComponent<PlayerStateMachine>();
            reticle = GetComponent<ReticleController>();
        }

        private void Start()
        {
            InteractText.SubscribeGlocMany();
            ExamineText.SubscribeGlocMany();
            LootText.SubscribeGloc();
        }

        public void EnableInteractInfo(bool state)
        {
            if (!state) gameManager.InteractInfoPanel.HideInfo();
            showInteractInfo = state;
        }

        public void ResetInteract()
        {
            gameManager.InteractInfoPanel.HideInfo();
            reticle.ResetReticle();
            interactableObject = null;
            raycastObject = null;
            isHolding = false;
            isPressed = false;
        }

        private void Update()
        {
            if (!isEnabled && !isHolding)
                return;

            Ray playerAim = MainCamera.ViewportPointToRay(new Vector3(0.5f, 0.5f, 0));
            if (GameTools.Raycast(playerAim, out RaycastHit hit, RaycastRange, CullLayers, InteractLayer))
            {
                raycastObject = hit.collider.gameObject;

                if (!isHolding)
                {
                    interactableObject = raycastObject;
                    localHitpoint = interactableObject.transform.InverseTransformPoint(hit.point);
                }

                if (raycastObject != null)
                {
                    if (lastRaycastObject != null && raycastObject != lastRaycastObject)
                    {
                        foreach (var hoverEnd in lastRaycastObject.GetComponents<IHoverEnd>())
                        {
                            hoverEnd.HoverEnd();
                        }
                    }

                    foreach (var hoverStart in raycastObject.GetComponents<IHoverStart>())
                    {
                        hoverStart.HoverStart();
                    }

                    lastRaycastObject = raycastObject;
                }

                if (showInteractInfo) OnInteractGUI();
            }
            else
            {
                if (isTimed)
                {
                    gameManager.ShowInteractProgress(false, 0f);
                    timedInteract = null;
                    reqHoldTime = 0f;
                    holdTime = 0f;
                    isTimed = false;
                }

                if (raycastObject != null)
                {
                    foreach (var hoverEnd in raycastObject.GetComponents<IHoverEnd>())
                    {
                        hoverEnd.HoverEnd();
                    }
                }

                gameManager.InteractInfoPanel.HideInfo();
                raycastObject = null;
            }

            if (InputManager.ReadButton(Controls.USE))
            {
                isHolding = true;

                if (interactableObject)
                {
                    if (!isPressed)
                    {
                        foreach (var interactStartPlayer in interactableObject.GetComponents<IInteractStartPlayer>())
                        {
                            interactStartPlayer.InteractStartPlayer(transform.root.gameObject);
                        }

                        foreach (var interactStart in interactableObject.GetComponents<IInteractStart>())
                        {
                            interactStart.InteractStart();
                        }

                        if (interactableObject.TryGetComponent(out IInteractTimed timedInteract))
                        {
                            if (!timedInteract.NoInteract)
                            {
                                this.timedInteract = timedInteract;
                                reqHoldTime = timedInteract.InteractTime;
                                isTimed = true;
                            }
                        }

                        if (interactableObject.TryGetComponent(out IStateInteract stateInteract))
                        {
                            StateParams stateParams = stateInteract.OnStateInteract();
                            if (stateParams != null) player.ChangeState(stateParams.stateKey, stateParams.stateData);
                        }

                        if (interactableObject.TryGetComponent(out DynamicObject dynamicObject))
                        {
                            if ((dynamicObject.interactType == DynamicObject.InteractType.Mouse || dynamicObject.lockPlayer) && !dynamicObject.IsLocked)
                            {
                                gameManager.FreezePlayer(true);
                                isDynamic = true;
                            }
                        }

                        Interact(interactableObject);
                        isPressed = true;
                    }

                    IInteractHold[] interactHolds = interactableObject.GetComponents<IInteractHold>();
                    IInteractDrag[] interactDrags = interactableObject.GetComponents<IInteractDrag>();

                    if (interactHolds.Length > 0 || interactDrags.Length > 0)
                    {
                        if (holdPointObject == null && RaycastObject != null)
                        {
                            GameObject holdPointObj = new("HoldPoint");
                            holdPointObj.transform.parent = RaycastObject.transform;
                            holdPointObj.transform.position = RaycastObject.transform.TransformPoint(localHitpoint);
                            holdPointObject = holdPointObj.transform;
                        }
                        else if (holdPointObject != null)
                        {
                            Vector3 holdPoint = holdPointObject.position;

                            foreach (var interactHold in interactHolds)
                            {
                                interactHold.InteractHold(holdPoint);
                            }

                            if (interactDrags.Length > 0)
                            {
                                float distanceToHoldPoint = Vector3.Distance(MainCamera.transform.position, holdPoint);
                                Vector3 fromHoldPointPos = playerAim.GetPoint(distanceToHoldPoint);
                                Vector3 dragDirection = fromHoldPointPos - holdPoint;

                                foreach (var interactDrag in interactDrags)
                                {
                                    interactDrag.InteractDrag(holdPoint, dragDirection);
                                }
                            }
                        }
                    }
                }
            }
            else if (isPressed)
            {
                if (isTimed)
                {
                    gameManager.ShowInteractProgress(false, 0f);
                    timedInteract = null;
                    reqHoldTime = 0f;
                    holdTime = 0f;
                }

                if (interactableObject)
                {
                    foreach (var interactStop in interactableObject.GetComponents<IInteractStop>())
                    {
                        interactStop.InteractStop();
                    }
                }

                isTimed = false;
                isPressed = false;
            }
            else
            {
                if (isDynamic)
                {
                    gameManager.FreezePlayer(false);
                    isDynamic = false;
                }

                if (holdPointObject)
                {
                    Destroy(holdPointObject.gameObject);
                    holdPointObject = null;
                }

                interactableObject = null;
                isHolding = false;
            }

            if (isPressed && isTimed)
            {
                if (holdTime < reqHoldTime)
                {
                    holdTime += Time.deltaTime;
                    float progress = Mathf.InverseLerp(0f, reqHoldTime, holdTime);
                    gameManager.ShowInteractProgress(true, progress);
                }
                else
                {
                    gameManager.ShowInteractProgress(false, 0f);
                    timedInteract.InteractTimed();
                    timedInteract = null;
                    reqHoldTime = 0f;
                    holdTime = 0f;
                    isTimed = false;
                }
            }

            if (isPressed && holdPointObject && interactableObject)
            {
                float distance = Vector3.Distance(MainCamera.transform.position, holdPointObject.position);
                if (distance > HoldDistance)
                {
                    if (interactableObject)
                    {
                        foreach (var interactStop in interactableObject.GetComponents<IInteractStop>())
                        {
                            interactStop.InteractStop();
                        }
                    }

                    interactableObject = null;
                    isPressed = false;
                }
            }
        }

        private void OnInteractGUI()
        {
            if (interactableObject == null)
                return;

            string titleText = null;
            string button1Text = null;
            string button2Text = null;

            InputReference button1Action = UseAction;
            InputReference button2Action = ExamineAction;

            if (interactableObject.TryGetComponent(out IInteractTitle interactMessage))
            {
                TitleParams messageParams = interactMessage.InteractTitle();
                titleText = messageParams.title ?? null;
                button1Text = messageParams.button1 ?? null;
                button2Text = messageParams.button2 ?? null;
            }

            if (interactableObject.TryGetComponent(out InteractableItem interactable))
            {
                if (interactable.InteractableType == InteractableTypeEnum.InventoryItem)
                {
                    if (interactable.UseInventoryTitle && interactable.PickupItem != null)
                    {
                        Item item = interactable.PickupItem.GetItem();
                        if (item != null) titleText ??= item.Title;
                    }
                    else
                    {
                        titleText ??= interactable.InteractTitle;
                    }

                    button1Text ??= InteractText;
                    button2Text ??= interactable.ExamineType != ExamineTypeEnum.None ? ExamineText : null;
                }
                else if (interactable.InteractableType == InteractableTypeEnum.GenericItem || interactable.InteractableType == InteractableTypeEnum.InventoryExpand)
                {
                    titleText ??= interactable.InteractTitle;
                    button1Text ??= InteractText;
                    button2Text ??= interactable.ExamineType != ExamineTypeEnum.None ? ExamineText : null;
                }
                else if (interactable.InteractableType == InteractableTypeEnum.ExamineItem)
                {
                    titleText ??= interactable.InteractTitle;
                    button1Text ??= ExamineText;
                    button1Action = ExamineAction;
                }
            }

            titleText ??= interactableObject.name;
            button1Text ??= InteractText;

            InteractContext button1 = null;
            if (!string.IsNullOrEmpty(button1Text))
            {
                button1 = new()
                {
                    InputAction = button1Action,
                    InteractName = button1Text
                };
            }

            InteractContext button2 = null;
            if (!string.IsNullOrEmpty(button2Text))
            {
                button2 = new()
                {
                    InputAction = button2Action,
                    InteractName = button2Text
                };
            }

            gameManager.InteractInfoPanel.ShowInfo(new()
            {
                ObjectName = titleText,
                Contexts = new[] { button1, button2 }
            });
        }

        public void Interact(GameObject interactObj)
        {
            // MULTIPLAYER PATCH: an object another player is examining is in their hands. Picking it up from there
            // would pull it out of their view mid-examine; it is theirs until they put it down.
            if (interactObj.TryGetComponent(out IExamineGate gate) && gate.IsHeldByOther)
            {
                gate.NotifyBlocked();
                return;
            }

            if (interactObj.TryGetComponent(out InteractableItem interactable))
            {
                bool isAddedToInventory = false;
                int shortcutId = -1;

                if (interactable.InteractableType == InteractableTypeEnum.InventoryItem)
                {
                    isAddedToInventory = inventory.AddItem(interactable.PickupItem.GUID, interactable.Quantity, interactable.ItemCustomData, out var addedItem);
                    if (isAddedToInventory)
                    {
                        if (interactable.AutoShortcut)
                            shortcutId = inventory.AutoShortcut(addedItem);

                        if (interactable.AutoEquip && addedItem.Item.Settings.isUsable && addedItem.Item.UsableSettings.usableType == UsableType.PlayerItem)
                            inventory.UseItem(addedItem, false);
                    }
                }

                if (interactable.InteractableType == InteractableTypeEnum.InventoryExpand)
                {
                    inventory.ExpandInventory(interactable.SlotsToExpand, interactable.ExpandRows);
                    isAddedToInventory = true;
                }

                if (isAddedToInventory)
                {
                    if (interactable.MessageType == MessageTypeEnum.Alert)
                    {
                        string pickupText = ShowLootedText ? LootText + " " + interactable.ItemName : interactable.ItemName;
                        if (shortcutId >= 0) pickupText += $"\nPress {shortcutId + 1} to Equip";

                        if (ShowDefaultPickupIcon)
                        {
                            gameManager.ShowItemPickupMessage(pickupText, DefaultPickupIcon, interactable.MessageTime);
                        }
                        else
                        {
                            var pickupIcon = interactable.PickupItem.GetItem().Icon;
                            gameManager.ShowItemPickupMessage(pickupText, pickupIcon, interactable.MessageTime);
                        }
                    }
                    else if (interactable.MessageType == MessageTypeEnum.Hint)
                    {
                        gameManager.ShowHintMessage(interactable.HintMessage, interactable.MessageTime);
                    }
                }

                if (isAddedToInventory || interactable.InteractableType == InteractableTypeEnum.GenericItem)
                {
                    interactable.OnInteract();
                }
            }
        }

        private void OnDrawGizmos()
        {
            if (interactableObject != null && holdPointObject != null)
            {
                Vector3 pointPos = interactableObject.transform.TransformPoint(localHitpoint);
                Gizmos.color = Color.red.Alpha(0.5f);
                Gizmos.DrawSphere(pointPos, 0.03f);
            }
        }
    }
}