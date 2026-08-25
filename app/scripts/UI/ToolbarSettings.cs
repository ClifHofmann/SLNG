using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace SLNG.App.UI;

public enum ToolbarDockPosition { Bottom, Right, Left, Top }

/// <summary>
/// Which <see cref="ToolbarItemDefinition"/> ids are enabled and in what order, persisted to
/// user://preferences.cfg (same ConfigFile pattern as Boot's user://logins.cfg). This is the
/// single source of truth shared by <see cref="ButtonBar"/> (renders/reorders buttons) and
/// <see cref="ToolbarPreferencesPage"/> (lets the user enable/disable them) so neither
/// duplicates persistence logic, and a drag-reorder and a Preferences checkbox both funnel
/// through the same Save() + Changed notification.
/// </summary>
public sealed class ToolbarSettings
{
    private const string ConfigPath = "user://preferences.cfg";
    private const string Section = "toolbar";

    private readonly List<string> _order = new();
    private readonly HashSet<string> _enabled = new();
    private readonly Dictionary<string, ToolbarDockPosition> _dockPositions = new();

    /// <summary>Raised after any mutation has been persisted, so dependent views (currently
    /// just ButtonBar) can rebuild from the new state.</summary>
    public event Action? Changed;

    /// <summary>Full known order — enabled and disabled ids alike.</summary>
    public IReadOnlyList<string> Order => _order;

    public bool IsEnabled(string id) => _enabled.Contains(id);

    public ToolbarDockPosition GetDockPosition(string id) => _dockPositions.TryGetValue(id, out var pos) ? pos : ToolbarDockPosition.Bottom;

    /// <summary>Seeds Order/Enabled with every currently-registered id, defaulting new
    /// (never-before-seen) ids to enabled and appended at the end. Call once, before Load(), so
    /// a first run — or a future id Load() has never heard of — still gets a sane default
    /// instead of being silently dropped.</summary>
    public void EnsureDefaults(IEnumerable<string> allIdsInDefaultOrder)
    {
        foreach (var id in allIdsInDefaultOrder)
        {
            if (!_order.Contains(id)) _order.Add(id);
            _enabled.Add(id);
            if (!_dockPositions.ContainsKey(id)) _dockPositions[id] = ToolbarDockPosition.Bottom;
        }
    }

    public void Load()
    {
        var cfg = new ConfigFile();
        if (cfg.Load(ConfigPath) != Error.Ok) return; // no file yet -- defaults from EnsureDefaults stand

        var savedOrder = ((string)cfg.GetValue(Section, "order", ""))
            .Split(',', StringSplitOptions.RemoveEmptyEntries);

        // Only reorder among ids we still know about (a saved id from a removed feature is
        // simply dropped); anything we know about that the save predates stays appended in its
        // registration position instead of being lost.
        var known = new HashSet<string>(_order);
        var merged = savedOrder.Where(known.Contains).ToList();
        foreach (var id in _order)
            if (!merged.Contains(id)) merged.Add(id);
        _order.Clear();
        _order.AddRange(merged);

        foreach (var id in _order)
        {
            bool fallback = _enabled.Contains(id);
            bool val = (bool)cfg.GetValue(Section, $"enabled_{id}", fallback);
            if (val) _enabled.Add(id); else _enabled.Remove(id);

            int posVal = (int)cfg.GetValue(Section, $"dock_{id}", (int)ToolbarDockPosition.Bottom);
            _dockPositions[id] = (ToolbarDockPosition)posVal;
        }
    }

    public void Save()
    {
        var cfg = new ConfigFile();
        cfg.Load(ConfigPath); // preserve any sections a future feature adds to this same file
        cfg.SetValue(Section, "order", string.Join(",", _order));
        foreach (var id in _order)
        {
            cfg.SetValue(Section, $"enabled_{id}", _enabled.Contains(id));
            cfg.SetValue(Section, $"dock_{id}", (int)(_dockPositions.ContainsKey(id) ? _dockPositions[id] : ToolbarDockPosition.Bottom));
        }
        cfg.Save(ConfigPath);
    }

    public void SetEnabled(string id, bool enabled)
    {
        if (enabled) _enabled.Add(id); else _enabled.Remove(id);
        Save();
        Changed?.Invoke();
    }

    public void SetDockPosition(string id, ToolbarDockPosition pos)
    {
        _dockPositions[id] = pos;
        Save();
        Changed?.Invoke();
    }

    /// <summary>Reorders just the enabled subset (what ButtonBar actually displays and what a
    /// drag can physically touch); any disabled id keeps its previous relative position,
    /// trailing after the enabled ones. A disabled item re-enabled later therefore reappears at
    /// the end of the bar rather than at some remembered slot -- simple, predictable, and
    /// sufficient for a handful of toolbar items.</summary>
    public void SetOrderForEnabled(IEnumerable<string> newEnabledOrder)
    {
        var disabledInOldOrder = _order.Where(id => !_enabled.Contains(id)).ToList();
        _order.Clear();
        _order.AddRange(newEnabledOrder);
        _order.AddRange(disabledInOldOrder);
        Save();
        Changed?.Invoke();
    }
}
