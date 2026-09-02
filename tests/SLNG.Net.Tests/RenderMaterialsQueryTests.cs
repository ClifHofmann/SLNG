using System.Linq;
using LibreMetaverse;
using LibreMetaverse.StructuredData;
using SLNG.Net;
using Xunit;

namespace SLNG.Net.Tests;

/// <summary>
/// BUG-RENDER-06 follow-up — the RenderMaterials query must carry each material id as an LLSD
/// <b>binary(16)</b>, the way the real viewer does (<c>llmaterialmgr.cpp</c> <c>processGetQueue</c>
/// -> <c>LLMaterialID::asLLSD()</c>). LibreMetaverse 3.1.3 sent them as <c>uuid</c> elements, which
/// the sim reads back with <c>.asBinary()</c> as nothing — so the cap returned zero materials with
/// no error and every Blinn-Phong face on SL fell through to a pixel-alpha guess (canopy flicker).
/// </summary>
public class RenderMaterialsQueryTests
{
    private static OSDArray Unzip(OSDMap query)
    {
        Assert.True(query.ContainsKey("Zipped"));
        var decoded = Helpers.ZDecompressOSD(query["Zipped"].AsBinary());
        return Assert.IsType<OSDArray>(decoded);
    }

    [Fact]
    public void Each_id_is_a_16_byte_binary_element_not_a_uuid()
    {
        var a = new UUID("6ad7601a-0000-4000-8000-000000000001");
        var b = new UUID("5825da35-0000-4000-8000-000000000002");

        var arr = Unzip(GridSession.BuildRenderMaterialsQuery(new[] { a, b }));

        Assert.Equal(2, arr.Count);
        Assert.All(arr, e => Assert.Equal(OSDType.Binary, e.Type));
        Assert.All(arr, e => Assert.Equal(16, e.AsBinary().Length));
    }

    [Fact]
    public void Round_trips_the_exact_id_bytes_in_order()
    {
        var ids = new[]
        {
            new UUID("6ad7601a-1111-2222-3333-444455556666"),
            new UUID("0000ffff-aaaa-bbbb-cccc-ddddeeeeffff"),
        };

        var arr = Unzip(GridSession.BuildRenderMaterialsQuery(ids));

        Assert.Equal(ids[0].GetBytes(), arr[0].AsBinary());
        Assert.Equal(ids[1].GetBytes(), arr[1].AsBinary());
        // and the sim's own reader path (LegacyMaterial parses ID the same way) recovers the id
        Assert.Equal(ids[0], new UUID(arr[0].AsBinary(), 0));
    }

    [Fact]
    public void Empty_id_list_still_produces_a_valid_zipped_empty_array()
    {
        var arr = Unzip(GridSession.BuildRenderMaterialsQuery(System.Array.Empty<UUID>()));
        Assert.Empty(arr);
    }
}
