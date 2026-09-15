using System.Collections.Generic;
using UnityEngine;

namespace QuietVillage.Multiplayer.Loading
{
    /// <summary>
    /// What the loading screen says: the splash tagline, the tips it rotates, and how long it lingers.
    /// </summary>
    /// <remarks>
    /// Design data, found by <see cref="LoadingScreen"/> at <c>Resources/Loading/LoadingScreenContent</c>. Without the asset
    /// the screen uses the defaults below, so a missing asset never breaks loading. Created by
    /// <c>Tools > Quiet Village > UI > Set Up Boot And Loading Screens</c>.
    /// </remarks>
    [CreateAssetMenu(fileName = "LoadingScreenContent", menuName = "Quiet Village/Loading Screen Content")]
    public class LoadingScreenContent : ScriptableObject
    {
        public const string ResourcePath = "Loading/LoadingScreenContent";

        [Tooltip("Line under the title on the startup splash.")]
        public string SplashTagline = DefaultTagline;

        [TextArea(2, 4)]
        [Tooltip("Shown one at a time while a level loads, in random order.")]
        public List<string> Tips = new(DefaultTips);

        [Tooltip("Seconds each tip stays up.")]
        [Min(2f)] public float SecondsPerTip = 7f;

        [Tooltip("Least time a level's loading screen stays up, so the tip and your role can be read.")]
        [Min(0f)] public float MinimumLevelSeconds = 3f;

        public const string DefaultTagline = "Hold the chapel. Survive the nights. Together.";

        public static readonly string[] DefaultTips =
        {
            "Barricades are the whole defence. Creatures that cannot path to you will tear through the nearest one instead.",
            "Scrap goes further in a Builder's hands. Let them repair while the others scavenge.",
            "A downed teammate has 30 seconds. Hold Use on them to get them back up, but watch your back while you do.",
            "Creatures will not step into light. Keep the generator fuelled, or drop a flare where you need it.",
            "Holy water drives back and stuns whatever is close. Save it for when a barricade is about to fall.",
            "The chapel bell stuns every creature in earshot, then needs a long rest. Ring it when it counts.",
            "Set a scrap trap outside a barricade by day. The first creature to break it will regret it.",
            "Press G to use your role's ability. Reinforce, heal, sense or scrounge: each one can turn a night.",
            "Stalkers go for whoever is alone. Stay together after dark.",
            "Brutes break barricades three times as fast. Reinforce the one they are heading for.",
            "When a Screamer finds you, the others are not far behind.",
            "Each dawn brings supplies, and the fallen return. Each night brings more creatures.",
            "Weapons will not kill them, but a solid hit makes a creature flinch long enough to get away.",
            "Locked crypts hold the best loot. Look for keys, lockpicks and padlock codes written on notes."
        };

        /// <summary>The project's content, or defaults when there is no asset.</summary>
        public static LoadingScreenContent LoadOrDefault()
        {
            var content = Resources.Load<LoadingScreenContent>(ResourcePath);
            return content != null ? content : CreateInstance<LoadingScreenContent>();
        }
    }
}
