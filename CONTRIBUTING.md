# Contributing to SLNG (Puris Viewer)

Thank you for your interest in contributing to SLNG! We welcome code contributions, bug reports, feature suggestions, and documentation improvements.

---

## 🧭 Project Philosophy & Rules

SLNG is an open-source next-generation viewer for **Second Life** and **OpenSimulator**, built with **.NET 8** and **Godot 4**.

Before submitting any code, please read our canonical guidelines in **[`AGENTS.md`](AGENTS.md)**. All developers (human and AI) follow these core principles:

1. **TPV Policy Compliance:** Any code interacting with the Second Life grid **must** strictly respect Linden Lab's [Third-Party Viewer Policy](https://secondlife.com/corporate/third-party-viewers). We never circumvent content permissions or enable unauthorized content export.
2. **Layering & Architecture:**
   - `SLNG.Core`: Engine- and protocol-agnostic domain model. No references to `SLNG.Net` or `Godot`.
   - `SLNG.Net`: Protocol handling via LibreMetaverse. Never leak LibreMetaverse types into the renderer.
   - `SLNG.Assets`: Thread-safe asset codecs (JPEG 2000, mesh decode) and caches.
   - `app/`: The Godot 4 (.NET) client. All Godot scenes, shaders, and UI live here.
3. **Threading Safety:** Asset decoding and network I/O occur on background threads; GPU uploads and node mutations happen on the Godot main thread via `CallDeferred`. Never mutate world state directly from network callbacks.
4. **UI Localization:** All user-facing UI text must be localized using `SLNG.App.UI.L10n.Tr(...)` and defined in both `app/i18n/en-US.json` and `app/i18n/de-DE.json`.
5. **Floating Windows:** All floating, draggable UI windows **must** inherit from `SLNG.App.UI.SLNGWindow`.

---

## 🛠️ Development Setup

### Prerequisites
- **[.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)**
- **[Godot Engine 4.7-stable (.NET/Mono build)](https://godotengine.org/download)** on your `PATH` as `godot`
- A Vulkan-capable graphics card and drivers

### Building & Running

```bash
# 1. Build the engine-agnostic core libraries and test suites
dotnet build SLNG.sln

# 2. Build the Godot client (deliberately outside SLNG.sln)
dotnet build app/SLNG.App.csproj

# 3. Run all unit and integration tests
dotnet test

# 4. Check code formatting
dotnet format SLNG.sln --verify-no-changes

# 5. Launch the client
godot --path app
```

Smoke test without launching a window:
```bash
godot --headless --path app -- --selftest
```

---

## 🌿 Git & Pull Request Workflow

1. **Branch Naming:** Create feature branches using descriptive names, ideally with a Feature ID:
   - `feature/<ID>-<short-description>` (e.g. `feature/FEAT-UI-30-chat-history-search`)
   - `fix/<ID>-<short-description>` (e.g. `fix/BUG-RENDER-17-water-reflection-edge`)
2. **Commit Messages:** Follow [Conventional Commits](https://www.conventionalcommits.org/):
   - `feat(<area>): [<ID>] <description>`
   - `fix(<area>): [<ID>] <description>`
   - `docs: <description>`
   - `chore: <description>`
3. **Verification Checklist:**
   Before opening a PR, ensure:
   - Both `SLNG.sln` and `app/SLNG.App.csproj` build cleanly.
   - `dotnet test` passes.
   - `dotnet format SLNG.sln` produces no diffs.
   - If UI was changed, strings are localized in `en-US` and `de-DE`.
   - `AppVersion` in `app/scripts/Boot.cs` is incremented if appropriate.

---

## 🐛 Reporting Bugs & Suggesting Features

- Use our [Issue Templates](https://github.com/) to report bugs or propose enhancements.
- Include GPU model, driver versions, and relevant excerpts from `client-output.log`.
- For feature requests, note how the behavior compares to standard Second Life viewers.
