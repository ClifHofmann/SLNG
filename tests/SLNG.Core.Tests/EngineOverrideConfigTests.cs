using System;
using System.IO;
using Xunit;

namespace SLNG.Core.Tests;

public sealed class EngineOverrideConfigTests
{
    [Fact]
    public void RemoveKey_EmptyOrWhitespace_ReturnsNoKeys()
    {
        var (hasOtherKeys, newContent) = EngineOverrideConfig.RemoveKey("", "rendering", "driver/threads/thread_model");
        Assert.False(hasOtherKeys);
        Assert.Equal(string.Empty, newContent);

        var (hasOtherKeysWs, newContentWs) = EngineOverrideConfig.RemoveKey("   \n\n  ", "rendering", "driver/threads/thread_model");
        Assert.False(hasOtherKeysWs);
        Assert.Equal(string.Empty, newContentWs);
    }

    [Fact]
    public void RemoveKey_OnlyTargetKey_ReturnsNoKeysAndEmptyContent()
    {
        string ini = @"[rendering]

driver/threads/thread_model=2";

        var (hasOtherKeys, newContent) = EngineOverrideConfig.RemoveKey(ini, "rendering", "driver/threads/thread_model");
        Assert.False(hasOtherKeys);
        Assert.Equal(string.Empty, newContent);
    }

    [Fact]
    public void RemoveKey_WithSpacesAroundEquals_MatchesAndRemoves()
    {
        string ini = @"[rendering]
driver/threads/thread_model = 2";

        var (hasOtherKeys, newContent) = EngineOverrideConfig.RemoveKey(ini, "rendering", "driver/threads/thread_model");
        Assert.False(hasOtherKeys);
        Assert.Equal(string.Empty, newContent);
    }

    [Fact]
    public void RemoveKey_OtherKeyInSameSection_PreservesSectionAndOtherKey()
    {
        string ini = @"[rendering]
driver/threads/thread_model=2
vulkan/foo=1";

        var (hasOtherKeys, newContent) = EngineOverrideConfig.RemoveKey(ini, "rendering", "driver/threads/thread_model");
        Assert.True(hasOtherKeys);
        Assert.Contains("[rendering]", newContent);
        Assert.Contains("vulkan/foo=1", newContent);
        Assert.DoesNotContain("driver/threads/thread_model", newContent);
    }

    [Fact]
    public void RemoveKey_OtherSection_OmitsEmptyRenderingSectionAndPreservesOtherSection()
    {
        string ini = @"[rendering]
driver/threads/thread_model=2

[display]
window/size/viewport_width=1920";

        var (hasOtherKeys, newContent) = EngineOverrideConfig.RemoveKey(ini, "rendering", "driver/threads/thread_model");
        Assert.True(hasOtherKeys);
        Assert.DoesNotContain("[rendering]", newContent);
        Assert.DoesNotContain("driver/threads/thread_model", newContent);
        Assert.Contains("[display]", newContent);
        Assert.Contains("window/size/viewport_width=1920", newContent);
    }

    [Fact]
    public void RemoveKey_TargetKeyNotFound_PreservesAll()
    {
        string ini = @"[display]
window/size/viewport_width=1920";

        var (hasOtherKeys, newContent) = EngineOverrideConfig.RemoveKey(ini, "rendering", "driver/threads/thread_model");
        Assert.True(hasOtherKeys);
        Assert.Contains("[display]", newContent);
        Assert.Contains("window/size/viewport_width=1920", newContent);
    }

    [Fact]
    public void RevertThreadModelPreference_DeletesFileWhenNoOtherKeys()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"slng_test_config_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        string tempFile = Path.Combine(tempDir, "engine_overrides.cfg");

        try
        {
            File.WriteAllText(tempFile, "[rendering]\ndriver/threads/thread_model=2\n");
            Assert.True(File.Exists(tempFile));

            bool reverted = EngineOverrideConfig.RevertThreadModelPreference(tempFile);
            Assert.True(reverted);
            Assert.False(File.Exists(tempFile));
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public void RevertThreadModelPreference_RewritesFileWhenOtherKeysPresent()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"slng_test_config_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        string tempFile = Path.Combine(tempDir, "engine_overrides.cfg");

        try
        {
            File.WriteAllText(tempFile, "[rendering]\ndriver/threads/thread_model=2\n\n[display]\nwidth=1920\n");
            Assert.True(File.Exists(tempFile));

            bool reverted = EngineOverrideConfig.RevertThreadModelPreference(tempFile);
            Assert.True(reverted);
            Assert.True(File.Exists(tempFile));

            string content = File.ReadAllText(tempFile);
            Assert.DoesNotContain("driver/threads/thread_model", content);
            Assert.Contains("[display]", content);
            Assert.Contains("width=1920", content);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public void RenderThreadFallback_WriteAndConsumeMarker()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"slng_test_marker_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        string tempMarker = Path.Combine(tempDir, "render_thread_fallback.json");

        try
        {
            bool written = RenderThreadFallback.WriteMarker(tempMarker, 10.23);
            Assert.True(written);
            Assert.True(File.Exists(tempMarker));

            bool consumed = RenderThreadFallback.TryConsumeMarker(tempMarker, out var marker);
            Assert.True(consumed);
            Assert.NotNull(marker);
            Assert.Equal(10.2, marker!.StalledSeconds);
            Assert.False(string.IsNullOrEmpty(marker.Timestamp));
            Assert.False(File.Exists(tempMarker));
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }
}

