namespace QuietVillage.Multiplayer.Characters
{
    /// <summary>
    /// Supplies the character a player had in the save this room is resuming, which wins over what they picked since.
    /// </summary>
    /// <remarks>
    /// A resumed game gives everyone back what they had, and their character is part of that: their role decided what
    /// they carry and how the team split its work. The save and the player's account belong to the bridge, so it
    /// answers; this module only asks, the same way <see cref="Flow.ISpawnGate"/> works.
    /// </remarks>
    public interface ISavedCharacterSource
    {
        /// <summary>Server: the character saved for this client's account.</summary>
        /// <returns><c>false</c> for a new game, or a player new to the save.</returns>
        bool TryGetSavedCharacter(ulong clientId, out CharacterChoice choice);
    }

    /// <summary>Where the bridge leaves its <see cref="ISavedCharacterSource"/>. Null means nobody's character is saved.</summary>
    public static class SavedCharacterSource
    {
        public static ISavedCharacterSource Active { get; set; }
    }
}
