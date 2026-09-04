---
name: user-docs
description: Creates and maintains end-user documentation for the SLNG viewer, keeping it synced with the AppVersion and available as Markdown.
---

# User Documentation Manager (user-docs)

This skill is used to generate, update, and maintain end-user documentation for the SLNG viewer. Both Claude Code and Gemini can use this skill to ensure the manual stays up to date.

## Target Audience
- **End-users**: Non-technical users connecting to Second Life or OpenSimulator.
- **Tone**: Friendly, clear, and practical. Avoid developer jargon, architectural details (e.g., Godot, SLNG.Core), and API discussions.

## Guidelines & Rules

1. **Language**: All documentation must be written in **English**.
2. **Format**: Use **Markdown (.md)** files. This makes the documentation easily exportable, readable on GitHub/GitLab, and convertible to HTML/PDF later.
3. **Location**: All end-user documentation should be stored in the `docs/user-manual/` directory. Keep it organized logically (e.g., `docs/user-manual/01-getting-started.md`).
4. **Versioning & Sync**: 
   - Always check the current `AppVersion` (e.g., in `app/scripts/Boot.cs`). 
   - If the version has changed, document any new features, fixes, or UI changes introduced. 
   - Explicitly state which version the documentation applies to (e.g., "Updated for v0.1.55-alpha").
5. **Terminology**: Cross-reference UI strings in `app/i18n/en-US.json` to ensure the manual uses the exact terminology the user sees on their screen.

## Standard Workflow

When invoked to update the user documentation:
1. **Discover**: Identify what has changed (read recent PRs, `docs/ROADMAP.md`, or ask the user for the changelog).
2. **Version Check**: Check `app/scripts/Boot.cs` for the latest `AppVersion`.
3. **Draft/Edit**: Create or update the relevant Markdown files in `docs/user-manual/`.
4. **Review**: Ensure no technical implementation details leaked into the end-user guide.

## Example Invocations (for Claude / Gemini)

- "Use the user-docs skill to write a quick start guide for the current version."
- "We just finished M4-7 (mesh hiding). Update the user manual accordingly using the user-docs skill."
