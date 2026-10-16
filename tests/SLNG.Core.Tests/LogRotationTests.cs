using System;
using System.IO;
using System.Linq;
using Xunit;

namespace SLNG.Core.Tests;

/// <summary>The perf sidecar's start-of-session rotation: the previous session's file is kept
/// under a timestamped name (Godot's own <c>godot&lt;stamp&gt;.log</c> scheme) instead of being
/// truncated, and only the newest few are kept.</summary>
public sealed class LogRotationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"slng-logrotation-{Guid.NewGuid():N}");
    private static readonly DateTime Now = new(2026, 10, 16, 9, 34, 5);

    public LogRotationTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private string Current => Path.Combine(_dir, "slng-perf.log");

    [Fact]
    public void ThePreviousSession_IsKeptUnderATimestampedName()
    {
        File.WriteAllText(Current, "previous session");

        string path = LogRotation.Rotate(_dir, "slng-perf", ".log", Now, maxFiles: 10);

        Assert.Equal(Current, path);
        Assert.False(File.Exists(Current));
        string kept = Path.Combine(_dir, "slng-perf2026-10-16T09.34.05.log");
        Assert.Equal("previous session", File.ReadAllText(kept));
    }

    [Fact]
    public void NoPreviousFile_NothingToRotate()
    {
        string path = LogRotation.Rotate(_dir, "slng-perf", ".log", Now, maxFiles: 10);

        Assert.Equal(Current, path);
        Assert.Empty(Directory.GetFiles(_dir));
    }

    [Fact]
    public void AnEmptyPreviousFile_IsNotKept()
    {
        File.WriteAllText(Current, "");

        LogRotation.Rotate(_dir, "slng-perf", ".log", Now, maxFiles: 10);

        Assert.Empty(Directory.GetFiles(_dir));
    }

    [Fact]
    public void TwoRotationsInOneSecond_DoNotOverwriteEachOther()
    {
        File.WriteAllText(Current, "first");
        LogRotation.Rotate(_dir, "slng-perf", ".log", Now, maxFiles: 10);
        File.WriteAllText(Current, "second");
        LogRotation.Rotate(_dir, "slng-perf", ".log", Now, maxFiles: 10);

        var kept = Directory.GetFiles(_dir).Select(File.ReadAllText).OrderBy(s => s).ToArray();
        Assert.Equal(new[] { "first", "second" }, kept);
    }

    [Fact]
    public void OnlyTheNewest_AreKept_CountingTheCurrentFile()
    {
        for (int i = 0; i < 12; i++)
        {
            File.WriteAllText(Current, $"session {i}");
            LogRotation.Rotate(_dir, "slng-perf", ".log", Now.AddMinutes(i), maxFiles: 4);
        }

        // maxFiles counts the file about to be written, as Godot's max_log_files does.
        var kept = Directory.GetFiles(_dir).Select(File.ReadAllText).OrderBy(s => s).ToArray();
        Assert.Equal(new[] { "session 10", "session 11", "session 9" }, kept);
    }

    [Fact]
    public void OtherFiles_InTheFolder_AreLeftAlone()
    {
        string godotLog = Path.Combine(_dir, "godot2026-10-15T15.01.58.log");
        string other = Path.Combine(_dir, "slng-perf-notes.txt");
        File.WriteAllText(godotLog, "engine");
        File.WriteAllText(other, "notes");
        for (int i = 0; i < 5; i++)
        {
            File.WriteAllText(Current, $"session {i}");
            LogRotation.Rotate(_dir, "slng-perf", ".log", Now.AddMinutes(i), maxFiles: 2);
        }

        Assert.True(File.Exists(godotLog));
        Assert.True(File.Exists(other));
    }

    [Fact]
    public void AFileStillOpenForWriting_CanBeRotated()
    {
        // The writer opens with FileShare.Delete, so a second client can move a running client's
        // file aside; the running client keeps writing into the moved file.
        using (var open = new FileStream(Current, FileMode.Create, FileAccess.Write,
                                         FileShare.ReadWrite | FileShare.Delete))
        {
            open.WriteByte(42);
            open.Flush();
            string path = LogRotation.Rotate(_dir, "slng-perf", ".log", Now, maxFiles: 10);
            Assert.Equal(Current, path);
            open.WriteByte(43);
        }

        string kept = Path.Combine(_dir, "slng-perf2026-10-16T09.34.05.log");
        Assert.Equal(new byte[] { 42, 43 }, File.ReadAllBytes(kept));
    }
}
