using Starter_Assets.Runtime.InputSystem;
using UnityEngine;

namespace Starter_Assets.Runtime.Mobile.Scripts
{
    public class VirtualInput : MonoBehaviour
    {
        [Header("Output")]
        public StarterAssetsInputs StarterAssetsInputs;

        public void VirtualMoveInput(Vector2 virtualMoveDirection)
        {
            StarterAssetsInputs.MoveInput(virtualMoveDirection);
        }

        public void VirtualLookInput(Vector2 virtualLookDirection)
        {
            StarterAssetsInputs.LookInput(virtualLookDirection);
        }

        public void VirtualJumpInput(bool virtualJumpState)
        {
            StarterAssetsInputs.JumpInput(virtualJumpState);
        }

        public void VirtualSprintInput(bool virtualSprintState)
        {
            StarterAssetsInputs.SprintInput(virtualSprintState);
        }
    }
}
