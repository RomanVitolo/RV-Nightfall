using System;
using System.Collections.Generic;
using UnityEngine;

namespace QuietVillage.Multiplayer.Characters
{
    /// <summary>The one thing a role can do on demand (the ability key), besides its perks. Carried out by gameplay.</summary>
    /// <remarks>Stored by number in the catalog asset, so add new abilities at the end.</remarks>
    public enum RoleAbility
    {
        None,

        /// <summary>Braces one barricade well beyond full, once per day and night.</summary>
        Reinforce,

        /// <summary>Heals every teammate close by, on a cooldown.</summary>
        HealingArea,

        /// <summary>Shows where every creature is, through walls, for a few seconds, on a cooldown.</summary>
        SenseCreatures,

        /// <summary>Turns up a piece of scrap anywhere, once per day and night.</summary>
        Scrounge
    }

    /// <summary>
    /// The characters a player can be: each one a role, with its bodies, perks and the items it starts with.
    /// </summary>
    /// <remarks>
    /// The one list every part of the game agrees on, like <see cref="Levels.LevelCatalog"/>. The lobby offers these,
    /// <see cref="CharacterSelections"/> checks what clients send against it, the player spawns with one of them, and
    /// the bridge and gameplay read perks from it. A character is its role: picking the Scout picks the Scout's body.
    ///
    /// Lives in the module so the lobby can show it, which means it holds nothing typed by UHFPS. Starting items are
    /// UHFPS item GUIDs as plain strings, filled in by title by <c>Tools > Quiet Village > Characters > Set Up
    /// Characters</c>, and perks are plain numbers that the bridge and gameplay code apply.
    ///
    /// Characters are identified by <see cref="Character.Id"/>, which is stored in saves and player preferences, so
    /// renaming a character is safe but changing its id forgets everyone's choice of it.
    /// </remarks>
    [CreateAssetMenu(fileName = "CharacterCatalog", menuName = "Multiplayer/Character Catalog")]
    public class CharacterCatalog : ScriptableObject
    {
        [Serializable]
        public class Variant
        {
            [Tooltip("Name shown on the colour button, e.g. \"Rust\".")]
            public string DisplayName;

            [Tooltip("Body prefab. Must be rigged to the same Humanoid Avatar as its character.")]
            public GameObject Prefab;
        }

        [Serializable]
        public class StartingItem
        {
            [Tooltip("Item title in the UHFPS inventory database; the setup tool finds the GUID from it.")]
            public string Title;

            [Tooltip("UHFPS item GUID. Filled from the title by the setup tool when empty.")]
            public string ItemGuid;

            [Tooltip("Inventory icon shown on the Character screen. Copied from the item by the setup tool when empty.")]
            public Sprite Icon;

            [Min(1)] public int Quantity = 1;
        }

        /// <summary>What a role does better than the rest. 1 and 0 mean "no different".</summary>
        [Serializable]
        public class Perks
        {
            [Tooltip("Multiplies the health one building item adds to a barricade.")]
            [Min(0.1f)] public float BarricadeRepair = 1f;

            [Tooltip("Multiplies the health this player's own healing items restore.")]
            [Min(0.1f)] public float Healing = 1f;

            [Tooltip("Multiplies UHFPS's run speed.")]
            [Min(0.1f)] public float RunSpeed = 1f;

            [Tooltip("Locked inventory slots unlocked on a fresh start. UHFPS's expandable rows cap how many exist.")]
            [Min(0)] public int ExtraInventorySlots;

            [Tooltip("Multiplies how fast this player revives a downed teammate: 2 takes half the hold.")]
            [Min(0.1f)] public float ReviveSpeed = 1f;

            [Tooltip("Multiplies the health a teammate this player revives gets back up with.")]
            [Min(0.1f)] public float ReviveHealth = 1f;
        }

        [Serializable]
        public class Character
        {
            [Tooltip("Stable id stored in saves and preferences. Do not change once players have used it.")]
            public string Id;

            [Tooltip("Name of the body, e.g. \"Sci-Fi Worker\".")]
            public string DisplayName;

            [Tooltip("Role shown everywhere the character is, e.g. \"Builder\".")]
            public string Role;

            [TextArea(2, 4)] public string Description;

            [Tooltip("Humanoid Avatar every variant's rig maps through. The shared body animations retarget with it.")]
            public Avatar HumanoidAvatar;

            [Tooltip("Uniform scale of the body, for rigs that are not human height.")]
            [Min(0.1f)] public float BodyScale = 1f;

            public List<Variant> Variants = new();
            public Perks Perks = new();

            [Tooltip("What this role does on the ability key.")]
            public RoleAbility Ability;
            public List<StartingItem> StartingItems = new();

            /// <summary>The variant at an index, clamped into range; <c>null</c> if there are none.</summary>
            public Variant VariantAt(int index)
            {
                if (Variants == null || Variants.Count == 0) return null;
                return Variants[Mathf.Clamp(index, 0, Variants.Count - 1)];
            }

            /// <summary>The role, or the character's name when no role was written.</summary>
            public string RoleOrName => !string.IsNullOrWhiteSpace(Role) ? Role : DisplayName;
        }

        [Tooltip("Humanoid controller every body plays. Generated by the HEROPLAYER setup (WorkerAnimator).")]
        [SerializeField] private RuntimeAnimatorController m_bodyAnimator;

        [SerializeField] private List<Character> m_characters = new();

        private static readonly Perks s_noPerks = new();

        /// <summary>Every character, in the order the lobby lists them.</summary>
        public IReadOnlyList<Character> Characters => m_characters;

        /// <summary>The controller that animates every body, in the lobby preview and in game.</summary>
        public RuntimeAnimatorController BodyAnimator => m_bodyAnimator;

        /// <summary>Perks that change nothing, for a player whose character is unknown.</summary>
        public static Perks NoPerks => s_noPerks;

        /// <summary>The character a player gets when they have not chosen one: the first usable one listed.</summary>
        public Character Default
        {
            get
            {
                foreach (var character in m_characters)
                {
                    if (IsUsable(character)) return character;
                }

                return null;
            }
        }

        /// <summary>Finds a usable character by id.</summary>
        /// <returns><c>null</c> if no usable character has that id.</returns>
        public Character Find(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;

            foreach (var character in m_characters)
            {
                if (IsUsable(character) && character.Id == id) return character;
            }

            return null;
        }

        /// <summary>
        /// Turns any choice into one that exists: an unknown character becomes the default, a variant out of range the
        /// nearest one.
        /// </summary>
        /// <remarks>
        /// Every choice that arrives from outside, from another client, a save or old preferences, goes through here, so
        /// nothing downstream has to handle a character that was removed or a colour that never existed.
        /// </remarks>
        public CharacterChoice Resolve(CharacterChoice choice)
        {
            var character = Find(choice.CharacterId) ?? Default;
            if (character == null) return CharacterChoice.None;

            var variant = Mathf.Clamp(choice.Variant, 0, Mathf.Max(0, character.Variants.Count - 1));
            return new CharacterChoice(character.Id, variant);
        }

        /// <summary>The role shown for a choice, e.g. in the room's player list; empty if it resolves to nothing.</summary>
        public string RoleOf(CharacterChoice choice)
        {
            var character = Find(Resolve(choice).CharacterId);
            return character != null ? character.RoleOrName : string.Empty;
        }

        /// <summary>One line per perk that differs from the norm, for the Character screen.</summary>
        public static List<string> Describe(Perks perks)
        {
            var lines = new List<string>();
            if (perks == null) return lines;

            AddMultiplier(lines, perks.BarricadeRepair, "barricade strength per item used");
            AddMultiplier(lines, perks.Healing, "healing from medical items");
            AddMultiplier(lines, perks.RunSpeed, "running speed");
            AddMultiplier(lines, perks.ReviveSpeed, "reviving speed");
            AddMultiplier(lines, perks.ReviveHealth, "health for teammates they revive");

            if (perks.ExtraInventorySlots > 0)
                lines.Add($"+{perks.ExtraInventorySlots} inventory slots");

            return lines;
        }

        /// <summary>What an ability does, in a sentence for the Character screen; empty for none.</summary>
        public static string DescribeAbility(RoleAbility ability) => ability switch
        {
            RoleAbility.Reinforce => "Ability (G): brace a barricade to 150%, once per day and night.",
            RoleAbility.HealingArea => "Ability (G): heal every teammate within 5 m (90 s cooldown).",
            RoleAbility.SenseCreatures => "Ability (G): see every creature through walls for 8 s (60 s cooldown).",
            RoleAbility.Scrounge => "Ability (G): scrounge a piece of Scrap anywhere, once per day and night.",
            _ => string.Empty
        };

        /// <summary>The perks in a few words, e.g. "+50% repair · +2 slots", for a roster card; empty if there are none.</summary>
        /// <remarks>Short enough to compare roles at a glance down the list; <see cref="Describe"/> says what each means.</remarks>
        public static string Summarise(Perks perks)
        {
            var parts = new List<string>();
            if (perks == null) return string.Empty;

            AddMultiplier(parts, perks.BarricadeRepair, "repair");
            AddMultiplier(parts, perks.Healing, "healing");
            AddMultiplier(parts, perks.RunSpeed, "speed");
            AddMultiplier(parts, perks.ReviveSpeed, "revive");

            if (perks.ExtraInventorySlots > 0) parts.Add($"+{perks.ExtraInventorySlots} slots");

            return string.Join("  ·  ", parts);
        }

        private static void AddMultiplier(List<string> lines, float multiplier, string what)
        {
            var percent = Mathf.RoundToInt((multiplier - 1f) * 100f);
            if (percent != 0) lines.Add($"{(percent > 0 ? "+" : "")}{percent}% {what}");
        }

        /// <summary>A character the game can actually put in a level: it has an id and a body.</summary>
        private static bool IsUsable(Character character)
        {
            if (character == null || string.IsNullOrEmpty(character.Id) || character.Variants == null) return false;

            foreach (var variant in character.Variants)
            {
                if (variant != null && variant.Prefab != null) return true;
            }

            return false;
        }

#if UNITY_EDITOR
        /// <summary>Edit-time access for the character tool, which seeds and completes entries.</summary>
        public List<Character> EditableCharacters => m_characters;

        public RuntimeAnimatorController EditableBodyAnimator
        {
            get => m_bodyAnimator;
            set => m_bodyAnimator = value;
        }
#endif
    }
}
