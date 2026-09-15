using System;
using System.Collections.Generic;
using UnityEngine;

namespace QuietVillage.Multiplayer.Settings
{
    /// <summary>
    /// The machine's display and audio settings chosen in the main menu: resolution, display mode, quality, VSync and
    /// master volume. Kept in PlayerPrefs and applied as the game starts.
    /// </summary>
    /// <remarks>
    /// Applied before the first scene loads, so the boot screen already runs at the chosen resolution.
    ///
    /// UHFPS has its own in-game Options menu on the player prefab. With no options file of its own it reads the values
    /// currently in effect, which are these, so the two agree. Once a player saves options in-game, UHFPS applies its
    /// file whenever a level loads, and those win inside the level.
    /// </remarks>
    public static class DisplaySettings
    {
        private const string Prefix = "QuietVillage.Settings.";

        /// <summary>A resolution by size only; refresh rates are left to the display.</summary>
        public readonly struct Size : IEquatable<Size>
        {
            public readonly int Width;
            public readonly int Height;

            public Size(int width, int height)
            {
                Width = width;
                Height = height;
            }

            public bool Equals(Size other) => Width == other.Width && Height == other.Height;
            public override bool Equals(object obj) => obj is Size other && Equals(other);
            public override int GetHashCode() => HashCode.Combine(Width, Height);
            public override string ToString() => $"{Width} × {Height}";
        }

        /// <summary>The display modes offered, in menu order.</summary>
        public static readonly FullScreenMode[] Modes =
            { FullScreenMode.ExclusiveFullScreen, FullScreenMode.FullScreenWindow, FullScreenMode.Windowed };

        public static string ModeName(FullScreenMode mode) => mode switch
        {
            FullScreenMode.ExclusiveFullScreen => "Fullscreen",
            FullScreenMode.FullScreenWindow => "Borderless window",
            _ => "Windowed"
        };

        /// <summary>Every size the display offers, smallest first, each once.</summary>
        public static List<Size> AvailableSizes()
        {
            var sizes = new List<Size>();
            foreach (var resolution in Screen.resolutions)
            {
                var size = new Size(resolution.width, resolution.height);
                if (!sizes.Contains(size)) sizes.Add(size);
            }

            // In the editor, or on a display that reports nothing, offer at least the current size.
            var current = CurrentSize;
            if (!sizes.Contains(current)) sizes.Add(current);

            sizes.Sort((a, b) => a.Width != b.Width ? a.Width.CompareTo(b.Width) : a.Height.CompareTo(b.Height));
            return sizes;
        }

        public static Size CurrentSize => Screen.fullScreenMode == FullScreenMode.Windowed
            ? new Size(Screen.width, Screen.height)
            : new Size(Screen.currentResolution.width, Screen.currentResolution.height);

        public static FullScreenMode CurrentMode => Screen.fullScreenMode;

        public static int CurrentQuality => QualitySettings.GetQualityLevel();

        public static bool CurrentVSync => QualitySettings.vSyncCount > 0;

        public static float CurrentVolume => AudioListener.volume;

        /// <summary>Applies the given settings now and remembers them for next time.</summary>
        public static void Apply(Size size, FullScreenMode mode, int quality, bool vsync, float volume)
        {
            if (quality >= 0 && quality < QualitySettings.names.Length) QualitySettings.SetQualityLevel(quality, true);

            // After the quality level, which carries its own VSync setting.
            QualitySettings.vSyncCount = vsync ? 1 : 0;
            AudioListener.volume = Mathf.Clamp01(volume);

            if (size.Width > 0 && size.Height > 0 && (!size.Equals(CurrentSize) || mode != CurrentMode))
                Screen.SetResolution(size.Width, size.Height, mode);

            PlayerPrefs.SetInt(Prefix + "Width", size.Width);
            PlayerPrefs.SetInt(Prefix + "Height", size.Height);
            PlayerPrefs.SetInt(Prefix + "Mode", (int)mode);
            PlayerPrefs.SetInt(Prefix + "Quality", quality);
            PlayerPrefs.SetInt(Prefix + "VSync", vsync ? 1 : 0);
            PlayerPrefs.SetFloat(Prefix + "Volume", Mathf.Clamp01(volume));
            PlayerPrefs.Save();
        }

        /// <summary>Puts back what the player last chose, before anything is shown.</summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void ApplySaved()
        {
            if (!PlayerPrefs.HasKey(Prefix + "Quality")) return;

            var quality = PlayerPrefs.GetInt(Prefix + "Quality", CurrentQuality);
            if (quality >= 0 && quality < QualitySettings.names.Length) QualitySettings.SetQualityLevel(quality, true);

            QualitySettings.vSyncCount = PlayerPrefs.GetInt(Prefix + "VSync", QualitySettings.vSyncCount) > 0 ? 1 : 0;
            AudioListener.volume = Mathf.Clamp01(PlayerPrefs.GetFloat(Prefix + "Volume", AudioListener.volume));

            // The editor's Game view is not a real screen; resizing it from here only fights the editor.
            if (Application.isEditor) return;

            var width = PlayerPrefs.GetInt(Prefix + "Width", 0);
            var height = PlayerPrefs.GetInt(Prefix + "Height", 0);
            var mode = (FullScreenMode)PlayerPrefs.GetInt(Prefix + "Mode", (int)Screen.fullScreenMode);
            if (width > 0 && height > 0) Screen.SetResolution(width, height, mode);
        }
    }
}
