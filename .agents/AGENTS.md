
# RTK - Rust Token Killer (Google Antigravity)

**Usage**: Token-optimized CLI proxy for shell commands.

## Rule

Always prefix shell commands with rtk (or tk) to minimize token consumption when executing terminal commands.
**Windows-Specific RTK Rule**: `rtk` only resolves direct `.exe` files. 
- For native binaries (`git`, `docker`, `cargo`), use normally: `rtk git status`
- For built-in commands, `.bat` / `.cmd` scripts, or aliases (e.g., `dir`, `npm`), you MUST wrap them: `rtk cmd /c "dir /O-D"` or `rtk powershell -c "Dein Befehl"`.

Examples:
- rtk git status
- rtk cargo test
- rtk ls
- rtk grep "pattern" src/
- rtk find "*.rs" .
- rtk docker ps
- rtk gh pr list

## Meta Commands
- rtk gain: Show token savings
- rtk gain --history: Command history with savings
- rtk discover: Find missed RTK opportunities
- rtk proxy <cmd>: Run raw (no filtering, for debugging)

# Communication Style Rule

- Be highly concise and direct.
- Do NOT output unnecessary commentary, boilerplate, explanations, or step-by-step narration for minor actions.
- Avoid repeating context or explaining obvious steps before/after running commands or making file edits.
- Focus on taking action, and summarize only key findings, results, or questions when user input is needed.

# Mandatory Versioning Rule

- **Always update the version number**: When implementing a new feature, a fix, or significant UI change, you MUST update the `AppVersion` constant in `app/scripts/Boot.cs` (e.g. from `v0.1.54-alpha` to `v0.1.55-alpha`) to reflect the new state. This ensures the version is visible on the login screen and title bar.

