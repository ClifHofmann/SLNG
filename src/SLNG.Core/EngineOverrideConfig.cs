using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace SLNG.Core;

/// <summary>
/// BUG-PERF-22: off-main-thread inspection and modification of Godot engine override files
/// (<c>engine_overrides.cfg</c>) without using Godot APIs.
/// </summary>
public static class EngineOverrideConfig
{
    public const string SectionRendering = "rendering";
    public const string KeyThreadModel = "driver/threads/thread_model";

    /// <summary>
    /// Removes <paramref name="targetKey"/> from <paramref name="targetSection"/> in the given INI/ConfigFile text.
    /// If no key-value pairs remain anywhere in the document, <c>HasOtherKeys</c> will be false.
    /// Empty sections that have no keys remaining are omitted from the output.
    /// </summary>
    public static (bool HasOtherKeys, string NewContent) RemoveKey(string content, string targetSection, string targetKey)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return (false, string.Empty);
        }

        var lines = content.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
        var sections = new List<IniSection>();
        var currentSection = new IniSection(string.Empty);
        sections.Add(currentSection);

        int totalOtherKeys = 0;

        foreach (var rawLine in lines)
        {
            string trimmed = rawLine.Trim();

            if (trimmed.StartsWith("[") && trimmed.EndsWith("]"))
            {
                string secName = trimmed.Substring(1, trimmed.Length - 2).Trim();
                currentSection = new IniSection(secName);
                sections.Add(currentSection);
                continue;
            }

            int eq = trimmed.IndexOf('=');
            bool isKey = eq > 0 && !trimmed.StartsWith(";") && !trimmed.StartsWith("#");

            if (isKey)
            {
                string key = trimmed.Substring(0, eq).Trim();
                if (string.Equals(currentSection.Name, targetSection, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(key, targetKey, StringComparison.OrdinalIgnoreCase))
                {
                    // Target key to remove: skip adding to section
                    continue;
                }

                currentSection.KeyCount++;
                totalOtherKeys++;
                currentSection.Lines.Add(rawLine);
            }
            else
            {
                // Blank line, comment, etc.
                currentSection.Lines.Add(rawLine);
            }
        }

        if (totalOtherKeys == 0)
        {
            return (false, string.Empty);
        }

        var sb = new StringBuilder();
        bool firstSection = true;

        foreach (var sec in sections)
        {
            if (sec.KeyCount == 0 && !string.IsNullOrEmpty(sec.Name))
            {
                // Omit sections that have no keys left
                continue;
            }

            if (sec.KeyCount == 0 && string.IsNullOrEmpty(sec.Name))
            {
                // File header comments / blanks before first section: only include if they have non-blank content
                bool hasContent = false;
                foreach (var l in sec.Lines)
                {
                    if (!string.IsNullOrWhiteSpace(l)) { hasContent = true; break; }
                }
                if (!hasContent) continue;
            }

            if (!firstSection)
            {
                sb.AppendLine();
            }

            if (!string.IsNullOrEmpty(sec.Name))
            {
                sb.AppendLine($"[{sec.Name}]");
            }

            foreach (var l in sec.Lines)
            {
                sb.AppendLine(l);
            }

            firstSection = false;
        }

        return (true, sb.ToString().TrimEnd() + Environment.NewLine);
    }

    /// <summary>
    /// Removes the thread_model override from the specified file using atomic file operations.
    /// If no other keys remain, the file is deleted.
    /// </summary>
    public static bool RevertThreadModelPreference(string overrideFilePath)
    {
        if (!File.Exists(overrideFilePath)) return false;

        string content;
        try
        {
            content = File.ReadAllText(overrideFilePath);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[EngineOverrideConfig] Failed to read {overrideFilePath}: {ex.Message}");
            return false;
        }

        var (hasOtherKeys, newContent) = RemoveKey(content, SectionRendering, KeyThreadModel);

        if (!hasOtherKeys)
        {
            try
            {
                File.Delete(overrideFilePath);
                return true;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[EngineOverrideConfig] Failed to delete empty override file {overrideFilePath}: {ex.Message}");
                return false;
            }
        }

        string dir = Path.GetDirectoryName(overrideFilePath) ?? string.Empty;
        string tempFile = Path.Combine(dir, $"{Path.GetFileName(overrideFilePath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(tempFile, newContent);
            File.Move(tempFile, overrideFilePath, overwrite: true);
            return true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[EngineOverrideConfig] Failed to rewrite {overrideFilePath}: {ex.Message}");
            try { if (File.Exists(tempFile)) File.Delete(tempFile); } catch { }
            return false;
        }
    }

    private sealed class IniSection
    {
        public string Name { get; }
        public List<string> Lines { get; } = new();
        public int KeyCount { get; set; }

        public IniSection(string name)
        {
            Name = name;
        }
    }
}

