using Godot;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using SLNG.Net;

namespace SLNG.App.UI;

/// <summary>
/// "Create Landmark" dialog (Firestorm-style: name, destination folder, optional new subfolder,
/// notes, OK/Cancel). Always creates the landmark at the agent's *current* location -- opened
/// fresh each time via <see cref="OpenForCurrentLocation"/> and freed on close, same one-shot-
/// dialog pattern as <see cref="ItemPropertiesWindow"/> rather than a persistent toggle panel
/// like <see cref="InventoryPanel"/>, since there is no ongoing state to keep around between uses.
/// </summary>
public partial class CreateLandmarkWindow : SLNGWindow
{
    private GridSession? _session;

    private LineEdit _nameEdit = null!;
    private OptionButton _folderOption = null!;
    private LinkButton _newFolderLink = null!;
    private HBoxContainer _newFolderRow = null!;
    private LineEdit _newFolderNameEdit = null!;
    private TextEdit _notesEdit = null!;
    private Label _status = null!;
    private Button _okButton = null!;

    // Parallel to _folderOption's items -- OptionButton indices are the only handle Godot gives
    // us back on selection, so the folder Guid for a given row has to be tracked alongside it.
    private readonly List<Guid> _folderIds = new();
    private Guid _landmarksFolderId;

    // Fired after a successful save with the destination folder id, so the caller (Boot) can
    // refresh an already-open Inventory panel -- otherwise the new item is invisible until the
    // user manually collapses/re-expands that folder.
    public Action<Guid>? OnLandmarkCreated;

    public void Initialize(GridSession? session) => _session = session;

    public override void _Ready()
    {
        base._Ready();

        Title = "Landmark erstellen";
        CustomMinimumSize = new Vector2(360, 420);
        Size = new Vector2(360, 420);
        Position = new Vector2(420, 200);

        OnCloseRequested = QueueFree;

        var margin = new MarginContainer();
        margin.AddThemeConstantOverride("margin_left", 14);
        margin.AddThemeConstantOverride("margin_right", 14);
        margin.AddThemeConstantOverride("margin_top", 12);
        margin.AddThemeConstantOverride("margin_bottom", 12);
        ContentContainer.AddChild(margin);

        var vbox = new VBoxContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
        vbox.AddThemeConstantOverride("separation", 6);
        margin.AddChild(vbox);

        var heading = new Label { Text = "Landmarken-Details" };
        heading.AddThemeFontSizeOverride("font_size", 16);
        vbox.AddChild(heading);
        vbox.AddChild(new Control { CustomMinimumSize = new Vector2(0, 4) });

        vbox.AddChild(new Label { Text = "Name:" });
        _nameEdit = new LineEdit { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        vbox.AddChild(_nameEdit);

        vbox.AddChild(new Label { Text = "Speicherort:" });
        _folderOption = new OptionButton { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        vbox.AddChild(_folderOption);

        _newFolderLink = new LinkButton { Text = "Neuen Ordner erstellen" };
        _newFolderLink.Pressed += () => _newFolderRow.Visible = !_newFolderRow.Visible;
        vbox.AddChild(_newFolderLink);

        _newFolderRow = new HBoxContainer { Visible = false };
        _newFolderNameEdit = new LineEdit
        {
            PlaceholderText = "Ordnername",
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        _newFolderRow.AddChild(_newFolderNameEdit);
        var createFolderBtn = new Button { Text = "Erstellen" };
        createFolderBtn.Pressed += OnCreateFolderConfirmed;
        _newFolderRow.AddChild(createFolderBtn);
        vbox.AddChild(_newFolderRow);

        vbox.AddChild(new Control { CustomMinimumSize = new Vector2(0, 4) });
        vbox.AddChild(new Label { Text = "Eigene Notizen:" });
        _notesEdit = new TextEdit
        {
            SizeFlagsVertical = SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(0, 90),
            WrapMode = TextEdit.LineWrappingMode.Boundary,
        };
        vbox.AddChild(_notesEdit);

        _status = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart };
        _status.AddThemeColorOverride("font_color", new Color(0.85f, 0.25f, 0.2f, 0.9f));
        vbox.AddChild(_status);

        var buttonsBox = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.End };
        var cancelBtn = new Button { Text = "Abbrechen", CustomMinimumSize = new Vector2(90, 0) };
        cancelBtn.Pressed += QueueFree;
        buttonsBox.AddChild(cancelBtn);
        _okButton = new Button { Text = "OK", CustomMinimumSize = new Vector2(90, 0) };
        _okButton.Pressed += OnOkPressed;
        buttonsBox.AddChild(_okButton);
        vbox.AddChild(buttonsBox);
    }

    /// <summary>Resets the form to the agent's current region and (re)loads the destination
    /// folder list. Call right after construction/AddChild -- there is no other entry point,
    /// since this window is always about "here, now".</summary>
    public void OpenForCurrentLocation()
    {
        _nameEdit.Text = _session?.CurrentRegionName ?? "";
        _notesEdit.Text = "";
        _status.Text = "";
        Visible = true;
        _ = PopulateFoldersAsync();
    }

    private async Task PopulateFoldersAsync()
    {
        if (_session?.LandmarksFolderId is not { } landmarksId)
        {
            Callable.From(() => { if (IsInstanceValid(this)) _status.Text = "Not connected yet."; }).CallDeferred();
            return;
        }
        _landmarksFolderId = landmarksId;

        var children = await _session.FetchInventoryChildrenAsync(landmarksId).ConfigureAwait(false);
        var subfolders = children.Where(c => c.IsFolder)
            .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        Callable.From(() =>
        {
            if (!IsInstanceValid(this)) return;

            _folderOption.Clear();
            _folderIds.Clear();

            _folderOption.AddItem("Landmarks");
            _folderIds.Add(landmarksId);
            foreach (var f in subfolders)
            {
                _folderOption.AddItem(f.Name);
                _folderIds.Add(f.Id);
            }
            _folderOption.Selected = 0;
        }).CallDeferred();
    }

    private void OnCreateFolderConfirmed()
    {
        var name = _newFolderNameEdit.Text.Trim();
        if (string.IsNullOrEmpty(name) || _session == null) return;

        var newFolderId = _session.CreateInventoryFolder(_landmarksFolderId, name);
        _folderOption.AddItem(name);
        _folderIds.Add(newFolderId);
        _folderOption.Selected = _folderIds.Count - 1;

        _newFolderNameEdit.Text = "";
        _newFolderRow.Visible = false;
    }

    private void OnOkPressed()
    {
        if (_session == null || _folderOption.Selected < 0 || _folderOption.Selected >= _folderIds.Count) return;

        var name = _nameEdit.Text.Trim();
        if (string.IsNullOrEmpty(name))
        {
            _status.Text = "Name darf nicht leer sein.";
            return;
        }

        var folderId = _folderIds[_folderOption.Selected];
        var notes = _notesEdit.Text;

        _okButton.Disabled = true;
        _status.Text = "";
        _ = CreateAsync(name, notes, folderId);
    }

    private async Task CreateAsync(string name, string notes, Guid folderId)
    {
        var result = await _session!.CreateLandmarkHereAsync(name, notes, folderId).ConfigureAwait(false);
        Callable.From(() =>
        {
            if (result.Success)
            {
                GD.Print($"[Landmark] created '{name}' ({result.ItemId}) in folder {folderId}");
                OnLandmarkCreated?.Invoke(folderId);
                if (IsInstanceValid(this)) QueueFree();
            }
            else
            {
                GD.PrintErr($"[Landmark] create '{name}' failed: {result.Message}");
                if (!IsInstanceValid(this)) return;
                _okButton.Disabled = false;
                _status.Text = string.IsNullOrEmpty(result.Message)
                    ? "Fehlgeschlagen."
                    : $"Fehlgeschlagen: {result.Message}";
            }
        }).CallDeferred();
    }
}
