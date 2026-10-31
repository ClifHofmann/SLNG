# [FEAT-PERF-27] The avatar under the camera loads first

- **Feature ID:** `FEAT-PERF-27`
- **Track:** `render` / `perf`
- **Status:** `🧪 Review`
- **Owner:** `claude`

## Why

Arriving in a crowded region (48 avatars, measured 2026-10-30) the main-thread work queue reached 40,000 items in a single
first-in-first-out `Visual` lane; the face materials, rigs and attachments of the avatar the person zooms on waited behind every
object of the region (`visual.create` 27,000 and `visual.reload` 12,000 items, 65 s to drain at 20 fps; `avatar.face_material` alone
was about half of the queue time). Rig claiming was already nearest-first, textures priority-ordered, but neither knew the focus.

## What it does

One notion of the **focus subject** (`SLNG.Core.PrioritySubject`), chosen by `SLNG.Core.FocusTracker` on the 4 Hz cull tick:

1. An explicit focus (roster double-click, Alt+Click on a person): the camera's focus point lies within 2 m of the avatar. Works from
   any distance. `AvatarController` hands the point over (`AvatarRenderer.FocusPointProvider`, only while `CameraFocusKind.Avatar`).
2. Proximity (wheel zoom, walking up): within 6 m of the camera and about 37 degrees of the view axis.

The tracker keeps the current subject against a marginally better one (0.75 score margin) and for 1.5 s after it stops qualifying.

Consumers, all reading `PrioritySubject.Focus` only when they take an item, so a change re-ranks everything already queued:

- **`MainThreadWorkQueue`** lanes are `SLNG.Core.BumpQueue`: first in first out, except that the focus avatar's items (tagged with the
  wearer's agent id at the avatar enqueue sites: rig commit, attachment commit, face materials, bake apply, outfit-merge jobs) are served
  first, at most 3 in a row, then one ordinary item, so the rest keeps a quarter of the throughput.
- **Rig claim** (`TryClaimNextRig`): focus avatar right after the local avatar, before the nearest.
- **Avatar cap**: the focus avatar is exempt from `RenderMaxNonImpostors` (full at once, takes no slot), and a focus change triggers an
  evaluation without the dwell time.
- **Textures**: fetches and decodes for the focus avatar's faces carry `PrioritySubject.FocusTexturePriority` (below the local
  avatar's own, above any scenery). Limit: a priority is fixed when the request is made, so a texture already queued stays where it was.

No per-frame sorting: the tracker loops over the shown avatars four times a second; the queues are heaps (O(log n)).

## Diagnostics (`--diag`)

`[FocusAvatar] <id8> is the focus now (N queued items, M pending rigs in all)`, then
`[FocusAvatar] <id8> complete in X s (work queue served K items ahead of older ones so far)` once the avatar is a full avatar with
nothing of it left in the work queue and no rig waiting for one second, or
`[FocusAvatar] <id8> lost the focus after X s, not complete`. Textures still on the wire are not part of "complete"; `[TexWait]` shows those.

## Verify in-world

Arrive in a crowd, then double-click an avatar in the radar (or Alt+Click one, or zoom onto one): read `complete in X s` in `godot.log`
and compare with how long the same avatar takes when it is not the focus (the `[AvatarCost]` skinnedMeshes plateau).

## Tests

`BumpQueueTests`, `FocusTrackerTests`, `PrioritySubjectTests` (incl. `FocusCompletionTimer`) in `SLNG.Core.Tests`; the self test
"work queue: the focus avatar's items run first" runs the real `MainThreadWorkQueue`.
