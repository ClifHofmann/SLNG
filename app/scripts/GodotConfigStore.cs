using Godot;
using SLNG.Core;

namespace SLNG.App;

/// <summary><see cref="IConfigStore"/> over a Godot <c>ConfigFile</c>. Loads the file when created and keeps every section
/// it does not know, as the other settings classes do.</summary>
internal sealed class GodotConfigStore : IConfigStore
{
    private readonly string _path;
    private readonly ConfigFile _cfg = new();

    public GodotConfigStore(string path)
    {
        _path = path;
        _cfg.Load(path); // a missing file is an empty one
    }

    public bool HasSection(string section) => _cfg.HasSection(section);

    public object? Get(string section, string key)
    {
        if (!_cfg.HasSectionKey(section, key)) return null;
        var value = _cfg.GetValue(section, key);
        return value.VariantType switch
        {
            Variant.Type.Bool => (bool)value,
            Variant.Type.Int => (long)value,
            Variant.Type.String => (string)value,
            _ => null,
        };
    }

    public void Set(string section, string key, object value)
    {
        switch (value)
        {
            case bool b: _cfg.SetValue(section, key, b); break;
            case int i: _cfg.SetValue(section, key, i); break;
            case long l: _cfg.SetValue(section, key, l); break;
            case string s: _cfg.SetValue(section, key, s); break;
        }
    }

    public void Save() => _cfg.Save(_path);
}
