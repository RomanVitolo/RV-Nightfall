using System.Collections;
using UnityEngine;
using UnityEngine.UI;
using UHFPS.Tools;
using Newtonsoft.Json.Linq;
using UHFPS.Input;
using QuietVillage.Multiplayer.Bridge;

namespace UHFPS.Runtime
{
    public class FlashlightItem : PlayerItemBehaviour
    {
        public enum EUVSwitchMethod
        {
            HoldButton,
            ToggleButton
        }

        public ItemGuid BatteryInventoryItem;
        public Light FlashlightLight;
        public float LightIntensity = 1f;

        public bool InfiniteBattery = false;
        public ushort BatteryLife = 320;
        public Percentage BatteryPercentage = 100;
        public Percentage BatteryLowPercent = 20;
        public float ReloadLightEnableOffset = 1f;
        public Color BatteryFullColor = Color.white;
        public Color BatteryLowColor = Color.red;

        public bool EnableUVFlashlight = true;
        public float UVBatteryDrainMultiplier = 1f;
        public EUVSwitchMethod UVSwitchMethod = EUVSwitchMethod.HoldButton;
        public InputReference SwitchToUVFlashlight;
        public Color UVFlashlightColor = Color.magenta;
        public Color NormalFlashlightColor = Color.white;
        public SoundClip UVFlashlightSwitchSound;
        
        public string FlashlightDrawState = "FlashlightDraw";
        public string FlashlightHideState = "FlashlightHide";
        public string FlashlightReloadState = "FlashlightReload";
        public string FlashlightIdleState = "FlashlightIdle";

        public string FlashlightHideTrigger = "Hide";
        public string FlashlightReloadTrigger = "Reload";

        public float FlashlightHideTrim = 0.5f;

        public SoundClip FlashlightClickOn;
        public SoundClip FlashlightClickOff;

        private AudioSource audioSource;
        private UVFlashlightController uvFlashlightController;
        private CanvasGroup flashlightPanel;
        private Image batteryFill;

        private bool isUVSwitched;
        private bool isEquipped;
        private bool isBusy;

        public float batteryEnergy;
        private float currentBattery;
        private Color batteryColor;

        public override string Name => "Flashlight";

        public override bool IsBusy() => !isEquipped || isBusy;

        public override bool CanCombine() => isEquipped && !isBusy;

        private void Awake()
        {
            audioSource = GetComponent<AudioSource>();
            uvFlashlightController = GetComponent<UVFlashlightController>();
            GameManager gameManager = LocalPlayerContext.ResolveGameManager(this);

            var behaviours = gameManager.GraphicReferences.Value["Flashlight"];
            flashlightPanel = (CanvasGroup)behaviours[0];
            batteryFill = (Image)behaviours[1];

            if (!SaveGameManager.GameWillLoad)
            {
                currentBattery = BatteryPercentage.From(BatteryLife);
                UpdateBattery();

                batteryColor = batteryEnergy > BatteryLowPercent.Ratio()
                    ? BatteryFullColor : BatteryLowColor;

                batteryFill.color = batteryColor;
            }
        }

        public override void OnUpdate()
        {
            if (!isEquipped || isBusy || InfiniteBattery) 
                return;

            // battery life
            currentBattery = currentBattery > 0 
                ? currentBattery -= Time.deltaTime * (isUVSwitched ? UVBatteryDrainMultiplier : 1f)
                : 0;
            
            UpdateBattery();
            UpdateFlashlightReveal();

            // battery icon
            batteryColor = batteryEnergy > BatteryLowPercent.Ratio()
                ? Color.Lerp(batteryColor, BatteryFullColor, Time.deltaTime * 10)
                : Color.Lerp(batteryColor, BatteryLowColor, Time.deltaTime * 10);

            batteryFill.color = batteryColor;
        }

        private void UpdateFlashlightReveal()
        {
            if (uvFlashlightController == null || !EnableUVFlashlight)
                return;

            if (UVSwitchMethod == EUVSwitchMethod.HoldButton)
            {
                // if the player holds the button, the UV flashlight will be enabled, otherwise it will be disabled
                isUVSwitched = InputManager.ReadButton(SwitchToUVFlashlight.ActionName);
            }
            else if (InputManager.ReadButtonOnce(this, SwitchToUVFlashlight.ActionName))
            {
                // if the player presses the button, the UV flashlight will be switched
                isUVSwitched = !isUVSwitched;
                audioSource.PlayOneShotSoundClip(UVFlashlightSwitchSound);
            }

            if (isUVSwitched && currentBattery > 0)
            {
                FlashlightLight.color = UVFlashlightColor;
                uvFlashlightController.EnableLight(true);
            }
            else
            {
                FlashlightLight.color = NormalFlashlightColor;
                uvFlashlightController.EnableLight(false);
            }
        }

        public override void OnItemCombine(InventoryItem combineItem)
        {
            if (combineItem.ItemGuid != BatteryInventoryItem || !isEquipped)
                return;

            SetLightState(false);
            LocalPlayerContext.ResolveInventory(this).RemoveItem(combineItem, 1);
            Animator.SetTrigger(FlashlightReloadTrigger);
            StartCoroutine(ReloadFlashlightBattery());
            isBusy = true;
        }

        IEnumerator ReloadFlashlightBattery()
        {
            yield return new WaitForAnimatorClip(Animator, FlashlightReloadState, ReloadLightEnableOffset);

            currentBattery = new Percentage(100).From(BatteryLife);
            UpdateBattery();

            SetLightState(true);
            isBusy = false;
        }

        public void SetLightState(bool state)
        {
            FlashlightLight.enabled = state;
            if (!state) audioSource.PlayOneShotSoundClip(FlashlightClickOff);
            else audioSource.PlayOneShotSoundClip(FlashlightClickOn);
        }

        private void UpdateBattery()
        {
            batteryEnergy = Mathf.InverseLerp(0, BatteryLife, currentBattery);
            batteryFill.fillAmount = batteryEnergy;
            
            float intensity = Mathf.Lerp(0, LightIntensity, batteryEnergy);
            FlashlightLight.intensity = intensity;

            if (uvFlashlightController != null && EnableUVFlashlight)
            {
                float intensity01 = Mathf.InverseLerp(0, LightIntensity, intensity);
                uvFlashlightController.FlashlightIntensity = intensity01;
            }
        }

        public override void OnItemSelect()
        {
            CanvasGroupFader.StartFadeInstance(flashlightPanel, true, 5f);

            ItemObject.SetActive(true);
            SetLightState(true);

            StartCoroutine(ShowFlashlight());
            isEquipped = false;
            isBusy = false;
        }

        IEnumerator ShowFlashlight()
        {
            yield return new WaitForAnimatorClip(Animator, FlashlightDrawState);
            isEquipped = true;
        }

        public override void OnItemDeselect()
        {
            CanvasGroupFader.StartFadeInstance(flashlightPanel, false, 5f,
                () => flashlightPanel.gameObject.SetActive(false));

            StopAllCoroutines();
            StartCoroutine(HideFlashlight());
            Animator.SetTrigger(FlashlightHideTrigger);
            isBusy = true;
        }

        IEnumerator HideFlashlight()
        {
            yield return new WaitForAnimatorClip(Animator, FlashlightHideState, FlashlightHideTrim);

            SetLightState(false);
            ItemObject.SetActive(false);

            isEquipped = false;
            isBusy = false;
        }

        public override void OnItemActivate()
        {
            flashlightPanel.alpha = 1f;
            flashlightPanel.gameObject.SetActive(true);

            SetLightState(true);
            ItemObject.SetActive(true);
            Animator.Play(FlashlightIdleState);

            isEquipped = true;
            isBusy = false;
        }

        public override void OnItemDeactivate()
        {
            flashlightPanel.alpha = 0f;
            flashlightPanel.gameObject.SetActive(false);

            StopAllCoroutines();
            ItemObject.SetActive(false);

            isEquipped = false;
            isBusy = false;
        }

        public override StorableCollection OnCustomSave()
        {
            bool isUVLight = isUVSwitched && UVSwitchMethod == EUVSwitchMethod.ToggleButton;
            
            return new StorableCollection()
            {
                { "batteryEnergy", currentBattery },
                { "isUVLight", isUVLight }
            };
        }

        public override void OnCustomLoad(JToken data)
        {
            currentBattery = data["batteryEnergy"].ToObject<float>();
            isUVSwitched = data["isUVLight"].ToObject<bool>();
            
            UpdateBattery();

            batteryColor = batteryEnergy > BatteryLowPercent.Ratio()
                ? BatteryFullColor : BatteryLowColor;

            batteryFill.color = batteryColor;
        }
    }
}