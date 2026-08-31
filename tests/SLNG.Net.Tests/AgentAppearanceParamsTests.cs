using System.Linq;
using LibreMetaverse;
using SLNG.Net;
using Xunit;

namespace SLNG.Net.Tests;

/// <summary>
/// FEAT-AVATAR-01: proves <see cref="AgentAppearanceParams"/> emits the visual-parameter array in
/// the order the wire actually uses, BEFORE any packet is ever sent.
///
/// This offline proof is the whole point. Three previous attempts to make a wearable edit work were
/// each tested by sending to a live grid and looking at the avatar, and each corrupted the user's
/// stored appearance because the ordering bug they all tripped over was invisible from the outside.
/// A round-trip assertion catches it at build time instead.
/// </summary>
public class AgentAppearanceParamsTests
{
    /// <summary>A wearable's decoded parameters, as <c>AssetWearable.Params</c> would supply them.</summary>
    private static Dictionary<int, float> Wearable(params (int Id, float Weight)[] entries)
        => entries.ToDictionary(e => e.Id, e => e.Weight);

    [Fact]
    public void BuildWireArray_has_the_requested_wire_length()
    {
        var built = AgentAppearanceParams.BuildWireArray(new[] { Wearable() });

        Assert.Equal(AgentAppearanceParams.DefaultLength, built.Length);
    }

    /// <summary>
    /// THE PROOF. Build an array from known per-parameter weights, decode it back the way the
    /// simulator does (positionally against Group0ParamIds), and require every value to land on the
    /// parameter it was authored for.
    ///
    /// Run this same round trip against LibreMetaverse's <c>MakeAppearancePacket</c> output and it
    /// fails for 195 of 218 slots — that is the bug this class exists to route around.
    /// </summary>
    [Fact]
    public void BuildWireArray_round_trips_every_value_onto_the_right_parameter()
    {
        var ids = VisualParams.Group0ParamIds;

        // Give every transmitted parameter a distinct, in-range weight, so a value landing on the
        // wrong id cannot coincidentally match.
        var authored = new Dictionary<int, float>();
        for (int i = 0; i < AgentAppearanceParams.DefaultLength; i++)
        {
            var vp = VisualParams.Params[ids[i]];
            // Spread across the parameter's own range; vary per index so neighbours differ.
            float t = (i % 17 + 1) / 18f;
            authored[ids[i]] = vp.MinValue + t * (vp.MaxValue - vp.MinValue);
        }

        var built = AgentAppearanceParams.BuildWireArray(new[] { authored });
        var decoded = AgentAppearanceParams.DecodeWireArray(built);

        for (int i = 0; i < AgentAppearanceParams.DefaultLength; i++)
        {
            int id = ids[i];
            var vp = VisualParams.Params[id];
            // One byte over the parameter's range is the transport's own resolution.
            float tolerance = (vp.MaxValue - vp.MinValue) / 255f + 1e-4f;

            Assert.True(decoded.ContainsKey(id), $"param {id} (slot {i}) missing after round trip");
            Assert.True(System.Math.Abs(decoded[id] - authored[id]) <= tolerance,
                $"param {id} (slot {i}) round-tripped to {decoded[id]}, authored {authored[id]}");
        }
    }

    /// <summary>The first worn wearable carrying a parameter wins; anything no wearable supplies
    /// falls back to that parameter's own default — the rule both the viewer and LibreMetaverse use.</summary>
    [Fact]
    public void ResolveWeight_prefers_the_first_wearable_then_the_parameter_default()
    {
        int id = VisualParams.Group0ParamIds[0];
        var vp = VisualParams.Params[id];
        float first = vp.MinValue + 0.25f * (vp.MaxValue - vp.MinValue);
        float second = vp.MinValue + 0.75f * (vp.MaxValue - vp.MinValue);

        Assert.Equal(first, AgentAppearanceParams.ResolveWeight(
            id, new[] { Wearable((id, first)), Wearable((id, second)) }));

        // No wearable supplies it -> the parameter's own default.
        Assert.Equal(vp.DefaultValue, AgentAppearanceParams.ResolveWeight(
            id, new[] { Wearable() }));
    }

    /// <summary>An empty worn set must still produce a full, default-valued array rather than a
    /// short or zero-filled one — a short array would be read as a truncated shape.</summary>
    [Fact]
    public void BuildWireArray_with_no_wearables_is_the_default_shape_not_zeros()
    {
        var ids = VisualParams.Group0ParamIds;
        var built = AgentAppearanceParams.BuildWireArray(System.Array.Empty<IReadOnlyDictionary<int, float>>());
        var decoded = AgentAppearanceParams.DecodeWireArray(built);

        Assert.Equal(AgentAppearanceParams.DefaultLength, built.Length);
        for (int i = 0; i < AgentAppearanceParams.DefaultLength; i++)
        {
            var vp = VisualParams.Params[ids[i]];
            float tolerance = (vp.MaxValue - vp.MinValue) / 255f + 1e-4f;
            Assert.True(System.Math.Abs(decoded[ids[i]] - vp.DefaultValue) <= tolerance,
                $"param {ids[i]} defaulted to {decoded[ids[i]]}, expected {vp.DefaultValue}");
        }
    }

    /// <summary>Height is resolved by parameter id, not by loop position — LibreMetaverse reads
    /// these off whatever its mis-ordered loop landed on, so its agent size is wrong for the same
    /// reason its param array is.</summary>
    [Fact]
    public void ComputeAgentHeight_responds_to_the_height_parameter_by_id()
    {
        const int paramHeight = 33;
        var vp = VisualParams.Params[paramHeight];

        float shortest = AgentAppearanceParams.ComputeAgentHeight(
            new[] { Wearable((paramHeight, vp.MinValue)) });
        float tallest = AgentAppearanceParams.ComputeAgentHeight(
            new[] { Wearable((paramHeight, vp.MaxValue)) });

        Assert.True(tallest > shortest,
            $"max height param should raise the agent; got {tallest} vs {shortest}");
        // Sanity: a plausible avatar height, not a degenerate value.
        Assert.InRange(tallest, 1.0f, 3.0f);
        Assert.InRange(shortest, 1.0f, 3.0f);
    }

    /// <summary>
    /// Demonstrates the upstream bug directly: reading a wire-order array as if it were in
    /// MakeAppearancePacket's order (first 218 of all Params) mis-assigns the overwhelming majority
    /// of values. Recorded here so the cost of getting the order wrong is visible next to the code
    /// that gets it right.
    /// </summary>
    [Fact]
    public void Reading_a_wire_array_in_encoder_order_mis_assigns_almost_everything()
    {
        var wire = VisualParams.Group0ParamIds;
        var encoderOrder = VisualParams.Params.Keys.Take(AgentAppearanceParams.DefaultLength).ToArray();

        int sameSlot = 0;
        for (int i = 0; i < AgentAppearanceParams.DefaultLength; i++)
            if (wire[i] == encoderOrder[i]) sameSlot++;

        Assert.Equal(23, sameSlot); // 23 of 218 — the other 195 land on the wrong parameter
    }
}
