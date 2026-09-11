using System.Collections.Generic;

namespace Modules.Multiplayer.Scripts.Runtime.Sessions
{
    /// <summary>One room as the lobby browser lists it.</summary>
    /// <remarks>
    /// A snapshot from the last query, not a live view — player counts can be seconds stale, and the
    /// join itself is what finally decides whether there is still space.
    /// </remarks>
    public readonly struct RoomListing
    {
        public readonly string Id;
        public readonly string Name;
        public readonly int PlayerCount;
        public readonly int MaxPlayers;
        public readonly bool IsLocked;
        public readonly bool HasPassword;

        public RoomListing(string id, string name, int playerCount, int maxPlayers, bool isLocked, bool hasPassword)
        {
            Id = id;
            Name = name;
            PlayerCount = playerCount;
            MaxPlayers = maxPlayers;
            IsLocked = isLocked;
            HasPassword = hasPassword;
        }

        public bool IsFull => PlayerCount >= MaxPlayers;

        /// <summary>A room locks when its host starts the game, so locked means already playing.</summary>
        public bool IsInGame => IsLocked;

        public bool IsJoinable => !IsLocked && !IsFull;
    }

    /// <summary>One player in the room this client belongs to.</summary>
    public readonly struct RoomMember
    {
        public readonly string Id;
        public readonly string DisplayName;
        public readonly bool IsHost;
        public readonly bool IsLocal;

        public RoomMember(string id, string displayName, bool isHost, bool isLocal)
        {
            Id = id;
            DisplayName = displayName;
            IsHost = isHost;
            IsLocal = isLocal;
        }
    }

    /// <summary>Outcome of a room query. Failure is a value rather than an exception: it is routine.</summary>
    public readonly struct RoomQueryResult
    {
        public readonly bool Success;
        public readonly IReadOnlyList<RoomListing> Rooms;
        public readonly string Error;

        private RoomQueryResult(bool success, IReadOnlyList<RoomListing> rooms, string error)
        {
            Success = success;
            Rooms = rooms;
            Error = error;
        }

        public static RoomQueryResult Succeeded(IReadOnlyList<RoomListing> rooms) => new(true, rooms, string.Empty);

        public static RoomQueryResult Failed(string error) => new(false, System.Array.Empty<RoomListing>(), error);
    }
}
