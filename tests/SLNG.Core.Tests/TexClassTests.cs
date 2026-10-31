using System;
using SLNG.Core;
using Xunit;

namespace SLNG.Core.Tests;

/// <summary>FEAT-PERF-28: which of five classes a texture request belongs to, for the per-class <c>[TexWait]</c> split.</summary>
public class TexClassTests
{
    [Fact]
    public void Your_own_avatar_is_self_whatever_else_is_true()
    {
        Assert.Equal(TexClass.Self, TexClassifier.Classify(isSelf: true, isAvatar: true, isBake: true, isFocus: true));
        Assert.Equal(TexClass.Self, TexClassifier.Classify(isSelf: true, isAvatar: true, isBake: false, isFocus: false));
    }

    [Fact]
    public void Anything_that_is_not_an_avatar_texture_is_scenery()
    {
        Assert.Equal(TexClass.Scenery, TexClassifier.Classify(isSelf: false, isAvatar: false, isBake: false, isFocus: false));
    }

    [Fact]
    public void The_focus_avatars_bakes_and_worn_textures_are_both_focus()
    {
        Assert.Equal(TexClass.Focus, TexClassifier.Classify(false, true, isBake: true, isFocus: true));
        Assert.Equal(TexClass.Focus, TexClassifier.Classify(false, true, isBake: false, isFocus: true));
    }

    [Fact]
    public void Other_avatars_split_into_bakes_and_worn_textures()
    {
        Assert.Equal(TexClass.OtherBake, TexClassifier.Classify(false, true, isBake: true, isFocus: false));
        Assert.Equal(TexClass.OtherWorn, TexClassifier.Classify(false, true, isBake: false, isFocus: false));
    }

    [Fact]
    public void Every_class_has_a_distinct_log_name()
    {
        var names = new System.Collections.Generic.HashSet<string>();
        foreach (TexClass c in Enum.GetValues<TexClass>()) Assert.True(names.Add(TexClassifier.Name(c)));
        Assert.Equal(TexClassifier.Count, names.Count);
    }

    [Fact]
    public void The_registry_hands_a_class_over_once_and_defaults_to_scenery()
    {
        var registry = new TexClassRegistry();
        var id = Guid.NewGuid();

        Assert.Equal(TexClass.Scenery, registry.Take(id));

        registry.Note(id, TexClass.OtherBake);
        Assert.Equal(TexClass.OtherBake, registry.Take(id));
        Assert.Equal(TexClass.Scenery, registry.Take(id));
    }

    [Fact]
    public void A_second_request_for_the_same_texture_keeps_the_more_important_class()
    {
        var registry = new TexClassRegistry();
        var id = Guid.NewGuid();

        registry.Note(id, TexClass.Scenery);
        registry.Note(id, TexClass.Focus);
        registry.Note(id, TexClass.OtherWorn);

        Assert.Equal(TexClass.Focus, registry.Take(id));
    }

    [Fact]
    public void The_registry_cannot_grow_without_bound()
    {
        var registry = new TexClassRegistry(capacity: 100);
        for (int i = 0; i < 1000; i++) registry.Note(Guid.NewGuid(), TexClass.Scenery);

        Assert.True(registry.Count <= 100);
    }
}
