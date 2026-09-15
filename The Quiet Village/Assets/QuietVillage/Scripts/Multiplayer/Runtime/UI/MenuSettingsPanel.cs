using System;
using System.Collections.Generic;
using QuietVillage.Multiplayer.Settings;
using UnityEngine;
using UnityEngine.UIElements;

namespace QuietVillage.Multiplayer.UI
{
    /// <summary>
    /// The main menu's Settings dialog: display and audio, applied with Apply and kept on this machine.
    /// </summary>
    /// <remarks>
    /// Reads what is in effect each time it opens rather than keeping its own copy, so it always shows the truth, even
    /// after UHFPS's in-game options changed something. Owned by <see cref="LobbyScreen"/>; this only runs the dialog.
    /// </remarks>
    internal sealed class MenuSettingsPanel
    {
        private readonly VisualElement m_dialog;
        private readonly DropdownField m_resolution;
        private readonly DropdownField m_mode;
        private readonly DropdownField m_quality;
        private readonly Toggle m_vsync;
        private readonly Slider m_volume;
        private readonly Label m_volumeValue;
        private readonly Label m_hint;

        private List<DisplaySettings.Size> m_sizes = new();

        public bool IsAvailable { get; }

        public bool IsOpen => IsAvailable && !m_dialog.ClassListContains("mp-hidden");

        public MenuSettingsPanel(VisualElement root)
        {
            m_dialog = root.Q("settings-dialog");
            m_resolution = root.Q<DropdownField>("options-resolution");
            m_mode = root.Q<DropdownField>("options-display-mode");
            m_quality = root.Q<DropdownField>("options-quality");
            m_vsync = root.Q<Toggle>("options-vsync");
            m_volume = root.Q<Slider>("options-volume");
            m_volumeValue = root.Q<Label>("options-volume-value");
            m_hint = root.Q<Label>("options-hint");

            var apply = root.Q<Button>("options-apply");
            var close = root.Q<Button>("options-close");

            IsAvailable = m_dialog != null && m_resolution != null && m_mode != null && m_quality != null && m_vsync != null
                          && m_volume != null && m_volumeValue != null && apply != null && close != null;
            if (!IsAvailable) return;

            apply.clicked += Apply;
            close.clicked += Close;

            m_volume.RegisterValueChangedCallback(evt =>
            {
                m_volumeValue.text = Percent(evt.newValue);

                // Heard straight away, so the player can judge it; kept only on Apply.
                AudioListener.volume = evt.newValue;
            });

            m_dialog.RegisterCallback<KeyDownEvent>(evt =>
            {
                if (evt.keyCode != KeyCode.Escape) return;
                Close();
                evt.StopPropagation();
            });
        }

        public void Open()
        {
            if (!IsAvailable) return;

            m_sizes = DisplaySettings.AvailableSizes();
            var sizeNames = new List<string>();
            foreach (var size in m_sizes) sizeNames.Add(size.ToString());
            m_resolution.choices = sizeNames;
            m_resolution.index = Mathf.Max(0, m_sizes.IndexOf(DisplaySettings.CurrentSize));

            var modeNames = new List<string>();
            foreach (var mode in DisplaySettings.Modes) modeNames.Add(DisplaySettings.ModeName(mode));
            m_mode.choices = modeNames;
            m_mode.index = Mathf.Max(0, Array.IndexOf(DisplaySettings.Modes, DisplaySettings.CurrentMode));

            m_quality.choices = new List<string>(QualitySettings.names);
            m_quality.index = Mathf.Clamp(DisplaySettings.CurrentQuality, 0, QualitySettings.names.Length - 1);

            m_vsync.SetValueWithoutNotify(DisplaySettings.CurrentVSync);

            m_volume.SetValueWithoutNotify(DisplaySettings.CurrentVolume);
            m_volumeValue.text = Percent(DisplaySettings.CurrentVolume);

            m_openVolume = DisplaySettings.CurrentVolume;

            if (m_hint != null)
                m_hint.text = Application.isEditor
                    ? "Resolution and display mode only take effect in a built game."
                    : "Saved on this computer. Options changed in-game take over inside a level.";

            m_dialog.EnableInClassList("mp-hidden", false);
            m_resolution.Focus();
        }

        // The volume when the dialog opened, put back if it is closed without applying.
        private float m_openVolume;

        public void Close()
        {
            if (!IsOpen) return;

            AudioListener.volume = m_openVolume;
            m_dialog.EnableInClassList("mp-hidden", true);
        }

        private void Apply()
        {
            var size = m_resolution.index >= 0 && m_resolution.index < m_sizes.Count ? m_sizes[m_resolution.index] : DisplaySettings.CurrentSize;
            var mode = m_mode.index >= 0 && m_mode.index < DisplaySettings.Modes.Length ? DisplaySettings.Modes[m_mode.index] : DisplaySettings.CurrentMode;

            DisplaySettings.Apply(size, mode, m_quality.index, m_vsync.value, m_volume.value);

            m_openVolume = m_volume.value;
            if (m_hint != null) m_hint.text = "Settings applied.";
        }

        private static string Percent(float value) => $"{Mathf.RoundToInt(value * 100f)}%";
    }
}
