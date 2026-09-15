using System;
using System.Globalization;
using System.Text;
using UnityEngine;

namespace QuietVillage.Multiplayer.Sessions
{
    /// <summary>A named set of <see cref="GameplaySettings"/>, or Custom once any value is changed by hand.</summary>
    public enum GameplayPreset : byte { Easy, Normal, Hard, Custom }

    /// <summary>
    /// How a room plays: how many nights to survive, the length of day and night, how many creatures come and how hard
    /// they hit, and how much loot and fuel there is. Chosen by the host in the waiting room.
    /// </summary>
    /// <remarks>
    /// Scales, not absolute values, so one set fits every level: 1 is the level as built. Travels as one short string,
    /// like <c>CharacterChoice</c>: a room property for everyone waiting to see, the host's preferences for its next room,
    /// and the save file, so a resumed game keeps the settings it was played with.
    ///
    /// Every value is clamped to its range when read, so a value from an old save or a tampered property can never ask
    /// for a zero-length night or a thousand creatures.
    /// </remarks>
    public readonly struct GameplaySettings : IEquatable<GameplaySettings>
    {
        public readonly GameplayPreset Preset;

        /// <summary>Day length, as a share of the level's own.</summary>
        public readonly float DayLength;

        /// <summary>Night length, as a share of the level's own.</summary>
        public readonly float NightLength;

        /// <summary>Creatures per night, as a share of the level's own count.</summary>
        public readonly float CreatureCount;

        /// <summary>Damage creatures deal to players and barricades, as a share.</summary>
        public readonly float CreatureDamage;

        /// <summary>Creature movement speed, as a share.</summary>
        public readonly float CreatureSpeed;

        /// <summary>How much of the level's loot each game keeps, as a share.</summary>
        public readonly float LootAmount;

        /// <summary>Seconds of floodlight one fuel canister gives.</summary>
        public readonly float FuelPerCanister;

        /// <summary>Nights the room must survive to win the run.</summary>
        public readonly int Nights;

        /// <summary>Whether players who died come back at dawn. Off makes death permanent for the rest of the run.</summary>
        public readonly bool DeadReturn;

        public static readonly Range DayLengthRange = new(0.5f, 2f);
        public static readonly Range NightLengthRange = new(0.5f, 2f);
        public static readonly Range CreatureCountRange = new(0.5f, 3f);
        public static readonly Range CreatureDamageRange = new(0.25f, 2.5f);
        public static readonly Range CreatureSpeedRange = new(0.6f, 1.5f);
        public static readonly Range LootAmountRange = new(0.4f, 2f);
        public static readonly Range FuelPerCanisterRange = new(10f, 120f);
        public static readonly Range NightsRange = new(1f, 10f);

        /// <summary>Inclusive limits of one setting.</summary>
        public readonly struct Range
        {
            public readonly float Min;
            public readonly float Max;

            public Range(float min, float max)
            {
                Min = min;
                Max = max;
            }

            public float Clamp(float value) => float.IsNaN(value) ? Min : Mathf.Clamp(value, Min, Max);
        }

        public GameplaySettings(GameplayPreset preset, float dayLength, float nightLength, float creatureCount,
            float creatureDamage, float creatureSpeed, float lootAmount, float fuelPerCanister, float nights, bool deadReturn)
        {
            Preset = preset;
            DayLength = DayLengthRange.Clamp(dayLength);
            NightLength = NightLengthRange.Clamp(nightLength);
            CreatureCount = CreatureCountRange.Clamp(creatureCount);
            CreatureDamage = CreatureDamageRange.Clamp(creatureDamage);
            CreatureSpeed = CreatureSpeedRange.Clamp(creatureSpeed);
            LootAmount = LootAmountRange.Clamp(lootAmount);
            FuelPerCanister = FuelPerCanisterRange.Clamp(fuelPerCanister);
            Nights = Mathf.RoundToInt(NightsRange.Clamp(nights));
            DeadReturn = deadReturn;
        }

        // ---- Presets ---------------------------------------------------------------------------------

        /// <summary>The level as built, and what a room starts with.</summary>
        public static GameplaySettings Normal => new(GameplayPreset.Normal, 1f, 1f, 1f, 1f, 1f, 1f, 40f, 4f, true);

        /// <summary>Longer days, shorter nights, fewer and slower creatures that hit softer, more loot and fuel, fewer nights; the dead return at dawn.</summary>
        public static GameplaySettings Easy => new(GameplayPreset.Easy, 1.25f, 0.75f, 0.6f, 0.6f, 0.85f, 1.4f, 55f, 3f, true);

        /// <summary>Shorter days, longer nights, more and faster creatures that hit harder, less loot and fuel, more nights; death is permanent.</summary>
        public static GameplaySettings Hard => new(GameplayPreset.Hard, 0.8f, 1.25f, 1.5f, 1.4f, 1.15f, 0.7f, 30f, 5f, false);

        public static GameplaySettings ForPreset(GameplayPreset preset) => preset switch
        {
            GameplayPreset.Easy => Easy,
            GameplayPreset.Hard => Hard,
            _ => Normal
        };

        /// <summary>A Custom copy, with any of the values changed.</summary>
        /// <remarks>
        /// Always Custom, even when the values happen to match a preset: the host chose Custom and is adjusting it, and the
        /// sliders should not vanish because one landed back on Normal's value.
        /// </remarks>
        public GameplaySettings AsCustom(float? dayLength = null, float? nightLength = null, float? creatureCount = null,
            float? creatureDamage = null, float? creatureSpeed = null, float? lootAmount = null, float? fuelPerCanister = null,
            float? nights = null, bool? deadReturn = null) =>
            new(GameplayPreset.Custom, dayLength ?? DayLength, nightLength ?? NightLength, creatureCount ?? CreatureCount,
                creatureDamage ?? CreatureDamage, creatureSpeed ?? CreatureSpeed, lootAmount ?? LootAmount,
                fuelPerCanister ?? FuelPerCanister, nights ?? Nights, deadReturn ?? DeadReturn);

        /// <summary>One line for everyone to read, e.g. "4 nights · Day 125% · Night 75% · Creatures ×0.6 · …".</summary>
        public string Summary()
        {
            string Percent(float value) => $"{Mathf.RoundToInt(value * 100f)}%";
            string Times(float value) => $"×{value.ToString("0.##", CultureInfo.InvariantCulture)}";

            return $"{Nights} {(Nights == 1 ? "night" : "nights")}  ·  Day {Percent(DayLength)}  ·  Night {Percent(NightLength)}  ·  Creatures {Times(CreatureCount)}  ·  " +
                   $"Damage {Times(CreatureDamage)}  ·  Speed {Times(CreatureSpeed)}  ·  Loot {Times(LootAmount)}  ·  " +
                   $"Fuel {Mathf.RoundToInt(FuelPerCanister)} s per canister  ·  " +
                   (DeadReturn ? "Dead return at dawn" : "Permadeath");
        }

        private bool SameValues(GameplaySettings other) =>
            Mathf.Approximately(DayLength, other.DayLength) && Mathf.Approximately(NightLength, other.NightLength)
            && Mathf.Approximately(CreatureCount, other.CreatureCount) && Mathf.Approximately(CreatureDamage, other.CreatureDamage)
            && Mathf.Approximately(CreatureSpeed, other.CreatureSpeed) && Mathf.Approximately(LootAmount, other.LootAmount)
            && Mathf.Approximately(FuelPerCanister, other.FuelPerCanister) && Nights == other.Nights && DeadReturn == other.DeadReturn;

        // ---- Encoding --------------------------------------------------------------------------------

        private const char Separator = ';';
        private const char Assignment = '=';

        /// <summary>
        /// Longest encoded settings accepted, so a property cannot carry a novel. Well above what <see cref="Encode"/>
        /// writes.
        /// </summary>
        public const int MaxEncodedLength = 256;

        /// <summary>The settings as <c>preset=Hard;day=0.8;...</c>, invariant culture.</summary>
        public string Encode()
        {
            var builder = new StringBuilder();
            void Add(string key, string value)
            {
                if (builder.Length > 0) builder.Append(Separator);
                builder.Append(key).Append(Assignment).Append(value);
            }

            string Number(float value) => value.ToString("0.###", CultureInfo.InvariantCulture);

            Add("preset", Preset.ToString());
            Add("day", Number(DayLength));
            Add("night", Number(NightLength));
            Add("creatures", Number(CreatureCount));
            Add("damage", Number(CreatureDamage));
            Add("speed", Number(CreatureSpeed));
            Add("loot", Number(LootAmount));
            Add("fuel", Number(FuelPerCanister));
            Add("nights", Nights.ToString(CultureInfo.InvariantCulture));
            Add("return", DeadReturn ? "1" : "0");
            return builder.ToString();
        }

        /// <summary>
        /// Reads what <see cref="Encode"/> wrote. Missing or unreadable values fall back to Normal's; anything empty is Normal.
        /// </summary>
        public static GameplaySettings Parse(string encoded)
        {
            var normal = Normal;
            if (string.IsNullOrWhiteSpace(encoded) || encoded.Length > MaxEncodedLength) return normal;

            var preset = GameplayPreset.Normal;
            float day = normal.DayLength, night = normal.NightLength, creatures = normal.CreatureCount, damage = normal.CreatureDamage,
                speed = normal.CreatureSpeed, loot = normal.LootAmount, fuel = normal.FuelPerCanister, nights = normal.Nights;
            var deadReturn = normal.DeadReturn;

            foreach (var entry in encoded.Split(Separator))
            {
                var assignment = entry.IndexOf(Assignment);
                if (assignment <= 0) continue;

                var key = entry.Substring(0, assignment).Trim();
                var value = entry.Substring(assignment + 1).Trim();

                if (key == "preset")
                {
                    if (Enum.TryParse(value, out GameplayPreset parsedPreset) && Enum.IsDefined(typeof(GameplayPreset), parsedPreset))
                        preset = parsedPreset;
                    continue;
                }

                if (key == "return")
                {
                    deadReturn = value != "0";
                    continue;
                }

                if (!float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)) continue;

                switch (key)
                {
                    case "day": day = number; break;
                    case "night": night = number; break;
                    case "creatures": creatures = number; break;
                    case "damage": damage = number; break;
                    case "speed": speed = number; break;
                    case "loot": loot = number; break;
                    case "fuel": fuel = number; break;
                    case "nights": nights = number; break;
                }
            }

            // Settings saved before runs had several nights, or before death could be permanent, play Normal's.
            return new GameplaySettings(preset, day, night, creatures, damage, speed, loot, fuel, nights, deadReturn);
        }

        public bool Equals(GameplaySettings other) => Preset == other.Preset && SameValues(other);

        public override bool Equals(object obj) => obj is GameplaySettings other && Equals(other);

        // Nested: Combine takes at most eight values.
        public override int GetHashCode() => HashCode.Combine(HashCode.Combine(Preset, DayLength, NightLength, CreatureCount,
            CreatureDamage, CreatureSpeed, LootAmount, FuelPerCanister), Nights, DeadReturn);

        public override string ToString() => Encode();
    }
}
