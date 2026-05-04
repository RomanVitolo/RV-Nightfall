using System;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Modules.Core.Runtime.Scripts
{
    public class EntityInput : NetworkBehaviour
    {
        public event Action OnPickUpPressed;
        public event Action OnInteractPressed;
        
        [SerializeField] private InputActionReference m_movementReference;
        [SerializeField] private float m_smoothTime = 0.1f;
        
        private Vector2 m_rawInput;
        
        public Vector2 MovementInput { get; private set; }
        

        private void Update()
        {
            if (!IsOwner) return;
            
           m_rawInput = m_movementReference.action.ReadValue<Vector2>();
           MovementInput = Vector2.MoveTowards(MovementInput, m_rawInput, 
               Time.deltaTime / m_smoothTime);

           if (Keyboard.current.eKey.wasPressedThisFrame)
               OnPickUpPressed?.Invoke();

           if (Mouse.current.leftButton.wasPressedThisFrame)
               OnInteractPressed?.Invoke();
        }
    }
}