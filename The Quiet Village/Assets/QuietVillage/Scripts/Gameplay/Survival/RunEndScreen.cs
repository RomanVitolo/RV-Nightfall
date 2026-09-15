using System;
using QuietVillage.Multiplayer.Bridge;
using QuietVillage.Multiplayer.Flow;
using QuietVillage.Multiplayer.Saves;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.UI;
using UnityEngine.UI;

namespace QuietVillage.Gameplay.Survival
{
    /// <summary>
    /// The end of a run, for everyone: whether it was won, how many nights it lasted, what each player did, and what
    /// happens next.
    /// </summary>
    /// <remarks>
    /// Reads the director's replicated outcome and summary, so every client shows the same result; it sends nothing but
    /// the host's choice, which goes through <see cref="SessionFlow"/> like every other level change.
    ///
    /// The host chooses: Play Again reloads the level as a new game (a won run has nothing left to resume), or, after
    /// losing a game that resumed a save, Retry from Save goes back to that save and New Game starts over. End Game
    /// closes the room. Everyone else waits for that choice, or leaves.
    ///
    /// Opens a few seconds after the run ends, so the last creature's death or the last player's fall is seen first.
    /// Built in code like <see cref="SurvivalHud"/>, so a level needs no UI setup.
    ///
    /// Added by <see cref="SurvivalDirector"/> on every client when it spawns.
    /// </remarks>
    public class RunEndScreen : MonoBehaviour
    {
        private const int SortingOrder = 100;
        private const float OpenDelaySeconds = 3f;

        private static readonly Color Gold = new(0.94f, 0.8f, 0.4f);
        private static readonly Color Blood = new(0.82f, 0.25f, 0.25f);

        private SurvivalDirector m_director;

        private Canvas m_canvas;
        private TextMeshProUGUI m_title;
        private TextMeshProUGUI m_subtitle;
        private TextMeshProUGUI m_summary;
        private TextMeshProUGUI m_status;
        private Button m_primary;
        private Button m_secondary;
        private Button m_leave;

        private float m_endedAt = -1f;
        private bool m_built;
        private bool m_choiceMade;

        private void Awake() => m_director = GetComponent<SurvivalDirector>();

        private void OnDestroy()
        {
            if (m_canvas != null) Destroy(m_canvas.gameObject);
        }

        private void Update()
        {
            if (m_director == null || !m_director.IsSpawned || !m_director.IsRunOver) return;

            if (m_endedAt < 0f) m_endedAt = Time.time;
            if (Time.time - m_endedAt < OpenDelaySeconds) return;

            if (!m_built) Build();

            Refresh();

            // UHFPS locks the cursor again whenever it resumes play; this screen needs it for as long as it is up.
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }

        private void Refresh()
        {
            var won = m_director.Outcome == RunOutcome.Won;
            var total = m_director.TotalNights;
            var survived = m_director.NightsSurvived;

            m_title.text = won ? "YOU SURVIVED" : "THE NIGHT TOOK EVERYONE";
            m_title.color = won ? Gold : Blood;
            m_subtitle.text = $"{survived} of {total} {(total == 1 ? "night" : "nights")} survived";
            m_summary.text = m_director.RunSummary;
        }

        // ---- Choices ---------------------------------------------------------------------------------

        /// <summary>Host: reloads the level, as a new game unless <paramref name="fromSave"/>.</summary>
        private void Restart(bool fromSave)
        {
            if (m_choiceMade) return;

            if (!fromSave) SaveCatalog.Active?.StartFresh();

            var flow = FindAnyObjectByType<SessionFlow>();
            if (flow == null || !flow.RestartLevel()) return;

            ChoiceMade("Restarting...");
        }

        private async void Leave()
        {
            if (m_choiceMade) return;

            ChoiceMade("Leaving...");

            try
            {
                var flow = FindAnyObjectByType<SessionFlow>();
                if (flow != null) await flow.LeaveGameAsync();
            }
            catch (Exception exception)
            {
                Debug.LogException(exception, this);
            }
        }

        private void ChoiceMade(string status)
        {
            m_choiceMade = true;
            m_status.text = status;

            foreach (var button in new[] { m_primary, m_secondary, m_leave })
            {
                if (button != null) button.interactable = false;
            }
        }

        // ---- Layout ----------------------------------------------------------------------------------

        private void Build()
        {
            m_built = true;
            EnsureEventSystem();

            // Stops the player moving and looking about behind the screen; the dead are frozen already.
            var gameManager = LocalPlayerContext.GameManager;
            if (gameManager != null) gameManager.FreezePlayer(true, true);

            var canvasObject = new GameObject("RunEndScreen", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            m_canvas = canvasObject.GetComponent<Canvas>();
            m_canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            m_canvas.sortingOrder = SortingOrder;

            var scaler = canvasObject.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 0.5f;

            var backdrop = CreateRect<Image>(canvasObject.transform, "Backdrop");
            Stretch(backdrop.rectTransform);
            backdrop.color = new Color(0f, 0f, 0f, 0.85f);

            m_title = CreateLabel(canvasObject.transform, "Title", 72f, new Vector2(0f, 330f), new Vector2(1600f, 100f));
            m_subtitle = CreateLabel(canvasObject.transform, "Subtitle", 32f, new Vector2(0f, 250f), new Vector2(1600f, 50f));
            m_subtitle.color = new Color(1f, 1f, 1f, 0.8f);

            m_summary = CreateLabel(canvasObject.transform, "Summary", 30f, new Vector2(0f, 20f), new Vector2(1200f, 380f));
            m_summary.enableWordWrapping = true;
            m_summary.lineSpacing = 8f;

            m_status = CreateLabel(canvasObject.transform, "Status", 26f, new Vector2(0f, -380f), new Vector2(1200f, 40f));
            m_status.color = new Color(1f, 1f, 1f, 0.7f);

            var isHost = m_director.IsServer;
            var won = m_director.Outcome == RunOutcome.Won;
            var resumed = SaveCatalog.Active != null && SaveCatalog.Active.HasResumedSave;

            if (isHost)
            {
                if (!won && resumed)
                {
                    m_primary = CreateButton(canvasObject.transform, "Retry from Save", new Vector2(-340f, -290f), () => Restart(fromSave: true));
                    m_secondary = CreateButton(canvasObject.transform, "New Game", new Vector2(0f, -290f), () => Restart(fromSave: false));
                    m_leave = CreateButton(canvasObject.transform, "End Game", new Vector2(340f, -290f), Leave);
                }
                else
                {
                    m_primary = CreateButton(canvasObject.transform, "Play Again", new Vector2(-170f, -290f), () => Restart(fromSave: false));
                    m_leave = CreateButton(canvasObject.transform, "End Game", new Vector2(170f, -290f), Leave);
                }

                m_status.text = "End Game closes the room for everyone.";
            }
            else
            {
                m_leave = CreateButton(canvasObject.transform, "Leave Game", new Vector2(0f, -290f), Leave);
                m_status.text = "Waiting for the host to play again...";
            }
        }

        private static void EnsureEventSystem()
        {
            if (EventSystem.current != null || FindAnyObjectByType<EventSystem>() != null) return;

            new GameObject("EventSystem", typeof(EventSystem), typeof(InputSystemUIInputModule));
        }

        private static T CreateRect<T>(Transform parent, string name) where T : Component
        {
            // Parented before the component exists, so it wakes already under its canvas.
            var holder = new GameObject(name, typeof(RectTransform));
            holder.transform.SetParent(parent, false);
            return holder.AddComponent<T>();
        }

        private static void Stretch(RectTransform rect)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = rect.offsetMax = Vector2.zero;
        }

        private static TextMeshProUGUI CreateLabel(Transform parent, string name, float size, Vector2 position, Vector2 box)
        {
            var label = CreateRect<TextMeshProUGUI>(parent, name);

            var rect = label.rectTransform;
            rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = position;
            rect.sizeDelta = box;

            label.font = TMP_Settings.defaultFontAsset;
            label.fontSize = size;
            label.alignment = TextAlignmentOptions.Center;
            label.raycastTarget = false;
            label.enableWordWrapping = false;
            return label;
        }

        private static Button CreateButton(Transform parent, string text, Vector2 position, Action onClick)
        {
            var image = CreateRect<Image>(parent, text);
            image.color = new Color(1f, 1f, 1f, 0.12f);

            var rect = image.rectTransform;
            rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = position;
            rect.sizeDelta = new Vector2(300f, 64f);

            var button = image.gameObject.AddComponent<Button>();
            var colors = button.colors;
            colors.highlightedColor = new Color(1f, 1f, 1f, 2.5f);
            colors.pressedColor = new Color(1f, 1f, 1f, 3.5f);
            colors.disabledColor = new Color(1f, 1f, 1f, 0.4f);
            button.colors = colors;
            button.onClick.AddListener(() => onClick());

            var label = CreateLabel(image.transform, "Label", 28f, Vector2.zero, rect.sizeDelta);
            Stretch(label.rectTransform);
            label.text = text;

            return button;
        }
    }
}
