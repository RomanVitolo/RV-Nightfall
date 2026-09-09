using System;
using System.Collections.Generic;

namespace UHFPS.Runtime
{
    [Serializable]
    public sealed class ItemAttributes : SerializedReferenceList<AttributesBase>
    {
        public ItemAttributes() : base() { }
        public ItemAttributes(ItemAttributes other) : base() 
        {
            Items = new List<AttributesBase>(other.Items);
        }

        /// <summary>
        /// Determines the attribute category for the specified attribute instance.
        /// </summary>
        /// <param name="attribute">The attribute instance to evaluate. Must not be null.</param>
        /// <returns>The category of the attribute, such as Player or Item. Returns 0 if the attribute does not match a known category.</returns>
        public static AttributesCategory OfGroup(AttributesBase attribute)
        {
            return attribute switch
            {
                PlayerAttribute => AttributesCategory.Player,
                ItemAttribute => AttributesCategory.Item,
                _ => 0
            };
        }
    }
}