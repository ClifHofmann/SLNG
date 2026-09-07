# [FEAT-SL-01] Second Life readiness — the TPV gaps that block a legal login

- **Feature ID:** `FEAT-SL-01`
- **Track:** `net` / `ui`
- **Status:** `✅ Done` — in-world 2026-09-07: still no ToS prompt, as expected — an account that accepted the ToS at sign-up never triggers the grid's `tos` login failure, so the gate has no trigger on the user's accounts. The load-bearing fix (removing LMV's `AgreeToTos=true` default, a live §1.f violation) is verified by reflection + tests; the gate path needs a fresh never-accepted account or a new ToS version. Quick sub-checks — About window shows channel "Puris", no "Export (Full Perm)" entry in the inventory menu, Aditi in the grid list — **all three confirmed in-world 2026-09-07 ("passt")**. Closed on that basis; the ToS-gate path stays unexercised by design.
- **Owner:** `claude`
- **Spec / Roadmap:** [ROADMAP.md](file:///E:/Git/SLNG/docs/ROADMAP.md) — folds into `MVP6-2`
- **Branch:** `feature/FEAT-SL-01-second-life-readiness`
- **Version:** `v0.20.0-alpha`

## Overview & Goal

Development has run entirely on OpenSim. The next target is Second Life's **beta grid, Aditi**,
and the Third-Party Viewer Policy governs everything that touches a Linden grid — including a
first test login. This task closes the gaps between "SLNG can technically connect" and "SLNG may
connect", for a **single user connecting their own viewer** — not distribution. Distribution adds
its own duties (§1.c disclosures, §4.b privacy policy, §1.d/e install/uninstall, §5 branding
review before naming a public release) that are out of scope here.

The policy is the source, not memory: <https://secondlife.com/corporate/third-party-viewers>, and
the policy text distinguishes two audiences throughout: §1.a/b, §2, §3.a, §4.a, §7 bind **anyone
who connects**; §1.c–g, §4.b, §5 bind only **a Developer who distributes** to others. This task
covers the first set in full and the two connect-time items from the second (§1.f, §1.g) because
they were trivial to add and make testing on Aditi honest.

**Already satisfied without changes:** the version reported at login is the real `AppVersion`
(`Boot.cs`, §1.b half of the disclosure).

This spec has two passes: the original §1.f/§1.g/Aditi work, and a **second audit pass** (below)
that went back over §2 and found two things that were shipping unconditionally, not merely
missing.

### The one that was a live violation, not a missing feature

LibreMetaverse's `LoginParams()` constructor sets **`AgreeToTos = true` and `ReadCritical = true`**.
Verified by reflection against the pinned 3.1.3 assembly, not read off the vendored source:

```
AgreeToTos default = True
ReadCritical default = True
```

SLNG never touched either field, so **every login SLNG has ever sent asserted that the user accepted
that grid's Terms of Service**, sight unseen. That is exactly the §1.f violation — "setting the flag
blindly is precisely the violation" — and it was shipping, not absent. It never showed on OpenSim
because OpenSim grids do not gate logins on a ToS.

The reference viewer states the rule in its own source (`indra/newview/lllogininstance.cpp`):

```cpp
request_params["agree_to_tos"] = false; // Always false here. Set true in
request_params["read_critical"] = false; // handleTOSResponse
```

…and only `handleTOSResponse(accepted)` sets the flag before `reconnect()`.

## Acceptance Criteria

- [x] **§1.f — ToS gate.** A login refused with `reason: "tos"` shows the grid's own text and does
      not proceed without the user's acceptance; accepting retries with `agree_to_tos`. Declining
      abandons the attempt and sends nothing.
- [x] `reason: "critical"` takes the same path, retried with `read_critical`.
- [x] Neither flag is ever true unless it followed a real acceptance in this session; the default
      is false on every attempt.
- [x] **§1.g — About window.** Names the viewer and shows its version, reachable **from the login
      screen** and from App → About. Inherits `SLNGWindow` (app-rules).
- [x] **Aditi** is a first-class grid entry, listed ahead of Agni.
- [x] **§2.b — no export shortcut on "full permissions".** The inventory context menu no longer
      offers an "Export" gated on copy/modify/transfer alone.
- [x] **§2.b — no undisclosed export of other creators' content.** The bake-diagnostic PNG dumps
      (decoded wearable textures written to disk) refuse to run on a Linden grid.
- [x] **§1.a — no unrequested protocol departure on SL.** The manual client-side bake ("Avatar neu
      backen", "Testmuster backen") and the test-skin generator refuse on any region that does the
      real SL server-side-baking handshake, instead of running SLNG's OpenSim (XBakes) composite
      path against a server that already composites for the client.
- [x] **§5.b — viewer identifier carries no Linden Lab trademark fragment.** The channel sent at
      login changed from `"SLNG"` (starts with "SL") to `"Puris"`, the viewer's actual public name.
- [x] Unit tests written and passing (255 in `SLNG.Net.Tests`, up from 240 before this spec).
- [ ] **In-world:** a real login to Aditi reaches the ToS dialog and gets through it. *Not yet
      attempted — see "Still open".*

## Technical Specs & Affected Files

| File | Change |
|---|---|
| `src/SLNG.Net/LoginCredentials.cs` | `AgreeToTos` / `ReadCritical` (both default **false**), `SecondLifeBetaLoginUri`, `Channel` default `"SLNG"` → `"Puris"` |
| `src/SLNG.Net/LoginResult.cs` | `RequiresTermsAcceptance` / `RequiresCriticalAcknowledgement`, and the two reason keys |
| `src/SLNG.Net/GridSession.cs` | `BuildLoginParams` (testable seam overwriting LMV's ToS defaults); null-response path reports the grid's real reason; `_isLindenGrid` + `IsLindenLabUri`; `DumpPreview` refuses on a Linden grid; `BakeAvatarAsync` and `CreateTestSkinAsync` refuse when `RegionHasServerSideBaking()` |
| `app/scripts/UI/TermsOfServiceWindow.cs` | new — the §1.f gate |
| `app/scripts/UI/AboutWindow.cs` | new — the §1.g window; `ViewerChannel` matches the `Channel` rename |
| `app/scripts/UI/TopMenu.cs` | App → About |
| `app/scripts/UI/InventoryPanel.cs` | removed the dead "Export (Full Perm)" context-menu entry (§2.b) |
| `app/scripts/Boot.cs` | `DialogLayer`, the About entry points, the ToS retry loop, the two Linden grid entries |
| `app/i18n/*.json` | `ui.about.*`, `ui.tos.*`, `ui.menu.about`, `ui.login.grid_sl*` |
| `tests/SLNG.Net.Tests/LoginTermsGateTests.cs` | new — ToS gate, `IsLindenLabUri`, channel-branding regression guard |
| `tests/SLNG.Net.Tests/LoginResultTests.cs` | channel-default assertion updated to `"Puris"` |

### The second defect this surfaced

`NetworkManager.LoginWithResponseAsync` returns the parsed response **only on success**:

```csharp
loginResultTcs.TrySetResult(LoginStatusCode == LoginStatus.Success ? LoginResponseData : null);
```

So *every* failure arrived at SLNG as a bare `null`, which `LoginAsync` reported as
`"no-response" / "Grid returned no login response."` — a wrong password, a banned account and a ToS
refusal were all indistinguishable, and the ToS gate could never have fired. The reason survives on
`NetworkManager.LoginErrorKey` / `.LoginMessage`, which is where it is now read from.

### Design notes (§1.f / §1.g pass)

- **A loop, not one retry.** A grid can demand both in turn: accept the ToS, and the next attempt
  comes back asking for the critical message. `handleTOSResponse → reconnect()` has the same shape.
- **The loading screen comes down while the gate is up**, mirroring the reference viewer's
  `gViewerWindow->setShowProgress(false)` — the login is not progressing, it is waiting on a person.
- **`DialogLayer`**, a new always-visible `CanvasLayer`, exists because `HudLayer` is created hidden
  and only shown after a *successful* login. Both of this task's windows must work before there is
  a session.
- **The About window links out rather than embedding a browser.** The reference viewer loads
  `secondlife.com/app/tos` in an embedded `LLMediaCtrl`; we have no such control, so the gate shows
  the grid's message text and offers a URL — one found in the message, or SL's own ToS page on a
  Linden grid — through `OS.ShellOpen`.

## Audit pass — what a full §2 / §5 re-read found

A second pass against the policy text (not memory) after the §1.f/§1.g work turned up two items
that were **live**, not missing, plus two items worth hardening pre-emptively:

1. **"Export (Full Perm)" context-menu entry — §2.b.** Gated on `canCopy && canModify &&
   canTransfer`, which the policy explicitly rejects as the test: *"before allowing the user to
   export the content, the Third-Party Viewer must verify that the Second Life creator name for
   each and every content component to be exported ... is the same as the Second Life name of the
   Third-Party Viewer user. This must be done for all content in Second Life, including content
   that may be set to 'full permissions.'"* It had no handler in `OnContextMenuIdPressed` — a dead
   menu item — but LL may analyze a viewer's code and content (§8.b), and an entry named exactly
   that with exactly the excluded shortcut reads as a violation on inspection alone. Removed
   outright rather than implemented, since nothing in this task needs a creator-verified export.

2. **Bake-diagnostic PNG dumps — §2.b.** `SLNG_BAKE_VERBOSE=1` writes every decoded input texture
   feeding an avatar bake — other people's skins, tattoos, clothing layers — to
   `%TEMP%\slng_bake\*.png`. Functionally an export SL's own viewer has no equivalent of, with no
   creator check. Harmless on OpenSim (no such restriction there, and it is the only place this
   flag has ever been used to chase a real bake defect); now refused outright on any Linden grid
   (`GridSession.IsLindenLabUri`, checked once at login and cached in `_isLindenGrid`).

3. **Manual client-side bake, unguarded — §1.a.** `BakeAvatarAsync` (the "Avatar neu backen" /
   "Testmuster backen" menu commands) is the OpenSim (XBakes) path: SLNG composites, uploads
   textures, and hand-sends a bake-carrying `AgentSetAppearance`. It never checked
   `RegionHasServerSideBaking()` — the same check the wearable-edit path (`WearWearableAsync`)
   already makes correctly. On a real SL region, running it anyway uploads textures the server has
   no use for and races SL's own composite pipeline with a raw packet: an unrequested protocol
   departure, and the most likely way to leave the avatar looking wrong to everyone else in the
   room. Now refuses with a chat message instead of running.

4. **Test-skin generator, same gap — §1.a / financial trap.** `CreateTestSkinAsync` creates three
   texture uploads plus a Body Part item — real L$ upload fees on Agni, spent on a tool built for
   the OpenSim bake investigation and answering a question a real skin already answers on SL. Same
   `RegionHasServerSideBaking()` guard.

5. **Viewer channel `"SLNG"` — §5.b.** *"Your Third-Party Viewer name must not be confusingly
   similar to or use any part of a Linden Lab trademark, including 'Second,' 'Life,' 'SL,' or
   'Linden.'"* The channel string is the viewer identifier a sim log shows (`viewer <channel>
   <version>`) and is held to the same rule as the displayed name. `"SLNG"` — the codebase's
   internal engineering name, predating Second Life as a stated target — starts with "SL" and
   fails on the letter, even though it never meant to imply a Linden Lab connection. Changed to
   `"Puris"`, the viewer's actual public name (`AboutWindow.ViewerName`, the login screen).
   `BuildInfo.Name` (the internal codebase constant), the `ChatLogger` local log directory, and the
   `LibreMetaverse.Logger` category name are **left as `"SLNG"`** — none of them is grid-facing or
   user-facing branding, and renaming the log directory would move where an existing user's chat
   logs live for no compliance benefit.

## What the tests guarantee

`LoginTermsGateTests` pins that **`BuildLoginParams` overwrites LibreMetaverse's `true` defaults**
(the regression guard: a package bump that re-defaults them cannot silently reintroduce the
violation), that a real acceptance is forwarded, that `tos` / `critical` are told apart from
`key` / `presence` and from success, that the Aditi URI is what it claims to be, that
`IsLindenLabUri` tells Agni and Aditi apart from OpenSim (the gate the bake-dump and manual-bake
refusals both key off), and that the channel default carries none of the four forbidden trademark
fragments. One test asserts LibreMetaverse's own defaults directly, so if upstream ever changes
them the comments explaining all of this fail loudly instead of quietly going stale.

The `RegionHasServerSideBaking()` guards on `BakeAvatarAsync` / `CreateTestSkinAsync` and the
`DumpPreview` refusal are **not** unit-tested: all three need a connected `GridClient`
(`_client.Network.Connected`), which is exactly the live-grid dependency `tests-rules` asks
integration tests to run against local OpenSim only, never a Linden grid — and there is no
OpenSim-side way to fake "this region does SL server-side baking." Covered by code review and by
`IsLindenLabUri`'s own tests instead; the in-world Aditi login is what exercises them for real.

## Still open

- **A login to Aditi has now happened and reached the world** (chat, travel/teleport activity
  observed in-session) — the first real confirmation this branch can actually connect to a Linden
  grid. **Not yet separately confirmed:** whether the login went through the ToS gate at all (an
  account that already accepted the grid's terms via another viewer would never see it), or
  whether the §2.b/§1.a refusals (bake dumps, manual bake, test-skin generator) have been
  exercised — none of those were used this session. What the Aditi visit DID surface, in a
  related feature (`FEAT-SL-02`), is a real capability-timing bug — see that spec's own "Found
  live on Aditi" section — which is a useful data point for `RegionHasServerSideBaking()` too
  (same live-capability-read shape, untested so far).
- **Whether Aditi's ToS response carries text or only a marker.** The gate handles both (it says so
  and offers the link when the message is empty), but which one arrives is unobserved.
- **The `RegionHasServerSideBaking()` refusals are unexercised against a real SSB region** — see
  "What the tests guarantee". The Aditi login is also the first real test of these.
- Everything in HANDOVER §6 that is *not* a TPV gap: on SL the bake is server-side, so
  FEAT-AVATAR-01's client-side bake is the OpenSim path. What carries over — the COF-based worn set,
  the layer-ordering tokens, the body-part rules, the duplicate cleanup, and the whole render side
  (BoM channel binding, bake fetch, local appearance refresh) — is already in code and untested
  against SL. FEAT-AVATAR-01's open item **(b), alpha wearables never reaching the bake, still
  applies on SL**; item (a), the upload read-back, becomes meaningless there.
- **Not attempted:** the distribution-only duties (§1.c–e, §4.b, §5 branding review) — irrelevant
  until this viewer is handed to anyone else.
