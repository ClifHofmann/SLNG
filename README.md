# SLNG — Second Life Next-Gen Viewer

![SLNG / Puris Viewer](https://img.shields.io/badge/Status-Alpha%20(MVP%202)-brightgreen)
![Godot 4](https://img.shields.io/badge/Godot-4.7_stable-blue?logo=godotengine)
![.NET 8](https://img.shields.io/badge/.NET-8.0-purple?logo=dotnet)
![LibreMetaverse](https://img.shields.io/badge/Protocol-LibreMetaverse-orange)

A next-generation, high-performance client for **Second Life** and **OpenSimulator**. 

SLNG (branded in-app as *Puris Viewer*) keeps full grid compatibility by reusing the battle-tested [LibreMetaverse](https://github.com/cinderblocks/libremetaverse) protocol library. The rendering engine, however, is completely replaced by **Godot 4 (.NET / Vulkan)**. This brings modern PBR lighting, cascaded shadows, seamless post-processing, and a beautiful, scalable Glassmorphism UI to the metaverse—without the tech-debt of a ~20-year-old OpenGL codebase.

> **Status: Alpha.** MVP 1 (Playable Client) and MVP 1.5 (CI/CD, Installers, Configurations) are complete. We are currently actively working on **MVP 2 (Navigation & Social Core)**. The client can log in, render terrain/prims/mesh, display Bento avatars with BoM (Bakes-on-Mesh), and supports group chats, friend lists, minimap, and world map teleports.

## 🚀 Key Features

- **Modern Rendering:** PBR materials, CSM shadows, and post-fx powered by Vulkan & Godot 4.
- **Fast Texture Pipeline:** Multi-threaded JPEG2000 decoding (CoreJ2K) and VRAM budget management.
- **Sleek UI:** A responsive, scalable Glassmorphism UI with multi-tabbed chat, profiles, and persistence.
- **Bento & BoM:** Full support for Bento skeletons, mesh attachments, and Bakes-on-Mesh.
- **CI/CD Ready:** Automated GitHub Actions build headless Windows installers (`.exe`) via InnoSetup.

---

## 🛠️ Tech Stack

| Concern | Choice |
|---|---|
| **Engine / Renderer** | Godot 4.7-stable, .NET (Vulkan) |
| **Language** | C# (.NET 8) |
| **Protocol** | LibreMetaverse (LLSD, UDP, HTTP CAPS) |
| **Physics** | Godot Jolt |
| **Targets** | Windows first (Linux/macOS buildable); SL Grid + OpenSim |

## 📁 Repository Layout

```
app/    Godot 4 .NET project (scenes, shaders, UI elements)
src/    Engine-agnostic C# domain libraries (SLNG.Core, SLNG.Net, SLNG.Assets)
tests/  Unit & integration tests (xUnit against OpenSim)
docs/   Architecture, specifications, roadmap, and ADRs
tools/  Build scripts, linters, and CI helpers
```

## 🎮 Build & Run

Ensure you have the **.NET 8 SDK** and **Godot 4 (.NET/mono)** installed.

```bash
# 1. Build the engine-agnostic core libraries and network stack
dotnet build SLNG.sln

# 2. Build the Godot client (Note: the app/ project is deliberately outside the .sln)
dotnet build app/SLNG.App.csproj

# 3. Run the Godot client
godot --path app
```
*Note: A `godot --headless --path app -- --selftest` command is available to smoke-test shaders, locales, and configurations without a grid connection.*

---

## 🤖 Built with AI, by two assistants in parallel

This project is developed primarily by AI agents. It is designed so that **Claude Code** and **Gemini CLI** can work the repository **at the same time** on separate git worktrees without colliding. 

If you are an AI reading this, you **MUST** read the following documents before touching any code:
- [AGENTS.md](AGENTS.md) — The single source of truth (architecture boundaries, threading rules, stack).
- [docs/AI_WORKFLOW.md](docs/AI_WORKFLOW.md) — How to handle the parallel-agent workflow & git worktrees.
- [docs/ROADMAP.md](docs/ROADMAP.md) — AI-sized tasks, workstreams, and milestone status.

---

## ⚖️ Legal & Licensing

Any build that connects to the Second Life grid must comply with the Linden Lab **Third-Party Viewer (TPV) Policy**. The permission and asset-protection model is a hard design constraint, not an afterthought — see [AGENTS.md](AGENTS.md#non-negotiables).

We ship a small amount of Second Life viewer artwork (the terrain blend ramp and the Windlight cloud texture) under **Creative Commons Attribution-Share Alike 3.0**. That licence requires the notice to travel with the program, so the canonical file lives at [`app/THIRD-PARTY-NOTICES.md`](app/THIRD-PARTY-NOTICES.md) (inside the Godot project, so exports include it) and the client shows it under **Preferences → Licences**.

**If you add a third-party asset, add it to that notice file**, including what you changed about it. Identifying changes is a condition of the licence, not a courtesy.
