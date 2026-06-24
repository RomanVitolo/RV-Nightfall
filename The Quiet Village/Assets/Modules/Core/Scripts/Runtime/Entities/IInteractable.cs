using Unity.Netcode;

namespace Modules.Core.Scripts.Runtime.Entities
{
    public interface IInteractable
    {
        void ToggleSelection(bool isSelected);
        NetworkObject NetworkObject { get; }
    }
}