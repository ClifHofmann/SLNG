# [BUG-RENDER-52] The project asks for 65,536x anisotropy; D3D12 removes the device

- **Feature ID:** `BUG-RENDER-52`
- **Track:** `render`
- **Status:** 🧪 Review
- **Owner:** `claude`
- **Spec / Roadmap:** [ROADMAP.md](../ROADMAP.md), background [FEAT-PERF-13](../ROADMAP.md) and the render-thread freeze (BUG-PERF-22)
- **Branch:** `fix/BUG-RENDER-52-anisotropy-level`

## Overview & Goal

The separate render thread freezes on NVIDIA under Vulkan (two times, 0 % CPU, render thread inside `nvoglv64`, no TDR).
Godot 4.6 made D3D12 the default driver for new Windows projects because of Vulkan driver problems, so D3D12 was tried
(2026-10-29, Godot 4.7, RTX 4070, driver 616.92, `--rendering-driver d3d12`). It lost the device seconds after entering a
region: `CreateResource failed with error 0x887a0005` (`DXGI_ERROR_DEVICE_REMOVED`), 38 to 800 times, right after
`[ReflProbe] bake #1`.

The D3D12 debug layer, read under `cdbX64` (it speaks through `OutputDebugString`, so neither `godot.log` nor the console
shows it), named the cause:

    D3D12 ERROR: ID3D12Device::CreateSampler2: MaxAnisotropy must be in the range [0 to 16]. 65536 specified.
    [ STATE_CREATION ERROR #742: CREATE_SAMPLER_INVALID]
    D3D12: Removing Device.
    D3D12 ERROR: ID3D12Device::RemoveDevice: Device removal has been triggered ... (DXGI_ERROR_INVALID_CALL ...)

## Cause

`app/project.godot` had `textures/default_filters/anisotropic_filtering_level=16`. The setting is an index (0 off, 1 = 2x,
2 = 4x, 3 = 8x, 4 = 16x), and Godot's default samplers use `1 << level`, so 16 became 65,536. Vulkan clamps the value to the
device limit and nobody noticed: the effective filtering was and is 16x. D3D12 validates it and removes the device.

## Fix

`anisotropic_filtering_level=4` (16x). Same result on Vulkan, a valid sampler on D3D12. Self test `project: anisotropic
filtering level is an index (0-4)` keeps the value in range.

## Second finding: the descriptor heap

With the sampler fixed (v0.28.52) D3D12 stays up, enters a region and bakes the reflection probe. Then, as the region fills, 
the log carries ~37,000 `Uniforms were never supplied for set (0|1)` and `Cannot create uniform set because there's not enough 
room in the RESOURCES descriptor heap. Please increase ... rendering/rendering_device/d3d12/max_resource_descriptors` 
(default 65,536; the sampler heap, `max_sampler_descriptors`, is 1,024). Our scenes build many materials and textures, 
so the heap runs out and draws lose their bindings. v0.28.53 sets `max_resource_descriptors=1000000` (about 32 MB of 
heap; Vulkan ignores the key). Unknown: how many descriptors a loaded Sirens needs; 1,000,000 is the D3D12 tier 1/2 
shader-visible limit, so if that is still too few D3D12 is out of reach for this renderer.

## Acceptance Criteria

- [x] `project.godot` carries 4; self test added, headless `--selftest` 184 ok.
- [ ] `godot --path app --rendering-driver d3d12 -- --diag` stays up in a region (no `0x887a0005`).
- [ ] With D3D12 and the separate render thread: fps at Sirens against Vulkan's 27, and a longer run for the freeze.

## Not covered

D3D12 may have more problems behind this one (the debug layer also warned about barrier-only command lists, harmless).
Whether D3D12 avoids the Vulkan freeze is a separate, open question, tracked in the FEAT-PERF-13 notes.
