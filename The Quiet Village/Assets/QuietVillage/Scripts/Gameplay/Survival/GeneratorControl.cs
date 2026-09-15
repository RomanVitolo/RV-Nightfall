using QuietVillage.Multiplayer.Bridge;
using UHFPS.Runtime;
using UnityEngine;

namespace QuietVillage.Gameplay.Survival
{
    /// <summary>
    /// One of the generator's two controls: its fuel cap (hold Use to pour a canister in) or its switch (press Use).
    /// </summary>
    /// <remarks>
    /// Two objects rather than one, because UHFPS calls a press handler at the start of every hold as well: a single
    /// object that both switched and refuelled would flick the power each time someone started to pour. Each control is
    /// a collider on the Interact layer, like a barricade, and asks the director, which decides for everyone.
    /// </remarks>
    public class GeneratorControl : MonoBehaviour, IInteractStart, IInteractTimed, IInteractTitle
    {
        public enum ControlKind { FuelCap, Switch }

        [SerializeField] private ControlKind m_kind;

        [field: SerializeField, Tooltip("Seconds to hold Use to pour one canister. Ignored by the switch.")]
        public float InteractTime { get; set; } = 2.5f;

        private const string NeedsFuelHint = "You need a fuel canister.";

        private static SurvivalDirector Director => SurvivalDirector.Active;

        private bool HasFuelItem
        {
            get
            {
                var inventory = LocalPlayerContext.Inventory;
                var director = Director;
                return director != null && inventory != null && inventory.ContainsItem(director.FuelItemGuid);
            }
        }

        /// <summary>The switch never holds; the cap holds only for someone carrying a canister.</summary>
        public bool NoInteract => m_kind == ControlKind.Switch || Director == null || !Director.IsSpawned || !HasFuelItem;

        public void InteractStart()
        {
            var director = Director;
            if (director == null || !director.IsSpawned) return;

            if (m_kind == ControlKind.Switch)
            {
                director.RequestSwitchPower();
                return;
            }

            if (!HasFuelItem)
            {
                var gameManager = LocalPlayerContext.GameManager;
                if (gameManager != null) gameManager.ShowHintMessage(NeedsFuelHint, 2f);
            }
        }

        public void InteractTimed()
        {
            if (m_kind != ControlKind.FuelCap || NoInteract) return;

            Director.RequestRefuel();
        }

        public TitleParams InteractTitle()
        {
            var director = Director;
            if (director == null) return new TitleParams { title = "Generator" };

            var fuel = Mathf.CeilToInt(director.FuelSeconds);
            var fuelText = $"{fuel / 60}:{fuel % 60:00} of light";

            return m_kind == ControlKind.Switch
                ? new TitleParams
                {
                    title = director.GeneratorRunning ? $"Generator (running, {fuelText})" : $"Generator (off, {fuelText})",
                    button1 = director.GeneratorRunning ? "Switch off" : "Switch on"
                }
                : new TitleParams
                {
                    title = $"Fuel tank ({fuelText})",
                    button1 = "Pour in a canister"
                };
        }

#if UNITY_EDITOR
        public void Configure(ControlKind kind) => m_kind = kind;
#endif
    }
}
