using Godot;
using System;
using SLNG.Core;
using SLNG.Net;
using static Godot.Control;

namespace SLNG.App.UI;

public partial class ItemPropertiesWindow : Window
{
    private GridSession _session = null!;
    private Guid _itemId;
    private Action _onSaved = null!;
    
    private LineEdit _nameEdit = null!;
    private LineEdit _descEdit = null!; // TextEdit would be multi-line, but SL descriptions are usually short enough for LineEdit or small TextEdit.
    private CheckBox _copyCheck = null!;
    private CheckBox _modifyCheck = null!;
    private CheckBox _transferCheck = null!;

    public void Initialize(GridSession session, Guid itemId, Action onSaved)
    {
        _session = session;
        _itemId = itemId;
        _onSaved = onSaved;

        var props = _session.GetItemProperties(_itemId);
        if (props == null)
        {
            // Close if we can't find it
            CallDeferred(MethodName.QueueFree);
            return;
        }

        Title = "Properties: {props.Name}";

        _nameEdit.Text = props.Name;
        _descEdit.Text = props.Description;
        _copyCheck.ButtonPressed = props.NextOwnerCanCopy;
        _modifyCheck.ButtonPressed = props.NextOwnerCanModify;
        _transferCheck.ButtonPressed = props.NextOwnerCanTransfer;
    }

    public override void _Ready()
    {
        // Window properties
        Size = new Vector2I(400, 300);
        MinSize = new Vector2I(300, 200);
        Exclusive = false;
        CloseRequested += QueueFree;

        var panel = new PanelContainer();
        panel.SetAnchorsPreset(LayoutPreset.FullRect);
        AddChild(panel);

        var margin = new MarginContainer(); 
        margin.SetAnchorsPreset(LayoutPreset.FullRect);
        margin.AddThemeConstantOverride("margin_left", 10);
        margin.AddThemeConstantOverride("margin_right", 10);
        margin.AddThemeConstantOverride("margin_top", 10);
        margin.AddThemeConstantOverride("margin_bottom", 10);
        panel.AddChild(margin);

        var vbox = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill };
        margin.AddChild(vbox);

        // Name
        vbox.AddChild(new Label { Text = "Name:" });
        _nameEdit = new LineEdit { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        vbox.AddChild(_nameEdit);

        // Description
        vbox.AddChild(new Label { Text = "Description:" });
        _descEdit = new LineEdit { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        vbox.AddChild(_descEdit);

        vbox.AddChild(new HSeparator { CustomMinimumSize = new Vector2(0, 10) });

        // Next Owner Permissions
        vbox.AddChild(new Label { Text = "Next Owner Can:" });
        _copyCheck = new CheckBox { Text = "Copy" };
        _modifyCheck = new CheckBox { Text = "Modify" };
        _transferCheck = new CheckBox { Text = "Transfer" };
        
        var checksBox = new HBoxContainer();
        checksBox.AddChild(_copyCheck);
        checksBox.AddChild(_modifyCheck);
        checksBox.AddChild(_transferCheck);
        vbox.AddChild(checksBox);
        
        vbox.AddChild(new Control { SizeFlagsVertical = SizeFlags.ExpandFill }); // Spacer

        // Buttons
        var buttonsBox = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.End };
        vbox.AddChild(buttonsBox);

        var btnCancel = new Button { Text = "Cancel", CustomMinimumSize = new Vector2(80, 0) };
        btnCancel.Pressed += QueueFree;
        buttonsBox.AddChild(btnCancel);

        var btnSave = new Button { Text = "Save", CustomMinimumSize = new Vector2(80, 0) };
        btnSave.Pressed += OnSavePressed;
        buttonsBox.AddChild(btnSave);
    }

    private void OnSavePressed()
    {
        _session.UpdateItemProperties(
            _itemId,
            _nameEdit.Text,
            _descEdit.Text,
            _copyCheck.ButtonPressed,
            _modifyCheck.ButtonPressed,
            _transferCheck.ButtonPressed
        );

        _onSaved?.Invoke();
        QueueFree();
    }
}
