using UnityEngine;
using ThunderWire.Attributes;
using QuietVillage.Multiplayer.Bridge;

namespace UHFPS.Runtime
{
    [InspectorHeader("Auto Player Parent")]
    public class AutoPlayerParent : MonoBehaviour, ICharacterControllerHit
    {
        public Transform Parent;

        public void OnCharacterControllerEnter(CharacterController controller)
        {
            Transform parent = Parent != null ? Parent : transform;
            LocalPlayerContext.PlayerManager.ParentToObject(parent);
        }

        public void OnCharacterControllerExit()
        {
            LocalPlayerContext.PlayerManager.UnparentFromObject();
        }
    }
}