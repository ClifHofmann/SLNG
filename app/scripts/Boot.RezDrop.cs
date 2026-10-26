using System;
using System.Globalization;
using Godot;
using SLNG.App;
using SLNG.App.UI;
using SLNG.Core;

// Boot, rez-from-inventory part (FEAT-INV-15): dropping an inventory object onto the 3D world.
//
// Follows the reference viewer's drag and drop (LLToolDragAndDrop::pick / dad3dRezObjectOnLand /
// dad3dRezObjectOnObject / dropObject, lltooldraganddrop.cpp): the drop is refused (forbidden cursor) wherever
// the viewer would refuse it, and otherwise ALWAYS sent -- the viewer does not check parcel rights on a drop,
// the simulator decides. The build-rights check below only colours the drag card.
public partial class Boot
{
    private enum RezDropState
    {
        Ready,
        NotConnected,
        NotAnObject,
        ItemNotFound,
        Worn,
        OverUi,
        NoSurface,
        OutsideRegion,
    }

    /// <summary>Where a drop would go, in the current region's coordinates.</summary>
    private readonly record struct RezDropTarget(
        System.Numerics.Vector3 Camera, System.Numerics.Vector3 Hit, Guid HitObjectId);

    // The worn lookup walks every prim of the scene, so it is not repeated for every mouse move of one drag.
    private const ulong RezItemCheckTtlMs = 1000;
    private Guid _rezItemCheckId;
    private ulong _rezItemCheckAtMs;
    private RezObjectResult _rezItemCheckResult;

    private WorldDropTarget? _worldDropTarget;

    /// <summary>Creates the control a drop on the 3D world lands on (see <see cref="WorldDropTarget"/> for why
    /// it must exist). Boot's FIRST child: everything drawn above it -- windows, HUD, menus -- wins the hit test.</summary>
    private void SetupWorldDropTarget()
    {
        _worldDropTarget = new WorldDropTarget(CanDropRez, DropRez);
        AddChild(_worldDropTarget);
        MoveChild(_worldDropTarget, 0);
    }

    /// <summary>The cursor position the drop is judged at, in the viewport's own coordinates -- the same space a
    /// click's <c>InputEventMouseButton.Position</c> is in, which is what the world raycast and the window
    /// rectangles expect. The position Godot hands to <c>_CanDropData</c> is local to the drop target
    /// instead, and is not used: it would shift the pick as soon as the control is offset or scaled.</summary>
    private Vector2 RezDropMousePosition() => GetViewport().GetMousePosition();

    private bool CanDropRez(Vector2 atPosition, Variant data)
    {
        if (!IsInventoryItemDrop(data)) return false;

        var state = EvaluateRezDrop(RezDropMousePosition(), data, checkItem: true, out _, out _, out var target);
        switch (state)
        {
            case RezDropState.Ready:
                // Only a hint: the sim decides, so the drop is still allowed when this says no.
                if (_session != null && !_session.CanBuildAt(target.Hit, out _))
                {
                    SetRezStatus("ui.rez.status_no_rights", warning: true);
                }
                else
                {
                    SetRezStatus("ui.rez.status_ready", ok: true);
                }
                break;
            case RezDropState.NotConnected:
                SetRezStatus("ui.rez.not_connected");
                break;
            case RezDropState.NotAnObject:
                SetRezStatus("ui.rez.status_not_object");
                break;
            case RezDropState.ItemNotFound:
                SetRezStatus("ui.rez.item_not_found");
                break;
            case RezDropState.Worn:
                SetRezStatus("ui.rez.status_worn");
                break;
            case RezDropState.OverUi:
                SetRezStatus("ui.rez.status_window", warning: true);
                break;
            case RezDropState.NoSurface:
                SetRezStatus("ui.rez.status_no_surface", warning: true);
                break;
            case RezDropState.OutsideRegion:
                SetRezStatus("ui.rez.status_other_region", warning: true);
                break;
        }

        // False shows Godot's forbidden cursor wherever the drop cannot rez.
        return state == RezDropState.Ready;
    }

    private void DropRez(Vector2 atPosition, Variant data)
    {
        if (!IsInventoryItemDrop(data)) return;

        var state = EvaluateRezDrop(RezDropMousePosition(), data, checkItem: false, out var itemId, out var itemName, out var target);
        if (state != RezDropState.Ready)
        {
            LogRezDrop(itemId, state, null, target);
        }

        switch (state)
        {
            case RezDropState.NotConnected:
                ShowToast(L10n.Tr("ui.rez.not_connected"));
                return;
            case RezDropState.NotAnObject:
                ShowToast(L10n.Tr("ui.rez.not_an_object"));
                return;
            case RezDropState.OverUi:
                ShowToast(L10n.Tr("ui.rez.blocked_by_ui"));
                return;
            case RezDropState.NoSurface:
                ShowToast(L10n.Tr("ui.rez.no_surface"));
                return;
            case RezDropState.OutsideRegion:
                ShowToast(L10n.Tr("ui.rez.other_region"));
                return;
        }

        if (_session == null) return;
        var result = _session.RezObjectFromInventory(itemId, target.Camera, target.Hit, target.HitObjectId);
        LogRezDrop(itemId, state, result, target);
        switch (result)
        {
            case RezObjectResult.Success:
                ShowToast(string.Format(L10n.Tr("ui.rez.success"), itemName));
                break;
            case RezObjectResult.NotAnObject:
                ShowToast(L10n.Tr("ui.rez.not_an_object"));
                break;
            case RezObjectResult.Worn:
                ShowToast(L10n.Tr("ui.rez.worn"));
                break;
            case RezObjectResult.ItemNotFound:
                ShowToast(L10n.Tr("ui.rez.item_not_found"));
                break;
            case RezObjectResult.OutsideRegion:
                ShowToast(L10n.Tr("ui.rez.other_region"));
                break;
            case RezObjectResult.NotConnected:
                ShowToast(L10n.Tr("ui.rez.not_connected"));
                break;
        }
    }

    /// <summary>One line per drop, so "I dropped it and nothing happened" can be answered from the log: what the
    /// client decided, what it sent, and the ray it sent (the simulator's refusal arrives separately as an
    /// <c>[Alert]</c> line). <c>result</c> is <c>-</c> when nothing was sent; the position fields are <c>-</c> when
    /// the drop never got as far as a ray. Positions are region-local, Second Life axes.</summary>
    private static void LogRezDrop(Guid itemId, RezDropState state, RezObjectResult? result, RezDropTarget target)
    {
        string camera = "-", hit = "-", objectId = "-", bypass = "-";
        if (state == RezDropState.Ready || state == RezDropState.OutsideRegion)
        {
            var ray = RezRay.For(target.Camera, target.Hit, target.HitObjectId);
            camera = FormatRezPoint(target.Camera);
            hit = FormatRezPoint(target.Hit);
            objectId = ray.TargetId == Guid.Empty ? "land" : ray.TargetId.ToString();
            bypass = ray.BypassRaycast ? "1" : "0";
        }

        GD.Print($"[Rez] item={itemId} state={state} result={(result?.ToString() ?? "-")} camera={camera} hit={hit} target={objectId} bypass={bypass}");
    }

    private static string FormatRezPoint(System.Numerics.Vector3 v)
        => string.Create(CultureInfo.InvariantCulture, $"{v.X:0.##},{v.Y:0.##},{v.Z:0.##}");

    /// <summary>The one decision behind both <see cref="CanDropRez"/> and <see cref="DropRez"/>: can this
    /// drop rez, and if not, why not. <paramref name="checkItem"/> also asks the session whether the item is
    /// still there, an object and not worn -- wanted for the live hint, not at the drop itself, where
    /// <see cref="GridSession.RezObjectFromInventory"/> asks again.</summary>
    private RezDropState EvaluateRezDrop(
        Vector2 atPosition, Variant data, bool checkItem,
        out Guid itemId, out string itemName, out RezDropTarget target)
    {
        itemId = Guid.Empty;
        itemName = string.Empty;
        target = default;

        if (_session == null || !_session.IsConnected) return RezDropState.NotConnected;
        if (!TryReadRezItemDrop(data, out itemId, out itemName)) return RezDropState.NotAnObject;

        if (checkItem)
        {
            switch (CachedRezItemCheck(itemId))
            {
                case RezObjectResult.NotAnObject: return RezDropState.NotAnObject;
                case RezObjectResult.ItemNotFound: return RezDropState.ItemNotFound;
                case RezObjectResult.Worn: return RezDropState.Worn;
            }
        }

        if (IsDropBlockedByUi(atPosition)) return RezDropState.OverUi;

        if (_objectSelectionController == null
            || !_objectSelectionController.TryGetRezTarget(atPosition, out var cameraPos, out var hitPoint, out var hitObjectId))
        {
            return RezDropState.NoSurface;
        }

        ulong region = _session.CurrentRegionHandle;
        var camera = RenderConfig.FromGodot(region, cameraPos);
        var hit = RenderConfig.FromGodot(region, hitPoint);
        target = new RezDropTarget(camera, hit, hitObjectId);
        if (!_session.IsInCurrentRegion(hit)) return RezDropState.OutsideRegion;

        return RezDropState.Ready;
    }

    private RezObjectResult CachedRezItemCheck(Guid itemId)
    {
        ulong now = Time.GetTicksMsec();
        if (itemId != _rezItemCheckId || now - _rezItemCheckAtMs > RezItemCheckTtlMs)
        {
            _rezItemCheckId = itemId;
            _rezItemCheckAtMs = now;
            _rezItemCheckResult = _session?.CheckRezItem(itemId) ?? RezObjectResult.NotConnected;
        }
        return _rezItemCheckResult;
    }

    private static void SetRezStatus(string key, bool ok = false, bool warning = false)
    {
        var (text, border) = ok
            ? (new Color(0.35f, 0.95f, 0.45f), new Color(0.25f, 0.85f, 0.4f, 0.9f))
            : warning
                ? (new Color(1.0f, 0.75f, 0.25f), new Color(0.9f, 0.65f, 0.2f, 0.9f))
                : (new Color(0.95f, 0.4f, 0.4f), new Color(0.85f, 0.3f, 0.3f, 0.9f));
        RezDragPreview.Active?.UpdateStatus(L10n.Tr(key), text, border);
    }

    private static bool IsInventoryItemDrop(Variant data)
        => data.VariantType == Variant.Type.String && data.AsString().StartsWith(WorldDropTarget.InventoryItemPrefix, StringComparison.Ordinal);

    private bool TryReadRezItemDrop(Variant data, out Guid itemId, out string itemName)
    {
        itemId = Guid.Empty;
        itemName = string.Empty;

        if (data.VariantType != Variant.Type.String) return false;
        string str = data.AsString();
        if (!str.StartsWith(WorldDropTarget.InventoryItemPrefix, StringComparison.Ordinal)) return false;

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
        if (hovered != null && hovered != this && hovered != _worldDropTarget && !IsDescendantOfDragPreview(hovered))
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
