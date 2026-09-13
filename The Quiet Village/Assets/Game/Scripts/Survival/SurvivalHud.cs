using Modules.Multiplayer.Bridge;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace QuietVillage.Survival
{
    /// <summary>
    /// Tells this player what time it is and what they carry to build with: "Night falls in 4:12 · Scrap 3".
    /// </summary>
    /// <remarks>
    /// A placeholder HUD built in code, so a level needs no UI setup and the UHFPS HUD prefab stays untouched. Reads
    /// the replicated clock and this player's own inventory; it sends nothing.
    ///
    /// The host can press F8 to end the current phase. It is for testing a night without waiting out the day, and
    /// goes through the director like any host decision, so every client sees the same skip.
    ///
    /// Added by <see cref="SurvivalDirector"/> on every client when it spawns.
    /// </remarks>
    public class SurvivalHud : MonoBehaviour
    {
        private const int SortingOrder = 40;

        private SurvivalDirector m_director;
        private TextMeshProUGUI m_clock;
        private TextMeshProUGUI m_supplies;

        private void Awake()
        {
            m_director = GetComponent<SurvivalDirector>();
            Build();
        }

        private void OnDestroy()
        {
            if (m_clock != null) Destroy(m_clock.canvas.gameObject);
        }

        private void Update()
        {
            if (m_director == null || m_clock == null) return;

            var visible = m_director.IsSpawned && LocalPlayerContext.IsReady;
            m_clock.canvas.enabled = visible;
            if (!visible) return;

            m_clock.text = ClockText();
            m_supplies.text = SuppliesText();

            if (m_director.IsServer && Keyboard.current != null && Keyboard.current.f8Key.wasPressedThisFrame)
                m_director.SkipPhase();
        }

        private string ClockText()
        {
            var remaining = Mathf.CeilToInt(m_director.SecondsRemaining);
            var time = $"{remaining / 60}:{remaining % 60:00}";

            return m_director.Phase switch
            {
                SurvivalPhase.Day => $"DAY  ·  Night falls in {time}",
                SurvivalPhase.Night => $"<color=#d05050>NIGHT</color>  ·  Dawn in {time}",
                _ => "DAWN  ·  You survived the night"
            };
        }

        private string SuppliesText()
        {
            var inventory = LocalPlayerContext.Inventory;
            var scrap = inventory != null ? inventory.GetItemQuantity(m_director.BuildItemGuid) : 0;

            var standing = 0;
            foreach (var barricade in m_director.Barricades)
            {
                if (barricade != null && barricade.IsStanding) standing++;
            }

            return $"Scrap {scrap}  ·  Barricades {standing}/{m_director.Barricades.Count}";
        }

        private void Build()
        {
            var canvasObject = new GameObject("SurvivalHud", typeof(Canvas), typeof(CanvasScaler));

            // Not parented to the director: a canvas under a scene object would be scaled and moved with it.
            var canvas = canvasObject.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = SortingOrder;

            var scaler = canvasObject.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 0.5f;

            m_clock = CreateLabel(canvas.transform, "Clock", 30f, -36f);
            m_supplies = CreateLabel(canvas.transform, "Supplies", 22f, -74f);
            m_supplies.color = new Color(1f, 1f, 1f, 0.75f);
        }

        private static TextMeshProUGUI CreateLabel(Transform parent, string name, float size, float y)
        {
            var label = new GameObject(name, typeof(RectTransform)).AddComponent<TextMeshProUGUI>();
            label.transform.SetParent(parent, false);

            var rect = label.rectTransform;
            rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 1f);
            rect.pivot = new Vector2(0.5f, 1f);
            rect.anchoredPosition = new Vector2(0f, y);
            rect.sizeDelta = new Vector2(1200f, size * 1.4f);

            label.font = TMP_Settings.defaultFontAsset;
            label.fontSize = size;
            label.alignment = TextAlignmentOptions.Center;
            label.raycastTarget = false;
            label.enableWordWrapping = false;
            return label;
        }
    }
}
