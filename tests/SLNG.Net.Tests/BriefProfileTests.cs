using System.Reflection;
using LibreMetaverse;
using LibreMetaverse.Messages.Linden;
using SLNG.Core;
using SLNG.Net;
using Xunit;

namespace SLNG.Net.Tests;

// FEAT-UI-39: the per-avatar note / payment-info / account-age cache behind the nearby-people list.
// No network: the cap reply is mapped from a hand-built AgentProfileMessage, the UDP replies are
// pushed through the private handlers exactly as the library raises them, and the connection-less
// paths are exercised on a session that never logged in.
public class BriefProfileTests
{
    private static readonly BindingFlags NonPublicInstance = BindingFlags.NonPublic | BindingFlags.Instance;

    private static void RaiseNotesReply(GridSession session, Guid avatar, string? notes)
    {
        var args = new AvatarNotesReplyEventArgs(new UUID(avatar), notes!);
        typeof(GridSession).GetMethod("OnAvatarNotesReply", NonPublicInstance)!
            .Invoke(session, new object?[] { null, args });
    }

    private static void RaisePropertiesReply(GridSession session, Guid avatar, string bornOn, bool identified, bool transacted)
    {
        var props = new Avatar.AvatarProperties
        {
            BornOn = bornOn,
            Identified = identified,
            Transacted = transacted,
            AboutText = "about",
            FirstLifeText = "",
            CharterMember = "",
            ProfileURL = "",
        };
        var args = new AvatarPropertiesReplyEventArgs(new UUID(avatar), props);
        typeof(GridSession).GetMethod("OnAvatarPropertiesReply", NonPublicInstance)!
            .Invoke(session, new object?[] { null, args });
    }

    // ---- ToBriefProfile: the AgentProfile capability's answer -> the neutral DTO ----------------

    [Theory]
    [InlineData(false, false, PaymentInfo.None)]
    [InlineData(true, false, PaymentInfo.OnFile)]
    [InlineData(false, true, PaymentInfo.Used)]
    [InlineData(true, true, PaymentInfo.Used)] // "has paid" beats "on file": the two never show together
    public void ToBriefProfile_maps_payment_flags(bool identified, bool transacted, PaymentInfo expected)
    {
        var m = new AgentProfileMessage { AvatarID = UUID.Random() };
        if (identified) m.Flags |= ProfileFlags.Identified;
        if (transacted) m.Flags |= ProfileFlags.Transacted;

        Assert.Equal(expected, GridSession.ToBriefProfile(m).Payment);
    }

    [Fact]
    public void ToBriefProfile_ignores_unrelated_flags()
    {
        var m = new AgentProfileMessage
        {
            AvatarID = UUID.Random(),
            Flags = ProfileFlags.AllowPublish | ProfileFlags.Online | ProfileFlags.AgeVerified,
        };

        Assert.Equal(PaymentInfo.None, GridSession.ToBriefProfile(m).Payment);
    }

    [Fact]
    public void ToBriefProfile_null_notes_become_known_empty()
    {
        var m = new AgentProfileMessage { AvatarID = UUID.Random(), Notes = null! };

        var p = GridSession.ToBriefProfile(m);

        Assert.Equal("", p.Notes); // "" = known empty; null would mean "not arrived"
        Assert.False(p.HasNote);
    }

    [Fact]
    public void ToBriefProfile_carries_notes_and_agent_id()
    {
        var id = UUID.Random();
        var m = new AgentProfileMessage { AvatarID = id, Notes = "met at the fair" };

        var p = GridSession.ToBriefProfile(m);

        Assert.Equal(id.Guid, p.AgentId);
        Assert.Equal("met at the fair", p.Notes);
        Assert.True(p.HasNote);
    }

    [Fact]
    public void ToBriefProfile_default_or_min_member_since_is_unknown()
    {
        Assert.Null(GridSession.ToBriefProfile(new AgentProfileMessage { AvatarID = UUID.Random() }).BornOnUtc);
        Assert.Null(GridSession.ToBriefProfile(
            new AgentProfileMessage { AvatarID = UUID.Random(), MemberSince = DateTime.MinValue }).BornOnUtc);
    }

    [Fact]
    public void ToBriefProfile_member_since_comes_out_as_utc()
    {
        var utc = new DateTime(2007, 5, 14, 12, 0, 0, DateTimeKind.Utc);
        var local = utc.ToLocalTime();
        var unspecified = new DateTime(2007, 5, 14, 12, 0, 0, DateTimeKind.Unspecified);

        var fromUtc = GridSession.ToBriefProfile(new AgentProfileMessage { AvatarID = UUID.Random(), MemberSince = utc }).BornOnUtc;
        var fromLocal = GridSession.ToBriefProfile(new AgentProfileMessage { AvatarID = UUID.Random(), MemberSince = local }).BornOnUtc;
        var fromUnspecified = GridSession.ToBriefProfile(new AgentProfileMessage { AvatarID = UUID.Random(), MemberSince = unspecified }).BornOnUtc;

        Assert.Equal(utc, fromUtc);
        Assert.Equal(DateTimeKind.Utc, fromUtc!.Value.Kind);
        Assert.Equal(utc, fromLocal);
        Assert.Equal(DateTimeKind.Utc, fromLocal!.Value.Kind);
        Assert.Equal(unspecified.Ticks, fromUnspecified!.Value.Ticks); // taken as already UTC, not shifted
        Assert.Equal(DateTimeKind.Utc, fromUnspecified.Value.Kind);
    }

    [Fact]
    public void ToBriefProfile_hide_age_only_when_true()
    {
        Assert.True(GridSession.ToBriefProfile(new AgentProfileMessage { AvatarID = UUID.Random(), HideAge = true }).AgeHidden);
        Assert.False(GridSession.ToBriefProfile(new AgentProfileMessage { AvatarID = UUID.Random(), HideAge = false }).AgeHidden);
        Assert.False(GridSession.ToBriefProfile(new AgentProfileMessage { AvatarID = UUID.Random(), HideAge = null }).AgeHidden);
    }

    // ---- Field-by-field merge ------------------------------------------------------------------

    [Fact]
    public void TryGetBriefProfile_is_false_before_anything_arrived()
    {
        using var session = new GridSession();

        Assert.False(session.TryGetBriefProfile(Guid.NewGuid(), out var profile));
        Assert.Null(profile);
    }

    [Fact]
    public void Notes_reply_then_properties_reply_keeps_both()
    {
        using var session = new GridSession();
        var id = Guid.NewGuid();

        RaiseNotesReply(session, id, "owes me a coffee");
        RaisePropertiesReply(session, id, "2007-05-14", identified: true, transacted: false);

        Assert.True(session.TryGetBriefProfile(id, out var p));
        Assert.Equal(id, p.AgentId);
        Assert.Equal("owes me a coffee", p.Notes);
        Assert.Equal(PaymentInfo.OnFile, p.Payment);
        Assert.Equal(new DateTime(2007, 5, 14, 0, 0, 0, DateTimeKind.Utc), p.BornOnUtc);
    }

    [Fact]
    public void Properties_reply_then_notes_reply_keeps_both()
    {
        using var session = new GridSession();
        var id = Guid.NewGuid();

        RaisePropertiesReply(session, id, "2007-05-14", identified: true, transacted: true);
        RaiseNotesReply(session, id, "owes me a coffee");

        Assert.True(session.TryGetBriefProfile(id, out var p));
        Assert.Equal("owes me a coffee", p.Notes);
        Assert.Equal(PaymentInfo.Used, p.Payment);
        Assert.NotNull(p.BornOnUtc);
    }

    [Fact]
    public void Properties_reply_alone_leaves_notes_not_arrived()
    {
        using var session = new GridSession();
        var id = Guid.NewGuid();

        RaisePropertiesReply(session, id, "", identified: false, transacted: false);

        Assert.True(session.TryGetBriefProfile(id, out var p));
        Assert.Null(p.Notes);                          // not arrived, which is not the same as empty
        Assert.Equal(PaymentInfo.None, p.Payment);     // but the payment status IS known
        Assert.Null(p.BornOnUtc);                      // unparseable / blank date
    }

    [Fact]
    public void Notes_reply_with_null_notes_is_known_empty()
    {
        using var session = new GridSession();
        var id = Guid.NewGuid();

        RaiseNotesReply(session, id, null);

        Assert.True(session.TryGetBriefProfile(id, out var p));
        Assert.Equal("", p.Notes);
    }

    [Fact]
    public void Later_properties_reply_without_a_date_does_not_wipe_the_date()
    {
        using var session = new GridSession();
        var id = Guid.NewGuid();

        RaisePropertiesReply(session, id, "2007-05-14", identified: false, transacted: false);
        RaisePropertiesReply(session, id, "", identified: false, transacted: true);

        Assert.True(session.TryGetBriefProfile(id, out var p));
        Assert.NotNull(p.BornOnUtc);
        Assert.Equal(PaymentInfo.Used, p.Payment); // the field that did arrive is updated
    }

    [Fact]
    public void A_later_properties_reply_never_lowers_a_known_payment_status()
    {
        using var session = new GridSession();
        var id = Guid.NewGuid();

        RaisePropertiesReply(session, id, "2007-05-14", identified: true, transacted: true);
        RaisePropertiesReply(session, id, "2007-05-14", identified: false, transacted: false);

        Assert.True(session.TryGetBriefProfile(id, out var p));
        Assert.Equal(PaymentInfo.Used, p.Payment);
    }

    [Fact]
    public void Properties_reply_still_raises_the_existing_AvatarPropertiesReceived_event()
    {
        using var session = new GridSession();
        var id = Guid.NewGuid();
        AvatarPropertiesEvent? received = null;
        session.AvatarPropertiesReceived += (_, e) => received = e;

        RaisePropertiesReply(session, id, "2007-05-14", identified: true, transacted: false);

        Assert.NotNull(received);
        Assert.Equal(id, received!.Properties.AgentId);
        Assert.Equal("2007-05-14", received.Properties.BornOn);
    }

    [Fact]
    public void Cap_reply_overlay_keeps_old_fields_the_reply_does_not_carry()
    {
        var id = Guid.NewGuid();
        var old = new AvatarBriefProfile(id, "old note", PaymentInfo.Used,
            new DateTime(2010, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var partial = new AvatarBriefProfile(id); // nothing arrived

        Assert.Equal(old, GridSession.OverlayBriefProfile(old, partial));

        var fuller = new AvatarBriefProfile(id, "", PaymentInfo.OnFile, null, AgeHidden: true);
        var merged = GridSession.OverlayBriefProfile(old, fuller);

        Assert.Equal("", merged.Notes);                  // an empty note IS an answer: the note was cleared
        Assert.Equal(PaymentInfo.OnFile, merged.Payment);
        Assert.Equal(old.BornOnUtc, merged.BornOnUtc);   // no date in the reply: keep ours
        Assert.True(merged.AgeHidden);
    }

    // ---- Events: exactly one per real change, none for a no-op ----------------------------------

    [Fact]
    public void Merge_that_changes_something_raises_exactly_one_event_with_the_new_value()
    {
        using var session = new GridSession();
        var id = Guid.NewGuid();
        var raised = new List<AvatarBriefProfile>();
        session.BriefProfileUpdated += (_, p) => raised.Add(p);

        var result = session.MergeBriefProfile(id, p => p with { Notes = "hello" });

        Assert.Single(raised);
        Assert.Equal(result, raised[0]);
        Assert.Equal(id, raised[0].AgentId);
        Assert.Equal("hello", raised[0].Notes);
    }

    [Fact]
    public void Merge_that_changes_nothing_raises_no_event()
    {
        using var session = new GridSession();
        var id = Guid.NewGuid();
        session.MergeBriefProfile(id, p => p with { Notes = "hello", Payment = PaymentInfo.None });
        var count = 0;
        session.BriefProfileUpdated += (_, _) => count++;

        session.MergeBriefProfile(id, p => p with { Notes = "hello" });
        session.MergeBriefProfile(id, p => p);
        RaiseNotesReply(session, id, "hello");

        Assert.Equal(0, count);

        RaiseNotesReply(session, id, "changed");
        Assert.Equal(1, count);
    }

    [Fact]
    public void Merge_that_learns_nothing_about_an_unknown_avatar_creates_no_entry_and_no_event()
    {
        using var session = new GridSession();
        var id = Guid.NewGuid();
        var count = 0;
        session.BriefProfileUpdated += (_, _) => count++;

        session.MergeBriefProfile(id, p => p);

        Assert.Equal(0, count);
        Assert.False(session.TryGetBriefProfile(id, out _));
    }

    [Fact]
    public void Merge_stamps_the_key_as_agent_id_even_if_the_update_returns_another()
    {
        using var session = new GridSession();
        var id = Guid.NewGuid();

        var merged = session.MergeBriefProfile(id, _ => new AvatarBriefProfile(Guid.NewGuid(), "x"));

        Assert.Equal(id, merged.AgentId);
        Assert.True(session.TryGetBriefProfile(id, out var cached));
        Assert.Equal(id, cached.AgentId);
    }

    [Fact]
    public void Concurrent_merges_of_different_fields_lose_nothing()
    {
        using var session = new GridSession();
        var ids = Enumerable.Range(0, 400).Select(_ => Guid.NewGuid()).ToArray();

        Parallel.ForEach(ids, new ParallelOptions { MaxDegreeOfParallelism = 8 }, id =>
        {
            Parallel.Invoke(
                () => session.MergeBriefProfile(id, p => p with { Notes = "n" }),
                () => session.MergeBriefProfile(id, p => p with { Payment = PaymentInfo.OnFile }),
                () => session.MergeBriefProfile(id, p => p with { BornOnUtc = new DateTime(2010, 1, 1, 0, 0, 0, DateTimeKind.Utc) }));
        });

        foreach (var id in ids)
        {
            Assert.True(session.TryGetBriefProfile(id, out var p));
            Assert.Equal("n", p.Notes);
            Assert.Equal(PaymentInfo.OnFile, p.Payment);
            Assert.NotNull(p.BornOnUtc);
        }
    }

    // ---- Disconnected: no throw, no cache effect ------------------------------------------------

    [Fact]
    public void RequestBriefProfile_without_connection_does_not_throw_or_touch_the_cache()
    {
        using var session = new GridSession();
        var id = Guid.NewGuid();
        var count = 0;
        session.BriefProfileUpdated += (_, _) => count++;

        var exception = Record.Exception(() =>
        {
            session.RequestBriefProfile(id);
            session.RequestBriefProfile(id, force: true);
            session.RequestBriefProfile(Guid.Empty);
        });

        Assert.Null(exception);
        Assert.False(session.TryGetBriefProfile(id, out _));
        Assert.Equal(0, count);
    }

    [Fact]
    public void SetAvatarNote_without_connection_does_not_throw_or_touch_the_cache()
    {
        using var session = new GridSession();
        var id = Guid.NewGuid();
        var count = 0;
        session.BriefProfileUpdated += (_, _) => count++;

        var exception = Record.Exception(() =>
        {
            session.SetAvatarNote(id, "a note");
            session.SetAvatarNote(id, null!);
            session.SetAvatarNote(Guid.Empty, "a note");
        });

        Assert.Null(exception);
        Assert.False(session.TryGetBriefProfile(id, out _));
        Assert.Equal(0, count);
    }

    // ---- The request gate (what RequestBriefProfile asks before it sends) -----------------------

    [Fact]
    public void Gate_refuses_the_empty_id_and_allows_an_unknown_avatar()
    {
        using var session = new GridSession();
        var now = DateTime.UtcNow;

        Assert.False(session.ShouldRequestBriefProfile(Guid.Empty, force: true, now));
        Assert.True(session.ShouldRequestBriefProfile(Guid.NewGuid(), force: false, now));
    }

    [Fact]
    public void Gate_skips_a_complete_fresh_entry_but_not_a_stale_or_incomplete_one_and_force_always_passes()
    {
        using var session = new GridSession();
        var complete = Guid.NewGuid();
        var noPayment = Guid.NewGuid();
        var noNotes = Guid.NewGuid();
        session.MergeBriefProfile(complete, p => p with { Notes = "", Payment = PaymentInfo.None });
        session.MergeBriefProfile(noPayment, p => p with { Notes = "x" });
        session.MergeBriefProfile(noNotes, p => p with { Payment = PaymentInfo.OnFile });
        var now = DateTime.UtcNow;

        Assert.False(session.ShouldRequestBriefProfile(complete, false, now));
        Assert.False(session.ShouldRequestBriefProfile(complete, false, now.AddMinutes(9)));
        Assert.True(session.ShouldRequestBriefProfile(complete, false, now.AddMinutes(11))); // gone stale
        Assert.True(session.ShouldRequestBriefProfile(noPayment, false, now));               // still missing a field
        Assert.True(session.ShouldRequestBriefProfile(noNotes, false, now));
        Assert.True(session.ShouldRequestBriefProfile(complete, true, now));                 // force overrides freshness
    }

    [Fact]
    public void Gate_honours_the_back_off_until_it_expires_and_force_overrides_it()
    {
        using var session = new GridSession();
        var id = Guid.NewGuid();
        var now = DateTime.UtcNow;
        session.SetBriefRetryAfter(id, now.AddSeconds(60), now);

        Assert.False(session.ShouldRequestBriefProfile(id, false, now));
        Assert.False(session.ShouldRequestBriefProfile(id, false, now.AddSeconds(59)));
        Assert.True(session.ShouldRequestBriefProfile(id, false, now.AddSeconds(61)));
        Assert.True(session.ShouldRequestBriefProfile(id, true, now));
    }
}
