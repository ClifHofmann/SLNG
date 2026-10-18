using System;
using Godot;
using SLNG.App;
using SLNG.App.UI;
using SLNG.Core;

public partial class Boot
{
    public override bool _CanDropData(Vector2 atPosition, Variant data)
    {
        if (_session == null || !_session.IsConnected) return false;
        if (IsDropBlockedByUi(atPosition)) return false;
        if (!TryReadRezItemDrop(data, out _, out _)) return false;

        if (_objectSelectionController == null || !_objectSelectionController.TryGetWorldRezPoint(atPosition, out _))
            return false;

        return true;
    }

    public override void _DropData(Vector2 atPosition, Variant data)
    {
        if (_session == null || !_session.IsConnected) return;
        if (IsDropBlockedByUi(atPosition)) return;
        if (!TryReadRezItemDrop(data, out var itemId, out var itemName)) return;

        if (_objectSelectionController == null || !_objectSelectionController.TryGetWorldRezPoint(atPosition, out var hitPoint))
        {
            ShowToast(L10n.Tr("ui.rez.no_surface"));
            return;
        }

        var slPos = RenderConfig.FromGodot(_session.CurrentRegionHandle, hitPoint);
        var result = _session.RezObjectFromInventory(itemId, slPos);

        switch (result)
        {
            case RezObjectResult.Success:
                ShowToast(string.Format(L10n.Tr("ui.rez.success"), itemName));
                break;
            case RezObjectResult.NoBuildRights:
                ShowToast(L10n.Tr("ui.rez.no_permission"));
                break;
            case RezObjectResult.NotAnObject:
                ShowToast(L10n.Tr("ui.rez.not_an_object"));
                break;
            case RezObjectResult.ItemNotFound:
                ShowToast(L10n.Tr("ui.rez.item_not_found"));
                break;
            case RezObjectResult.NotConnected:
                ShowToast(L10n.Tr("ui.rez.not_connected"));
                break;
        }
    }

    private bool TryReadRezItemDrop(Variant data, out Guid itemId, out string itemName)
    {
        itemId = Guid.Empty;
        itemName = string.Empty;

        if (data.VariantType != Variant.Type.String) return false;
        string str = data.AsString();
        if (!str.StartsWith("slng_item|")) return false;

        var parts = str.Split('|');
        if (parts.Length < 6) return false;

        if (!Guid.TryParse(parts[1], out itemId)) return false;
        itemName = parts[2];
        if (!bool.TryParse(parts[4], out bool isFolder) || isFolder) return false;
        if (!int.TryParse(parts[5], out int assetType)) return false;

        if (assetType != AssetTypeIds.Object && assetType != AssetTypeIds.Link)
            return false;

        if (_session != null && !_session.IsObjectItem(itemId))
            return false;

        return true;
    }

    private bool IsDropBlockedByUi(Vector2 screenPosition)
    {
        var hovered = GetViewport()?.GuiGetHoveredControl();
        if (hovered != null && hovered != this)
        {
            return true;
        }

        var tree = GetTree();
        if (tree == null) return false;

        var windows = tree.GetNodesInGroup(SLNGWindow.WindowGroupName);
        for (int i = 0; i < windows.Count; i++)
        {
            if (windows[i] is Control { Visible: true } ctrl && ctrl.IsVisibleInTree())
            {
                if (ctrl.GetGlobalRect().HasPoint(screenPosition)) return true;
            }
        }

        if (_inWorldContextMenu != null && _inWorldContextMenu.Visible && _inWorldContextMenu.GetGlobalRect().HasPoint(screenPosition))
        {
            return true;
        }

        return false;
    }
}
