using UnityEngine;
using ThunderWire.Attributes;

namespace UHFPS.Runtime
{
    [InspectorHeader("NPC Body Part")]
    public class NPCBodyPart : MonoBehaviour, IDamagable
    {
        [HideInInspector]
        public NPCHealth HealthScript;
        public bool IsHeadDamage;

        public void ApplyDamage(int damage, Transform sender = null)
        {
            if (HealthScript == null)
                return;

            if (HealthScript.AllowHeadhsot && IsHeadDamage)
                damage = Mathf.RoundToInt(damage * HealthScript.HeadshotMultiplier);

            HealthScript.ApplyDamage(damage, sender);
        }

        public void ApplyDamageMax(Transform sender = null)
        {
            if (HealthScript == null)
                return;

            HealthScript.ApplyDamageMax(sender);
        }
    }
}