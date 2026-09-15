using QuietVillage.Multiplayer.Bridge;
using UHFPS.Runtime;
using UnityEngine;

namespace QuietVillage.Gameplay.Survival
{
    /// <summary>
    /// The ground outside one barricade, where a player can set a scrap trap for the next creature that comes to break it.
    /// </summary>
    /// <remarks>
    /// One per barricade, placed at runtime by <see cref="SurvivalDirector"/> on every client at the barricade's attack
    /// point, which is exactly where a creature stands to break it. Whether it is armed lives in the director's replicated
    /// list, indexed like barricade health, so this only shows it and asks. Setting it means going outside, which is the
    /// point: it is a night-time risk or a day-time plan.
    ///
    /// A trigger on the Interact layer: walked through, ignored by the creatures' wall checks, found by UHFPS's reticle.
    /// </remarks>
    public class BarricadeTrap : MonoBehaviour, IInteractStart, IInteractTimed, IInteractTitle
    {
        private SurvivalDirector m_director;
        private int m_index;
        private GameObject m_spikes;
        private Renderer m_base;

        public float InteractTime { get; set; } = 2f;

        public bool NoInteract => m_director == null || !m_director.IsSpawned || m_director.IsTrapArmed(m_index) || !HasScrap;

        private bool HasScrap
        {
            get
            {
                var inventory = LocalPlayerContext.Inventory;
                return m_director != null && inventory != null
                                          && inventory.GetItemQuantity(m_director.BuildItemGuid) >= m_director.TrapCost;
            }
        }

        public static BarricadeTrap Create(SurvivalDirector director, int index, Vector3 floor, Transform parent)
        {
            var root = new GameObject($"BarricadeTrap {index}");
            root.transform.SetParent(parent, false);
            root.transform.position = floor;

            var trap = root.AddComponent<BarricadeTrap>();
            trap.m_director = director;
            trap.m_index = index;

            // A dark ring on the ground marks the spot; spikes stand up in it once it is armed.
            trap.m_base = SurvivalProps.Shape(PrimitiveType.Cylinder, root.transform, new Vector3(0f, 0.02f, 0f),
                new Vector3(1.1f, 0.02f, 1.1f), new Color(0.18f, 0.16f, 0.14f)).GetComponent<Renderer>();

            trap.m_spikes = new GameObject("Spikes");
            trap.m_spikes.transform.SetParent(root.transform, false);
            var metal = new Color(0.3f, 0.28f, 0.26f);
            for (var i = 0; i < 6; i++)
            {
                var angle = i * 60f;
                var offset = Quaternion.Euler(0f, angle, 0f) * new Vector3(0.35f, 0.15f, 0f);
                SurvivalProps.Shape(PrimitiveType.Cube, trap.m_spikes.transform, offset, new Vector3(0.05f, 0.35f, 0.05f), metal,
                    Quaternion.Euler(0f, angle, 20f));
            }

            var trigger = root.AddComponent<BoxCollider>();
            trigger.isTrigger = true;
            trigger.center = new Vector3(0f, 0.25f, 0f);
            trigger.size = new Vector3(1.2f, 0.5f, 1.2f);

            SurvivalProps.SetLayer(root, "Interact");
            trap.Show(false);
            return trap;
        }

        /// <summary>Shows whether it is armed. Called on every client from the replicated list.</summary>
        internal void Show(bool armed)
        {
            if (m_spikes != null && m_spikes.activeSelf != armed) m_spikes.SetActive(armed);
            if (m_base != null) m_base.enabled = true;
        }

        public void InteractStart()
        {
            if (m_director == null || m_director.IsTrapArmed(m_index) || HasScrap) return;

            var gameManager = LocalPlayerContext.GameManager;
            if (gameManager != null) gameManager.ShowHintMessage($"You need {m_director.TrapCost} Scrap to set a trap.", 2f);
        }

        public void InteractTimed()
        {
            if (!NoInteract) m_director.RequestTrap(m_index);
        }

        public TitleParams InteractTitle()
        {
            var armed = m_director != null && m_director.IsTrapArmed(m_index);
            return new TitleParams
            {
                title = armed ? "Scrap trap (armed)" : "Trap spot",
                button1 = armed ? null : $"Set trap ({(m_director != null ? m_director.TrapCost : 2)} Scrap)"
            };
        }
    }
}
