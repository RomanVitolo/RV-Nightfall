using UnityEngine;
using ThunderWire.Attributes;
using Newtonsoft.Json.Linq;
using Modules.Multiplayer.Bridge;

namespace UHFPS.Runtime
{
    [InspectorHeader("Dynamic Broken Fix")]
    public class DynamicBrokenFix : MonoBehaviour, IDynamicUnlock, IInventorySelector, ISaveable
    {
        public MeshRenderer DisabledRenderer;
        public ItemGuid FixableItem;

        [Header("Hint Text")]
        public bool ShowHintText;
        public GString NoFitHintText;
        public float HintTime = 2f;

        private DynamicObject dynamicObject;
        private bool isFixed;

        // MULTIPLAYER PATCH: resolved on use instead of cached in Awake. World objects Awake at scene load,
        // before Netcode spawns the local player, so the cached reference stayed null for the whole session.
        private GameManager gameManager => LocalPlayerContext.GameManager;

        private void Start()
        {
            NoFitHintText.SubscribeGloc();
        }

        public void OnTryUnlock(DynamicObject dynamicObject)
        {
            LocalPlayerContext.Inventory.OpenItemSelector(this);
            this.dynamicObject = dynamicObject;
        }

        public void OnInventoryItemSelect(Inventory inventory, InventoryItem selectedItem)
        {
            if (selectedItem.ItemGuid == FixableItem)
            {
                DisabledRenderer.enabled = true;
                inventory.RemoveItem(selectedItem);
                dynamicObject.TryUnlockResult(true);
                isFixed = true;
            }
            else if(ShowHintText)
            {
                gameManager.ShowHintMessage(NoFitHintText, HintTime);
            }
        }

        public StorableCollection OnSave()
        {
            return new StorableCollection()
            {
                { nameof(isFixed), isFixed }
            };
        }

        public void OnLoad(JToken data)
        {
            isFixed = (bool)data[nameof(isFixed)];
            if(isFixed) DisabledRenderer.enabled = true;
        }
    }
}