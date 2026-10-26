using SLNG.Core.Camera;
using Xunit;

namespace SLNG.Core.Tests.Camera;

public class CameraZoomLimitsTests
{
    [Theory]
    [InlineData(CameraFocusKind.Object, 0.02f)] // OBJECT_MIN_ZOOM, llagentcamera.cpp:91
    [InlineData(CameraFocusKind.Land, 0.15f)] // LAND_MIN_ZOOM, llagentcamera.cpp:88
    [InlineData(CameraFocusKind.Avatar, 0.5f)] // AVATAR_MIN_ZOOM, llagentcamera.cpp:90
    [InlineData(CameraFocusKind.OwnAvatar, 0.5f)]
    public void Minimum_distance_per_kind_of_focus_is_the_viewers(CameraFocusKind kind, float metres)
        => Assert.Equal(metres, CameraZoomLimits.MinDistance(kind));

    [Fact]
    public void An_object_can_be_approached_closer_than_the_ground_and_the_ground_closer_than_a_person()
    {
        Assert.True(CameraZoomLimits.MinDistance(CameraFocusKind.Object) < CameraZoomLimits.MinDistance(CameraFocusKind.Land));
        Assert.True(CameraZoomLimits.MinDistance(CameraFocusKind.Land) < CameraZoomLimits.MinDistance(CameraFocusKind.Avatar));
    }

    [Fact]
    public void Following_ones_own_avatar_is_no_closer_than_the_old_floor()
        // Unchanged on purpose: the viewer's own floor there is 0.77 m followed by mouselook, which SLNG lacks.
        => Assert.Equal(0.5f, CameraZoomLimits.MinDistance(CameraFocusKind.OwnAvatar));

    [Fact]
    public void Near_plane_is_the_old_default_for_every_ordinary_distance()
    {
        foreach (float d in new[] { 0.1f, 0.5f, 1f, 4f, 20f, 200f })
            Assert.Equal(0.05f, CameraZoomLimits.NearPlane(d));
    }

    [Fact]
    public void Near_plane_shrinks_to_half_the_distance_when_the_camera_is_close()
    {
        Assert.Equal(0.04f, CameraZoomLimits.NearPlane(0.08f), 5);
        Assert.Equal(0.025f, CameraZoomLimits.NearPlane(0.05f), 5);
        Assert.Equal(0.01f, CameraZoomLimits.NearPlane(0.02f), 5);
    }

    [Fact]
    public void Near_plane_never_goes_below_one_centimetre()
    {
        Assert.Equal(CameraZoomLimits.MinNearPlane, CameraZoomLimits.NearPlane(0.001f));
        Assert.Equal(CameraZoomLimits.MinNearPlane, CameraZoomLimits.NearPlane(0f));
        Assert.Equal(CameraZoomLimits.MinNearPlane, CameraZoomLimits.NearPlane(-1f));
        Assert.Equal(CameraZoomLimits.MinNearPlane, CameraZoomLimits.NearPlane(float.NaN));
        Assert.Equal(0.01f, CameraZoomLimits.MinNearPlane, 6);
    }

    [Theory]
    [InlineData(CameraFocusKind.Object)]
    [InlineData(CameraFocusKind.Land)]
    [InlineData(CameraFocusKind.Avatar)]
    [InlineData(CameraFocusKind.OwnAvatar)]
    public void The_focus_point_is_never_clipped_at_the_closest_allowed_distance(CameraFocusKind kind)
    {
        float distance = CameraZoomLimits.MinDistance(kind);
        float near = CameraZoomLimits.NearPlane(distance);

        // The point the camera looks at must sit well beyond the near plane (a 2x margin), or the surface it was
        // aimed at would be cut away.
        Assert.True(near <= distance * 0.5f + 1e-6f, $"{kind}: near {near} too close to the focus at {distance}");
        Assert.True(near < distance);
    }

    [Fact]
    public void Near_plane_follows_the_wheel_all_the_way_in_without_ever_reaching_the_focus()
    {
        // Walk the wheel from 1 m to the object floor, one notch at a time.
        float distance = 1f;
        float floor = CameraZoomLimits.MinDistance(CameraFocusKind.Object);
        for (int i = 0; i < 40; i++)
        {
            distance = CameraZoom.WheelStep(distance, -1, floor);
            Assert.True(CameraZoomLimits.NearPlane(distance) < distance, $"notch {i}: distance {distance}");
        }

        Assert.Equal(floor, distance);
    }

    [Fact]
    public void Nineteen_wheel_notches_cover_the_range_from_a_person_floor_down_to_an_object_floor()
    {
        // 0.5 m -> 0.02 m is a factor of 25 = 2^4.64, i.e. 19 notches of 2^(1/4): usable as a zoom, not a cliff.
        float distance = CameraZoomLimits.AvatarMinZoom;
        int notches = 0;
        while (distance > CameraZoomLimits.ObjectMinZoom)
        {
            distance = CameraZoom.WheelStep(distance, -1, CameraZoomLimits.ObjectMinZoom);
            notches++;
        }

        Assert.Equal(19, notches);
    }
}
