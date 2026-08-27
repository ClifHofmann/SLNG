# Spec: FEAT-RENDER-06 (Fullbright Material Property)

## Context
In SL/OpenSim, primitives and individual faces can have the `Fullbright` property. These faces are rendered at full texture brightness and are completely unaffected by scene lighting, shadows, or Windlight/EEP atmospherics.

## Requirements
1. **Protocol Extraction:** Read `Fullbright` boolean from `TextureEntryFace` (LibreMetaverse).
2. **Material Pipeline:** Pass this state through the `MaterialResolver` into the rendering pipeline.
3. **Shader Implementation:** 
   - Since `StandardMaterial3D` was replaced by a custom spatial shader family (`FEAT-RENDER-01`), the fullbright logic must be implemented there.
   - Introduce a new uniform (e.g., `uniform bool is_fullbright;`).
   - If true, bypass the lighting calculation. (In Godot, this often means writing directly to `ALBEDO` and setting `EMISSION` to the same value, or using `render_mode unshaded;` if supported per-material instance).

## Acceptance Criteria
- [ ] Prims with Fullbright flag render without directional light, shadows, or ambient light influence.
- [ ] Non-Fullbright prims continue to render with EEP/Windlight atmospherics intact.
- [ ] Toggle state updates correctly if changed dynamically.

## Dependencies
- `FEAT-RENDER-01` (Custom Spatial Shader Family)
- `FEAT-ENV-01` (Windlight / EEP)
