using System;
using System.Globalization;

namespace QuietVillage.Multiplayer.Characters
{
    /// <summary>Which character a player picked, and in which colour.</summary>
    /// <remarks>
    /// Travels as one short string (<see cref="Encode"/>) everywhere it has to go: player preferences, the room's player
    /// properties, the message to the host, the player's spawn data and the save file. One format means one parser, and
    /// a value written by any of them can be read by all of the others.
    ///
    /// Not validated here: an id may name a character that no longer exists. <see cref="CharacterCatalog.Resolve"/>
    /// makes any choice usable.
    /// </remarks>
    public readonly struct CharacterChoice : IEquatable<CharacterChoice>
    {
        private const char Separator = ':';

        /// <summary>Longest encoded choice accepted from another client, so a message cannot carry a novel.</summary>
        public const int MaxEncodedLength = 64;

        public readonly string CharacterId;
        public readonly int Variant;

        public CharacterChoice(string characterId, int variant)
        {
            CharacterId = characterId ?? string.Empty;
            Variant = Math.Max(0, variant);
        }

        /// <summary>No choice at all; resolves to the catalog's default character.</summary>
        public static CharacterChoice None => new(string.Empty, 0);

        public bool IsEmpty => string.IsNullOrEmpty(CharacterId);

        /// <summary>The choice as <c>id:variant</c>, or empty for <see cref="None"/>.</summary>
        public string Encode() => IsEmpty ? string.Empty : $"{CharacterId}{Separator}{Variant.ToString(CultureInfo.InvariantCulture)}";

        /// <summary>Reads what <see cref="Encode"/> wrote. Anything unreadable becomes <see cref="None"/>.</summary>
        public static CharacterChoice Parse(string encoded)
        {
            if (string.IsNullOrWhiteSpace(encoded) || encoded.Length > MaxEncodedLength) return None;

            var separator = encoded.LastIndexOf(Separator);
            if (separator <= 0) return new CharacterChoice(encoded.Trim(), 0);

            var id = encoded.Substring(0, separator).Trim();
            var variant = int.TryParse(encoded.Substring(separator + 1), NumberStyles.Integer, CultureInfo.InvariantCulture,
                out var parsed)
                ? parsed
                : 0;

            return new CharacterChoice(id, variant);
        }

        public bool Equals(CharacterChoice other) => CharacterId == other.CharacterId && Variant == other.Variant;

        public override bool Equals(object obj) => obj is CharacterChoice other && Equals(other);

        public override int GetHashCode() => HashCode.Combine(CharacterId, Variant);

        public override string ToString() => IsEmpty ? "(none)" : Encode();
    }
}
