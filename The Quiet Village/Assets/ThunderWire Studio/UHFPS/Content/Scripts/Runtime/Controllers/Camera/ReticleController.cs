using System;
using UnityEngine;
using UnityEngine.UI;
using ThunderWire.Attributes;
using Object = UnityEngine.Object;
using Modules.Multiplayer.Bridge;

namespace UHFPS.Runtime
{
    [Serializable]
    public sealed class Reticle
    {
        public Sprite Sprite;
        public Color Color = Color.white;
        public Vector2 Size = Vector2.one;
    }

    [InspectorHeader("Reticle Controller", space = false)]
    [RequireComponent(typeof(InteractController))]
    public class ReticleController : MonoBehaviour
    {
        [Header("Interact")] public Reticle DefaultReticle;
        public Reticle InteractReticle;
        public bool DynamicReticle = true;
        public float ChangeTime = 0.05f;

        [Header("Custom Reticles")] [RequireInterface(typeof(IReticleProvider))]
        public Object[] ReticleProviders;

        private InteractController interactController;
        private RectTransform crosshairRect;
        private Image crosshairImage;

        private CustomInteractReticle holdReticle;
        private Vector2 crosshairChangeVel;
        private bool resetReticle;

        public bool ReticleDisabled { get; set; }

        private void Awake()
        {
            interactController = GetComponent<InteractController>();
            GameManager gameManager = LocalPlayerContext.ResolveGameManager(this);
            crosshairImage = gameManager.ReticleImage;
            crosshairRect = gameManager.ReticleImage.rectTransform;
        }

        private void Update()
        {
            if (ReticleDisabled)
                return;

            if (interactController.RaycastObject != null || holdReticle != null)
            {
                GameObject raycastObject = interactController.RaycastObject;
                OnChangeReticle(raycastObject);
            }
            else
            {
                OnChangeReticle(null);
            }
        }

        public void ResetReticle()
        {
            OnChangeReticle(null);
            ChangeReticle(DefaultReticle);
        }

        public void EnableReticle(bool state, bool reset = true)
        {
            crosshairImage.enabled = state;
            ReticleDisabled = !state;
            if (reset) ResetReticle();
        }

        private void OnChangeReticle(GameObject raycastObject)
        {
            CustomInteractReticle customReticle = null;
            
            if (holdReticle != null && !holdReticle.enabled)
                holdReticle = null;

            bool hasActiveHoldReticle = holdReticle != null && holdReticle.enabled;

            // --------------------------------------------------
            // CustomInteractReticle (attached to object) or held
            // --------------------------------------------------
            if ((raycastObject != null && raycastObject.TryGetComponent(out customReticle) && customReticle.enabled) || hasActiveHoldReticle)
            {
                CustomInteractReticle reticleProvider = hasActiveHoldReticle ? holdReticle : customReticle;

                // CustomInteractReticle must implement IReticleProvider
                IReticleProvider customProvider = reticleProvider;
                var (_, reticle, hold) = customProvider.OnProvideReticle();

                // Hold only if provider is enabled
                if (hold && reticleProvider.enabled)
                    holdReticle = reticleProvider;
                else
                    holdReticle = null;

                ChangeReticle(reticle);
                resetReticle = true;
                return;
            }

            // --------------------------------------------------
            // Global IReticleProvider array (Custom Reticles)
            // --------------------------------------------------
            bool customReticleFlag = false;

            foreach (var provider in ReticleProviders)
            {
                if (provider == null)
                    continue;

                // Must implement IReticleProvider
                if (provider is not IReticleProvider reticleProvider)
                    continue;

                // Underlying component (MonoBehaviour/Behaviour) must be enabled
                if (provider is Behaviour behaviour && !behaviour.isActiveAndEnabled)
                    continue;

                // Get reticle data from provider
                var (targetType, reticle, hold) = reticleProvider.OnProvideReticle();
                if (targetType == null || reticle == null)
                    continue;

                // If targetType is specified, raycastObject must have component of that type
                Component component = null;
                bool matchTarget = raycastObject != null && raycastObject.TryGetComponent(targetType, out component);
                
                // If component is MonoBehaviour and not active/enabled, skip reticle change
                if (component is MonoBehaviour mono && !mono.isActiveAndEnabled)
                    continue;
                
                // If component is IDraggableObject and dragging is not allowed, skip reticle change
                if (component is IDraggableObject draggableObject && !draggableObject.AllowDragging)
                    continue;
                
                if (matchTarget || hold)
                {
                    ChangeReticle(reticle);
                    customReticleFlag = true;
                    resetReticle = true;
                    break;
                }
            }

            // --------------------------------------------------
            // No custom reticle active -> default / interact logic
            // --------------------------------------------------
            if (!customReticleFlag)
            {
                if (resetReticle)
                {
                    crosshairImage.color = Color.white;
                    crosshairRect.sizeDelta = DefaultReticle.Size;
                    resetReticle = false;
                }

                if (raycastObject != null)
                {
                    if (DynamicReticle)
                    {
                        crosshairImage.sprite = InteractReticle.Sprite;
                        crosshairImage.color = InteractReticle.Color;
                        crosshairRect.sizeDelta = Vector2.SmoothDamp(
                            crosshairRect.sizeDelta,
                            InteractReticle.Size,
                            ref crosshairChangeVel,
                            ChangeTime
                        );
                    }
                    else
                    {
                        ChangeReticle(InteractReticle);
                    }
                }
                else
                {
                    if (DynamicReticle)
                    {
                        crosshairImage.sprite = DefaultReticle.Sprite;
                        crosshairImage.color = DefaultReticle.Color;
                        crosshairRect.sizeDelta = Vector2.SmoothDamp(
                            crosshairRect.sizeDelta,
                            DefaultReticle.Size,
                            ref crosshairChangeVel,
                            ChangeTime
                        );
                    }
                    else
                    {
                        ChangeReticle(DefaultReticle);
                    }
                }
            }
            else
            {
                resetReticle = true;
            }
        }


        private void ChangeReticle(Reticle reticle)
        {
            if (reticle != null)
            {
                crosshairImage.sprite = reticle.Sprite;
                crosshairImage.color = reticle.Color;
                crosshairRect.sizeDelta = reticle.Size;
            }
            else
            {
                crosshairImage.sprite = null;
                crosshairImage.color = Color.white;
                crosshairRect.sizeDelta = Vector2.zero;
            }
        }
    }
}