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

    // "Neuen Ordner erstellen" and OK stay disabled until the initial folder fetch lands --
    // otherwise a folder created via OnCreateFolderConfirmed while PopulateFoldersAsync is still
    // in flight gets silently wiped out (and the selection reset to "Landmarks") the moment that
    // fetch's deferred callback runs and unconditionally rebuilds the list from its own snapshot.
    private bool _foldersLoaded;

    // Fired after a successful save with (folderId, itemId, assetId) so the caller (Boot) can
    // refresh an already-open Inventory panel -- otherwise the new item is invisible until the
    // user manually collapses/re-expands that folder. The asset id comes straight from the
    // create response (see GridSession.CreateLandmarkHereAsync's doc comment) rather than a
    // later folder-contents re-fetch, which can briefly report it as empty for a just-created
    // item -- passing it through here lets the Inventory panel show a "Teleport"-ready row
    // immediately instead of one that's greyed out until a second manual refresh.
    public Action<Guid, Guid, Guid>? OnLandmarkCreated;

    public void Initialize(GridSession? session) => _session = session;

    public override void _Ready()
    {
        base._Ready();

        PersistId = "create_landmark"; // FEAT-UI-11: one-shot dialog, but reopen where it was left

        Title = "Create Landmark";
        CustomMinimumSize = new Vector2(360, 420);
        Size = new Vector2(360, 420);
        Position = new Vector2(420, 200);

        OnCloseRequested = QueueFree;

        // Inset comes from SLNGWindow.ContentContainer now (FEAT-UI-26); this container is kept
        // only because the layout below hangs off it.
        var margin = new MarginContainer();
        ContentContainer.AddChild(margin);

        var vbox = new VBoxContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
        vbox.AddThemeConstantOverride("separation", 6);
        margin.AddChild(vbox);

        var heading = new Label { Text = "Landmark Details" };
        heading.AddThemeFontSizeOverride("font_size", 16);
        vbox.AddChild(heading);
        vbox.AddChild(new Control { CustomMinimumSize = new Vector2(0, 4) });

        vbox.AddChild(new Label { Text = "Name:" });
        _nameEdit = new LineEdit { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        vbox.AddChild(_nameEdit);

        vbox.AddChild(new Label { Text = "Location:" });
        _folderOption = new OptionButton { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        vbox.AddChild(_folderOption);

        _newFolderLink = new LinkButton { Text = "New Folder", Disabled = true };
        _newFolderLink.Pressed += () => _newFolderRow.Visible = !_newFolderRow.Visible;
        vbox.AddChild(_newFolderLink);

        _newFolderRow = new HBoxContainer { Visible = false };
        _newFolderNameEdit = new LineEdit
        {
            PlaceholderText = "Folder name",
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
        };
        _newFolderRow.AddChild(_newFolderNameEdit);
        var createFolderBtn = new Button { Text = "Create" };
        createFolderBtn.Pressed += OnCreateFolderConfirmed;
        _newFolderRow.AddChild(createFolderBtn);
        vbox.AddChild(_newFolderRow);

        vbox.AddChild(new Control { CustomMinimumSize = new Vector2(0, 4) });
        vbox.AddChild(new Label { Text = "Notes:" });
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
        var cancelBtn = new Button { Text = "Cancel", CustomMinimumSize = new Vector2(90, 0) };
        cancelBtn.Pressed += QueueFree;
        buttonsBox.AddChild(cancelBtn);
        _okButton = new Button { Text = "OK", CustomMinimumSize = new Vector2(90, 0), Disabled = true };
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
        _foldersLoaded = false;
        _newFolderLink.Disabled = true;
        _okButton.Disabled = true;
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

            _foldersLoaded = true;
            _newFolderLink.Disabled = false;
            _okButton.Disabled = false;
        }).CallDeferred();
    }

    private void OnCreateFolderConfirmed()
    {
        // Guarded by _newFolderLink being disabled until this is true, but double-check: the
        // link can't be pressed while disabled, yet nothing stops Enter from re-firing this via
        // the name field in principle, so keep the check explicit rather than relying only on
        // the button's disabled state.
        if (!_foldersLoaded) return;

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
        if (!_foldersLoaded || _session == null) return;
        if (_folderOption.Selected < 0 || _folderOption.Selected >= _folderIds.Count) return;

        var name = _nameEdit.Text.Trim();
        if (string.IsNullOrEmpty(name))
        {
            _status.Text = "Name must not be empty.";
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
                GD.Print($"[Landmark] created '{name}' ({result.ItemId}, asset {result.AssetId}) in folder {folderId}");
                if (result.ItemId is { } itemId && result.AssetId is { } assetId)
                    OnLandmarkCreated?.Invoke(folderId, itemId, assetId);
                if (IsInstanceValid(this)) QueueFree();
            }
            else
            {
                GD.PrintErr($"[Landmark] create '{name}' failed: {result.Message}");
                if (!IsInstanceValid(this)) return;
                _okButton.Disabled = false;
                _status.Text = string.IsNullOrEmpty(result.Message)
                    ? "Failed."
                    : $"Failed: {result.Message}";
            }
        }).CallDeferred();
    }
}
