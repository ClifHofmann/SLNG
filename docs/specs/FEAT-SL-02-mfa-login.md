# [FEAT-SL-02] Second Life login with multi-factor authentication (MFA)

- **Feature ID:** `FEAT-SL-02`
- **Track:** `net` / `ui`
- **Status:** `🧪 Review`
- **Owner:** `claude`
- **Spec / Roadmap:** [ROADMAP.md](file:///E:/Git/SLNG/docs/ROADMAP.md)
- **Not to be confused with** [FEAT-SL-02-age-settings.md](file:///E:/Git/SLNG/docs/specs/FEAT-SL-02-age-settings.md): the id was used twice; this file is the MFA login.

## Overview & Goal

A Second Life account with MFA switched on cannot log in with name and password alone: the grid
answers `reason: "mfa_challenge"` and wants the current code from the person's authenticator app.
SLNG never asked, and showed the failure as "Grid returned no login response (Canceled)" (BUG-NET-30).
Goal: recognise the challenge as its own outcome, ask for the code, retry, and remember the grid's
"remember this computer" token per saved login the way the Linden viewer does.

## The real flow (Linden viewer, `scratch/slviewer`, commit `4ef9f8f`)

All paths below are `indra/newview/`.

**Request.** Every login request carries two MFA fields, always (empty strings when unused):
`request_params["token"] = ""` (`lllogininstance.cpp:229`) and `request_params["mfa_hash"] = mfa_hash`
(`:269`). `mfa_hash` is read from the secure store keyed by grid and user id (`:244-267`; the debug
setting `MFAHash` can override it for testing). `agree_to_tos` / `read_critical` are always `false` on
the first attempt (`:214-215`), so an MFA account that has not accepted the Terms gets a `tos` challenge
too.

**Challenge.** The grid refuses with `reason == "mfa_challenge"` (`:409`). The viewer hides its progress
screen and opens the MFA prompt with the text `LLTrans::getString(response["message_id"])` (`:418`; the
only MFA string the viewer ships is `LoginFailedAuthenticationMFARequired`, "To continue logging in, enter
a new token from your multifactor authentication app", `skins/default/xui/en/strings.xml:202`).
`llstartup.cpp:1307` suppresses its own error pop-up for `tos` and `mfa_challenge` because "the specialized
floater has already scolded the user". Unlike `key` (wrong password, `llstartup.cpp:1286`), a challenge does
**not** clear the saved credential.

**The hash that rides on a failure.** If the refusal itself carries an `mfa_hash`, the viewer puts it into
the retry (`lllogininstance.cpp:324-327`, and clears `token`) and calls `saveMFAHash` on it (`:329`). So a
challenge may carry a hash that must be echoed together with the code. SLNG does the same
(`LoginResult.MfaHash` on a challenge becomes `LoginCredentials.MfaHash` on the retry).

**Prompt.** Notification `PromptMFAToken` / `PromptMFATokenWithSave` (`notifications.xml:12551-12595`):
the message, one text field `token`, optionally a checkbox "Remember this computer for 30 days.",
Continue, Cancel. The checkbox form is used only when the viewer's `RememberUser` ("remember login")
setting is on (`lllogininstance.cpp:515-523`). The default is unticked. (`floater_mfa.xml` also exists but
is not what `showMFAChallange` opens.)

**Answer.** `handleMFAChallenge` (`:527-548`): whitespace is removed from the token with a regex
(`:534`, "SL-17034"); a non-empty token with Continue sets `params["token"]` and `mSaveMFA` = the checkbox
(`:541-542`), then `reconnect()`. Cancel ends the attempt. There is **no client-side check of length or
digits**; the grid judges the code.

**Wrong or expired code.** The viewer has no separate path. The retry goes through the same
`handleLoginFailure` and a repeat `mfa_challenge` re-opens the same prompt (`:409-418`), with whatever
`message_id` the grid sends. The wire therefore carries no distinct "wrong code" reason that the viewer
reads; SLNG tells the two apart by what it sent: a challenge in answer to an attempt that carried a code
is a refused code (`MfaLogin.IsRejectedCode`). What `message_id` SL attaches to a bad code is **not**
established from the viewer source.

**Terms after a code.** If a `tos` refusal arrives while a token is set, the token is presumed spent, and the
viewer asks for a fresh one before retrying (`:488-493`, "SL-18511"). SLNG's loop does the same.

**Success.** The success response may carry `mfa_hash`; `saveMFAHash(response)` runs on it
(`llstartup.cpp:4128`). It is stored only if the response has one **and** `RememberUser` is on **and**
`mSaveMFA` (default `true`, `lllogininstance.cpp:86`; set by the checkbox after a challenge) is true
(`:557-560`); it is deleted if `mSaveMFA` is false (`:561-564`). So a login that is not challenged keeps
refreshing the stored hash with whatever the grid returns.

**What "remember" stores, for how long.** The opaque `mfa_hash` string, in the viewer's secure store
(`addToProtectedMap("mfa_hash", grid, user_id, hash)`, `lllogininstance.cpp:559`), a file `bin_conf.dat`
that is XOR-obfuscated with the machine id (`llsechandler_basic.cpp:1276-1342`, comment: "totally lame ...
at least obfuscate the data"). The viewer keeps **no timestamp and does no expiry**; the label says 30
days, and the grid enforces it: an expired or unknown hash simply draws a fresh challenge. The hash is
removed together with the saved login ("Forget user", `llfloaterforgetuser.cpp:172,242`).

## What SLNG does

| Viewer | SLNG |
|---|---|
| fields always present | present only when the attempt carries a code or a hash (`LoginParams.MfaEnabled`, see below) |
| `mfa_challenge` own outcome | `LoginResult.RequiresMfaToken` / `MfaChallengeErrorKey` |
| strip whitespace, send any non-empty | `MfaLogin.NormalizeToken`; the window enables Login only for six digits (`MfaLogin.IsPlausibleToken`) |
| prompt, remember checkbox only with "remember login" | `MfaPromptWindow`; the checkbox only when "Save Login" is ticked; unticked by default |
| hash from challenge echoed | `creds with { MfaHash = result.MfaHash ?? creds.MfaHash }` |
| hash stored per grid+user in `bin_conf.dat` | `mfa_hash` key in the profile's `[first last @ grid]` section of `logins.cfg` (`MfaHashStore`) |
| hash never expires locally | same; dropped when the grid rejects it |
| deleted with the user | dropped when "Save Login" is unticked and on any account/grid change in the form |

### LibreMetaverse 3.1.6

- `LoginParams.Token` is sent as `token`, `LoginParams.MfaHash` as `mfa_hash`, **only** when
  `LoginParams.MfaEnabled` is true (`NetworkManager.BeginLogin`). `Settings.Connection.MfaEnabled` defaults to
  `false` and is merely copied into `LoginParams` by the `LoginCredential` constructor. `GridSession.BuildLoginParams`
  sets `LoginParams.MfaEnabled` directly, per attempt, so the shared client settings are never changed.
- `LoginWithResponseAsync` returns `null` for **every** refused login, not only a dead connection:
  `UpdateLoginStatus(Failed)` cancels the result task. The grid's answer, however, is parsed into the public
  field `NetworkManager.LoginResponseData` first, failures included, with `Reason`, `Message`, `MfaHash`.
  `GridSession.LoginAsync` clears that field before the call and reads it after a `null` result
  (`GridSession.FailureFromGridAnswer`). Consequence beyond MFA: a wrong password (`key`), a `tos` and a
  `critical` demand now reach the caller with their own reason. Before, all three were reported as
  "no login response" -- including the Terms-of-Service gate of FEAT-SL-01, which could therefore never
  open on a live grid.
- `LoginParams(client, first, last, password, ...)` (what `LoginAsync` used to call directly) defaults
  `AgreeToTos`/`ReadCritical` to **true**. `BuildLoginParams` -- which overwrites both from the credentials
  and was already tested -- was dead code; `LoginAsync` now uses it (TPV Policy §1.f).
- `LoginErrorKey` is never reset between attempts; do not read it for the current answer.

### Secrets

`LoginCredentials.MfaToken` and `.MfaHash` are secrets like `Password`. The compiler-generated record
`ToString()` prints every property, so `LoginCredentials` and `LoginResult` override `PrintMembers` and say
only `[set]` / `[none]`. The login log lines print the reason key and "mfa_hash attached", never a value.
The code is held in a local variable for the retry, blanked in the `LoginOutcome` handed back, and cleared
from the prompt's text field when it is submitted or cancelled. It is never stored. The hash is stored
exactly as the password hash is: plain text in `logins.cfg`, one key beside `pass_hash`, no extra
encryption (the viewer's own store is only obfuscation). Login-equivalent for the MFA step only; the
account password is still required.

## Acceptance Criteria
- [x] The challenge is its own login outcome, verified against `lllogininstance.cpp` / `llstartup.cpp` (above).
- [x] `MfaPromptWindow` (SLNGWindow): six digits, paste friendly, remember box only with "Save Login", Login / Cancel; Boot retries on the same session, repeats on a refused code, Cancel returns to the login screen with the form intact.
- [x] `mfa_hash` kept per saved profile, sent on later logins, dropped when the grid rejects it, when "Save Login" is off, on an account/grid change.
- [x] No code or hash in a log line, notification or printed record (tests pin `ToString`).
- [x] Unit tests (`tests/SLNG.Net.Tests/MfaLoginTests.cs`, including the whole exchange against a local stand-in login server) and selftest checks (`mfa prompt window`, `mfa hash store`, locale parity, window insets).
- [x] German manual paragraph "Anmeldung mit Zwei-Faktor-Code (MFA)".
- [ ] **Not verified: the live exchange with a real MFA account** -- the tester's account is the check.

## Technical Specs & Affected Files
- `src/SLNG.Net/MfaLogin.cs` (pure rules), `LoginCredentials.cs`, `LoginResult.cs`, `GridSession.Session.cs`
- `app/scripts/UI/MfaPromptWindow.cs`, `app/scripts/MfaHashStore.cs`, `app/scripts/Boot.cs` (`LoginWithPromptsAsync`), `app/scripts/SelfTest.Mfa.cs`
- `app/i18n/en-US.json`, `de-DE.json` (`ui.mfa.*`)
- `tests/SLNG.Net.Tests/MfaLoginTests.cs`

## Open questions (need a real MFA account)
- Whether SL attaches an `mfa_hash` to the challenge itself, and what `message_id` it sends for a refused code.
- Whether a fresh `mfa_hash` comes back on every successful login (the viewer's code stores whatever arrives).
- Whether the login endpoint requires the `token`/`mfa_hash` keys to be present to issue the challenge (the tester's first, key-less login did get a refusal, but we only saw the reason as "Canceled").
