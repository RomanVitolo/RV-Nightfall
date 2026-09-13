using QuietVillage.Multiplayer.Bridge;
using UHFPS.Runtime;
using UnityEngine;
using UnityEngine.AI;

namespace QuietVillage.Gameplay.Survival
{
    /// <summary>
    /// An opening in a shelter that players close with materials and creatures tear open.
    /// </summary>
    /// <remarks>
    /// Holds no health of its own: <see cref="SurvivalDirector"/> keeps every barricade's health in one replicated
    /// list, and this shows whatever it is told. Building is a UHFPS timed interaction, so it uses the same hold,
    /// progress bar and reticle as the rest of the game.
    ///
    /// Standing means blocking twice over: a solid collider stops players, and a carving NavMeshObstacle removes the
    /// opening from the creatures' NavMesh, so a creature whose target is behind it cannot path in and attacks the
    /// barricade instead. That is the whole defence model; nothing special-cases shelters.
    ///
    /// Both colliders sit on this object, on the Interact layer, because UHFPS interacts with the object whose
    /// collider its ray hits: the solid one while standing, the trigger while open.
    /// </remarks>
    public class Barricade : MonoBehaviour, IInteractStart, IInteractTimed, IInteractTitle
    {
        [Tooltip("Authored index, unique in the level. Clients match barricades to health by it.")]
        [SerializeField] private int m_order;

        [Tooltip("Solid while standing.")]
        [SerializeField] private BoxCollider m_blocker;

        [Tooltip("Carves the opening out of the NavMesh while standing.")]
        [SerializeField] private NavMeshObstacle m_obstacle;

        [Tooltip("Plank visuals, shown in order as health rises.")]
        [SerializeField] private GameObject[] m_planks = System.Array.Empty<GameObject>();

        [Tooltip("Outside the opening, where a creature stands to attack it.")]
        [SerializeField] private Transform m_attackPoint;

        [field: SerializeField, Tooltip("Seconds to hold Use to add one item.")]
        public float InteractTime { get; set; } = 2f;

        private const string NeedsItemHint = "You need Scrap to barricade this.";

        private SurvivalDirector m_director;

        public int Order => m_order;

        /// <summary>Position in the director's list, assigned when the level loads.</summary>
        public int Index { get; private set; } = -1;

        public int Health { get; private set; }

        public bool IsStanding => Health > 0;

        /// <summary>Where creatures attack from; the barricade itself if none was authored.</summary>
        public Vector3 AttackPoint => m_attackPoint != null ? m_attackPoint.position : transform.position;

        private bool IsFull => m_director != null && Health >= m_director.BarricadeMaxHealth;

        private bool HasBuildItem
        {
            get
            {
                var inventory = LocalPlayerContext.Inventory;
                return m_director != null && inventory != null && inventory.ContainsItem(m_director.BuildItemGuid);
            }
        }

        /// <summary>True when holding Use would do nothing. UHFPS checks it before starting the hold.</summary>
        public bool NoInteract => m_director == null || !m_director.IsSpawned || IsFull || !HasBuildItem;

        /// <summary>
        /// Whether a standing body at <paramref name="feet"/> would be caught inside this barricade if it went up now.
        /// </summary>
        /// <remarks>
        /// Measured against the blocker's box rather than a physics query: on the host a remote player has no collider
        /// running, and the blocker itself is off while the opening is open, so neither would be found by an overlap.
        /// </remarks>
        internal bool WouldTrap(Vector3 feet)
        {
            if (m_blocker == null) return false;

            // A body's width, so someone pressed against the edge of the opening still counts.
            const float bodyRadius = 0.45f;

            var local = m_blocker.transform.InverseTransformPoint(feet) - m_blocker.center;
            var half = m_blocker.size * 0.5f;

            // The barricade object sits at the opening's middle height, so its feet are half a height below it.
            return Mathf.Abs(local.x) <= half.x + bodyRadius
                   && Mathf.Abs(local.z) <= half.z + bodyRadius
                   && local.y >= -half.y - 0.5f && local.y <= half.y;
        }

        internal void Bind(SurvivalDirector director, int index)
        {
            m_director = director;
            Index = index;
        }

        /// <summary>Shows the health the host decided. Called on every client.</summary>
        internal void ShowHealth(int health, int maxHealth)
        {
            Health = health;

            var standing = health > 0;
            if (m_blocker != null) m_blocker.enabled = standing;
            if (m_obstacle != null) m_obstacle.enabled = standing;

            // One plank per share of health, so damage is visible before the barricade gives way.
            var shown = maxHealth > 0 ? Mathf.CeilToInt(m_planks.Length * (float)health / maxHealth) : 0;
            for (var i = 0; i < m_planks.Length; i++)
            {
                if (m_planks[i] != null) m_planks[i].SetActive(i < shown);
            }
        }

        public void InteractStart()
        {
            if (m_director == null || IsFull || HasBuildItem) return;

            var gameManager = LocalPlayerContext.GameManager;
            if (gameManager != null) gameManager.ShowHintMessage(NeedsItemHint, 2f);
        }

        public void InteractTimed()
        {
            if (NoInteract) return;

            m_director.RequestBuild(this);
        }

        public TitleParams InteractTitle()
        {
            var percent = m_director != null && m_director.BarricadeMaxHealth > 0
                ? Mathf.RoundToInt(100f * Health / m_director.BarricadeMaxHealth)
                : 0;

            return new TitleParams
            {
                title = !IsStanding ? "Unprotected opening" :IsFull ? "Barricade" : $"Damaged barricade ({percent}%)",
                button1 = IsFull ? null : IsStanding ? "Repair (1 Scrap)" : "Barricade (1 Scrap)"
            };
        }
    }
}
