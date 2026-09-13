using System;
using System.Collections.Generic;
using UnityEngine;

namespace QuietVillage.Multiplayer.Levels
{
    /// <summary>
    /// The levels a room can be played in: one scene per settlement, each named for the lobby.
    /// </summary>
    /// <remarks>
    /// The one list every part of the flow agrees on. The lobby offers these, <see cref="Flow.SessionFlow"/> loads
    /// one, <see cref="Flow.NetworkPlayerSpawner"/> spawns players into any of them, and the menus treat any of them
    /// as "in game". A scene not listed here is never a level, even if it is in Build Settings, which keeps UHFPS's
    /// demo and menu scenes from being mistaken for one.
    ///
    /// Levels are identified by scene name because Netcode loads scenes by name, so two level scenes must not share
    /// a name even in different folders. Tools > Quiet Village > Multiplayer > Levels keeps this list and Build Settings in step.
    /// </remarks>
    [CreateAssetMenu(fileName = "LevelCatalog", menuName = "Multiplayer/Level Catalog")]
    public class LevelCatalog : ScriptableObject
    {
        [Serializable]
        public class Level
        {
            [Tooltip("Name shown in the lobby.")]
            public string DisplayName;

            [Tooltip("Scene file name, without folder or extension. Must be in Build Settings.")]
            public string SceneName;

            [Tooltip("One line shown under the picker, e.g. what makes this settlement different.")]
            [TextArea(1, 3)]
            public string Description;
        }

        [SerializeField] private List<Level> m_levels = new();

        /// <summary>Every level, in the order the lobby lists them.</summary>
        public IReadOnlyList<Level> Levels => m_levels;

        /// <summary>The level a room gets when nothing else chose one: the first listed.</summary>
        public Level Default
        {
            get
            {
                foreach (var level in m_levels)
                {
                    if (level != null && !string.IsNullOrEmpty(level.SceneName)) return level;
                }

                return null;
            }
        }

        /// <summary>Finds a level by its scene name.</summary>
        /// <returns><c>null</c> if the scene is not a listed level.</returns>
        public Level Find(string sceneName)
        {
            if (string.IsNullOrEmpty(sceneName)) return null;

            foreach (var level in m_levels)
            {
                if (level != null && level.SceneName == sceneName) return level;
            }

            return null;
        }

        /// <summary>True if the scene is one of the listed levels.</summary>
        public bool Contains(string sceneName) => Find(sceneName) != null;

        /// <summary>The name to show for a scene: its level's display name, or the scene name if it has none.</summary>
        public string DisplayNameOf(string sceneName)
        {
            var level = Find(sceneName);
            return level != null && !string.IsNullOrWhiteSpace(level.DisplayName) ? level.DisplayName : sceneName;
        }

#if UNITY_EDITOR
        /// <summary>Edit-time access for the level tools, which add and rename entries.</summary>
        public List<Level> EditableLevels => m_levels;
#endif
    }
}
