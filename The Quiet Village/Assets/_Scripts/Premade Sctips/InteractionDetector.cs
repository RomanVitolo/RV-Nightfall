using UnityEngine;

namespace _Scripts.Premade_Scripts
{
    public class InteractionDetector : MonoBehaviour
    {
        [SerializeField] private float m_detectionRadius = 3f;
        [SerializeField] private float m_detectionAngle = 60f;
        [SerializeField] private LayerMask m_pickupLayer;

        private IInteractable m_closestInteractable;
        private bool m_isOwner = false;
        
        public IInteractable ClosestInteractable => m_closestInteractable;

        public void Initialize(bool isOwner)
        {
            m_isOwner = isOwner;
        }

        private void Update()
        {
            if (!m_isOwner)
                return;
            
            DetectInteractables();
        }

        private void DetectInteractables()
        {
            var hits = Physics.OverlapSphere(transform.position, m_detectionRadius, m_pickupLayer);

            var closestDistance = float.MaxValue;
            IInteractable candidate = null;

            foreach (Collider hit in hits)
            {
                var pickable = hit.GetComponent<IInteractable>();
                if (pickable == null)
                    continue;

                var directionToPickable = (hit.transform.position - transform.position).normalized;
                var angleToPickable = Vector3.Angle(transform.forward, directionToPickable);

                if (angleToPickable > m_detectionAngle * 0.5f)
                    continue;

                var distance = Vector3.Distance(transform.position, hit.transform.position);
                
                if (!(distance < closestDistance)) continue;
                closestDistance = distance;
                candidate = pickable;
            }

            if (candidate == m_closestInteractable) return;
            m_closestInteractable?.ToggleSelection(false);
            m_closestInteractable = candidate;
            m_closestInteractable?.ToggleSelection(true);
        }
    }
}