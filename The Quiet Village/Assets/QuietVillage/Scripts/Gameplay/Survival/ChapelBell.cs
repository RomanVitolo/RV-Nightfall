using QuietVillage.Multiplayer.Bridge;
using UHFPS.Runtime;
using UnityEngine;

namespace QuietVillage.Gameplay.Survival
{
    /// <summary>
    /// A bell in the middle of the shelter. Ringing it stuns every creature in earshot; then it needs a long rest.
    /// </summary>
    /// <remarks>
    /// Placed at runtime by <see cref="SurvivalDirector"/> on every client, at the same spot (the middle of the level's
    /// barricades, on the floor), so it needs neither a scene change nor a network object: the cooldown lives in the
    /// director, and ringing is a request to the host. A UHFPS timed interaction, like building.
    /// </remarks>
    public class ChapelBell : MonoBehaviour, IInteractTimed, IInteractTitle
    {
        private SurvivalDirector m_director;

        public float InteractTime { get; set; } = 1f;

        public bool NoInteract => m_director == null || !m_director.IsSpawned || m_director.BellCooldownRemaining > 0f
                                  || !LocalPlayerStanding;

        public static ChapelBell Create(SurvivalDirector director, Vector3 floor, Transform parent)
        {
            var root = new GameObject("ChapelBell");
            root.transform.SetParent(parent, false);
            root.transform.position = floor;

            var iron = new Color(0.12f, 0.12f, 0.13f);
            var bronze = new Color(0.55f, 0.38f, 0.16f);

            // Two posts, a beam and the bell hanging from it.
            SurvivalProps.Shape(PrimitiveType.Cube, root.transform, new Vector3(-0.55f, 1.1f, 0f), new Vector3(0.12f, 2.2f, 0.12f), iron);
            SurvivalProps.Shape(PrimitiveType.Cube, root.transform, new Vector3(0.55f, 1.1f, 0f), new Vector3(0.12f, 2.2f, 0.12f), iron);
            SurvivalProps.Shape(PrimitiveType.Cube, root.transform, new Vector3(0f, 2.2f, 0f), new Vector3(1.3f, 0.12f, 0.14f), iron);
            SurvivalProps.Shape(PrimitiveType.Sphere, root.transform, new Vector3(0f, 1.75f, 0f), new Vector3(0.55f, 0.6f, 0.55f), bronze);
            SurvivalProps.Shape(PrimitiveType.Cylinder, root.transform, new Vector3(0f, 1.5f, 0f), new Vector3(0.62f, 0.06f, 0.62f), bronze);

            // Walk-through: it was not in the NavMesh bake, and a solid frame would trap creatures and players alike.
            var trigger = root.AddComponent<BoxCollider>();
            trigger.isTrigger = true;
            trigger.center = new Vector3(0f, 1.3f, 0f);
            trigger.size = new Vector3(1.4f, 2.4f, 0.8f);

            SurvivalProps.SetLayer(root, "Interact");

            var bell = root.AddComponent<ChapelBell>();
            bell.m_director = director;
            return bell;
        }

        public void InteractTimed()
        {
            if (!NoInteract) m_director.RequestRingBell();
        }

        public TitleParams InteractTitle()
        {
            var remaining = m_director != null ? Mathf.CeilToInt(m_director.BellCooldownRemaining) : 0;
            return new TitleParams
            {
                title = remaining > 0 ? $"Chapel bell (rest {remaining / 60}:{remaining % 60:00})" : "Chapel bell",
                button1 = remaining > 0 ? null : "Ring"
            };
        }

        private static bool LocalPlayerStanding
        {
            get
            {
                var player = LocalPlayerContext.Player;
                var health = player != null ? player.GetComponent<PlayerHealthSync>() : null;
                return health != null && health.IsStanding;
            }
        }
    }
}
