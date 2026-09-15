using System.Collections.Generic;
using QuietVillage.Multiplayer.Bridge;
using QuietVillage.Multiplayer.Characters;
using TMPro;
using UHFPS.Runtime;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace QuietVillage.Gameplay.Survival
{
    /// <summary>
    /// The local player's role ability on the G key: when it is ready, using it, and what it shows.
    /// </summary>
    /// <remarks>
    /// Authority: owner for timing and for the abilities that touch only this player (Sense Creatures shows markers on
    /// this screen; Scrounge adds to this player's inventory, which is owner-run). Reinforce and Healing Area change shared
    /// state, so they ask the host (SurvivalDirector.Abilities.cs).
    ///
    /// Two kinds of limit: a cooldown in seconds (Healing Area, Sense Creatures), or once per day and night (Reinforce,
    /// Scrounge), which comes back when the next day begins. Both are kept here, on the director rather than on the player,
    /// so a player who dies and returns at dawn does not come back with everything reset.
    ///
    /// Added by <see cref="SurvivalDirector"/> on every client when it spawns. Keyboard only, like the host's test keys;
    /// UHFPS's input asset is left untouched.
    /// </remarks>
    public class RoleAbilities : MonoBehaviour
    {
        private const int SortingOrder = 41;
        private const float HealingCooldown = 90f;
        private const float SenseCooldown = 60f;
        private const float SenseSeconds = 8f;
        private const float ReinforceReach = 3.5f;

        private SurvivalDirector m_director;

        private float m_readyAt;
        private int m_usedOnNight = -1;
        private float m_senseUntil;

        private Canvas m_canvas;
        private TextMeshProUGUI m_label;
        private readonly List<TextMeshProUGUI> m_markers = new();

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
            var ability = LocalAbility(out var health);
            var visible = m_director != null && m_director.IsSpawned && ability != RoleAbility.None && health != null;
            if (m_canvas.enabled != visible) m_canvas.enabled = visible;
            if (!visible) return;

            m_label.text = $"[G] {NameOf(ability)}  ·  {StatusOf(ability)}";

            ShowSenseMarkers();

            if (Keyboard.current == null || !Keyboard.current.gKey.wasPressedThisFrame) return;
            if (!health.IsStanding || m_director.IsRunOver || !CanActNow()) return;

            Use(ability);
        }

        // ---- Using -----------------------------------------------------------------------------------

        private void Use(RoleAbility ability)
        {
            if (!IsReady(ability))
            {
                Hint(IsOncePerCycle(ability) ? "Already used. It comes back when the next day begins." : "Not ready yet.");
                return;
            }

            switch (ability)
            {
                case RoleAbility.Reinforce:
                    var barricade = TargetBarricade();
                    if (barricade == null)
                    {
                        Hint("Look at a barricade, or stand beside one, to reinforce it.");
                        return;
                    }

                    // Spent now, handed back if the host refuses; the answer comes to HandleReinforceResult.
                    m_usedOnNight = m_director.Night;
                    m_director.RequestReinforce(barricade.Index);
                    break;

                case RoleAbility.HealingArea:
                    m_readyAt = Time.time + HealingCooldown;
                    m_director.RequestHealingArea();
                    break;

                case RoleAbility.SenseCreatures:
                    if (NightCreature.All.Count == 0)
                    {
                        Hint("Nothing is out there. Yet.");
                        return;
                    }

                    m_readyAt = Time.time + SenseCooldown;
                    m_senseUntil = Time.time + SenseSeconds;
                    break;

                case RoleAbility.Scrounge:
                    var inventory = LocalPlayerContext.Inventory;
                    if (inventory == null || !inventory.AddItem(m_director.BuildItemGuid, 1, new ItemCustomData()))
                    {
                        Hint("No room to carry anything more.");
                        return;
                    }

                    m_usedOnNight = m_director.Night;
                    Hint("You scrounged up a piece of Scrap.");
                    break;
            }
        }

        internal void HandleReinforceResult(bool ok)
        {
            if (ok)
            {
                Hint("Barricade reinforced.");
                return;
            }

            m_usedOnNight = -1;
            Hint("That barricade cannot be reinforced right now.");
        }

        private Barricade TargetBarricade()
        {
            var camera = LocalPlayerContext.PlayerCamera;
            if (camera != null && Physics.Raycast(camera.transform.position, camera.transform.forward, out var hit, ReinforceReach,
                    Physics.DefaultRaycastLayers, QueryTriggerInteraction.Collide))
            {
                var aimed = hit.collider.GetComponentInParent<Barricade>();
                if (aimed != null) return aimed;
            }

            var player = LocalPlayerContext.Player;
            if (player == null) return null;

            Barricade nearest = null;
            var nearestDistance = ReinforceReach * ReinforceReach;
            foreach (var barricade in m_director.Barricades)
            {
                if (barricade == null) continue;

                var distance = Vector3.SqrMagnitude(barricade.transform.position - player.transform.position);
                if (distance >= nearestDistance) continue;

                nearestDistance = distance;
                nearest = barricade;
            }

            return nearest;
        }

        // ---- State -----------------------------------------------------------------------------------

        private static bool IsOncePerCycle(RoleAbility ability) => ability is RoleAbility.Reinforce or RoleAbility.Scrounge;

        private bool IsReady(RoleAbility ability) =>
            IsOncePerCycle(ability) ? m_usedOnNight != m_director.Night : Time.time >= m_readyAt;

        private string StatusOf(RoleAbility ability)
        {
            if (ability == RoleAbility.SenseCreatures && Time.time < m_senseUntil)
                return $"<color=#e05050>Sensing {Mathf.CeilToInt(m_senseUntil - Time.time)} s</color>";

            if (IsReady(ability)) return "<color=#80e080>Ready</color>";
            if (IsOncePerCycle(ability)) return "Used until next day";

            var seconds = Mathf.CeilToInt(m_readyAt - Time.time);
            return $"{seconds / 60}:{seconds % 60:00}";
        }

        private static string NameOf(RoleAbility ability) => ability switch
        {
            RoleAbility.Reinforce => "Reinforce",
            RoleAbility.HealingArea => "Healing Area",
            RoleAbility.SenseCreatures => "Sense Creatures",
            RoleAbility.Scrounge => "Scrounge",
            _ => string.Empty
        };

        private static RoleAbility LocalAbility(out PlayerHealthSync health)
        {
            health = null;
            var player = LocalPlayerContext.Player;
            if (player == null) return RoleAbility.None;

            health = player.GetComponent<PlayerHealthSync>();
            var character = player.GetComponent<PlayerCharacter>();
            return character != null && character.Character != null ? character.Character.Ability : RoleAbility.None;
        }

        /// <summary>Not from a menu, the inventory, or the end-of-run screen.</summary>
        private static bool CanActNow()
        {
            var gameManager = LocalPlayerContext.GameManager;
            return gameManager != null && !gameManager.IsPaused && !gameManager.IsInventoryShown;
        }

        private static void Hint(string message)
        {
            var gameManager = LocalPlayerContext.GameManager;
            if (gameManager != null) gameManager.ShowHintMessage(message, 2.5f);
        }

        // ---- Sense Creatures -------------------------------------------------------------------------

        /// <summary>A red marker over every creature, drawn on the screen so walls do not hide it, while sensing lasts.</summary>
        private void ShowSenseMarkers()
        {
            var sensing = Time.time < m_senseUntil;
            var camera = LocalPlayerContext.PlayerCamera;
            var shown = 0;

            if (sensing && camera != null)
            {
                foreach (var creature in NightCreature.All)
                {
                    if (creature == null || creature.IsDying) continue;

                    var world = creature.transform.position + Vector3.up * 1.2f;
                    var screen = camera.WorldToScreenPoint(world);
                    if (screen.z <= 0f) continue;

                    var marker = MarkerAt(shown++);
                    marker.enabled = true;
                    marker.rectTransform.position = screen;
                    marker.text = $"<b>[!]</b>\n<size=60%>{Mathf.RoundToInt(screen.z)} m</size>";
                }
            }

            for (var i = shown; i < m_markers.Count; i++) m_markers[i].enabled = false;
        }

        private TextMeshProUGUI MarkerAt(int index)
        {
            while (m_markers.Count <= index)
            {
                var marker = CreateLabel(m_canvas.transform, "SenseMarker", 34f);
                marker.color = new Color(0.9f, 0.2f, 0.2f, 0.9f);
                marker.rectTransform.anchorMin = marker.rectTransform.anchorMax = Vector2.zero;
                marker.rectTransform.sizeDelta = new Vector2(120f, 90f);
                m_markers.Add(marker);
            }

            return m_markers[index];
        }

        // ---- Layout ----------------------------------------------------------------------------------

        private void Build()
        {
            var canvasObject = new GameObject("RoleAbilityHud", typeof(Canvas), typeof(CanvasScaler));
            m_canvas = canvasObject.GetComponent<Canvas>();
            m_canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            m_canvas.sortingOrder = SortingOrder;
            m_canvas.enabled = false;

            var scaler = canvasObject.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 0.5f;

            m_label = CreateLabel(canvasObject.transform, "Ability", 22f);
            var rect = m_label.rectTransform;
            rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 1f);
            rect.pivot = new Vector2(0.5f, 1f);
            rect.anchoredPosition = new Vector2(0f, -108f);
            rect.sizeDelta = new Vector2(1200f, 32f);
            m_label.color = new Color(1f, 1f, 1f, 0.8f);
        }

        private static TextMeshProUGUI CreateLabel(Transform parent, string name, float size)
        {
            // Parented before the text component exists, so it wakes already under its canvas.
            var labelObject = new GameObject(name, typeof(RectTransform));
            labelObject.transform.SetParent(parent, false);
            var label = labelObject.AddComponent<TextMeshProUGUI>();

            label.font = TMP_Settings.defaultFontAsset;
            label.fontSize = size;
            label.alignment = TextAlignmentOptions.Center;
            label.raycastTarget = false;
            label.textWrappingMode = TextWrappingModes.NoWrap;
            return label;
        }
    }
}
