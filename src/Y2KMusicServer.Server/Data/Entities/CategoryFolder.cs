namespace Y2KMusicServer.Server.Data.Entities;

/// <summary>
/// A filesystem folder feeding a category. Replaces the legacy
/// <c>cat_&lt;name&gt;_folder.txt</c> files.
/// </summary>
public sealed class CategoryFolder
{
    public int Id { get; set; }
    public int CategoryId { get; set; }
    public Category? Category { get; set; }
    public string Path { get; set; } = null!;
}
