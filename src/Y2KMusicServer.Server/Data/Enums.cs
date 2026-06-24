namespace Y2KMusicServer.Server.Data;

/// <summary>How a playlist entry got there. Stored as a string in the database.</summary>
public enum PlaylistSource
{
    Auto,
    Manual,
    Request,

    /// <summary>An Auto DJ pick made because a time-slot schedule was active, as
    /// opposed to <see cref="Auto"/> — the enabled-category fallback used when no
    /// slots are configured anywhere. Added last to keep existing ordinals; stored
    /// as the string "Schedule".</summary>
    Schedule
}

/// <summary>Lifecycle of a listener request. Stored as a string in the database.</summary>
public enum RequestStatus
{
    Pending,
    Accepted,
    Dismissed
}
