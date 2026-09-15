using UHFPS.Runtime;
using UnityEngine;

namespace QuietVillage.Gameplay.Survival
{
    /// <summary>
    /// Takes a player's weapon hit on a night creature's collider and staggers the creature instead of hurting it.
    /// </summary>
    /// <remarks>
    /// UHFPS's pistol, axe and knife call <see cref="IDamagable"/> on whatever collider their ray hits, on the client of
    /// the player using them. The damage number is ignored: creatures cannot be killed. The hit only asks the host for a
    /// stagger, which the host limits (<see cref="NightCreature.RequestStagger"/>), so no UHFPS weapon code is changed.
    ///
    /// Added by <see cref="NightCreature"/> on every client, on each of its solid colliders.
    /// </remarks>
    public class CreatureHitbox : MonoBehaviour, IDamagable
    {
        private NightCreature m_creature;

        public void Bind(NightCreature creature) => m_creature = creature;

        public void ApplyDamage(int damage, Transform sender = null)
        {
            if (m_creature != null && damage > 0) m_creature.RequestStagger();
        }

        public void ApplyDamageMax(Transform sender = null)
        {
            if (m_creature != null) m_creature.RequestStagger();
        }
    }
}
