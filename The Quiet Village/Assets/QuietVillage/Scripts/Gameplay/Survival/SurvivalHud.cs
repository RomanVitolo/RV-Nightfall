using QuietVillage.Multiplayer.Bridge;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace QuietVillage.Gameplay.Survival
{
    /// <summary>
    /// Tells this player what time it is and what they carry to build with: "DAY 2/4 · Night falls in 4:12 · Scrap 3".
    /// </summary>
    /// <remarks>
    /// A placeholder HUD built in code, so a level needs no UI setup and the UHFPS HUD prefab stays untouched. Reads
    /// the replicated clock and this player's own inventory; it sends nothing.
    ///
    /// The host can press F8 to end the current phase. It is for testing a night without waiting out the day, and
    /// goes through the director like any host decision, so every client sees the same skip. F9 switches every creature
    /// to the next body in the creature catalog, for trying bodies out one after another.
    ///
    /// Added by <see cref="SurvivalDirector"/> on every client when it spawns.
    /// </remarks>
    public class SurvivalHud : MonoBehaviour
    {
        private const int SortingOrder = 40;

        private SurvivalDirector m_director;

        // Kept rather than read back through TMP_Text.canvas, which caches its parent lookup and can hold null when
        // the label woke before it was parented.
        private Canvas m_canvas;
        private TextMeshProUGUI m_clock;
        private TextMeshProUGUI m_supplies;

        private void Awake()
        {
            m_director = GetComponent<SurvivalDirector>();
            Build();
        }

        private void OnDestroy()
        {
            if (m_canvas != null) Destroy(m_canvas.gameObject);
        }

        private void Update()
        {
            if (m_director == null || m_canvas == null || m_clock == null || m_supplies == null) return;

            var visible = m_director.IsSpawned && LocalPlayerContext.IsReady;
            if (m_canvas.enabled != visible) m_canvas.enabled = visible;
            if (!visible) return;

            m_clock.text = ClockText();
            m_supplies.text = SuppliesText();

            if (!m_director.IsServer || Keyboard.current == null) return;

            if (Keyboard.current.f8Key.wasPressedThisFrame) m_director.SkipPhase();

            if (Keyboard.current.f9Key.wasPressedThisFrame)
            {
                var body = m_director.CycleTestCreature();
                var gameManager = LocalPlayerContext.GameManager;
                if (gameManager != null)
                    gameManager.ShowHintMessage(body != null
                        ? $"Creatures now: {(string.IsNullOrEmpty(body.DisplayName) ? body.Id : body.DisplayName)}"
                        : "No creature bodies set up. Run Tools > Quiet Village > Creatures > Set Up Creatures.", 3f);
            }
        }

        private string ClockText()
        {
            var remaining = Mathf.CeilToInt(m_director.SecondsRemaining);
            var time = $"{remaining / 60}:{remaining % 60:00}";

            var night = m_director.Night;
            var total = m_director.TotalNights;

            return m_director.Phase switch
            {
                SurvivalPhase.Day => $"DAY {night}/{total}  ·  Night falls in {time}",
                SurvivalPhase.Night => $"<color=#d05050>NIGHT {night}/{total}</color>  ·  Dawn in {time}",
                SurvivalPhase.Dawn => $"DAWN  ·  Night {night} of {total} survived  ·  Day {night + 1} in {time}",
                _ => total == 1 ? "DAWN  ·  You survived the night" : $"DAWN  ·  You survived all {total} nights"
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

            var supplies = $"Scrap {scrap}  ·  Barricades {standing}/{m_director.Barricades.Count}";
            if (!m_director.HasGenerator) return supplies;

            var canisters = inventory != null && !string.IsNullOrEmpty(m_director.FuelItemGuid)
                ? inventory.GetItemQuantity(m_director.FuelItemGuid)
                : 0;
            var fuel = Mathf.CeilToInt(m_director.FuelSeconds);
            var state = m_director.GeneratorRunning ? "<color=#f0d070>ON</color>" : "off";

            return $"{supplies}  ·  Canisters {canisters}  ·  Generator {state} {fuel / 60}:{fuel % 60:00}";
        }

        private void Build()
        {
            var canvasObject = new GameObject("SurvivalHud", typeof(Canvas), typeof(CanvasScaler));

            // Not parented to the director: a canvas under a scene object would be scaled and moved with it.
            var canvas = canvasObject.GetComponent<Canvas>();
            m_canvas = canvas;
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
            // Parented before the text component exists, so it wakes already under its canvas.
            var labelObject = new GameObject(name, typeof(RectTransform));
            labelObject.transform.SetParent(parent, false);
            var label = labelObject.AddComponent<TextMeshProUGUI>();

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
