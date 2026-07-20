using System;
using Xunit;
using SLNG.Core;

namespace SLNG.Core.Tests;

public class InventoryEntryTests
{
    private static InventoryEntry CreateItem(bool canCopy, bool canModify, bool canTransfer)
    {
        return new InventoryEntry(
            Id: Guid.NewGuid(),
            ParentId: Guid.NewGuid(),
            OwnerId: Guid.NewGuid(),
            Name: "Test Item",
            IsFolder: false,
            PreferredFolderType: -1,
            AssetId: Guid.NewGuid(),
            AssetType: 0,
            InventoryType: 0,
            IsLink: false,
            LinkTargetId: Guid.Empty,
            CanCopy: canCopy,
            CanModify: canModify,
            CanTransfer: canTransfer
        );
    }

    private static InventoryEntry CreateFolder()
    {
        return new InventoryEntry(
            Id: Guid.NewGuid(),
            ParentId: Guid.NewGuid(),
            OwnerId: Guid.NewGuid(),
            Name: "Test Folder",
            IsFolder: true,
            PreferredFolderType: -1,
            AssetId: Guid.Empty,
            AssetType: -1,
            InventoryType: -1,
            IsLink: false,
            LinkTargetId: Guid.Empty,
            CanCopy: false,
            CanModify: false,
            CanTransfer: false
        );
    }

    [Theory]
    [InlineData(true, true, true, true)]
    [InlineData(true, true, false, false)]
    [InlineData(true, false, true, false)]
    [InlineData(false, true, true, false)]
    [InlineData(false, false, false, false)]
    public void IsFullPerm_RequiresAllThreePermissions(bool c, bool m, bool t, bool expected)
    {
        var item = CreateItem(c, m, t);
        Assert.Equal(expected, item.IsFullPerm);
    }

    [Theory]
    [InlineData(true, true, true, "")]
    [InlineData(false, true, true, " (no copy)")]
    [InlineData(true, false, true, " (no modify)")]
    [InlineData(true, true, false, " (no transfer)")]
    [InlineData(false, false, true, " (no copy) (no modify)")]
    [InlineData(false, true, false, " (no copy) (no transfer)")]
    [InlineData(true, false, false, " (no modify) (no transfer)")]
    [InlineData(false, false, false, " (no copy) (no modify) (no transfer)")]
    public void GetPermissionSuffix_FormatsCorrectly(bool c, bool m, bool t, string expectedSuffix)
    {
        var item = CreateItem(c, m, t);
        Assert.Equal(expectedSuffix, item.GetPermissionSuffix());
    }

    [Fact]
    public void GetPermissionSuffix_AlwaysEmptyForFolders()
    {
        var folder = CreateFolder();
        Assert.Equal(string.Empty, folder.GetPermissionSuffix());
    }
}
