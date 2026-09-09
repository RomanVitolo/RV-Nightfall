using UHFPS.Tools;
using UnityEngine;

namespace UHFPS.Runtime
{
    public class InteractIconObject : MonoBehaviour, IHoverStart, IHoverEnd, IInteractStart, IInteractStop, IInteractIconSource
    {
        public Sprite HoverIcon;
        public Vector2 HoverSize;

        public Sprite HoldIcon;
        public Vector2 HoldSize;

        public bool UseTransformOffset = false;
        public Vector3 IconOffset;
        public Transform IconTransform;

        private InteractIconModule module;
        private bool isHover;      // icon currently shown
        private bool isHovering;   // pointer is over this object
        private bool isHolding;    // currently interacting

        public Vector3 IconPosition => UseTransformOffset && IconTransform != null ?
            IconTransform.position : transform.TransformPoint(IconOffset);

        private void Start()
        {
            module = GameManager.Module<InteractIconModule>();
            if (module == null)
                throw new System.NullReferenceException("InteractIconModule not found in GameManager!");
        }

        public InteractIconData GetInteractIconData()
        {
            bool useHoldIcon = isHolding && HoldIcon != null;

            Sprite sprite = useHoldIcon ? HoldIcon : HoverIcon;
            Vector2 size = useHoldIcon ? HoldSize : HoverSize;

            return new InteractIconData(IconPosition, sprite, size);
        }

        public void HoverStart()
        {
            isHovering = true;

            if (isHover || isHolding)
                return;

            module?.ShowInteractIcon(this);
            isHover = true;
        }

        public void HoverEnd()
        {
            isHovering = false;

            if (!isHover || isHolding)
                return;

            module?.DestroyInteractIcon(this);
            isHover = false;
        }

        public void InteractStart()
        {
            if (!isHover)
                return;

            isHolding = true;
        }

        public void InteractStop()
        {
            if (!isHover)
                return;

            isHolding = false;

            if (!isHovering)
            {
                module?.DestroyInteractIcon(this);
                isHover = false;
            }
        }

        private void OnDrawGizmosSelected()
        {
            Gizmos.color = Color.green.Alpha(0.5f);
            Gizmos.DrawSphere(IconPosition, 0.025f);
        }
    }
}