using System.Text.RegularExpressions;
using SLNG.Core.Input;
using Xunit;

namespace SLNG.Core.Tests;

/// <summary>
/// Makes the list of missing Second Life shortcuts impossible to forget. Every entry of
/// <see cref="SlShortcutGaps"/> names the ROADMAP ticket that must bind it; this fails when that ticket
/// does not exist, and - the case it is really for - when it is already ✅ Done, i.e. the feature was
/// built and nobody bound its chord. To make it pass: add the action to <c>KeyActions</c> (recipe in
/// <c>docs/specs/FEAT-UI-43-keybindings.md</c>) and delete the entry from <c>SlShortcutGaps</c>.
/// </summary>
public class SlShortcutGapTests
{
    private static readonly Regex TicketId = new(@"^(?:M\d+(?:\.\d+)?-\d+|MVP\d+-\d+|[A-Z]+-[A-Z]+-\d+)$", RegexOptions.Compiled);

    private static string? RoadmapPath()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "docs", "ROADMAP.md");
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    /// <summary>Ticket id -> the table row's task cell (second column), for every row of the roadmap.</summary>
    private static Dictionary<string, string> RoadmapRows(string path)
    {
        var rows = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in File.ReadLines(path))
        {
            if (!line.StartsWith("| ", StringComparison.Ordinal)) continue;
            var cells = line.Split(" | ", 3, StringSplitOptions.None);
            if (cells.Length < 2) continue;
            var id = cells[0][2..].Trim();
            if (TicketId.IsMatch(id)) rows[id] = cells[1];
        }
        return rows;
    }

    [Fact]
    public void Every_gap_has_a_well_formed_ticket_and_a_reason()
    {
        foreach (var gap in SlShortcutGaps.All)
        {
            Assert.True(KeyChord.TryParse(gap.Chord, out _), $"gap chord '{gap.Chord}' does not parse");
            Assert.False(string.IsNullOrWhiteSpace(gap.SlName), $"gap {gap.Chord} has no SL command name");
            Assert.False(string.IsNullOrWhiteSpace(gap.Why), $"gap {gap.Chord} has no reason");
            Assert.False(string.IsNullOrWhiteSpace(gap.Feature), $"gap {gap.Chord} names no ROADMAP ticket that binds it");
            Assert.Matches(TicketId, gap.Feature);
        }
        Assert.Equal(SlShortcutGaps.All.Count, SlShortcutGaps.All.Select(g => KeyChord.Parse(g.Chord)).Distinct().Count());
    }

    [Fact]
    public void Every_gap_ticket_exists_in_the_roadmap_and_is_not_done()
    {
        var path = RoadmapPath();
        if (path == null) return; // no checkout of docs/ here (a packaged test run); the roadmap is the source of truth

        var rows = RoadmapRows(path);
        var problems = new List<string>();
        foreach (var gap in SlShortcutGaps.All)
        {
            if (!rows.TryGetValue(gap.Feature, out var task))
            {
                problems.Add($"{gap.Feature} (gap {gap.Chord}) is not a row in docs/ROADMAP.md: create it or link the gap to a real ticket");
            }
            else if (task.Contains('✅'))
            {
                problems.Add($"{gap.Feature} is done: bind {gap.Chord} ({gap.SlName}) and remove it from SlShortcutGaps");
            }
        }
        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    [Fact]
    public void A_gap_chord_is_not_also_a_default_in_the_catalog()
    {
        // A chord that is in both lists is either a faked binding or a gap nobody removed.
        var bound = KeyActions.All.SelectMany(a => a.Defaults).ToHashSet();
        foreach (var gap in SlShortcutGaps.All)
            Assert.False(bound.Contains(KeyChord.Parse(gap.Chord)), $"{gap.Chord} ({gap.SlName}) is bound in KeyActions: remove it from SlShortcutGaps");

        // ... and no catalog action stands for a command that is listed as a gap.
        var names = new HashSet<string>(KeyActions.All.Select(a => a.SlRef ?? ""));
        foreach (var gap in SlShortcutGaps.All)
            Assert.DoesNotContain("menu:" + gap.SlName, names);
    }

    [Fact]
    public void The_roadmap_row_of_every_gap_ticket_points_back_at_the_gap_list()
    {
        var path = RoadmapPath();
        if (path == null) return;

        // A developer who opens the ticket must see the keybinding duty; a row that never mentions it would
        // keep the link one-directional.
        var lines = File.ReadAllLines(path);
        foreach (var feature in SlShortcutGaps.All.Select(g => g.Feature).Distinct())
        {
            var row = lines.FirstOrDefault(l => l.StartsWith($"| {feature} |", StringComparison.Ordinal));
            Assert.True(row != null && row.Contains("FEAT-UI-43 gap list", StringComparison.Ordinal),
                $"{feature}'s ROADMAP row does not mention the FEAT-UI-43 gap list (\"binds <chord> ... FEAT-UI-43 gap list\")");
        }
    }
}
