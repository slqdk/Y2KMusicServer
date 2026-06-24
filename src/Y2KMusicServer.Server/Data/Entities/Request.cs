namespace Y2KMusicServer.Server.Data.Entities;

/// <summary>A listener's track request, submitted from the public request page.</summary>
public sealed class Request
{
    public int Id { get; set; }
    public int TrackId { get; set; }
    public Track? Track { get; set; }
    public string? RequesterName { get; set; }
    public RequestStatus Status { get; set; }
    public DateTime CreatedAt { get; set; }
}
