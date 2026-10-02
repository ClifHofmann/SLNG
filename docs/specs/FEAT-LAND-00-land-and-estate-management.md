# [FEAT-LAND-00] Land and estate management windows (overview)

- **Feature ID:** `FEAT-LAND-00` (umbrella for `FEAT-LAND-01` … `FEAT-LAND-10`)
- **Track:** `net` | `ui`
- **Status:** `🚧 In Progress`
- **Owner:** unassigned
- **Spec / Roadmap:** [ROADMAP.md](file:///E:/Git/SLNG/docs/ROADMAP.md)

## Overview & Goal
Reported 2026-10-02 with a Firestorm screenshot: SLNG has no counterpart of the two floaters a
resident uses to see and manage land.

- **Land-Info** (*About Land*, per parcel): tabs General, Covenant, Objects, Options, Media, Sound,
  Access, and more.
- **Region/Estate** (*Region/Grundbesitz*, per estate and region): tabs Estate, Access, Covenant,
  Region, Terrain, Environment, Debug.

Today only `RegionRestartWindow` and `CreateLandmarkWindow` touch this area, and the ROADMAP only
has terrain sculpting (`MVP4-2`) and buying land (`MVP5-2`).

## Order of work
Read first, write last. Each ticket is shippable on its own.

| Ticket | Content | Needs |
|---|---|---|
| FEAT-LAND-01 | Land-Info shell + General tab | — |
| FEAT-LAND-02 | Options, Media, Sound tabs | 01 |
| FEAT-LAND-03 | Objects tab | 01 |
| FEAT-LAND-04 | Access tab | 01 |
| FEAT-LAND-05 | Covenant (shared) | 01 |
| FEAT-LAND-06 | Region/Estate shell + Estate tab | 05 |
| FEAT-LAND-07 | Estate Access tab | 06 |
| FEAT-LAND-08 | Region, Terrain, Environment tabs | 06 |
| FEAT-LAND-09 | Parcel editing | 02, 03, 04 |
| FEAT-LAND-10 | Estate editing | 07, 08 |

## Rules for the whole group
- Every window inherits `SLNG.App.UI.SLNGWindow`; every ticket bumps `AppVersion`.
- **Permissions come from the sim.** A control is enabled only when the sim's own reply says the
  agent may use it (parcel rights in `ParcelProperties`, estate manager/owner in the estate info).
  The client never infers a right and never offers an action the sim would refuse. A refusal is
  shown as a refusal, not as an empty list. This is the TPV Policy rule (honour permissions) and
  the reason writing is last.
- Each message is checked against the real viewer source before it is implemented
  (`llviewerparcelmgr.cpp`, `llfloaterland.cpp`, `llfloaterregioninfo.cpp`, `llpanelestateinfo.cpp`),
  not written from memory. OpenSim is the test target; on Second Life, writes are tested only on land
  the developer owns.
- Names go through the existing display-name cache; no per-row lookups on the main thread.
- Network replies arrive on LibreMetaverse threads: buffer them and apply on the main thread, like
  every other world event.

## Acceptance Criteria (group)
- [ ] The General tab shows the same name, owner, type, rating and traffic as Firestorm on the same parcel.
- [ ] No write control is enabled for an agent the sim does not grant that right.
- [ ] Each ticket has unit tests for its message encode/decode.

## Sub-tasks / Progress
- [ ] FEAT-LAND-01 … FEAT-LAND-10 (see table)
