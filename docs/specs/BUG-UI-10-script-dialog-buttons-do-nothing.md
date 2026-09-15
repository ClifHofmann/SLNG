# [BUG-UI-10] Script dialog buttons have no effect — the menu opens, the choice never lands

- **Feature ID:** `BUG-UI-10`
- **Track:** `ui` (+ `net`)
- **Status:** `⏸️ Pending`
- **Owner:** *(unassigned)*
- **Spec / Roadmap:** [ROADMAP.md](file:///E:/Git/SLNG/docs/ROADMAP.md)

## Overview & Goal

Reported live 2026-09-15: *„Bei beiden Posestands ist es so, dass der Klick auf die Buttons keine
Wirkung hat außer dass das Menü kommt. Normal sollten die Buttons aber die Pose wechseln."*

The `llDialog` menu **opens correctly** — so the inbound half (`ScriptDialog` → `ScriptDialogEvent`
→ `ScriptDialogWindow`) works. Pressing one of its buttons does nothing at all: no pose change, no
second menu, no reaction from the object. The outbound reply is therefore either not sent, or sent
in a form the script does not accept.

**On both pose stands**, which makes it independent of BUG-ANIM's pose-stand investigation and of
the object-specific `ClickAction` work in v0.22.163 — those were about *reaching* the menu. This is
about the menu's answer.

## Why it matters beyond pose stands

`llDialog` is how a large share of in-world content is operated: vendors, rezzers, furniture menus,
HUD-less devices, teleport boards. If the reply never lands, none of them respond. It is plausible
that this has never worked and only surfaced now because pose stands were being tested closely.

## What is already known

- **Inbound is fine.** The menu appears with its buttons, so `OnScriptDialog` and the window are
  doing their job.
- **The reply path exists and looks structurally right:**
  `ScriptDialogWindow` → `GridSession.ReplyToScriptDialog(objectId, channel, buttonIndex, buttonLabel)`
  → `AgentManager.ReplyToScriptDialog`, which fills a `ScriptDialogReplyPacket` with
  `ButtonIndex`, `ButtonLabel`, `ChatChannel`, `ObjectID` and sends it.
- Nothing has been measured yet — no log line confirms the packet is built or sent.

## Suspects, in the order worth checking

1. **The object id.** `ScriptDialogReplyPacket.ObjectID` must be the **simulator's object UUID**.
   This project has already been caught once this week passing `Entity.Id` — an internal ECS
   `Guid.NewGuid()` — where an object UUID was required (see FEAT-ANIM-03's seat resolution, fixed
   v0.22.155). `ScriptDialogEvent.ObjectId` comes from `e.ObjectID.Guid`, which looks correct, but
   it has not been verified end to end.
2. **The button index.** The reference viewer lays dialog buttons out in a bottom-up 3-column grid
   while the script's list order is different. If the window renders in display order and sends the
   displayed index, the index and the label disagree — many scripts branch on the **label**, which
   would still work, but some branch on the index.
3. **The channel.** `ScriptDialogEvent` carries the simulator-assigned reply channel. A dialog
   answered on the wrong channel is simply not heard.
4. **Not sent at all.** `ReplyToScriptDialog` is gated on `_client.Network.Connected`; the window's
   handler could also be failing before it reaches the call.

## First step: measure, do not guess

Add a log line where the reply is built — object id, channel, index, label — and one press of a
dialog button says which of the four suspects it is. Three rounds of guessing were spent on the
pose stand before a measurement settled it; do not repeat that here.

A scripted probe in `tools/testassets` that echoes what it receives on its dialog channel would
confirm the answer from the other side, and is cheap to write.

## Acceptance Criteria

- [ ] Pressing a button in an `llDialog` menu produces the script's reaction — verified on both
      pose stands, where the pose must change.
- [ ] Verified against a second kind of content (a vendor or a furniture menu), so the fix is not
      pose-stand-shaped.
- [ ] The reply carries the simulator's object UUID, the dialog's own channel, and an index that
      matches the label sent with it.
- [ ] Unit test over whatever the reply construction turns out to get wrong.

## Technical Specs & Affected Files

- `app/scripts/UI/ScriptDialogWindow.cs` — button layout and what index it reports.
- `src/SLNG.Net/GridSession.cs` — `ReplyToScriptDialog`, and `OnScriptDialog` for the ids it hands
  out.
- `src/SLNG.Core/GridEvents.cs` — `ScriptDialogEvent`.

## Sub-tasks / Progress

- [ ] Log the reply as it is built; press a button; identify the suspect
- [ ] Fix
- [ ] Test
- [ ] In-world: both pose stands, plus one unrelated dialog-driven object
