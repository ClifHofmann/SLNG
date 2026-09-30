using LibreMetaverse;
using Xunit;

namespace SLNG.Net.Tests;

/// <summary>
/// FEAT-INV-09: "show original" opens the folders between the inventory root and a link's target.
/// The path is read off a real LibreMetaverse store.
/// </summary>
public class InventoryFolderPathTests
{
    private readonly UUID _owner = UUID.Random();
    private readonly UUID _root = UUID.Random();
    private readonly Inventory _store;

    public InventoryFolderPathTests()
    {
        _store = new Inventory(new GridClient(), _owner)
        {
            RootFolder = new InventoryFolder(_root) { ParentUUID = UUID.Zero, Name = "My Inventory", OwnerID = _owner },
        };
    }

    private UUID AddFolder(UUID parent, string name)
    {
        var id = UUID.Random();
        _store.UpdateNodeFor(new InventoryFolder(id) { ParentUUID = parent, Name = name, OwnerID = _owner });
        return id;
    }

    private UUID AddItem(UUID parent, string name)
    {
        var id = UUID.Random();
        _store.UpdateNodeFor(new InventoryItem(id) { ParentUUID = parent, Name = name, OwnerID = _owner });
        return id;
    }

    [Fact]
    public void The_path_runs_from_the_root_down_to_the_folder_holding_the_target()
    {
        var objects = AddFolder(_root, "Objects");
        var quiz = AddFolder(objects, "OmniQuiz");
        var board = AddItem(quiz, "Quiz board");

        Assert.Equal(new[] { _root.Guid, objects.Guid, quiz.Guid }, GridSession.FolderPathTo(_store, board));
        // A folder's path ends at its parent, not at itself.
        Assert.Equal(new[] { _root.Guid, objects.Guid }, GridSession.FolderPathTo(_store, quiz));
        Assert.Equal(new[] { _root.Guid }, GridSession.FolderPathTo(_store, objects));
    }

    [Fact]
    public void An_unknown_target_has_no_path()
    {
        Assert.Empty(GridSession.FolderPathTo(_store, UUID.Random()));
        Assert.Empty(GridSession.FolderPathTo(_store, UUID.Zero));
        Assert.Empty(GridSession.FolderPathTo(null, AddItem(_root, "Hat")));
    }
}
