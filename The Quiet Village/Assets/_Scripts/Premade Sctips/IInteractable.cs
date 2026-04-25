using Unity.Netcode;

namespace _Scripts.Premade_Scripts
{
    public interface IInteractable
    {
        void ToggleSelection(bool isSelected);
        NetworkObject NetworkObject { get; }
    }
}