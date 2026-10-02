using System.Xml.Linq;

namespace SLNG.Core.ChatLogs;

/// <summary>
/// FEAT-UI-41: reads single values out of a Firestorm / Linden settings file
/// (<c>settings.xml</c>, <c>settings_per_account.xml</c>). The format is LLSD XML: a map of
/// <c>&lt;key&gt;Name&lt;/key&gt;&lt;map&gt;…&lt;key&gt;Value&lt;/key&gt;&lt;integer&gt;1&lt;/integer&gt;…&lt;/map&gt;</c>.
/// Only the handful of values that decide where Firestorm keeps a log and how it names a file are
/// ever read; the file is never written. A missing file, a missing key and malformed XML all read as
/// "not set" -- which, for the viewer too, means the built-in default applies.
/// </summary>
public static class LlsdSettingsFile
{
    /// <summary>The control's value as text (<c>&lt;string&gt;</c>, <c>&lt;integer&gt;</c> or
    /// <c>&lt;boolean&gt;</c> content), or null when the control is absent.</summary>
    public static string? ReadValue(string? xml, string name)
    {
        if (string.IsNullOrWhiteSpace(xml)) return null;

        XDocument doc;
        try { doc = XDocument.Parse(xml); }
        catch (System.Xml.XmlException) { return null; }

        XElement? top = doc.Root?.Element("map");
        if (top is null) return null;

        foreach (XElement key in top.Elements("key"))
        {
            if (key.Value != name) continue;
            if (key.NextNode is not XElement { Name.LocalName: "map" } control) continue;

            foreach (XElement inner in control.Elements("key"))
            {
                if (inner.Value == "Value" && inner.NextNode is XElement value) return value.Value;
            }
        }
        return null;
    }

    /// <summary>The control as a boolean (<c>1</c>/<c>true</c> or <c>0</c>/<c>false</c>), or null when
    /// absent or not one of those.</summary>
    public static bool? ReadBool(string? xml, string name)
    {
        string? v = ReadValue(xml, name)?.Trim();
        return v switch
        {
            "1" or "true" or "TRUE" or "True" => true,
            "0" or "false" or "FALSE" or "False" => false,
            _ => null,
        };
    }

    /// <summary>Reads a settings file from disk as text, or null when it cannot be read. Opened
    /// shared: the other viewer may be running and have it open.</summary>
    public static string? ReadFile(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(fs, System.Text.Encoding.UTF8);
            return reader.ReadToEnd();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
