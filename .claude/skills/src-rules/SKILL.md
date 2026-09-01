---
name: src-rules
description: Engine-agnostic rules for src/ (SLNG.Core, SLNG.Net, SLNG.Assets) — the layering contract, the LibreMetaverse boundary, network-thread safety, and the LMV decoding traps this project has already been burned by. Loads automatically when working under src/.
paths: src/**
---

# Rules for `src/`

`AGENTS.md` states the contract (**Layering & boundaries**, **Threading model**,
**Non-negotiables**) and stays authoritative. This file is the working detail behind it,
plus the traps that cost this project real debugging time.

## The one rule that is never negotiable here

**No `using Godot;` anywhere under `src/`.** `src/` is the engine-agnostic half of the
project; `app/` is the only place Godot exists. A single Godot type in `src/` makes the
domain unbuildable without the engine and unusable from a test.

Convert at the boundary to `System.Numerics` / neutral DTOs.

## The LibreMetaverse boundary

**No LibreMetaverse type crosses a public boundary of `SLNG.Net` or `SLNG.Assets`.**
`Primitive`, `UUID`, `FacetedMesh`, `AssetMesh` are internal implementation detail. The
renderer receives a neutral mesh/material description, never an LMV object.

Asset codecs (CoreJ2K / JPEG2000, mesh decode) live in `SLNG.Assets`, not `SLNG.Net`.

## Never mutate `World` from a network callback

LibreMetaverse raises its events on **background network threads**. The `Dictionary`-based
world state is not thread-safe and the Godot main thread reads it concurrently.

Buffer incoming events, apply them to the world on **one** thread, drained once per frame.
This applies to every LMV event handler, not just the obvious ones.

## LibreMetaverse traps (all confirmed the hard way)

- **`Agent.MultipleSims` defaults to `false`** in LMV 3.1.3 — that alone broke seeing across
  a sim border. With it on, no LMV avatar handler is `CurrentSim`-gated by default; code that
  assumed one region has to be re-checked. See `src/SLNG.Net/GridSession.cs`.
- **`TerseObjectUpdate` needs its own subscription.** Subscribing only to `ObjectUpdate`
  makes physics-driven and script-moved objects look frozen between incidental full resyncs.
- **LMV mis-parses 4-influence skin weights** (it misses the `0xFF` rule). Use SLNG's
  `src/SLNG.Assets/MeshSkinWeightDecoder.cs`, never `face.Weights`.
- **`GenerateFacetedMesh` never sets `Face.ID`** — it is always `0`. Use the face's position
  in the list as the SL face number.
- **Field-name collisions bite:** `MaxAge` vs `PartMaxAge`, `PartFlags` vs `PartDataFlags`.
  The obvious-looking field is the wrong one; verify against the LLSD key.
- **`Primitive.Light` latches.** OpenSim omits the block when a light is disabled rather than
  sending a zeroed one, so the old value survives. Detect absence in the raw `ObjectUpdate`
  bytes, don't trust the parsed struct.
- **Attachment caches lag.** `GetAttachmentsByItemId()` still lists detached items. For
  "worn right now", read the scene: `CurrentSim.ObjectsPrimitives` where `ParentID ==
  Self.LocalID`, keyed by the `AttachItemID` name-value.

## Conventions

- `async`/`await` for all I/O. No magic UUIDs or endpoints — constants in config.
- `PascalCase` types/methods, `_camelCase` private fields, one public type per file.
- Protocol and asset code is **test-first** where feasible; see `.claude/skills/tests-rules/`.

## Verifying a change here

`dotnet build SLNG.sln` + `dotnet test` covers `src/`. It does **not** compile `app/` —
run `/slng-verify` for the full check before committing.
