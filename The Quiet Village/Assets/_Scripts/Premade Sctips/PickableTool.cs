using Unity.Netcode.Components;
using UnityEngine;

namespace _Scripts.Premade_Scripts
{
    public class PickableTool : BasePickable
    {
        [SerializeField] private ComponentController m_componentController;
        
        protected override void ApplyAvailabilityState(bool newValue)
        {
            if(IsServer)
                m_componentController.SetEnabled(newValue);
        }

        protected override void OnPickedUp()
        {
           // no code
        }

        public void Drop(Vector3 position)
        {
            if (!IsServer) return;
            
            transform.position = new Vector3(position.x, transform.position.y, position.z);
            m_isAvailable.Value = true;
        }
    }
}