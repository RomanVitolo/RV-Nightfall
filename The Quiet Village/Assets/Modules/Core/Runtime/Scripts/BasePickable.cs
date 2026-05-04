using Unity.Netcode;
using UnityEngine;

namespace Modules.Core.Runtime.Scripts
{
    public abstract class BasePickable : NetworkBehaviour, IInteractable
    {
        protected NetworkVariable<bool> m_isAvailable = new(true, NetworkVariableReadPermission.Everyone,
            NetworkVariableWritePermission.Server);

        [SerializeField] private SelectionOutline m_outline;
        [SerializeField] private ObjectType m_objectType;
        
        public bool CanBePickedUp => m_isAvailable.Value;
        public ObjectType ObjectType => m_objectType;

        public override void OnNetworkSpawn()
        {
            base.OnNetworkSpawn();
            m_isAvailable.OnValueChanged += OnAvailabilityChanged;
            ApplyAvailabilityState(m_isAvailable.Value);
        }

        public override void OnNetworkDespawn()
        {
            m_isAvailable.OnValueChanged -= OnAvailabilityChanged;
            base.OnNetworkDespawn();
        }

        public void PickUp()
        {
            if (!IsServer) return;
            
            m_isAvailable.Value = false;
            OnPickedUp();
        }

        public void ToggleSelection(bool isSelected)
        {
            if(m_outline != null)
                m_outline.ToggleOutline(isSelected);
        }
        
        protected abstract void ApplyAvailabilityState(bool newValue);
        protected abstract void OnPickedUp();
        
        private void OnAvailabilityChanged(bool previousValue, bool newValue)
        {
            ApplyAvailabilityState(newValue);
        }
       
    }
}