using SLNG.Core;
using Xunit;

namespace SLNG.Core.Tests;

/// <summary>BUG-PERF-24: one rank scale for scenery and for other avatars' textures, with the focus avatar and your own above it.</summary>
public class TexturePriorityTests
{
    [Fact]
    public void An_object_is_ranked_by_radius_over_distance()
    {
        Assert.Equal(0.1f, TexturePriority.ForWorld(radius: 0.5f, distance: 5f, behindCamera: false), 5);
        Assert.Equal(0.5f, TexturePriority.ForWorld(radius: 1f, distance: 2f, behindCamera: false), 5);
    }

    [Fact]
    public void Something_behind_the_camera_keeps_half_its_rank_not_none()
    {
        float front = TexturePriority.ForWorld(1f, 4f, behindCamera: false);
        float behind = TexturePriority.ForWorld(1f, 4f, behindCamera: true);

        Assert.True(behind > 0f);
        Assert.Equal(front * 0.5f, behind, 6);
    }

    [Fact]
    public void A_tiny_distance_cannot_blow_the_rank_up()
    {
        // The scenery's rule: distances under 0.1 m count as 0.1 m.
        Assert.Equal(TexturePriority.ForWorld(1f, 0.1f, false), TexturePriority.ForWorld(1f, 0f, false));
        Assert.Equal(10f, TexturePriority.ForWorld(1f, 0f, false), 5);
    }

    [Fact]
    public void An_avatar_and_scenery_of_the_same_size_rank_the_same()
    {
        float scenery = TexturePriority.ForWorld(radius: 0.95f, distance: 10f, behindCamera: false);
        float avatar = TexturePriority.ForAvatar(isSelf: false, isFocus: false, sizePriority: TexturePriority.ForWorld(0.95f, 10f, false));

        Assert.Equal(scenery, avatar);
    }

    [Fact]
    public void A_nearer_avatar_ranks_above_a_farther_one_and_above_small_scenery()
    {
        float near = TexturePriority.ForAvatar(false, false, TexturePriority.ForWorld(0.95f, 4f, false));
        float far = TexturePriority.ForAvatar(false, false, TexturePriority.ForWorld(0.95f, 40f, false));
        float smallScenery = TexturePriority.ForWorld(0.05f, 3f, false);

        Assert.True(near > far);
        Assert.True(far > smallScenery);
    }

    [Fact]
    public void The_focus_avatar_ranks_above_any_scenery_and_any_other_avatar()
    {
        float closestPossible = TexturePriority.ForWorld(radius: 1000f, distance: 0f, behindCamera: false);
        float focus = TexturePriority.ForAvatar(false, isFocus: true, sizePriority: 0f);

        Assert.Equal(PrioritySubject.FocusTexturePriority, focus);
        Assert.True(focus > closestPossible);
        Assert.True(focus > TexturePriority.ForAvatar(false, false, closestPossible));
    }

    [Fact]
    public void Your_own_avatar_ranks_above_everything_including_the_focus()
    {
        float self = TexturePriority.ForAvatar(isSelf: true, isFocus: false, sizePriority: 0f);

        Assert.Equal(PrioritySubject.SelfTexturePriority, self);
        Assert.True(self > PrioritySubject.FocusTexturePriority);
        Assert.Equal(self, TexturePriority.ForAvatar(isSelf: true, isFocus: true, sizePriority: 5f));
    }
}
