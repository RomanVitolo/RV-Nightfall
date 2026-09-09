using System;
using Newtonsoft.Json;

namespace UHFPS.Runtime
{
    public enum AttributesCategory
    {
        Player  = 0,
        Item    = 1
    }

    /// <summary>
    /// Represents the base class for all attribute types.
    /// </summary>
    [Serializable]
    public abstract class AttributesBase : ISerializedReferenceListItem
    {
        /// <summary>Human-readable name shown in UI/Debug.</summary>
        [JsonIgnore]
        public abstract string Name { get; }

        /// <summary>Category of this attribute.</summary>
        [JsonIgnore]
        public abstract AttributesCategory Category { get; }
    }

    /// <summary>
    /// Represents an attribute that can be applied to a player.
    /// </summary>
    [Serializable]
    public abstract class PlayerAttribute : AttributesBase
    {
        public override string Name => "Player/";
        public override AttributesCategory Category => AttributesCategory.Player;

        /// <summary>Priority used when applying multiple attributes. Higher priority attributes are applied first.</summary>
        [JsonIgnore]
        public abstract int Priority { get; }

        /// <summary>Applies the attribute to the player.</summary>
        public abstract void Apply(PlayerManager player);
    }

    /// <summary>
    /// Represents an attribute that contains item-related properties.
    /// </summary>
    [Serializable]
    public abstract class ItemAttribute : AttributesBase
    {
        public override string Name => "Item/";
        public override AttributesCategory Category => AttributesCategory.Item;
    }
}
