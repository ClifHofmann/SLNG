using System;
using System.Collections.Generic;
using System.Numerics;
using SLNG.Core.Components;
using SLNG.Core.ECS;

namespace SLNG.Core;

/// <summary>
/// Which prims the radar map shows and how big it draws them (FEAT-UI-39 phase 2b). The starting point
/// is the reference viewer's mini-map: <c>LLViewerObject::setScale</c> lists a prim when you own it or
/// it is longer than 7.5 m (a build's walls and floors, never its trinkets), and
/// <c>LLViewerObjectList::renderObjectsForMap</c> sizes and colours it. Two things differ on purpose,
/// because on a built-up region the viewer's rule picks up too much: the size is the prim's footprint
/// (its two horizontal sides) rather than its 3D length, so a tree or a pole is not a "big object", and
/// the player chooses how big a prim has to be. Pure: it reads the world and answers, and the app draws
/// what comes back.
/// </summary>
public static class RadarObjects
{
    /// <summary>The sizes the player can choose between: a prim whose footprint is longer than this is
    /// listed. The first is the viewer's own threshold (<c>scale.magVecSquared() &gt; 7.5 * 7.5</c>); the
    /// others leave out the many mid-sized pieces of a built-up region. A prim the agent owns is always
    /// listed, whatever its size.</summary>
    public static readonly IReadOnlyList<float> MinSizePresetsMetres = new[] { 7.5f, 15f, 30f };

    /// <summary>The size a new radar starts with: the middle one. The viewer's 7.5 m, on a region of
    /// mesh scenery, paints most of the map over.</summary>
    public const float DefaultMinSizeMetres = 15f;

    /// <summary>The biggest square drawn, as a radius. The viewer's <c>MiniMapPrimMaxRadius</c>: a megaprim
    /// would otherwise blot out the map, and drawing it costs the same however far it spreads.</summary>
    public const float MaxRadiusMetres = 16f;

    /// <summary>The smallest square an owned prim is drawn at, so a button you own is still found.</summary>
    public const float MinOwnedRadiusMetres = 2f;

    /// <summary>Prims further above or below the avatar than this are left out (the viewer's
    /// <c>MiniMapPrimMaxVertDistance</c>): a skybox does not belong on the ground's map.</summary>
    public const float MaxVerticalDistanceMetres = 256f;

    /// <summary>How opaque a phantom prim is drawn, 0..1 (the viewer's <c>FSNetMapPhantomOpacity</c> of 90).</summary>
    public const float PhantomOpacity = 0.9f;

    /// <summary>The most prims the map draws. Every prim is two draw calls from managed code on every frame the
    /// radar is open, so on a region of mesh scenery an unbounded layer costs milliseconds per frame -- the larger
    /// ones are what shape the map anyway. See <see cref="LimitForDrawing"/>.</summary>
    public const int MaxDrawnObjects = 600;

    /// <summary>The map's object layer is looked at again no more often than this.</summary>
    public const double MinScanSeconds = 0.5;

    /// <summary>...and no less often than this, however big the region is: a stale layer beats a stalled
    /// frame.</summary>
    public const double MaxScanSeconds = 5.0;

    /// <summary>The share of the time a scan may take, at most. Looking through a crowded region's
    /// objects costs real milliseconds, so the wait between scans grows with what the last one cost.</summary>
    public const double ScanTimeShare = 0.005;

    // The viewer's fudge factor for the mean of the two horizontal sides, as a radius.
    private const float RadiusFudge = 1.3f;

    /// <summary>The prim's footprint: the length of its two horizontal sides taken together. A tall thin
    /// tree or lamp post has a small one; a wall or a floor has a large one.</summary>
    public static float FootprintMetres(Vector3 scale) => MathF.Sqrt(scale.X * scale.X + scale.Y * scale.Y);

    /// <summary>True if a prim of this size belongs on the map: one you own always does, any other when its
    /// footprint is longer than <paramref name="minSizeMetres"/>.</summary>
    public static bool IsListed(bool youOwn, Vector3 scale, float minSizeMetres)
        => youOwn || FootprintMetres(scale) > minSizeMetres;

    /// <summary>The index of the preset nearest <paramref name="metres"/>, so a value from a hand-edited
    /// settings file still lands on one of the offered sizes.</summary>
    public static int NearestMinSizeIndex(float metres)
    {
        int best = 0;
        float bestDistance = float.MaxValue;
        for (int i = 0; i < MinSizePresetsMetres.Count; i++)
        {
            float distance = MathF.Abs(MinSizePresetsMetres[i] - metres);
            if (!(distance < bestDistance)) continue; // NaN never wins, so it keeps the first
            bestDistance = distance;
            best = i;
        }
        return best;
    }

    /// <summary>The offered size nearest <paramref name="metres"/> (see <see cref="NearestMinSizeIndex"/>).</summary>
    public static float ClampMinSize(float metres) => MinSizePresetsMetres[NearestMinSizeIndex(metres)];

    /// <summary>The square's radius in metres: the mean of the two horizontal sides, halved, with the
    /// viewer's fudge, capped at <see cref="MaxRadiusMetres"/> and -- for an owned prim -- raised to
    /// <see cref="MinOwnedRadiusMetres"/>.</summary>
    public static float RadiusFor(Vector3 scale, bool youOwn)
    {
        float radius = (scale.X + scale.Y) * 0.5f * 0.5f * RadiusFudge;
        radius = MathF.Min(radius, MaxRadiusMetres);
        if (youOwn) radius = MathF.Max(radius, MinOwnedRadiusMetres);
        return radius;
    }

    /// <summary>False for a prim more than <see cref="MaxVerticalDistanceMetres"/> from the avatar's
    /// height. With no known height there is nothing to measure against, so nothing is left out.</summary>
    public static bool InVerticalRange(float objectZ, float? viewerZ)
        => viewerZ is not { } z || MathF.Abs(objectZ - z) <= MaxVerticalDistanceMetres;

    /// <summary>Cuts <paramref name="objects"/> down to at most <paramref name="max"/> entries, in place: the prims
    /// you own first (they are what you look for, however small), then the biggest of the rest. Anything under the
    /// limit is left exactly as it is.</summary>
    public static void LimitForDrawing(List<RadarObject> objects, int max = MaxDrawnObjects)
    {
        if (objects.Count <= max) return;

        var yours = new List<RadarObject>();
        var others = new List<RadarObject>();
        foreach (var o in objects) (o.IsYours ? yours : others).Add(o);

        yours.Sort((a, b) => b.Radius.CompareTo(a.Radius));
        others.Sort((a, b) => b.Radius.CompareTo(a.Radius));

        int keepYours = Math.Min(yours.Count, max);
        int keepOthers = Math.Max(0, max - keepYours);

        objects.Clear();
        for (int i = 0; i < keepOthers && i < others.Count; i++) objects.Add(others[i]);
        for (int i = 0; i < keepYours; i++) objects.Add(yours[i]);
    }

    /// <summary>How long to wait before the next scan, given how long the last one took: the minimum
    /// while scans are cheap, longer while they are not, never beyond the maximum.</summary>
    public static double NextScanDelaySeconds(double lastScanSeconds)
    {
        if (double.IsNaN(lastScanSeconds) || lastScanSeconds < 0) lastScanSeconds = 0;
        return Math.Clamp(lastScanSeconds / ScanTimeShare, MinScanSeconds, MaxScanSeconds);
    }

    /// <summary>
    /// Fills <paramref name="into"/> (cleared first) with every prim the map shows, placed in the
    /// current region's metres. Skipped: anything that is not a plain prim (avatars, trees and grass),
    /// anything worn, a linked child whose root has not arrived yet (its position is not known), and
    /// whatever <see cref="InVerticalRange"/> rules out. Reads the world, so call it on the thread
    /// that owns the world -- the main thread.
    /// </summary>
    /// <param name="currentRegion">The region the radar is drawn relative to.</param>
    /// <param name="viewerZ">The local avatar's height, or null if not known.</param>
    /// <param name="minSizeMetres">How long a prim's footprint must be for a prim you do not own to be
    /// collected; see <see cref="IsListed"/>.</param>
    /// <returns>How many objects were collected.</returns>
    public static int Collect(World world, ulong currentRegion, float? viewerZ, float minSizeMetres, List<RadarObject> into)
    {
        into.Clear();
        double currentX = RegionHandle.OriginX(currentRegion);
        double currentY = RegionHandle.OriginY(currentRegion);

        foreach (var entity in world.GetAllEntities())
        {
            var prim = entity.GetComponent<PrimitiveComponent>();
            if (prim == null || PrimPCode.IsFoliage(prim.Shape.PCode)) continue;

            // A worn prim lies where its avatar is and is carried with it: not part of the land.
            if (prim.AttachmentPoint != 0 || entity.HasComponent<AttachmentComponent>()) continue;

            bool youOwn = prim.YouAreOwner;
            if (!IsListed(youOwn, prim.Scale, minSizeMetres)) continue;

            var transform = entity.GetComponent<TransformComponent>();
            if (transform == null) continue;
            var position = transform.Position;
            if (!float.IsFinite(position.X) || !float.IsFinite(position.Y) || !float.IsFinite(position.Z)) continue;

            // A child whose root has not arrived has only its offset from the root, not a place.
            if (transform.ParentLocalId != 0 && world.GetEntity(entity.RegionHandle, transform.ParentLocalId) == null)
                continue;

            if (!InVerticalRange(position.Z, viewerZ)) continue;

            bool belowWater = world.Terrains.TryGetValue(entity.RegionHandle, out var terrain)
                && position.Z < terrain.WaterHeight;

            var offset = new Vector2(
                (float)(RegionHandle.OriginX(entity.RegionHandle) - currentX),
                (float)(RegionHandle.OriginY(entity.RegionHandle) - currentY));

            into.Add(new RadarObject(
                new Vector2(position.X, position.Y) + offset,
                RadiusFor(prim.Scale, youOwn),
                youOwn,
                belowWater,
                prim.IsPhantom));
        }

        return into.Count;
    }
}
