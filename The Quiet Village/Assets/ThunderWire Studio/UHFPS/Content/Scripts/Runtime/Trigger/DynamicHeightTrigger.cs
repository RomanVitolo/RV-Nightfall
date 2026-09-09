using ThunderWire.Attributes;
using UnityEngine;
using Modules.Multiplayer.Bridge;

namespace UHFPS.Runtime
{
    [InspectorHeader("Dynamic Height Trigger")]
    [HelpBox("Specifies the zone in which the player dynamic crouch height will be applied.")]
    public class DynamicHeightTrigger : MonoBehaviour
    {
        public bool ShowGizmos = true;
        
        public void OnTriggerEnter(Collider other)
        {
            if (other.CompareTag("Player"))
            {
                PlayerStateMachine player = LocalPlayerContext.Presence.StateMachine;
                if (player != null) player.EnableDynamicHeight = true;
            }
        }

        public void OnTriggerExit(Collider other)
        {
            if (other.CompareTag("Player"))
            {
                PlayerStateMachine player = LocalPlayerContext.Presence.StateMachine;
                if (player != null) player.EnableDynamicHeight = false;
            }
        }

        public void OnDrawGizmosSelected()
        {
            if (!ShowGizmos)
                return;
            
            Gizmos.color = new Color(0.48f, 0.5f, 0.2f, 0.1f);
            Collider col = GetComponent<Collider>();
            
            if (col != null)
            {
                if (col is BoxCollider box)
                {
                    Gizmos.DrawCube(transform.position + box.center, box.size);
                }
                else if (col is SphereCollider sphere)
                {
                    Gizmos.DrawSphere(transform.position + sphere.center, sphere.radius);
                }
                else if (col is CapsuleCollider capsule)
                {
                    Vector3 point1 = transform.position + capsule.center +
                                     Vector3.up * (capsule.height / 2 - capsule.radius);
                    Vector3 point2 = transform.position + capsule.center -
                                     Vector3.up * (capsule.height / 2 - capsule.radius);
                    Gizmos.DrawSphere(point1, capsule.radius);
                    Gizmos.DrawSphere(point2, capsule.radius);
                    Gizmos.DrawLine(point1, point2);
                }
            }
        }
    }
}