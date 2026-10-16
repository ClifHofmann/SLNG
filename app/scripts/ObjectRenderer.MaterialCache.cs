using System;
using System.Collections.Generic;
using Godot;
using SLNG.Core;

namespace SLNG.App;

public partial class ObjectRenderer
{
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

    /// <summary>FEAT-PERF-20 Phase A1: detaches a surface material onto a private copy (copy-on-write)
    /// for private mutation such as texture animation, planar mirror, selection, or MOAP media.</summary>
    private ShaderMaterial? DetachSurfaceMaterial(VisualState state, int surface)
    {
        if (!RenderConfig.ShareMaterials || state.SurfaceMaterialKeys == null)
            return SurfaceMaterial(state, surface);

        if (surface < 0 || surface >= state.SurfaceMaterialKeys.Length)
            return SurfaceMaterial(state, surface);

        if (state.SurfaceDetached != null && state.SurfaceDetached[surface])
            return SurfaceMaterial(state, surface); // already detached

        var current = SurfaceMaterial(state, surface);
        if (current == null) return null;

        var copy = (ShaderMaterial)current.Duplicate();
        if (state.MeshInstance.MaterialOverride != null)
        {
            state.MeshInstance.MaterialOverride = copy;
        }
        else
        {
            state.MeshInstance.SetSurfaceOverrideMaterial(surface, copy);
        }

        state.SurfaceDetached ??= new bool[state.SurfaceMaterialKeys.Length];
        state.SurfaceDetached[surface] = true;
        return copy;
    }

    /// <summary>FEAT-PERF-20 Phase A1: returns a previously detached surface onto the shared material entry.</summary>
    private void ReturnSurfaceMaterial(VisualState state, int surface)
    {
        if (!RenderConfig.ShareMaterials || state.SurfaceMaterialKeys == null)
            return;

        if (surface < 0 || surface >= state.SurfaceMaterialKeys.Length)
            return;

        if (state.SurfaceDetached == null || !state.SurfaceDetached[surface])
            return; // not detached

        var key = state.SurfaceMaterialKeys[surface];
        ShaderMaterial shared;
        if (_materialCache.TryGet(key, out var entry) && entry.Material != null)
        {
            shared = entry.Material;
        }
        else
        {
            var current = SurfaceMaterial(state, surface);
            if (current == null) return;
            var cleanMat = (ShaderMaterial)current.Duplicate();
            cleanMat.SetShaderParameter(PrimShaderFamily.TexAnimB, default(Variant));
            if (ReferenceEquals(cleanMat.Shader, PrimShaderFamily.Mirror))
            {
                cleanMat.Shader = PrimShaderFamily.Opaque;
            }
            _materialCache.Register(key, cleanMat, new List<Guid>());
            shared = cleanMat;
        }

        if (state.MeshInstance.MaterialOverride != null)
        {
            state.MeshInstance.MaterialOverride = shared;
        }
        else
        {
            state.MeshInstance.SetSurfaceOverrideMaterial(surface, shared);
        }

        state.SurfaceDetached[surface] = false;
    }

    /// <summary>FEAT-PERF-20 Phase A1: detaches all surfaces of a selected visual onto private copies.</summary>
    private void DetachVisualForSelection(Guid id)
    {
        if (!RenderConfig.ShareMaterials) return;
        if (!_visuals.TryGetValue(id, out var state) || state.SurfaceMaterialKeys == null) return;
        for (int i = 0; i < state.SurfaceMaterialKeys.Length; i++)
            DetachSurfaceMaterial(state, i);
    }

    /// <summary>FEAT-PERF-20 Phase A1: returns all surfaces of a deselected visual to shared materials.</summary>
    private void ReturnVisualFromSelection(Guid id)
    {
        if (!RenderConfig.ShareMaterials) return;
        if (!_visuals.TryGetValue(id, out var state) || state.SurfaceMaterialKeys == null) return;
        for (int i = 0; i < state.SurfaceMaterialKeys.Length; i++)
            ReturnSurfaceMaterial(state, i);
    }
}
