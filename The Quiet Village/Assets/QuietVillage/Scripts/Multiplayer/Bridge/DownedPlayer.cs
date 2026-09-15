using TMPro;
using UHFPS.Runtime;
using UnityEngine;
using UnityEngine.UI;

namespace QuietVillage.Multiplayer.Bridge
{
    /// <summary>
    /// What being down means for the player at this machine: they crawl, cannot use items or interact, and see how
    /// long they have left.
    /// </summary>
    /// <remarks>
    /// Authority: owner. Whether a player is down is the host's (<see cref="PlayerHealthSync"/>); movement, items and
    /// interaction are owner-run, so this applies the consequences where they run. It follows the replicated state and
    /// sends nothing.
    ///
    /// The crawl is UHFPS's own crouch at a crawling speed, with every state that would stand the player up switched
    /// off, so the camera drops, the collider shrinks, and teammates see the crouch through <see cref="PlayerLocomotionSync"/>
    /// without a state of our own. UHFPS's states and components are only toggled, never patched.
    ///
    /// Becoming dead from down leaves everything as it is: UHFPS's death takes over from there.
    ///
    /// Local player only: <see cref="HeroPlayerNetworkSetup"/> adds it to the owner's copy.
    /// </remarks>
    public class DownedPlayer : MonoBehaviour
    {
        private const float CrawlSpeed = 0.8f;
        private const int SortingOrder = 45;

        // Every state that stands a crouching player up or moves them faster than a crawl.
        private static readonly string[] StandingStates =
        {
            PlayerStateMachine.IDLE_STATE,
            PlayerStateMachine.WALK_STATE,
            PlayerStateMachine.RUN_STATE,
            PlayerStateMachine.JUMP_STATE,
            PlayerStateMachine.SLIDING_STATE
        };

        private PlayerHealthSync m_health;
        private PlayerStateMachine m_machine;
        private PlayerItemsManager m_items;
        private InteractController m_interact;

        private bool m_downed;
        private float m_crouchSpeed;

        private Canvas m_canvas;
        private TextMeshProUGUI m_label;

        public void Bind(PlayerHealthSync health)
        {
            m_health = health;
            m_machine = GetComponent<PlayerStateMachine>();
            m_items = GetComponentInChildren<PlayerItemsManager>(true);
            m_interact = GetComponentInChildren<InteractController>(true);
            BuildOverlay();
        }

        private void OnDestroy()
        {
            if (m_canvas != null) Destroy(m_canvas.gameObject);
        }

        private void Update()
        {
            if (m_health == null) return;

            var downed = m_health.IsDowned;
            if (downed != m_downed)
            {
                m_downed = downed;
                if (downed) Collapse();
                else if (m_health.IsStanding) GetUp();
            }

            if (m_canvas != null && m_canvas.enabled != downed) m_canvas.enabled = downed;
            if (!downed || m_label == null) return;

            var seconds = Mathf.CeilToInt(m_health.BleedOutRemaining);
            m_label.text = $"<size=140%><color=#d05050>YOU ARE DOWN</color></size>\n" +
                           $"Bleeding out in {seconds / 60}:{seconds % 60:00}  ·  A teammate can revive you";
        }

        private void Collapse()
        {
            if (m_items != null)
            {
                m_items.DeactivateCurrentItem();
                m_items.IsItemsUsable = false;
            }

            if (m_interact != null)
            {
                m_interact.ResetInteract();
                m_interact.SetEnabled(false);
            }

            if (m_machine == null) return;

            foreach (var state in StandingStates) m_machine.SetStateEnabled(state, false);

            // Before entering the crouch, which reads its speed as it enters.
            m_crouchSpeed = m_machine.PlayerBasicSettings.CrouchSpeed;
            m_machine.PlayerBasicSettings.CrouchSpeed = CrawlSpeed;
            m_machine.ChangeState(PlayerStateMachine.CROUCH_STATE, true);
        }

        private void GetUp()
        {
            if (m_items != null) m_items.IsItemsUsable = true;
            if (m_interact != null) m_interact.SetEnabled(true);

            if (m_machine == null) return;

            foreach (var state in StandingStates) m_machine.SetStateEnabled(state, true);

            m_machine.PlayerBasicSettings.CrouchSpeed = m_crouchSpeed;
            m_machine.ChangeToIdle();
        }

        private void BuildOverlay()
        {
            var canvasObject = new GameObject("DownedOverlay", typeof(Canvas), typeof(CanvasScaler));
            m_canvas = canvasObject.GetComponent<Canvas>();
            m_canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            m_canvas.sortingOrder = SortingOrder;
            m_canvas.enabled = false;

            var scaler = canvasObject.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 0.5f;

            // A red wash, so being down reads at a glance even without the text.
            var tint = new GameObject("Tint", typeof(RectTransform)).AddComponent<Image>();
            tint.transform.SetParent(canvasObject.transform, false);
            tint.rectTransform.anchorMin = Vector2.zero;
            tint.rectTransform.anchorMax = Vector2.one;
            tint.rectTransform.offsetMin = tint.rectTransform.offsetMax = Vector2.zero;
            tint.color = new Color(0.35f, 0f, 0f, 0.3f);
            tint.raycastTarget = false;

            var labelObject = new GameObject("Label", typeof(RectTransform));
            labelObject.transform.SetParent(canvasObject.transform, false);
            m_label = labelObject.AddComponent<TextMeshProUGUI>();

            var rect = m_label.rectTransform;
            rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0f);
            rect.pivot = new Vector2(0.5f, 0f);
            rect.anchoredPosition = new Vector2(0f, 160f);
            rect.sizeDelta = new Vector2(1400f, 120f);

            m_label.font = TMP_Settings.defaultFontAsset;
            m_label.fontSize = 28f;
            m_label.alignment = TextAlignmentOptions.Center;
            m_label.raycastTarget = false;
            m_label.enableWordWrapping = false;
        }
    }
}
