using System;
using System.Collections.Generic;
using Godot;
using SLNG.Core;

namespace SLNG.App;

public partial class ObjectRenderer
{
    /// <summary>FEAT-PERF-20 Phase A1: why a surface left its shared material. A surface can be detached for
    /// several reasons at once (an animated face of a selected object); it goes back to the shared material only
    /// when the LAST reason is gone, so ending one (deselect) cannot undo another (animation, mirror, media).</summary>
    [Flags]
    private enum DetachReason : byte
    {
        TexAnim = 1,
        Mirror = 2,
        Selection = 4,
        Media = 8,
    }

    /// <summary>FEAT-PERF-20 Phase A1: releases all shared material references held by this visual state.</summary>
    private void ReleaseSurfaceMaterials(VisualState state)
    {
        if (state.SurfaceMaterialKeys == null) return;
        if (RenderConfig.ShareMaterials)
        {
            for (int i = 0; i < state.SurfaceMaterialKeys.Length; i++)
            {
                _materialCache.Release(state.SurfaceMaterialKeys[i]);
            }
        }
        state.SurfaceMaterialKeys = null;
        state.SurfaceDetached = null;
    }

    /// <summary>FEAT-PERF-20 Phase A1: puts a surface on a private copy of its material (copy-on-write) for
    /// <paramref name="reason"/>, or returns the copy it already has. The alpha depth pass (next_pass) is copied
    /// too: Duplicate() is shallow, and a shared next_pass would carry this surface's private writes (texture
    /// animation uniforms) to every sharer.</summary>
    private ShaderMaterial? DetachSurfaceMaterial(VisualState state, int surface, DetachReason reason)
    {
        if (!RenderConfig.ShareMaterials || state.SurfaceMaterialKeys == null)
            return SurfaceMaterial(state, surface);

        if (surface < 0 || surface >= state.SurfaceMaterialKeys.Length)
            return SurfaceMaterial(state, surface);

        state.SurfaceDetached ??= new byte[state.SurfaceMaterialKeys.Length];
        if (state.SurfaceDetached[surface] != 0)
        {
            state.SurfaceDetached[surface] |= (byte)reason;
            return SurfaceMaterial(state, surface); // already on a private copy
        }

        var current = SurfaceMaterial(state, surface);
        if (current == null) return null;

        var copy = (ShaderMaterial)current.Duplicate();
        if (copy.NextPass is ShaderMaterial nextPass) copy.NextPass = (Material)nextPass.Duplicate();
        if (state.MeshInstance.MaterialOverride != null)
        {
            state.MeshInstance.MaterialOverride = copy;
        }
        else
        {
            state.MeshInstance.SetSurfaceOverrideMaterial(surface, copy);
        }

        state.SurfaceDetached[surface] = (byte)reason;
        return copy;
    }

    /// <summary>FEAT-PERF-20 Phase A1: drops <paramref name="reason"/>; when no reason is left, the surface goes
    /// back to the shared material. If the shared entry is gone (it should not be while this surface holds its
    /// reference), the surface keeps its private copy rather than inventing a shared one.</summary>
    private void ReturnSurfaceMaterial(VisualState state, int surface, DetachReason reason)
    {
        if (!RenderConfig.ShareMaterials || state.SurfaceMaterialKeys == null)
            return;

        if (surface < 0 || surface >= state.SurfaceMaterialKeys.Length)
            return;

        if (state.SurfaceDetached == null || (state.SurfaceDetached[surface] & (byte)reason) == 0)
            return; // not detached for this reason

        state.SurfaceDetached[surface] &= (byte)~reason;
        if (state.SurfaceDetached[surface] != 0) return; // another reason still needs the private copy

        var key = state.SurfaceMaterialKeys[surface];
        if (!_materialCache.TryGet(key, out var entry) || entry.Material == null)
        {
            GD.PushWarning($"[SharedMaterials] {state.EntityId}: no shared entry to return surface {surface} to; keeping its private copy");
            return;
        }

        if (state.MeshInstance.MaterialOverride != null)
        {
            state.MeshInstance.MaterialOverride = entry.Material;
        }
        else
        {
            state.MeshInstance.SetSurfaceOverrideMaterial(surface, entry.Material);
        }
    }

    /// <summary>FEAT-PERF-20 Phase A1: detaches all surfaces of a selected visual onto private copies.</summary>
    private void DetachVisualForSelection(Guid id)
    {
        if (!RenderConfig.ShareMaterials) return;
        if (!_visuals.TryGetValue(id, out var state) || state.SurfaceMaterialKeys == null) return;
        for (int i = 0; i < state.SurfaceMaterialKeys.Length; i++)
            DetachSurfaceMaterial(state, i, DetachReason.Selection);
    }

    /// <summary>FEAT-PERF-20 Phase A1: returns all surfaces of a deselected visual to shared materials (unless
    /// another reason still holds them).</summary>
    private void ReturnVisualFromSelection(Guid id)
    {
        if (!RenderConfig.ShareMaterials) return;
        if (!_visuals.TryGetValue(id, out var state) || state.SurfaceMaterialKeys == null) return;
        for (int i = 0; i < state.SurfaceMaterialKeys.Length; i++)
            ReturnSurfaceMaterial(state, i, DetachReason.Selection);
    }
}
