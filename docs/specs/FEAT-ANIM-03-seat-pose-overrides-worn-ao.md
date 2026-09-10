# [FEAT-ANIM-03] Furniture sit pose takes precedence over a worn AO HUD

- **Feature ID:** `FEAT-ANIM-03`
- **Track:** `render` (+ `net` boundary, `core` state)
- **Status:** `⏸️ Pending`
- **Owner:** `claude`
- **Spec / Roadmap:** [ROADMAP.md](file:///E:/Git/SLNG/docs/ROADMAP.md)

## Overview & Goal

In most viewers, sitting on furniture while wearing an AO HUD lets the AO's sit/stand
animation fight — and often override — the pose the furniture plays. The usual
workaround is a patch script added to the AO HUD that disables it while seated.

SLNG owns its own local per-bone animation blender (`AvatarAnimationPlayer`) and already
does source-aware substitution for the self avatar (FEAT-ANIM-01 locomotion). So it can
resolve this **at render time, per viewer**, with no HUD script and no protocol
interference: when seated on an object that plays a pose, that seat-sourced animation
set wins the local blend; animations sourced from the avatar's own attachments (the AO)
are excluded while the seat pose is active.

This is the same class of local decision as Firestorm's client-side AO options — not a
circumvention of anything, nothing suppressed on the wire.

## Why the data is available

The `AvatarAnimation` packet carries `AnimationSourceList` — the **source object UUID**
for each playing animation. LibreMetaverse surfaces it via
`AvatarManager.AvatarAnimation` → `AvatarAnimationEventArgs.Animations` (a
`List<Animation>`, each with `AnimationSourceObjectID`), for **every** avatar including
self.

SLNG already subscribes to that event (`GridSession._client.Avatars.AvatarAnimation +=
OnAvatarAnimation`, `GridSession.cs:464`) but `OnAvatarAnimation` (`GridSession.cs:4521`)
forwards only the `AnimationID` and drops the source. The DTO
`AvatarAnimationEvent(Guid AgentId, List<Guid> AnimationIds)` has no source field.

(Note: `AgentManager.AnimationsChanged` — the *self* event — genuinely discards the
source list; see the `// FIXME` in `AgentManager.PacketHandlers.cs:540`. That path is a
dead end, but `AvatarManager.AvatarAnimation` is not, and it fires for the self agent
too.)

## The rule

For an avatar with `SittingOnLocalId != 0` whose seat object is the **source** of at
least one currently-playing animation:

1. The seat-sourced animation set is authoritative for the local blend.
2. Animations whose source object is one of **that avatar's own attachments** are
   excluded from the local render blend while (1) holds.
3. Agent-sourced (source == `Guid.Empty`) animations — built-in `SIT`, an
   `llSetAnimationOverride("Sitting", …)` result — are outranked by the seat-sourced
   set but otherwise left alone.

Fallbacks that must keep working:
- **Ground sit** (`llSitOnGround`, no seat object) → no seat-sourced anim → rule does
  not fire → AO ground-sit plays normally.
- **Poseballs / couples / cuddle systems** → objects you sit on that source the anim →
  they win (desired).
- **Standing / walking** → not seated → rule does not fire; FEAT-ANIM-01 unchanged.

## Acceptance Criteria

- [ ] Wearing a worn AO HUD, sitting on furniture that plays a pose: SLNG renders the
      **furniture pose**, not the AO's sit animation, with no HUD patch script.
- [ ] Removing the AO HUD changes nothing visible while seated on that furniture.
- [ ] Ground sit with an AO that has a ground-sit override: the AO ground-sit still
      plays.
- [ ] Poseball / two-person animation: the poseball pose plays (seat-sourced), AO
      suppressed.
- [ ] Standing up: the AO resumes immediately, no stuck seat pose, no missing-frame
      T-pose.
- [ ] A remote avatar sitting on furniture while wearing an AO renders in the furniture
      pose for the observer too (same source check, remote path).
- [ ] Preference toggle present (default **on**), label ~ "Möbel-Posen haben beim
      Sitzen Vorrang vor dem AO" / "Furniture poses override the AO when seated".
- [ ] Unit test: given a synthetic animation set with seat / attachment / agent
      sources + a seated flag, the resolver excludes exactly the attachment-sourced
      entries and only while a seat-sourced entry is present.
- [ ] No regression: FEAT-ANIM-01 locomotion prediction, BUG-ANIM-01 pelvis track,
      BUG-ANIM-02 tie-break.

## Technical Specs & Affected Files

### `SLNG.Net` — carry the source across the boundary (engine-neutral)
- `src/SLNG.Core/GridEvents.cs` — widen `AvatarAnimationEvent`:
  `record AvatarAnimationEvent(Guid AgentId, IReadOnlyList<AnimationSignal> Animations)`
  with `readonly record struct AnimationSignal(Guid AnimId, Guid SourceObjectId)`.
  Keep it `System.Guid` only — **no LibreMetaverse type crosses the boundary**
  (AGENTS.md).
- `src/SLNG.Net/GridSession.cs` `OnAvatarAnimation` — populate `SourceObjectId` from
  `anim.AnimationSourceObjectID.Guid`.
- Update `IWorldEventSource` / any test doubles for the new shape.

### `SLNG.Core` — hold the source map on the avatar
- `src/SLNG.Core/Components/AvatarComponent.cs` — add
  `IReadOnlyDictionary<Guid, Guid> AnimationSources` (animId → sourceObjectId)
  alongside `ActiveAnimations`.
- `src/SLNG.Core/WorldSimulation.cs` `ApplyAvatarAnimation` — fill it from the event.
- The seat object id is already derivable: the avatar's `SittingOnLocalId` → the
  `PrimitiveComponent` for that local id → its object `Guid`. Expose a resolved
  `SittingOnObjectId` (Guid) on `AvatarComponent` if not already present, so `app`
  doesn't need a localId→Guid lookup.

### `app` — the render-time resolver
- `app/scripts/AvatarRenderer.cs` `ApplyActiveAnimations` — before building `desired`:
  - if `avatar.SittingOnObjectId` is set and any signal has
    `SourceObjectId == avatar.SittingOnObjectId` →
    build the worn-attachment id set (scene scan: `ObjectsPrimitives` where
    `ParentID == avatar.LocalId`, i.e. attachments) and drop every signal whose
    `SourceObjectId` is in that set.
  - honour the preference toggle; when off, current behaviour.
- `app/scripts/AvatarAnimationPlayer.cs` — no change needed if the filtering happens in
  `ApplyActiveAnimations`; the player just receives a smaller set. (If a
  bone-level exclusion is chosen instead of set-level, do it here.)
- Preference: new key in the animation/AO prefs section (same `ConfigFile` pattern as
  `CameraSettings` / `DofSettings`), surfaced in the Preferences window. Locale strings
  both languages (selftest locale parity).

### Timing / edge handling
- **Grace window:** seat pose and AO sit anim can arrive in separate `AvatarAnimation`
  packets. On `SittingOnObjectId` becoming set, wait a short debounce (≈0.5–1 s) before
  applying attachment anims, so the seat pose that lands a beat later doesn't flash the
  AO pose first.
- **Non-AO worn animators** (worn pet, bento collar idle): a blanket drop catches them.
  v1 = blanket drop of attachment-sourced anims while a seat pose is active (documented
  limitation). Follow-up refinement options, in order of effort:
  (a) only drop attachment-sourced anims that key the pelvis / spine / upper-leg chain;
  (b) per-attachment allowlist in prefs;
  (c) only drop those with priority ≥ the seat anim's priority.
- **`viewer-parity` first:** confirm against `scratch/slviewer` how the reference viewer
  treats object-sourced vs attachment-sourced animations while seated (`LLVOAvatar`
  animation source handling, the AO's own sit logic) and set the default toggle state
  accordingly.

## Sub-tasks / Progress

- [ ] `viewer-parity`: object-sourced vs attachment-sourced animation handling while
      seated in the reference viewer; sensible default for the toggle.
- [ ] `SLNG.Net` boundary: widen `AvatarAnimationEvent` with `AnimationSignal`
      (source), populate from LMV.
- [ ] `SLNG.Core`: `AnimationSources` + resolved `SittingOnObjectId` on
      `AvatarComponent`; fill in `ApplyAvatarAnimation`.
- [ ] `app`: seat-vs-attachment resolver in `ApplyActiveAnimations` + grace window.
- [ ] Preference toggle + locale strings + Preferences UI row.
- [ ] Unit tests (resolver) + FEAT-ANIM-01/BUG-ANIM-01/BUG-ANIM-02 regression.
- [ ] In-world: furniture pose vs AO, ground sit, poseball, stand-up, remote avatar.
