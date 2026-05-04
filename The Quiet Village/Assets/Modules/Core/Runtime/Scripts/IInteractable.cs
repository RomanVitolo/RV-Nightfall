using Unity.Netcode;

namespace Modules.Core.Runtime.Scripts
{
    public interface IInteractable
    {
        void ToggleSelection(bool isSelected);
        NetworkObject NetworkObject { get; }
    }
}