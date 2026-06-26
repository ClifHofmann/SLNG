# ADR 0001 — Engine and stack

- Status: accepted
- Date: 2026-06-26

## Context

We are building a next-gen Second Life / OpenSim viewer with a modern graphics
engine. Two large problems dominate: (a) the **protocol** — login, the legacy UDP
message system, HTTP CAPS, and the asset formats (JPEG2000, LLMesh, glTF materials,
avatar bake); and (b) the **renderer** — the thing we actually want to modernize.

Rewriting the protocol from scratch is the classic project-killer; it is ~80% of the
effort and adds no user-visible value. The graphics is where the value is.

## Options considered

1. **Fork the Linden viewer and modernize** (C++/OpenGL → Vulkan). Lowest protocol
   risk, but we inherit a 20-year-old architecture that fights every change.
2. **Greenfield in Rust + wgpu.** Maximum control and no engine licensing, but we'd
   build both a protocol stack and a renderer from zero — slowest path to a demo.
3. **Hybrid: reuse a proven protocol library + a modern host engine.** Reuse
   LibreMetaverse (C#) for the protocol; use a modern engine for rendering.
   - 3a. Unity — fast results, but proprietary runtime, royalty/licensing
     uncertainty, and C# GC stalls in the hot path.
   - 3b. **Godot 4 (.NET)** — open source, Vulkan, native PBR, C# support so
     LibreMetaverse links directly with no FFI.

Crystal Frost (Unity + LibreMetaverse) proved option 3 works in practice — and also
proved its pitfalls (performance, RAM, avatar complexity). It is now effectively
dormant; we treat it as a reference, not a dependency.

## Decision

Adopt **option 3b: Godot 4 (.NET) + LibreMetaverse**.

- LibreMetaverse handles the protocol we will not rewrite.
- Godot 4 gives us a modern Vulkan renderer (PBR, shadows, GI, post-fx) for free,
  so we never write a renderer just to get a first shot on screen.
- C# end-to-end means the protocol library and the engine share one language and
  runtime — no FFI glue.
- Open source and royalty-free, unlike Unity.

We keep `src/` engine-agnostic so the renderer is replaceable; Godot is an
implementation detail behind the world model.

## Consequences

- **Positive:** fastest credible path to a demoable, modern-looking client; reuses
  existing LibreMetaverse experience; no licensing risk; testable headless core.
- **Negative / risks:** C# GC must be managed in hot paths (pool allocations, keep
  decode off the main thread); Godot's high-end rendering features need tuning for
  unbounded user content; we are bound by LibreMetaverse's protocol coverage and by
  the Linden Lab TPV Policy for any SL-grid build.
- **Revisit if:** Godot's renderer becomes a hard ceiling for the visual target, or
  C# GC proves unmanageable at scale — at which point a Rust + wgpu core (option 2)
  is the fallback, eased by the engine-agnostic `src/` boundary.
