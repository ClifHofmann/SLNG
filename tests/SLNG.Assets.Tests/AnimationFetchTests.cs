using SLNG.Net;
using Xunit;

namespace SLNG.Assets.Tests;

// 2026-09-30: after the asset cache was cleared, the built-in stand animation came back from the grid
// as "Transfer failed with status code Error". AssetService answered with a two-joint stand-in and
// remembered it for fifteen minutes (sliding), so the avatar stood in a T-pose until the viewer was
// restarted. A failed fetch has to be null, has to be asked for again, and the stand-in used while
// not connected must not outlive the login.
public sealed class AnimationFetchTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "slng-anim-" + Guid.NewGuid().ToString("N"));
    private static readonly Guid Stand = Guid.Parse("2408fe9e-df1d-1d7d-f4ff-1384fa7b350f");

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
    }

    /// <summary>The smallest animation the decoder accepts: a header and no joints.</summary>
    private static byte[] RealAnimation()
    {
        using var stream = new MemoryStream();
        using var w = new BinaryWriter(stream);
        w.Write((ushort)1); w.Write((ushort)0);   // version, sub-version
        w.Write(3);                               // priority
        w.Write(2.5f);                            // length
        w.Write((byte)0);                         // emote name, empty
        w.Write(0f); w.Write(2.5f);               // in / out point
        w.Write(1);                               // loop
        w.Write(0.2f); w.Write(0.2f);             // ease in / out
        w.Write(0u);                              // hand pose
        w.Write(0u);                              // joint count
        return stream.ToArray();
    }

    private AssetService Service(Func<Guid, Task<byte[]?>> fetch, Func<bool>? connected = null)
        => new(new GridSession(), _dir)
        {
            AnimationFetchOverride = fetch,
            IsConnectedOverride = connected ?? (() => true),
            AnimationRetryDelay = TimeSpan.Zero,
        };

    [Fact]
    public async Task An_animation_the_grid_serves_is_decoded()
    {
        var service = Service(_ => Task.FromResult<byte[]?>(RealAnimation()));

        var data = await service.GetAnimationAsync(Stand);

        Assert.NotNull(data);
        Assert.Empty(data!.Joints);
        Assert.Equal(2.5f, data.Length);
    }

    [Fact]
    public async Task An_animation_the_grid_does_not_answer_for_is_null_not_a_stand_in()
    {
        var service = Service(_ => Task.FromResult<byte[]?>(null));

        Assert.Null(await service.GetAnimationAsync(Stand));
    }

    [Fact]
    public async Task A_failure_is_not_remembered_the_next_request_asks_again()
    {
        bool gridWorks = false;
        int asked = 0;
        var service = Service(_ =>
        {
            asked++;
            return Task.FromResult<byte[]?>(gridWorks ? RealAnimation() : null);
        });

        Assert.Null(await service.GetAnimationAsync(Stand));
        gridWorks = true;
        var later = await service.GetAnimationAsync(Stand);

        Assert.NotNull(later);
        Assert.Empty(later!.Joints);
        Assert.True(asked >= 3, "two tries for the first request, then the second request asked again");
    }

    [Fact]
    public async Task A_first_failure_is_retried_once_before_giving_up()
    {
        int asked = 0;
        var service = Service(_ =>
        {
            asked++;
            return Task.FromResult<byte[]?>(asked == 1 ? null : RealAnimation());
        });

        var data = await service.GetAnimationAsync(Stand);

        Assert.NotNull(data);
        Assert.Equal(2, asked);
    }

    [Fact]
    public async Task What_the_grid_served_is_remembered()
    {
        int asked = 0;
        var service = Service(_ =>
        {
            asked++;
            return Task.FromResult<byte[]?>(RealAnimation());
        });

        await service.GetAnimationAsync(Stand);
        await service.GetAnimationAsync(Stand);

        Assert.Equal(1, asked);
    }

    [Fact]
    public async Task The_stand_in_used_before_login_does_not_outlive_the_login()
    {
        bool connected = false;
        var service = Service(_ => Task.FromResult<byte[]?>(RealAnimation()), () => connected);

        var before = await service.GetAnimationAsync(Stand);
        connected = true;
        var after = await service.GetAnimationAsync(Stand);

        Assert.NotEmpty(before!.Joints);   // the stand-in
        Assert.Empty(after!.Joints);       // the real one, fetched once there is a grid
    }

    [Fact]
    public async Task An_animation_already_on_disk_is_not_fetched()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllBytes(Path.Combine(_dir, Stand + ".anim"), RealAnimation());
        int asked = 0;
        var service = Service(_ => { asked++; return Task.FromResult<byte[]?>(null); });

        var data = await service.GetAnimationAsync(Stand);

        Assert.NotNull(data);
        Assert.Equal(0, asked);
    }
}
