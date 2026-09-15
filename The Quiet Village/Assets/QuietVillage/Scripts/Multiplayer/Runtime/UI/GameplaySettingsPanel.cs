using System;
using System.Collections.Generic;
using System.Globalization;
using QuietVillage.Multiplayer.Sessions;
using UnityEngine;
using UnityEngine.UIElements;

namespace QuietVillage.Multiplayer.UI
{
    /// <summary>
    /// The waiting room's Game Settings: a preset for everyone to see, and for the host, sliders under Custom.
    /// </summary>
    /// <remarks>
    /// Shows the room's settings (<see cref="SessionService.RoomSettings"/>) and, for the host, changes them there, so
    /// everyone waiting sees each change as it is made. The host's last settings are remembered on this machine and start
    /// its next room. Locked for everyone once the game is starting, and for good in a room that resumes a save, which
    /// keeps the settings it was played with.
    ///
    /// Owned by <see cref="LobbyScreen"/>, which says when it may be edited; this only runs the section.
    /// </remarks>
    internal sealed class GameplaySettingsPanel
    {
        private const string PrefsKey = "QuietVillage.GameplaySettings";

        private static readonly List<string> PresetNames = new() { "Easy", "Normal", "Hard", "Custom" };

        private readonly DropdownField m_preset;
        private readonly Label m_summary;
        private readonly Label m_hint;
        private readonly VisualElement m_custom;
        private readonly List<SettingSlider> m_sliders = new();
        private Toggle m_deadReturn;

        private SessionService m_sessions;
        private bool m_canEdit;

        /// <summary>False when the layout lacks the section, e.g. an older LobbyScreen.uxml; rooms then play Normal.</summary>
        public bool IsAvailable { get; }

        /// <summary>The host's settings from its last room, or Normal: what a new room starts with.</summary>
        public static GameplaySettings Remembered => GameplaySettings.Parse(PlayerPrefs.GetString(PrefsKey, string.Empty));

        public GameplaySettingsPanel(VisualElement root)
        {
            m_preset = root.Q<DropdownField>("settings-preset");
            m_summary = root.Q<Label>("settings-summary");
            m_hint = root.Q<Label>("settings-hint");
            m_custom = root.Q("settings-custom");

            IsAvailable = m_preset != null && m_summary != null && m_hint != null && m_custom != null;
            if (!IsAvailable) return;

            m_preset.choices = PresetNames;
            m_preset.RegisterValueChangedCallback(evt => ChoosePreset(evt.newValue));

            AddSlider("Nights to survive", GameplaySettings.NightsRange, s => s.Nights, (s, v) => s.AsCustom(nights: v), Whole);
            AddSlider("Day length", GameplaySettings.DayLengthRange, s => s.DayLength, (s, v) => s.AsCustom(dayLength: v), Percent);
            AddSlider("Night length", GameplaySettings.NightLengthRange, s => s.NightLength, (s, v) => s.AsCustom(nightLength: v), Percent);
            AddSlider("Creatures", GameplaySettings.CreatureCountRange, s => s.CreatureCount, (s, v) => s.AsCustom(creatureCount: v), Times);
            AddSlider("Creature damage", GameplaySettings.CreatureDamageRange, s => s.CreatureDamage, (s, v) => s.AsCustom(creatureDamage: v), Times);
            AddSlider("Creature speed", GameplaySettings.CreatureSpeedRange, s => s.CreatureSpeed, (s, v) => s.AsCustom(creatureSpeed: v), Times);
            AddSlider("Loot", GameplaySettings.LootAmountRange, s => s.LootAmount, (s, v) => s.AsCustom(lootAmount: v), Times);
            AddSlider("Fuel per canister", GameplaySettings.FuelPerCanisterRange, s => s.FuelPerCanister, (s, v) => s.AsCustom(fuelPerCanister: v), Seconds);
            AddDeadReturnToggle();
        }

        public void Bind(SessionService sessions) => m_sessions = sessions;

        /// <summary>Shows the room's current settings.</summary>
        /// <param name="canEdit">True for the host, before the game starts, in a room that does not resume a save.</param>
        public void Refresh(bool canEdit)
        {
            if (!IsAvailable || m_sessions == null || !m_sessions.IsConnected) return;

            var settings = m_sessions.RoomSettings;
            var locked = m_sessions.RoomSettingsLocked;
            m_canEdit = canEdit && !locked;

            m_preset.SetValueWithoutNotify(PresetNames[(int)settings.Preset]);
            m_preset.SetEnabled(m_canEdit);

            m_summary.text = settings.Summary();
            m_hint.text = locked ? "This room continues a saved game, so it keeps the settings it was played with."
                : !m_sessions.IsHost ? "The host chooses these."
                : canEdit ? "Pick a preset, or Custom to set each one."
                : string.Empty;

            // Sliders only for the host's Custom; everyone else reads the summary.
            var showSliders = m_canEdit && settings.Preset == GameplayPreset.Custom;
            m_custom.EnableInClassList("mp-hidden", !showSliders);

            foreach (var slider in m_sliders) slider.Show(settings);
            m_deadReturn?.SetValueWithoutNotify(settings.DeadReturn);
        }

        private void ChoosePreset(string name)
        {
            if (!m_canEdit || m_sessions == null) return;

            var index = PresetNames.IndexOf(name);
            if (index < 0) return;

            var preset = (GameplayPreset)index;

            // Custom starts from whatever is set now, so choosing it changes nothing until a slider moves.
            var settings = preset == GameplayPreset.Custom ? m_sessions.RoomSettings.AsCustom() : GameplaySettings.ForPreset(preset);
            Apply(settings);
        }

        private void Apply(GameplaySettings settings)
        {
            m_sessions.SetRoomSettings(settings);

            PlayerPrefs.SetString(PrefsKey, settings.Encode());
            PlayerPrefs.Save();
        }

        // ---- Sliders ---------------------------------------------------------------------------------

        private void AddDeadReturnToggle()
        {
            var row = new VisualElement();
            row.AddToClassList("mp-setting-row");

            m_deadReturn = new Toggle("Dead return at dawn");
            m_deadReturn.AddToClassList("mp-setting-toggle");
            m_deadReturn.RegisterValueChangedCallback(evt =>
            {
                if (!m_canEdit || m_sessions == null) return;
                Apply(m_sessions.RoomSettings.AsCustom(deadReturn: evt.newValue));
            });

            row.Add(m_deadReturn);
            m_custom.Add(row);
        }

        private static string Percent(float value) => $"{Mathf.RoundToInt(value * 100f)}%";

        private static string Times(float value) => $"×{value.ToString("0.00", CultureInfo.InvariantCulture)}";

        private static string Seconds(float value) => $"{Mathf.RoundToInt(value)} s";

        private static string Whole(float value) => Mathf.RoundToInt(value).ToString(CultureInfo.InvariantCulture);

        private void AddSlider(string label, GameplaySettings.Range range, Func<GameplaySettings, float> read,
            Func<GameplaySettings, float, GameplaySettings> write, Func<float, string> format)
        {
            var slider = new SettingSlider(label, range, read, format);
            slider.Changed += value =>
            {
                if (!m_canEdit || m_sessions == null) return;
                Apply(write(m_sessions.RoomSettings, value));
            };

            m_custom.Add(slider.Row);
            m_sliders.Add(slider);
        }

        /// <summary>One labelled slider and its value.</summary>
        private sealed class SettingSlider
        {
            private readonly Slider m_slider;
            private readonly Label m_value;
            private readonly Func<GameplaySettings, float> m_read;
            private readonly Func<float, string> m_format;

            public VisualElement Row { get; }

            public event Action<float> Changed;

            public SettingSlider(string label, GameplaySettings.Range range, Func<GameplaySettings, float> read, Func<float, string> format)
            {
                m_read = read;
                m_format = format;

                Row = new VisualElement();
                Row.AddToClassList("mp-setting-row");

                m_slider = new Slider(label, range.Min, range.Max);
                m_slider.AddToClassList("mp-setting-slider");
                m_slider.RegisterValueChangedCallback(evt =>
                {
                    m_value.text = m_format(evt.newValue);
                    Changed?.Invoke(evt.newValue);
                });
                Row.Add(m_slider);

                m_value = new Label();
                m_value.AddToClassList("mp-setting-value");
                Row.Add(m_value);
            }

            public void Show(GameplaySettings settings)
            {
                var value = m_read(settings);

                // Not while the host is dragging it: the room's value lags the drag by a round trip.
                if (m_slider.panel?.focusController?.focusedElement != m_slider) m_slider.SetValueWithoutNotify(value);
                m_value.text = m_format(m_slider.value);
            }
        }
    }
}
