using LibreMetaverse;
using SLNG.Core;
using SLNG.Net;
using Xunit;

namespace SLNG.Net.Tests;

/// <summary>
/// FEAT-ANIMESH-02: the hop from LibreMetaverse's <c>ObjectManager.ObjectAnimation</c> event to
/// the neutral <see cref="ObjectAnimationEvent"/>. The converter is a pure static so it can be
/// pinned without a live <c>Simulator</c>; that the library really raises the event for a
/// received <c>ObjectAnimation</c> packet (3.1.6, <c>List&lt;Animation&gt;</c>, the sequence id
/// preserved, an empty list raised as an empty list) was established separately against the pinned
/// assembly and is why this class does not subscribe to the raw packet instead.
///
/// <para>Wire meaning (message_template.msg:7421-7434, <c>process_object_animation</c>,
/// llviewermessage.cpp:4108-4171): <c>Sender.ID</c> is the PRIM's object UUID, then a variable
/// list of <c>{AnimID, AnimSequenceID}</c>; the message replaces that prim's whole list.</para>
/// </summary>
public class ObjectAnimationConverterTests
{
    private static readonly UUID Object = new("11111111-2222-3333-4444-555555555555");
    private static readonly UUID Walk = new("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly UUID Wave = new("bbbbbbbb-0000-0000-0000-000000000002");
    private static readonly UUID Idle = new("cccccccc-0000-0000-0000-000000000003");
    private static readonly UUID Sit = new("dddddddd-0000-0000-0000-000000000004");

    private static Animation Anim(UUID id, int seq) => new()
    {
        AnimationID = id,
        AnimationSequence = seq,
        AnimationSourceObjectID = Object,
    };

    [Fact]
    public void CarriesRegionObjectAndEveryAnimationInOrder()
    {
        var evt = ObjectAnimationConverter.FromWire(
            0x0000_03E8_0000_0100UL, Object, new List<Animation> { Anim(Walk, 4), Anim(Wave, 9) });

        Assert.Equal(0x0000_03E8_0000_0100UL, evt.RegionHandle);
        Assert.Equal(Object.Guid, evt.ObjectId);
        Assert.Equal(
            new[] { new SignaledAnimation(Walk.Guid, 4), new SignaledAnimation(Wave.Guid, 9) },
            evt.Animations);
    }

    [Fact]
    public void AnEmptyListStaysAnEmptyListBecauseItMeansStopEverything()
    {
        var evt = ObjectAnimationConverter.FromWire(1UL, Object, new List<Animation>());

        Assert.NotNull(evt.Animations);
        Assert.Empty(evt.Animations);
    }

    [Fact]
    public void ANullListIsTreatedAsEmpty()
    {
        var evt = ObjectAnimationConverter.FromWire(1UL, Object, null);

        Assert.NotNull(evt.Animations);
        Assert.Empty(evt.Animations);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(-1)]
    [InlineData(int.MaxValue)]
    [InlineData(int.MinValue)]
    public void TheSequenceIdIsPassedThroughUntouched(int sequence)
    {
        // A changed sequence id is how the sim restarts an animation that is already playing, so
        // it has to arrive as sent -- including values a naive unsigned conversion would mangle.
        var evt = ObjectAnimationConverter.FromWire(1UL, Object, new List<Animation> { Anim(Walk, sequence) });

        Assert.Equal(sequence, evt.Animations[0].SequenceId);
    }

    [Fact]
    public void NothingIsDeduplicatedOrUnioned()
    {
        // Collapsing repeated ids ("keep the larger sequence id") and the union across a linkset
        // belong to the control avatar (llcontrolavatar.cpp:559-607), not to this layer.
        var evt = ObjectAnimationConverter.FromWire(
            1UL, Object, new List<Animation> { Anim(Walk, 1), Anim(Walk, 5), Anim(Walk, 1) });

        Assert.Equal(3, evt.Animations.Count);
    }

    [Fact]
    public void TheEventDoesNotAliasTheLibraryList()
    {
        // The library hands the same List<Animation> to its event and to Primitive.SignaledAnimations
        // and may reuse it; the neutral event must not change underneath the world queue.
        var source = new List<Animation> { Anim(Walk, 1) };
        var evt = ObjectAnimationConverter.FromWire(1UL, Object, source);

        source[0] = Anim(Wave, 2);
        source.Add(Anim(Idle, 3));

        Assert.Equal(new[] { new SignaledAnimation(Walk.Guid, 1) }, evt.Animations);
    }

    [Fact]
    public void TheDiagnosticLineNamesRegionObjectCountAndTheFirstIds()
    {
        var evt = ObjectAnimationConverter.FromWire(
            256000UL, Object, new List<Animation> { Anim(Walk, 1), Anim(Wave, 2) });

        Assert.Equal(
            "[ObjectAnimation] region=256000 object=11111111 count=2 first=aaaaaaaa,bbbbbbbb",
            ObjectAnimationConverter.Describe(evt));
    }

    [Fact]
    public void TheDiagnosticLineShowsAtMostThreeIdsButTheTrueCount()
    {
        var evt = ObjectAnimationConverter.FromWire(
            256000UL, Object,
            new List<Animation> { Anim(Walk, 1), Anim(Wave, 1), Anim(Idle, 1), Anim(Sit, 1) });

        Assert.Equal(
            "[ObjectAnimation] region=256000 object=11111111 count=4 first=aaaaaaaa,bbbbbbbb,cccccccc",
            ObjectAnimationConverter.Describe(evt));
    }

    [Fact]
    public void TheDiagnosticLineForAnEmptyListSaysSo()
    {
        var evt = ObjectAnimationConverter.FromWire(256000UL, Object, new List<Animation>());

        Assert.Equal(
            "[ObjectAnimation] region=256000 object=11111111 count=0",
            ObjectAnimationConverter.Describe(evt));
    }
}
