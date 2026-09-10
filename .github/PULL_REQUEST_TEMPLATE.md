## Description
Briefly describe the change and the rationale behind it. Mention the related issue or roadmap feature ID (e.g. `[FEAT-XXX-01]` or `[BUG-XXX-02]`).

## Type of Change
- [ ] Bug fix (non-breaking change which fixes an issue)
- [ ] New feature (non-breaking change which adds functionality)
- [ ] Refactoring / Performance improvement
- [ ] Documentation / Tooling

## Checklist
- [ ] My code adheres to the project conventions in [`AGENTS.md`](AGENTS.md).
- [ ] Layering boundaries are respected: `src/` does **not** reference `Godot` or leak LibreMetaverse types into the renderer.
- [ ] `dotnet build SLNG.sln` compiles cleanly.
- [ ] `dotnet build app/SLNG.App.csproj` compiles cleanly.
- [ ] `dotnet test` passes without errors.
- [ ] `dotnet format SLNG.sln` is clean.
- [ ] Any floating UI window inherits from `SLNGWindow`.
- [ ] New/modified UI strings are localized in both `app/i18n/en-US.json` and `app/i18n/de-DE.json`.
- [ ] `AppVersion` in `app/scripts/Boot.cs` is bumped if this change impacts user-visible behavior.
