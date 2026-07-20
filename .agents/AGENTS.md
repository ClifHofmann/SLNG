
# RTK - Rust Token Killer (Google Antigravity)

**Usage**: Token-optimized CLI proxy for shell commands.

## Rule

Always prefix shell commands with rtk (or tk) to minimize token consumption when executing terminal commands.

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
