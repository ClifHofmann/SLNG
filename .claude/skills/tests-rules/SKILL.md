---
name: tests-rules
description: Rules for tests/ — what an SLNG test must assert, the xUnit project layout, fixture handling, and the OpenSim integration-test policy (never the live SL grid). Loads automatically when working under tests/.
paths: tests/**
---

# Rules for `tests/`

This codebase is written largely by AI agents, so **output must be verifiable**. Tests are
how protocol and asset code earns trust here — they are not a formality.

## Projects

| Project | Covers |
|---|---|
| `tests/SLNG.Core.Tests` | World model / ECS diffing, pure logic |
| `tests/SLNG.Net.Tests` | Protocol decoding; integration against a **local OpenSim** grid |
| `tests/SLNG.Assets.Tests` | J2K / mesh / material decode against committed fixtures |

xUnit throughout. `dotnet test` runs all three.

## What a test must do

- **Assert behaviour, not absence of exceptions.** "It didn't throw" is not a test. Pin the
  meaningful fields: dimensions, mip/LOD structure, material values, decoded weights.
- **Prefer test-first** for protocol and asset work — the failing test is the spec, and it
  is the only thing that makes an AI-written decoder reviewable.
- **Keep fixtures small and deterministic**, committed under `tests/`. A cached `.j2c` or a
  captured packet beats many live-restart rounds; decoding a cached asset offline and
  diffing two decoders is the fastest way to settle a "renders wrong" question.
- **Poll with timeouts, not sleeps**, in anything touching a grid.

## Grid policy

Integration tests run against a **local OpenSim** grid (`tools/opensim-up.ps1` /
`tools/opensim-up.sh`). **Never test against the live Second Life grid** — that is the TPV
line in `AGENTS.md`, and OpenSim is the default target precisely to avoid it.

## Known-answer probes

For "this renders differently from Firestorm" questions, a full-perm **scripted probe object**
you rez yourself beats screenshotting no-modify grid content: you know the intended answer,
so the test has a ground truth. Prior art lives in `tools/Probe/` and `tools/ProbeTerrain/`
(both git-ignored — don't commit throwaway probes).

## Coverage expectation

Every non-trivial change ships tests. Finish with a one-line note in the task saying what is
now guaranteed.
