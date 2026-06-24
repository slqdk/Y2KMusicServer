namespace Y2KMusicServer.Server.Data.Entities;

/// <summary>
/// One of the 14 categories: 7 built-in (Pop, Rock, Metal, Dance, Techno,
/// Country, Classical) + 7 custom slots (Custom1..Custom7). Custom names are
/// user-renamable. Fresh installs start with every category disabled — the
/// legacy app refused to persist Enabled=true for a category with no folders,
/// and a fresh install has none.
/// </summary>
public sealed class Category
{
    public int Id { get; set; }
    public string Name { get; set; } = null!;
    public bool IsCustom { get; set; }
    public bool Enabled { get; set; }
    public int DisplayOrder { get; set; }

    public ICollection<CategoryFolder> Folders { get; set; } = new List<CategoryFolder>();
    public ICollection<CategorySlot> Slots { get; set; } = new List<CategorySlot>();
}
