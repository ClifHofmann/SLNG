using System;
using System.Collections.Generic;
using Godot;
using SLNG.Core;

namespace SLNG.App;

// FEAT-PERF-27: the avatar under the camera finishes loading first.
//
// Measured on a crowded arrival (48 avatars, 2026-10-30): the main-thread work queue reached 40,000 items, one lane,
// first in first out, and the focus avatar's rigs, attachments and face materials waited their turn behind every
// object of the region. One notion of "focus subject" (SLNG.Core.PrioritySubject, chosen by FocusTracker) is now
// read by the work queue's lanes, the rig claim, the avatar cap and the texture priority. It is re-ranked when the
// focus changes (a few times a minute), never per frame: the tracker runs on the 4 Hz cull tick over the shown avatars.
public partial class AvatarRenderer
{
    private readonly FocusTracker _focusTracker = new();
    private readonly FocusCompletionTimer _focusTimer = new();
    private readonly List<FocusTracker.Candidate> _focusCandidates = new();

    /// <summary>The camera's explicit focus point (Alt+Click / roster focus on an avatar), or null. Set by
    /// <c>AvatarController</c>; a roster double-click or an Alt+Click focuses from any distance, which proximity alone cannot see.</summary>
    public Func<Godot.Vector3?>? FocusPointProvider { get; set; }

    /// <summary>Queues main-thread work on behalf of an avatar, so the work queue can serve the focus avatar first.</summary>
    private static void EnqueueFor(Guid agentId, MainThreadWorkQueue.Lane lane, Action work,
        string? coalesceKey = null, string label = "?") =>
        MainThreadWorkQueue.Enqueue(lane, work, coalesceKey, label, agentId);

    /// <summary>Fetch/decode priority for this avatar's textures: the focus avatar's go before the scenery's.</summary>
    private static float FocusTexturePriorityFor(AvatarVisual? visual) =>
        visual != null && PrioritySubject.IsFocus(visual.AgentId) ? PrioritySubject.FocusTexturePriority : 0f;

    /// <summary>One look at who the camera is on. Cheap: a loop over the shown avatars, on the cull tick.</summary>
    private void UpdateFocusAvatar(Camera3D? camera, Godot.Vector3 camPos)
    {
        double now = NowSeconds;
        Godot.Vector3? focusPoint = FocusPointProvider?.Invoke();
        Godot.Vector3 forward = camera != null ? -camera.GlobalTransform.Basis.Z : Godot.Vector3.Forward;

        _focusCandidates.Clear();
        foreach (var visual in _visuals.Values)
        {
            if (visual.IsSelf || visual.IsControlAvatar || !visual.Shown || visual.AgentId == Guid.Empty) continue;
            var toAvatar = visual.GodotPos - camPos;
            float dist = toAvatar.Length();
            float cos = dist > 0.001f ? toAvatar.Dot(forward) / dist : 1f;
            float toFocusPoint = focusPoint.HasValue ? visual.GodotPos.DistanceTo(focusPoint.Value) : float.NaN;
            _focusCandidates.Add(new FocusTracker.Candidate(visual.AgentId, dist, toFocusPoint, cos));
        }

        Guid subject = _focusTracker.Update(now, _focusCandidates);
        if (_focusTracker.Changed)
        {
            PrioritySubject.Set(subject);
            var lost = _focusTimer.Track(subject, now);
            if (lost is { } l)
                LogFocus($"[FocusAvatar] {FocusId(l.Subject)} lost the focus after {l.Seconds:0.0} s, not complete");
            if (subject != Guid.Empty)
                LogFocus($"[FocusAvatar] {FocusId(subject)} is the focus now ({MainThreadWorkQueue.PendingFor(subject)} queued items, " +
                         $"{_pendingRigs.Count} pending rigs in all)");
            // It becomes a full avatar at once, whatever the cap is; the dwell time does not apply.
            TriggerAvatarLimitEvaluation(ignoreDwell: true);
        }

        if (subject != Guid.Empty && _focusTimer.Update(now, IsFocusAvatarComplete(subject)) is { Completed: true } done)
            LogFocus($"[FocusAvatar] {FocusId(done.Subject)} complete in {done.Seconds:0.0} s " +
                     $"(work queue served {MainThreadWorkQueue.BumpedTotal} items ahead of older ones so far)");
    }

    /// <summary>"Complete" for the diagnostics: a full avatar with nothing of it left in the work queue and no rig waiting
    /// or in preparation. Textures still on the wire are not seen here; <c>[TexWait]</c> shows those.</summary>
    private bool IsFocusAvatarComplete(Guid agentId)
    {
        bool found = false;
        foreach (var visual in _visuals.Values)
        {
            if (visual.AgentId != agentId) continue;
            found = true;
            if (visual.IsReduced) return false;
            break;
        }
        if (!found) return false;
        if (MainThreadWorkQueue.PendingFor(agentId) > 0) return false;
        foreach (var req in _pendingRigs.Values)
            if (req.Visual.AgentId == agentId) return false;
        return true;
    }

    private static string FocusId(Guid id) => id.ToString("N")[..8];

    private static void LogFocus(string line)
    {
        if (SLNG.Core.Diag.Verbose) SLNG.Core.Diag.Write(line);
    }
}
