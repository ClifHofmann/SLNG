# Handover to Claude: Texture Loading Bug

## Status
Textures are either taking over 2 minutes to load, or they fail entirely and spam the Godot console with `[WARNING]: Codestream truncated in tile 0` from Magick.NET.

## What Gemini Tried
1. **Parallelization**: In `app/scripts/ObjectRenderer.cs` (`ApplyFaceMaterialsAsync`), I changed the sequential `await BuildFaceMaterialAsync` loop into a concurrent `Task.WhenAll` to speed up multi-face object texture fetching. This might be overwhelming the UDP queue.
2. **Threading**: I moved `Image.CreateFromData` and `image.GenerateMipmaps()` in `AvatarRenderer`, `ObjectRenderer`, and `TerrainRenderer` to run on background threads instead of blocking the Godot main thread inside `CallDeferred`. This prevents the engine from freezing when textures arrive.
3. **Network layer**: I tried replacing the reflection hack in `GridSession.cs` (`FetchTextureDataAsync`) with `_client.Assets.RequestImageAsync` to use HTTP fetching. This still produced truncated textures. I reverted it back to `TexturePipeline` (UDP) via reflection, as it handles packet retries, but textures are still failing.
4. **Warning Suppression**: I added `image.Warning += ...` to MagickImage in `AssetService.cs`, but OpenJPEG writes warnings directly to standard error, bypassing .NET, so the console is still spammed.

## Claude's Task
1. Figure out why LibreMetaverse `TexturePipeline` or HTTP fetching is dropping/truncating textures against OpenSim.
2. Check if my `Task.WhenAll` in `ApplyFaceMaterialsAsync` is congesting the UDP pipeline. If so, maybe implement a concurrency limiter (`SemaphoreSlim`) in `AssetService.cs` when requesting textures.
3. Ensure textures actually load and apply in a reasonable timeframe without spamming the console.
