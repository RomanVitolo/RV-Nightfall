using System.Collections.Generic;
using UnityEngine;
using UHFPS.Input;
using Unity.Cinemachine;
using UnityEngine.Events;
using QuietVillage.Multiplayer.Bridge;

namespace UHFPS.Runtime
{
    public abstract class PuzzleBase : MonoBehaviour, IInteractStart
    {
        public CinemachineCamera PuzzleCamera;
        public float SwitchCameraFadeSpeed = 5;
        public ControlsContext[] ControlsContexts;

        public LayerMask CullLayers;
        public Layer InteractLayer;
        public Layer DisabledLayer;
        public bool EnablePointer;

        public List<Collider> CollidersEnable = new List<Collider>();
        public List<Collider> CollidersDisable = new List<Collider>();

        public UnityEvent<bool> OnScreenFade;

        // MULTIPLAYER PATCH: resolved on use instead of cached in Awake. World objects Awake at scene load,
        // before Netcode spawns the local player, so the cached reference stayed null for the whole session.
        protected PlayerPresenceManager playerPresence => LocalPlayerContext.Presence;
        protected PlayerManager playerManager => LocalPlayerContext.PlayerManager;
        protected GameManager gameManager => LocalPlayerContext.GameManager;
        private bool canSwitch;

        /// <summary>
        /// Specifies when the camera is switched to a puzzle or normal camera. [true = puzzle, false = normal]
        /// </summary>
        protected bool isActive;

        /// <summary>
        /// Specifies when the camera can be switched back to normal camera using the default functionality.
        /// </summary>
        protected bool canManuallySwitch;

        /// <summary>
        /// Determines whether the colliders switch to puzzle mode or normal mode.
        /// </summary>
        protected bool switchColliders;

        public virtual void Awake()
        {
            foreach (var control in ControlsContexts)
            {
                control.SubscribeGloc();
            }
        }

        public virtual void Update()
        {
            if (!canSwitch || !isActive || !canManuallySwitch)
                return;

            if (InputManager.ReadButtonOnce(this, Controls.EXAMINE))
                SwitchBack();
        }

        /// <summary>
        /// This function is called when you interact with the object. It freezes the player and switches the camera to the puzzle camera.
        /// </summary>
        public virtual void InteractStart()
        {
            if (!isActive)
            {
                // MULTIPLAYER PATCH: one player at a time. The grant, if it has to wait for the host, comes back here.
                if (TryGetComponent(out IPuzzleGate gate) && !gate.TryBeginPuzzle(InteractStart)) return;

                playerPresence.FreezePlayer(true);
                playerManager.PlayerItems.IsItemsUsable = false;
                playerPresence.SwitchActiveCamera(PuzzleCamera.gameObject, SwitchCameraFadeSpeed, OnBackgroundFade, () => { canSwitch = true; });
                canManuallySwitch = true;
                switchColliders = true;
                isActive = true;
            }
        }

        /// <summary>
        /// This function is called before switching to the puzzle camera, after the screen fades to black.
        /// </summary>
        public virtual void OnBackgroundFade()
        {
            if (isActive)
            {
                gameManager.DisableAllGamePanels();
                gameManager.ShowControlsInfo(true, ControlsContexts);

                if (EnablePointer)
                {
                    gameManager.ShowPointer(CullLayers, InteractLayer, (hit, interactStart) =>
                    {
                        interactStart.InteractStart();
                    });
                }

                if (switchColliders)
                {
                    CollidersEnable.ForEach(x => x.enabled = true);
                    CollidersDisable.ForEach(x => x.enabled = false);
                }
            }
            else
            {
                // MULTIPLAYER PATCH: the player is back in their own view; others may use the puzzle now.
                if (TryGetComponent(out IPuzzleGate gate)) gate.EndPuzzle();

                playerPresence.FreezePlayer(false);
                playerManager.PlayerItems.IsItemsUsable = true;
                gameManager.ShowControlsInfo(false, null);
                gameManager.ShowPanel(GameManager.PanelType.MainPanel);

                if (switchColliders)
                {
                    CollidersEnable.ForEach(x => x.enabled = false);
                    CollidersDisable.ForEach(x => x.enabled = true);
                }
            }

            OnScreenFade?.Invoke(isActive);
        }

        /// <summary>
        /// Calling this function switches the puzzle camera to the normal camera.
        /// </summary>
        protected void SwitchBack()
        {
            if (isActive)
            {
                playerPresence.SwitchToPlayerCamera(SwitchCameraFadeSpeed, OnBackgroundFade);
                if (EnablePointer) gameManager.HidePointer();
                canSwitch = false;
                isActive = false;
            }
        }

        /// <summary>
        /// Disable the puzzle interaction functionality. The GameObject layer will be set to Disabled Layer.
        /// </summary>
        protected void DisableInteract(bool includeChild = true)
        {
            gameObject.layer = DisabledLayer;

            if (includeChild)
            {
                foreach (Transform tr in transform)
                {
                    tr.gameObject.layer = DisabledLayer;
                }
            }
        }
    }
}