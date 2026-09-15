using QuietVillage.Multiplayer.Sessions;
using Unity.Netcode;
using UnityEngine;

namespace QuietVillage.Gameplay.Survival
{
    /// <summary>
    /// The room's gameplay settings, applied to this level: phase lengths, creature numbers and strength, loot and fuel.
    /// </summary>
    /// <remarks>
    /// The host reads them from the room when the level spawns; they were locked when the room started, and a resumed
    /// save's room was created with the save's own. Most of what they change is decided on the host alone (creatures,
    /// damage, fuel). Two things every client works out for itself are sent with the spawn: how long day and night last,
    /// which drive the clock and the light, and the share of loot kept, which the loot seed needs to remove the same
    /// spots everywhere.
    /// </remarks>
    public partial class SurvivalDirector
    {
        private const string SaveSettings = "settings";

        private readonly NetworkVariable<float> m_dayLength =
            new(0f, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

        private readonly NetworkVariable<float> m_nightLength =
            new(0f, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

        // Whether the dead come back at dawn; clients need it for the dawn hint.
        private readonly NetworkVariable<bool> m_deadReturn =
            new(true, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

        private readonly NetworkVariable<float> m_lootAmount =
            new(1f, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

        // Server: the settings this level plays with.
        private GameplaySettings m_settings = GameplaySettings.Normal;

        /// <summary>Server: how the room plays. Normal outside a room.</summary>
        public GameplaySettings Settings => m_settings;

        /// <summary>Seconds of day, after the settings; the level's own length until the director spawns.</summary>
        private float DayLength => m_dayLength.Value > 0f ? m_dayLength.Value : m_dayDuration;

        private float NightLength => m_nightLength.Value > 0f ? m_nightLength.Value : m_nightDuration;

        /// <summary>Server, first thing on spawn: takes the room's settings and publishes what clients need of them.</summary>
        private void ApplyRoomSettings()
        {
            var sessions = FindAnyObjectByType<SessionService>();
            m_settings = sessions != null && sessions.IsConnected
                ? sessions.RoomSettings
                // Played outside a room, e.g. straight from the editor: the level as built, with its own fuel value.
                : new GameplaySettings(GameplayPreset.Normal, 1f, 1f, 1f, 1f, 1f, 1f, m_secondsPerCanister,
                    GameplaySettings.Normal.Nights, GameplaySettings.Normal.DeadReturn);

            m_dayLength.Value = m_dayDuration * m_settings.DayLength;
            m_nightLength.Value = m_nightDuration * m_settings.NightLength;
            m_lootAmount.Value = m_settings.LootAmount;
            m_totalNights.Value = Mathf.Max(1, m_settings.Nights);
            m_deadReturn.Value = m_settings.DeadReturn;

            Debug.Log($"{nameof(SurvivalDirector)}: playing with {m_settings.Preset} settings ({m_settings.Encode()}).", this);
        }

        /// <summary>
        /// How many of a loot group's spots to keep under the settings, e.g. 5 of 12 becomes 3 when loot is scarce.
        /// </summary>
        /// <remarks>
        /// A fractional share is settled by the seeded random, so every client agrees. Keys and padlock notes are left as
        /// they are: a crypt must stay openable however little loot there is, and two copies of a key are pointless.
        /// </remarks>
        private int ScaledKeep(string group, int keep, System.Random random)
        {
            if (group.StartsWith("key:") || group.StartsWith("note:")) return keep;

            var scaled = keep * m_lootAmount.Value;
            var whole = Mathf.FloorToInt(scaled);
            return whole + (random.NextDouble() < scaled - whole ? 1 : 0);
        }
    }
}
