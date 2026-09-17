# RTK - Rust Token Killer (Google Antigravity)

**Usage**: Token-optimized CLI proxy for shell commands.

## Rule

Always prefix shell commands with `rtk` to minimize token consumption.

```bash
rtk git status
rtk cargo test
rtk ls src/
rtk grep "pattern" src/
rtk find "*.rs" .
rtk docker ps
rtk gh pr list
```

**On Windows, `rtk` only resolves direct `.exe` files.** Native binaries (`git`,
`docker`, `cargo`) work as above. Built-in commands, `.bat` / `.cmd` scripts and
aliases (`dir`, `npm`) must be wrapped, or the call fails:

```bash
rtk cmd /c "dir /O-D"
rtk powershell -c "<command>"
```

## Meta Commands

```bash
rtk gain              # Show token savings
rtk gain --history    # Command history with savings
rtk discover          # Find missed RTK opportunities
rtk proxy <cmd>       # Run raw (no filtering, for debugging)
```

## Why

RTK filters and compresses command output before it reaches the LLM context, saving 60-90% tokens on common operations. Always use `rtk <cmd>` instead of raw commands.
