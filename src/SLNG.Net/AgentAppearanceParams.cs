using LibreMetaverse;

namespace SLNG.Net;

/// <summary>
/// FEAT-AVATAR-01: builds the <c>AgentSetAppearance</c> visual-parameter array in the order the
/// wire actually uses, because LibreMetaverse's own encoder does not.
///
/// <para><b>The upstream bug.</b> <c>AppearanceManager.MakeAppearancePacket</c> fills the 218 wire
/// slots by iterating <c>VisualParams.Params</c> — <i>every</i> parameter, all 672 of them,
/// including the group-1/2 ones that are never transmitted — and taking the first 218. The wire
/// order is <c>VisualParams.Group0ParamIds</c>: the 253 <b>transmitted</b> ids (group 0
/// TWEAKABLE + group 3 TRANSMIT_NOT_TWEAKABLE), ascending. The two agree for 23 slots and diverge
/// from index 23 on, so 195 of 218 values are written to the wrong parameter and the simulator
/// persists that as the avatar's shape. Pinned by <c>SLNG.Assets.Tests.VisualParamOrderTests</c>.</para>
///
/// <para><b>What the real viewer does.</b> <c>llprocessparams.cpp:155-163</c> iterates every param
/// too, but <i>filters by group</i>:
/// <code>
/// if (param-&gt;getGroup() == VISUAL_PARAM_GROUP_TWEAKABLE ||
///     param-&gt;getGroup() == VISUAL_PARAM_GROUP_TRANSMIT_NOT_TWEAKABLE)
/// </code>
/// — which is exactly the <c>Group0ParamIds</c> set. LibreMetaverse is missing that filter. This
/// class reinstates it.</para>
///
/// <para>Deliberately pure and LibreMetaverse-free in its signatures: each worn wearable is passed
/// as its decoded <c>paramId → weight</c> map, so every rule here is unit-testable without a live
/// client (see <c>AgentAppearanceParamsTests</c>). The caller supplies those maps from
/// <c>AppearanceManager.GetWearables()</c> once the assets are decoded.</para>
/// </summary>
internal static class AgentAppearanceParams
{
    /// <summary>Wire array length for an avatar with no Physics layer — what the simulator expects
    /// and what <c>MakeAppearancePacket</c> allocates. Matches LibreMetaverse's own constant.</summary>
    internal const int DefaultLength = 218;

    /// <summary>Wire array length once a Physics wearable is worn.</summary>
    internal const int PhysicsLength = 251;

    /// <summary>Resolves one transmitted parameter's weight the way both the viewer and
    /// LibreMetaverse do: the first worn wearable that carries the id wins, otherwise the
    /// parameter's own default. Returns the default for an unknown id.</summary>
    internal static float ResolveWeight(int paramId, IReadOnlyList<IReadOnlyDictionary<int, float>> wearableParams)
    {
        if (!VisualParams.Params.TryGetValue(paramId, out var vp)) return 0f;

        foreach (var wearable in wearableParams)
        {
            if (wearable.TryGetValue(paramId, out var value)) return value;
        }
        return vp.DefaultValue;
    }

    /// <summary>Builds the visual-parameter byte array in <c>Group0ParamIds</c> order — byte[i]
    /// belongs to <c>Group0ParamIds[i]</c>, which is what the simulator, LibreMetaverse's
    /// <c>Avatar.DecodeVisualParams</c> and SLNG's <c>AvatarShapeService</c> all assume.
    ///
    /// <paramref name="length"/> is the wire length (<see cref="DefaultLength"/> /
    /// <see cref="PhysicsLength"/>); it is clamped to the id table, which is longer (253).</summary>
    internal static byte[] BuildWireArray(
        IReadOnlyList<IReadOnlyDictionary<int, float>> wearableParams, int length = DefaultLength)
    {
        var ids = VisualParams.Group0ParamIds;
        int n = Math.Min(length, ids.Length);
        var result = new byte[n];

        for (int i = 0; i < n; i++)
        {
            int paramId = ids[i];
            if (!VisualParams.Params.TryGetValue(paramId, out var vp)) continue;

            float weight = ResolveWeight(paramId, wearableParams);
            result[i] = Utils.FloatToByte(weight, vp.MinValue, vp.MaxValue);
        }

        return result;
    }

    // Param ids that feed the agent-height calculation. LibreMetaverse reads these off whatever
    // parameter its (mis-ordered) loop happened to land on, so the height it computes is wrong for
    // the same reason the array is -- they must be resolved by id, like everything else here.
    private const int ParamHeight = 33;
    private const int ParamHeelHeight = 198;
    private const int ParamPlatformHeight = 503;
    private const int ParamHeadSize = 682;
    private const int ParamLegLength = 692;
    private const int ParamNeckLength = 756;
    private const int ParamHipLength = 842;

    /// <summary>Agent height for <c>AgentSetAppearance.AgentData.Size.Z</c>. Same formula and
    /// constants as <c>MakeAppearancePacket</c>'s "Agent Size" region, but with each input looked up
    /// by parameter id instead of by loop position.</summary>
    internal static float ComputeAgentHeight(IReadOnlyList<IReadOnlyDictionary<int, float>> wearableParams)
    {
        // "Takes into account the Shoe Heel/Platform offsets but not the HeadSize offset."
        const double agentSizeBase = 1.706;

        double height =
            agentSizeBase
            + ResolveWeight(ParamLegLength, wearableParams) * .1918
            + ResolveWeight(ParamHipLength, wearableParams) * .0375
            + ResolveWeight(ParamHeight, wearableParams) * .12022
            + ResolveWeight(ParamHeadSize, wearableParams) * .01117
            + ResolveWeight(ParamNeckLength, wearableParams) * .038
            + ResolveWeight(ParamHeelHeight, wearableParams) * .08
            + ResolveWeight(ParamPlatformHeight, wearableParams) * .07;

        return (float)height;
    }

    /// <summary>Re-reads a built array the way the simulator will and checks every slot carries the
    /// weight it was built from. The last gate before anything reaches the grid: this task has
    /// corrupted a real avatar three times, each because a wrong assumption was only visible after
    /// the packet had already been persisted. If this returns false, do not send.</summary>
    internal static bool VerifyRoundTrip(
        byte[] wire, IReadOnlyList<IReadOnlyDictionary<int, float>> wearableParams, out string failure)
    {
        var ids = VisualParams.Group0ParamIds;

        if (wire.Length == 0 || wire.Length > ids.Length)
        {
            failure = $"array length {wire.Length} is not a plausible wire length (id table holds {ids.Length})";
            return false;
        }

        var decoded = DecodeWireArray(wire);

        for (int i = 0; i < wire.Length; i++)
        {
            int id = ids[i];
            if (!VisualParams.Params.TryGetValue(id, out var vp)) continue;

            if (!decoded.TryGetValue(id, out var got))
            {
                failure = $"param {id} (slot {i}) missing after round trip";
                return false;
            }

            float expected = ResolveWeight(id, wearableParams);
            // One byte over the parameter's own range is the transport's resolution.
            float tolerance = (vp.MaxValue - vp.MinValue) / 255f + 1e-4f;
            if (Math.Abs(got - expected) > tolerance)
            {
                failure = $"param {id} (slot {i}) round-tripped to {got}, expected {expected}";
                return false;
            }
        }

        failure = string.Empty;
        return true;
    }

    /// <summary>Reads a wire array back the way the simulator and <c>AvatarShapeService</c> do —
    /// positionally against <c>Group0ParamIds</c>. Exists so a built array can be round-tripped and
    /// verified BEFORE anything is sent; that verification is the whole reason this class is worth
    /// having over just trusting the permutation.</summary>
    internal static Dictionary<int, float> DecodeWireArray(byte[] wire)
    {
        var ids = VisualParams.Group0ParamIds;
        var result = new Dictionary<int, float>(Math.Min(wire.Length, ids.Length));

        for (int i = 0; i < wire.Length && i < ids.Length; i++)
        {
            if (!VisualParams.Params.TryGetValue(ids[i], out var vp)) continue;
            result[ids[i]] = Utils.ByteToFloat(wire[i], vp.MinValue, vp.MaxValue);
        }

        return result;
    }
}
