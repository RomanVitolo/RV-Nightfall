using System;
using Unity.Netcode;
using UnityEngine;

namespace _Scripts.Premade_Scripts
{
    public class EntityController : NetworkBehaviour
    {
        [SerializeField] private EntityInput m_entityInput;
        [SerializeField] private AgentMover m_agentMover;
        
        [SerializeField] private InteractionDetector m_interactionDetector;
        [SerializeField] private Animator m_animator;
        [SerializeField] private AnimationEvents m_animationEvents;

        private static readonly int Interact = Animator.StringToHash("Interact");
        
        private bool m_isInteracting;

        public override void OnNetworkSpawn()
        {
            base.OnNetworkSpawn();
            m_interactionDetector.Initialize(IsOwner);
            if (IsOwner)
            {
                m_animationEvents.OnInteract += HandleInteractAction;
                m_animationEvents.OnAnimationDone += HandleAnimationDone;
            }
        }
        
        public override void OnNetworkDespawn()
        {
            if (IsOwner)
            {
                m_animationEvents.OnInteract -= HandleInteractAction;
                m_animationEvents.OnAnimationDone -= HandleAnimationDone;
            }
            base.OnNetworkDespawn();
        }

        private void OnEnable()
        {
            m_entityInput.OnPickUpPressed += HandlePickUpPressed;
        }

        private void OnDisable()
        {
            m_entityInput.OnPickUpPressed -= HandlePickUpPressed;
        }

        private void Update()
        {
            if (!IsOwner) return;

            var movementInput = m_entityInput.MovementInput;
            m_agentMover.Move(movementInput);
        }

        private void HandlePickUpPressed()
        {
            if (m_isInteracting)
                return;
            if(m_interactionDetector.ClosestInteractable == null)
                return;
            m_animator.SetBool(Interact, true);
            m_isInteracting = true;
        }
        
        private void HandleAnimationDone()
        {
            m_isInteracting = false;
        }

        private void HandleInteractAction()
        {
            if(m_interactionDetector.ClosestInteractable is BasePickable)
            {
                RequestPickUpServerRpc
                    (m_interactionDetector.ClosestInteractable.NetworkObject.NetworkObjectId);
            }
        }
        
        [Rpc(SendTo.Server)]
        private void RequestPickUpServerRpc(ulong networkObjectId)
        {
            if(!NetworkManager.SpawnManager.SpawnedObjects
                   .TryGetValue(networkObjectId, out NetworkObject target))
            {
                return;
            }
            if(!target.TryGetComponent(out BasePickable pickableItem))
            {
                return;
            }
            if(!pickableItem.CanBePickedUp)
            {
                return;
            }

            pickableItem.PickUp();
        }
    }
}