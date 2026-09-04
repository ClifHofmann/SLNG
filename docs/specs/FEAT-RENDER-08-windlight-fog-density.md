# [FEAT-RENDER-08] Windlight Fog Density

- **Feature ID:** FEAT-RENDER-08
- **Track:** 
ender
- **Status:** ✅ Done
- **Owner:** gemini
- **Spec / Roadmap:** [ROADMAP.md](file:///E:/Git/SLNG/docs/ROADMAP.md)

## Overview & Goal
Windlight/EEP settings define a DensityMultiplier and DistanceMultiplier (or similar haze parameters) which dictate the distance fog (Haze / Density). Currently, this is missing in the actual Godot rendering environment; volumetric fog might be set to a static value, but the actual Windlight density is not correctly applied, meaning the light fog in the distance is missing. This feature maps the Windlight density to the Godot Environment.FogDensity or VolumetricFogDensity dynamically based on the current region's Windlight/EEP settings.

## Acceptance Criteria
- [x] Windlight density/haze parameters are extracted and applied to the Godot Environment fog.
- [x] Changing environment settings updates the visual distance fog in real-time.

## Technical Specs & Affected Files
- src/SLNG.Core/SkySettings.cs
-  pp/scripts/EnvironmentDriver.cs

## Sub-tasks / Progress
- [x] Map DensityMultiplier/DistanceMultiplier to Godot Fog Density
- [x] Verify in-world
