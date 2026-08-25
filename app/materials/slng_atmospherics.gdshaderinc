// Atmospherics seam for SLNG's world surfaces (FEAT-RENDER-01, ADR 0002, ADR 0003).
//
// Shared by prims, avatars, terrain, and water.
// Windlight / EEP atmospheric scattering is applied here per-fragment in Phase 5.
// Ships as a no-op so that filling it later modifies this single function without
// having to touch each individual shader or introduce visual regressions early.

vec3 slng_apply_atmospherics(vec3 color, vec3 view_position) {
    return color;
}
