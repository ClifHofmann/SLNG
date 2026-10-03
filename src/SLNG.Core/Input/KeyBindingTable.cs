namespace SLNG.Core.Input;

/// <summary>What <see cref="KeyBindingTable.ApplySaved"/> found in a saved file.</summary>
/// <param name="Applied">Overrides that changed an action's chords.</param>
/// <param name="UnknownActions">Ids not in this build's catalog (a file from a newer or older version): ignored.</param>
/// <param name="InvalidEntries">Chord texts that did not parse (an unknown key name, a typo): dropped.</param>
public readonly record struct KeyBindingLoadResult(int Applied, int UnknownActions, int InvalidEntries);

/// <summary>
/// The live key-binding table: the catalog's defaults with the person's overrides on top. Pure and
/// engine-agnostic - the dispatcher and the polled controllers read it, the Keyboard page edits it,
/// and <see cref="ToSaved"/> / <see cref="ApplySaved"/> are the whole persistence contract.
///
/// <para><b>Main thread only.</b> Nothing here is synchronized; the table is edited from the UI and
/// read from <c>_Input</c> / <c>_Process</c>, all on Godot's main thread.</para>
///
/// <para><b>Overrides are differences.</b> An action that has its default chords stores nothing, so a
/// default changed in a later version reaches everyone who never customised that action, and
/// "reset" is "delete the entry". An action the person deliberately unbound is stored as an empty
/// string - distinct from "no entry".</para>
/// </summary>
public sealed class KeyBindingTable
{
    private readonly List<KeyAction> _actions;
    private readonly Dictionary<string, KeyAction> _byId;
    private readonly Dictionary<string, List<KeyChord>> _current;

    // chord -> the actions bound to it, rebuilt lazily after a change. The empty array is shared so a
    // key press that matches nothing (the common case) allocates nothing.
    private Dictionary<KeyChord, KeyAction[]>? _byChord;
    private static readonly KeyAction[] NoActions = Array.Empty<KeyAction>();

    /// <summary>Bumped on every change. A cache keyed on it (the app's per-frame "is this held"
    /// lookup) knows when to rebuild.</summary>
    public int Version { get; private set; }

    /// <summary>Raised after any change. Handlers run on the thread that changed the table.</summary>
    public event Action? Changed;

    public KeyBindingTable() : this(KeyActions.All) { }

    public KeyBindingTable(IEnumerable<KeyAction> catalog)
    {
        _actions = catalog.ToList();
        _byId = new Dictionary<string, KeyAction>(StringComparer.Ordinal);
        _current = new Dictionary<string, List<KeyChord>>(StringComparer.Ordinal);
        foreach (var action in _actions)
        {
            _byId[action.Id] = action;
            _current[action.Id] = action.Defaults.ToList();
        }
    }

    public IReadOnlyList<KeyAction> Actions => _actions;

    public KeyAction? Find(string id) => _byId.TryGetValue(id, out var action) ? action : null;

    /// <summary>The chords currently bound to the action (empty = unbound). Unknown id: empty.</summary>
    public IReadOnlyList<KeyChord> ChordsOf(string id) =>
        _current.TryGetValue(id, out var chords) ? chords : Array.Empty<KeyChord>();

    public bool IsDefault(string id) =>
        _byId.TryGetValue(id, out var action) && SameChords(_current[id], action.Defaults);

    /// <summary>The actions whose chord list contains <paramref name="chord"/> exactly. Holds the
    /// same array between calls; do not modify it. Held actions that ignore Shift are NOT expanded
    /// here - this is for the dispatcher, which matches discrete presses exactly.</summary>
    public IReadOnlyList<KeyAction> ActionsFor(KeyChord chord)
    {
        _byChord ??= BuildChordIndex();
        return _byChord.TryGetValue(chord, out var actions) ? actions : NoActions;
    }

    // ---- editing --------------------------------------------------------------------------------

    /// <summary>Replaces the action's chords. Duplicates are dropped; an empty list unbinds it.
    /// Does not look at conflicts - the caller decides (see <see cref="Conflicts"/>).</summary>
    public void SetChords(string id, IEnumerable<KeyChord> chords)
    {
        if (!_current.TryGetValue(id, out var list)) return;
        var next = chords.Distinct().ToList();
        if (SameSequence(list, next)) return;
        _current[id] = next;
        Touch();
    }

    /// <summary>Adds <paramref name="chord"/> to the action, or - when <paramref name="replaceIndex"/> is a valid
    /// position - puts it in place of the chord there (the Keyboard page's "change this key"). When <paramref name="replaceConflicts"/>
    /// is set the chord is first taken away from every action that already claims it in an
    /// overlapping context (for an action that ignores Shift, taking "Shift+A" removes its plain "A");
    /// otherwise a conflict is left in place for the caller to have refused beforehand. Returns the
    /// actions it was taken from.</summary>
    public IReadOnlyList<KeyAction> Assign(string id, KeyChord chord, bool replaceConflicts, int replaceIndex = -1)
    {
        var taken = new List<KeyAction>();
        if (!_byId.ContainsKey(id)) return taken;

        if (replaceConflicts)
        {
            foreach (var other in Conflicts(id, chord))
            {
                _current[other.Id] = _current[other.Id].Where(c => !other.Claims(c, chord)).ToList();
                taken.Add(other);
            }
        }
        var list = _current[id].ToList();
        if (replaceIndex >= 0 && replaceIndex < list.Count) list[replaceIndex] = chord;
        else if (!list.Contains(chord)) list.Add(chord);
        _current[id] = list.Distinct().ToList();
        Touch();
        return taken;
    }

    /// <summary>Removes one chord from one action.</summary>
    public void Remove(string id, KeyChord chord)
    {
        if (!_current.TryGetValue(id, out var list) || !list.Contains(chord)) return;
        _current[id] = list.Where(c => c != chord).ToList();
        Touch();
    }

    public void Reset(string id)
    {
        if (!_byId.TryGetValue(id, out var action) || IsDefault(id)) return;
        _current[id] = action.Defaults.ToList();
        Touch();
    }

    public void ResetAll()
    {
        bool any = false;
        foreach (var action in _actions)
        {
            if (IsDefault(action.Id)) continue;
            _current[action.Id] = action.Defaults.ToList();
            any = true;
        }
        if (any) Touch();
    }

    // ---- conflicts ------------------------------------------------------------------------------

    /// <summary>
    /// The OTHER actions that already claim <paramref name="chord"/> in a context that overlaps the
    /// action's own. Exact chord comparison, so W (walk) and Alt+W (camera) never conflict; the one
    /// widening is an action that <see cref="KeyAction.IgnoreExtraShift"/>s, which also claims the same
    /// chord plus Shift (turning left with Shift held is still turning left).
    ///
    /// <para>Contexts that cannot be live together do not conflict: the chat bar's Enter and the
    /// world's Enter are different <see cref="KeyContext"/>s.</para>
    /// </summary>
    public IReadOnlyList<KeyAction> Conflicts(string id, KeyChord chord)
    {
        var result = new List<KeyAction>();
        if (!_byId.TryGetValue(id, out var self)) return result;
        foreach (var other in _actions)
        {
            if (other.Id == id) continue;
            if (!KeyContexts.Overlaps(self.Context, other.Context)) continue;
            if (_current[other.Id].Any(bound => other.Claims(bound, chord))) result.Add(other);
        }
        return result;
    }

    // ---- persistence ----------------------------------------------------------------------------

    /// <summary>Action id -> "Ctrl+I;Ctrl+Shift+I" for every action that differs from its default.
    /// An unbound action maps to the empty string. Nothing else is included, so a person who never
    /// touched the Keyboard page has an empty result.</summary>
    public IReadOnlyDictionary<string, string> ToSaved()
    {
        var saved = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var action in _actions)
        {
            if (IsDefault(action.Id)) continue;
            saved[action.Id] = KeyChord.FormatList(_current[action.Id]);
        }
        return saved;
    }

    /// <summary>
    /// Applies a saved file on top of the defaults. Tolerant by design, because the file outlives the
    /// build that wrote it: an id this catalog does not have is ignored, a chord that does not parse
    /// is dropped, and an entry whose chords ALL fail to parse is ignored as a whole (keeping the
    /// default is safer than silently unbinding something over a typo). Never throws.
    /// Starts from the defaults, so calling it twice does not accumulate.
    /// </summary>
    public KeyBindingLoadResult ApplySaved(IReadOnlyDictionary<string, string> saved)
    {
        int applied = 0, unknown = 0, invalid = 0;
        foreach (var action in _actions) _current[action.Id] = action.Defaults.ToList();

        foreach (var (id, text) in saved)
        {
            if (!_byId.TryGetValue(id, out var action)) { unknown++; continue; }

            var chords = KeyChord.ParseList(text, out int skipped);
            invalid += skipped;
            bool wantedSomething = !string.IsNullOrWhiteSpace(text);
            if (wantedSomething && chords.Count == 0) continue; // nothing usable: keep the default
            if (SameChords(chords, action.Defaults)) continue;

            _current[id] = chords;
            applied++;
        }
        Touch();
        return new KeyBindingLoadResult(applied, unknown, invalid);
    }

    // ---- internals ------------------------------------------------------------------------------

    private void Touch()
    {
        Version++;
        _byChord = null;
        Changed?.Invoke();
    }

    private Dictionary<KeyChord, KeyAction[]> BuildChordIndex()
    {
        var lists = new Dictionary<KeyChord, List<KeyAction>>();
        foreach (var action in _actions)
        {
            foreach (var chord in _current[action.Id])
            {
                if (!lists.TryGetValue(chord, out var list)) lists[chord] = list = new List<KeyAction>();
                list.Add(action);
            }
        }
        return lists.ToDictionary(kv => kv.Key, kv => kv.Value.ToArray());
    }

    private static bool SameSequence(IReadOnlyList<KeyChord> a, IReadOnlyList<KeyChord> b) =>
        a.Count == b.Count && a.Zip(b).All(p => p.First == p.Second);

    private static bool SameChords(IReadOnlyCollection<KeyChord> a, IReadOnlyCollection<KeyChord> b) =>
        a.Count == b.Count && new HashSet<KeyChord>(a).SetEquals(b);

    /// <summary>Problems in a catalog, for the tests and the startup self-check: a duplicated id,
    /// a held action in a context where there is no world to hold a key in, and the same default
    /// chord on two actions whose contexts overlap. Empty = sound.</summary>
    public static IReadOnlyList<string> Validate(IReadOnlyList<KeyAction> catalog)
    {
        var problems = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var a in catalog)
            if (!seen.Add(a.Id)) problems.Add($"duplicate action id '{a.Id}'");

        var table = new KeyBindingTable(catalog.GroupBy(a => a.Id).Select(g => g.First()));
        foreach (var a in table.Actions)
        {
            if (a.Kind == KeyActionKind.Held && (a.Context & KeyContext.World) == 0)
                problems.Add($"'{a.Id}' is held but never live in the world context");
            foreach (var chord in a.Defaults)
                foreach (var other in table.Conflicts(a.Id, chord))
                    if (string.CompareOrdinal(a.Id, other.Id) < 0)
                        problems.Add($"default chord {chord} is on both '{a.Id}' and '{other.Id}'");
        }
        return problems;
    }
}
