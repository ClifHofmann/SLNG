using System;
using Godot;
using SLNG.Core;
using SLNG.Core.Components;
using SLNG.Core.ECS;
using SLNG.Net;

namespace SLNG.App;

public partial class CursorManager : Node
{
    private World? _world;
    private Camera3D? _camera;
    private GridSession? _session;
    private ImageTexture? _magnifierTexture;
    private ImageTexture? _sitTexture;
    private ImageTexture? _touchTexture;
    private Guid _lastHoveredEntityId = Guid.Empty;
    private Godot.Input.CursorShape _lastReportedShape = Godot.Input.CursorShape.Arrow;

    public void Initialize(World world, Camera3D camera, GridSession? session = null)
    {
        _world = world;
        _camera = camera;
        _session = session;
        _ = SetupCustomCursorsAsync();
    }

    /// <summary>Rasterizes icon glyphs from the Material Symbols font into cursor textures
    /// and registers them with Godot's Input system.</summary>
    private async System.Threading.Tasks.Task SetupCustomCursorsAsync()
    {
        var iconFont = GD.Load<Font>("res://assets/fonts/MaterialSymbolsOutlined.ttf");

        // Magnifying glass for Alt-zoom (Cross shape)
        _magnifierTexture = await RasterizeCursorGlyphAsync(iconFont, "search", 26, 2);
        if (_magnifierTexture != null)
        {
            Godot.Input.SetCustomMouseCursor(_magnifierTexture, Godot.Input.CursorShape.Cross, new Vector2(16, 16));
        }

        // Chair icon for objects with ClickAction == Sit (Help shape)
        _sitTexture = await RasterizeCursorGlyphAsync(iconFont, "chair", 24, 2);
        if (_sitTexture != null)
        {
            Godot.Input.SetCustomMouseCursor(_sitTexture, Godot.Input.CursorShape.Help, new Vector2(16, 16));
        }

        // Touch / pointer hand icon for scripted or clickable objects (PointingHand shape)
        _touchTexture = await RasterizeCursorGlyphAsync(iconFont, "pan_tool_alt", 24, 2);
        if (_touchTexture != null)
        {
            Godot.Input.SetCustomMouseCursor(_touchTexture, Godot.Input.CursorShape.PointingHand, new Vector2(10, 4));
        }
    }

    private async System.Threading.Tasks.Task<ImageTexture?> RasterizeCursorGlyphAsync(
        Font iconFont,
        string glyphName,
        int fontSize,
        int outlineSize)
    {
        var subViewport = new SubViewport
        {
            Size = new Vector2I(32, 32),
            TransparentBg = true,
            RenderTargetUpdateMode = SubViewport.UpdateMode.Once,
        };
        AddChild(subViewport);

        var label = new Label
        {
            Text = glyphName,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        label.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        label.AddThemeFontOverride("font", iconFont);
        label.AddThemeFontSizeOverride("font_size", fontSize);
        label.AddThemeColorOverride("font_color", Colors.White);
        if (outlineSize > 0)
        {
            label.AddThemeConstantOverride("outline_size", outlineSize);
            label.AddThemeColorOverride("font_outline_color", Colors.Black);
        }
        subViewport.AddChild(label);

        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);

        ImageTexture? cursorTexture = null;
        var viewportTexture = subViewport.GetTexture();
        var image = viewportTexture?.GetImage();
        if (image != null)
        {
            cursorTexture = ImageTexture.CreateFromImage(image);
            image.Dispose();
        }

        viewportTexture?.Dispose();
        subViewport.QueueFree();
        return cursorTexture;
    }

    public override void _PhysicsProcess(double delta)
    {
        using var _phase = MainThreadPhase.Enter("cursor-pick");

        if (_world == null || _camera == null || !IsInstanceValid(_camera)) return;

        // 1. Alt key overrides hover with Cross/Zoom cursor
        if (Godot.Input.IsKeyPressed(Key.Alt))
        {
            ApplyCursorShape(Godot.Input.CursorShape.Cross);
            return;
        }

        // 2. Check UI hover.
        var viewport = GetViewport();
        if (viewport == null) return;
        var focusOwner = viewport.GuiGetFocusOwner();
        bool hasUiFocus = focusOwner is LineEdit || focusOwner is TextEdit
            || viewport.GuiGetHoveredControl() != null;
        
        if (hasUiFocus)
        {
            ApplyCursorShape(Godot.Input.CursorShape.Arrow);
            return;
        }

        Godot.Input.CursorShape desiredShape = Godot.Input.CursorShape.Arrow;

        // 3. Raycast into the world
        var mousePos = viewport.GetMousePosition();
        var spaceState = _camera.GetWorld3D().DirectSpaceState;
        
        var rayOrigin = _camera.ProjectRayOrigin(mousePos);
        var rayEnd = rayOrigin + _camera.ProjectRayNormal(mousePos) * 1000f;

        var query = PhysicsRayQueryParameters3D.Create(rayOrigin, rayEnd);
        // Include both solid Objects and Phantom prims (e.g. pose stands, cushions, seats).
        // Deliberately NOT PhysicsLayers.Terrain to avoid heightmap normalization spam.
        query.CollisionMask = PhysicsLayers.Objects | PhysicsLayers.Phantom;

        var result = spaceState.IntersectRay(query);

        // Penetrate through avatar colliders so the cursor detects the object underneath/behind
        var exclude = new Godot.Collections.Array<Rid>();
        while (result.Count > 0 && result.ContainsKey("collider"))
        {
            var col = result["collider"].As<Node>();
            if (col is StaticBody3D sb && sb.HasMeta("LocalId") && sb.GetMeta("LocalId").AsString() == "Avatar")
            {
                exclude.Add(sb.GetRid());
                query.Exclude = exclude;
                result = spaceState.IntersectRay(query);
                continue;
            }
            break;
        }

        if (result.Count > 0)
        {
            var collider = result["collider"].AsGodotObject();
            if (collider is Node node)
            {
                if (node.HasMeta("EntityId"))
                {
                    var idStr = node.GetMeta("EntityId").AsString();
                    if (System.Guid.TryParse(idStr, out var entityId))
                    {
                        var rawEntity = _world.GetEntity(entityId);
                        if (rawEntity != null)
                        {
                            var entity = rawEntity;
                            uint rawLocalId = 0;
                            if (node.HasMeta("LocalId") && uint.TryParse(node.GetMeta("LocalId").AsString(), out var lid))
                            {
                                rawLocalId = lid;
                            }
                            uint localId = rawLocalId;

                            // Linkset parent resolution
                            var transform = rawEntity.GetComponent<TransformComponent>();
                            if (transform != null && transform.ParentLocalId != 0)
                            {
                                var parent = _world.GetEntity(rawEntity.RegionHandle, transform.ParentLocalId);
                                if (parent != null)
                                {
                                    entity = parent;
                                    localId = transform.ParentLocalId;
                                }
                            }

                            // Check if avatar is already sitting on this object or linkset
                            uint sittingOn = _session?.SittingOnLocalId ?? 0;
                            bool isSittingOnThisObject = sittingOn != 0 &&
                                (sittingOn == rawLocalId || sittingOn == localId ||
                                 rawEntity.GetComponent<TransformComponent>()?.ParentLocalId == sittingOn ||
                                 entity.GetComponent<TransformComponent>()?.ParentLocalId == sittingOn);

                            var rawPrim = rawEntity.GetComponent<PrimitiveComponent>();
                            var rootPrim = entity != rawEntity ? entity.GetComponent<PrimitiveComponent>() : null;
                            byte clickAction = (rawPrim != null && rawPrim.ClickAction != 0) ? rawPrim.ClickAction : (rootPrim?.ClickAction ?? 0);
                            bool isTouch = (rawPrim != null && rawPrim.IsTouch) || (rootPrim != null && rootPrim.IsTouch);

                            // Sit (1), Buy (2), Pay (3), OpenTask (4), PlayMedia (5), OpenMedia (6), or touchable script (isTouch)
                            if (clickAction == 1 && !isSittingOnThisObject)
                            {
                                desiredShape = Godot.Input.CursorShape.Help;
                            }
                            else if (clickAction != 0 || isTouch)
                            {
                                desiredShape = Godot.Input.CursorShape.PointingHand;
                            }

                            if (Diagnostics.Enabled && entity.Id != _lastHoveredEntityId)
                            {
                                _lastHoveredEntityId = entity.Id;
                                GD.Print($"[Cursor] Hovered {entity.Id:N} (LocalId={localId}, clickAction={clickAction}, touch={isTouch}, sitting={isSittingOnThisObject}) -> shape {desiredShape}");
                            }
                        }
                    }
                }
            }
        }
        else if (_lastHoveredEntityId != Guid.Empty)
        {
            _lastHoveredEntityId = Guid.Empty;
        }

        ApplyCursorShape(desiredShape);
    }

    private void ApplyCursorShape(Godot.Input.CursorShape shape)
    {
        if (shape != _lastReportedShape)
        {
            _lastReportedShape = shape;
            Godot.Input.SetDefaultCursorShape(shape);
        }
    }

    public override void _ExitTree()
    {
        // Unset custom cursors and dispose retained textures on exit
        Godot.Input.SetCustomMouseCursor(null, Godot.Input.CursorShape.Cross);
        Godot.Input.SetCustomMouseCursor(null, Godot.Input.CursorShape.Help);
        Godot.Input.SetCustomMouseCursor(null, Godot.Input.CursorShape.PointingHand);

        _magnifierTexture?.Dispose();
        _magnifierTexture = null;

        _sitTexture?.Dispose();
        _sitTexture = null;

        _touchTexture?.Dispose();
        _touchTexture = null;
    }
}
