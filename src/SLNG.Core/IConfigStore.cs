namespace SLNG.Core;

/// <summary>
/// The sectioned key/value file the client keeps its preferences in (Godot's <c>ConfigFile</c> in the app), reduced to what
/// the per-account stores need so their rules can be tested without the engine. Values are <c>bool</c>, <c>long</c>/<c>int</c>
/// or <c>string</c>; anything else reads as absent.
/// </summary>
public interface IConfigStore
{
    bool HasSection(string section);

    /// <summary>The stored value, or null when the key is not there (or is not a bool, number or string).</summary>
    object? Get(string section, string key);

    void Set(string section, string key, object value);

    /// <summary>Writes the file. Called once after a batch of <see cref="Set"/>.</summary>
    void Save();
}
