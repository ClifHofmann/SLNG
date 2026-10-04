using SLNG.Core.ChatLogs;
using SLNG.Core.Services;
using Xunit;

namespace SLNG.Core.Tests.ChatLogs;

// FEAT-UI-15. The Recent list is what lets a closed IM or group chat be reopened: the log on disk only
// knows a cleaned name, the tab needs the id.
public sealed class RecentConversationListTests
{
    private static readonly DateTime T0 = new(2026, 10, 4, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void The_most_recently_active_conversation_is_first()
    {
        var list = new RecentConversationList();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        list.Touch(ChatLogKind.Im, a, "Alice", T0);
        list.Touch(ChatLogKind.Group, b, "Builders", T0.AddMinutes(1));
        list.Touch(ChatLogKind.Im, a, "Alice", T0.AddMinutes(2));

        Assert.Equal(new[] { a, b }, list.Items.Select(c => c.Id));
    }

    [Fact]
    public void A_repeat_message_on_top_is_not_worth_saving_but_a_new_position_is()
    {
        var list = new RecentConversationList();
        var a = Guid.NewGuid();
        Assert.True(list.Touch(ChatLogKind.Im, a, "Alice", T0));
        Assert.False(list.Touch(ChatLogKind.Im, a, "Alice", T0.AddMinutes(1)));
        Assert.Equal(T0.AddMinutes(1), list.Items[0].LastActivityUtc);

        list.Touch(ChatLogKind.Im, Guid.NewGuid(), "Bob", T0.AddMinutes(2));
        Assert.True(list.Touch(ChatLogKind.Im, a, "Alice", T0.AddMinutes(3)));
    }

    [Fact]
    public void An_im_and_a_group_with_the_same_id_are_two_entries()
    {
        var list = new RecentConversationList();
        var id = Guid.NewGuid();
        list.Touch(ChatLogKind.Im, id, "X", T0);
        list.Touch(ChatLogKind.Group, id, "X", T0);
        Assert.Equal(2, list.Items.Count);
    }

    [Fact]
    public void Nearby_chat_and_nameless_or_idless_entries_are_not_listed()
    {
        var list = new RecentConversationList();
        Assert.False(list.Touch(ChatLogKind.Local, Guid.NewGuid(), "Main", T0));
        Assert.False(list.Touch(ChatLogKind.Im, Guid.Empty, "Alice", T0));
        Assert.False(list.Touch(ChatLogKind.Im, Guid.NewGuid(), "  ", T0));
        Assert.Empty(list.Items);
    }

    [Fact]
    public void The_list_is_capped_and_drops_the_oldest()
    {
        var list = new RecentConversationList(capacity: 3);
        var ids = Enumerable.Range(0, 5).Select(_ => Guid.NewGuid()).ToArray();
        for (int i = 0; i < ids.Length; i++) list.Touch(ChatLogKind.Im, ids[i], "P" + i, T0.AddMinutes(i));

        Assert.Equal(new[] { ids[4], ids[3], ids[2] }, list.Items.Select(c => c.Id));
    }

    [Fact]
    public void Remove_takes_one_entry_out()
    {
        var list = new RecentConversationList();
        var a = Guid.NewGuid();
        list.Touch(ChatLogKind.Im, a, "Alice", T0);
        Assert.True(list.Remove(ChatLogKind.Im, a));
        Assert.False(list.Remove(ChatLogKind.Im, a));
        Assert.Empty(list.Items);
    }

    [Fact]
    public void Save_and_load_round_trip_in_order()
    {
        string dir = Path.Combine(Path.GetTempPath(), "slng-recent-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(dir, "sub", "recent.json");
        try
        {
            var list = new RecentConversationList();
            var a = Guid.NewGuid();
            var b = Guid.NewGuid();
            list.Touch(ChatLogKind.Im, a, "Alice Ünï", T0);
            list.Touch(ChatLogKind.Group, b, "Builders", T0.AddMinutes(5));
            Assert.True(list.Save(path));

            var loaded = new RecentConversationList();
            loaded.Load(path);
            Assert.Equal(list.Items, loaded.Items);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void A_missing_or_corrupt_file_is_an_empty_list()
    {
        string path = Path.Combine(Path.GetTempPath(), "slng-recent-" + Guid.NewGuid().ToString("N") + ".json");
        var list = new RecentConversationList();
        list.Touch(ChatLogKind.Im, Guid.NewGuid(), "Alice", T0);

        list.Load(path);
        Assert.Empty(list.Items);

        File.WriteAllText(path, "{ not json");
        try
        {
            list.Touch(ChatLogKind.Im, Guid.NewGuid(), "Alice", T0);
            list.Load(path);
            Assert.Empty(list.Items);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
