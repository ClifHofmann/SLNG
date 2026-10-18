using System;
using Godot;
using SLNG.App;
using SLNG.App.UI;
using SLNG.Core;

public partial class Boot
{
    public override bool _CanDropData(Vector2 atPosition, Variant data)
    {
        if (data.VariantType != Variant.Type.String) return false;
        string str = data.AsString();
        if (!str.StartsWith("slng_item|")) return false;

        bool isObj = TryReadRezItemDrop(data, out _, out _);

        if (_session == null || !_session.IsConnected)
        {
            RezDragPreview.Active?.UpdateStatus(
                L10n.Tr("ui.rez.not_connected"),
                new Color(0.95f, 0.35f, 0.35f),
                new Color(0.85f, 0.25f, 0.25f, 0.9f));
            return true;
        }

        if (!isObj)
        {
            RezDragPreview.Active?.UpdateStatus(
                L10n.Tr("ui.rez.status_not_object"),
                new Color(0.95f, 0.4f, 0.4f),
                new Color(0.85f, 0.3f, 0.3f, 0.9f));
            return true;
        }

        if (IsDropBlockedByUi(atPosition))
        {
            RezDragPreview.Active?.UpdateStatus(
                L10n.Tr("ui.rez.status_window"),
                new Color(0.95f, 0.65f, 0.25f),
                new Color(0.85f, 0.55f, 0.2f, 0.9f));
            return true;
        }

        if (_objectSelectionController == null || !_objectSelectionController.TryGetWorldRezPoint(atPosition, out var hitPoint))
        {
            RezDragPreview.Active?.UpdateStatus(
                L10n.Tr("ui.rez.status_no_surface"),
                new Color(0.9f, 0.55f, 0.3f),
                new Color(0.8f, 0.45f, 0.25f, 0.9f));
            return true;
        }

        var slPos = RenderConfig.FromGodot(_session.CurrentRegionHandle, hitPoint);
        if (!_session.CanBuildAt(slPos, out _))
        {
            RezDragPreview.Active?.UpdateStatus(
                L10n.Tr("ui.rez.status_no_rights"),
                new Color(0.95f, 0.3f, 0.3f),
                new Color(0.9f, 0.25f, 0.25f, 0.9f));
            return true;
        }

        RezDragPreview.Active?.UpdateStatus(
            L10n.Tr("ui.rez.status_ready"),
            new Color(0.35f, 0.95f, 0.45f),
            new Color(0.25f, 0.85f, 0.4f, 0.9f));
        return true;
    }

    public override void _DropData(Vector2 atPosition, Variant data)
    {
        if (data.VariantType != Variant.Type.String) return;
        string str = data.AsString();
        if (!str.StartsWith("slng_item|")) return;

        if (_session == null || !_session.IsConnected)
        {
            ShowToast(L10n.Tr("ui.rez.not_connected"));
            return;
        }

        bool isObj = TryReadRezItemDrop(data, out var itemId, out var itemName);
        if (!isObj)
        {
            ShowToast(L10n.Tr("ui.rez.not_an_object"));
            return;
        }

        if (IsDropBlockedByUi(atPosition))
        {
            ShowToast(L10n.Tr("ui.rez.blocked_by_ui"));
            return;
        }

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

        if (assetType == AssetTypeIds.Object)
            return true;

        if (assetType == AssetTypeIds.Link)
        {
            if (_session != null && !_session.IsObjectItem(itemId))
                return false;
            return true;
        }

        return false;
    }

    private bool IsDropBlockedByUi(Vector2 screenPosition)
    {
        var hovered = GetViewport()?.GuiGetHoveredControl();
        if (hovered != null && hovered != this && !IsDescendantOfDragPreview(hovered))
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

    private static bool IsDescendantOfDragPreview(Control? ctrl)
    {
        var active = RezDragPreview.Active;
        if (active == null || ctrl == null) return false;
        if (ctrl == active) return true;
        return active.IsAncestorOf(ctrl);
    }
}
