# Third-party notices

SLNG / Puris Viewer is licensed under the **MIT License** (see `LICENSE` in the repository root).

In addition, the project incorporates or distributes the third-party material listed below. Each entry states what is
included, where it came from, its licence, and **what we changed** — the last part being an explicit
condition of the Creative Commons licence covering the Second Life viewer artwork.

---

## Second Life™ Viewer Artwork — Linden Research, Inc.

Licensed under **Creative Commons Attribution-Share Alike 3.0**
(<https://creativecommons.org/licenses/by-sa/3.0/legalcode>).

### Files included

| File in this repository | Source in the Second Life viewer | Changes made |
|---|---|---|
| `app/textures/sl_alpha_gradient_2d.png` | `indra/newview/skins/default/textures/alpha_gradient_2d.j2c` | **Re-encoded from JPEG 2000 to PNG.** Pixel values are unchanged — the conversion is lossless and was verified byte-for-byte against the decoded original. Not resized, recoloured or retouched. |
| `app/textures/clouds2.tga` | `indra/newview/app_settings/windlight/clouds2.tga` | **None.** Byte-identical to the original. |

`sl_alpha_gradient_2d.png` is the terrain detail-texture blend ramp (`IMG_ALPHA_GRAD_2D`). It is a
lookup table rather than a picture: the renderer indexes it to reproduce the viewer's terrain
texture transitions. The re-encoding to PNG exists only so the engine can load it natively.

Neither file is a Linden Lab trademark. Linden Lab's trademarks — including the Second Life brand
name and the Second Life Eye-in-Hand logo — are **not** licensed here and are not used by this
project; see <https://secondlife.com/corporate/brand/trademark/>.

### Copyright and permission notice

The following notice is reproduced verbatim from `doc/LICENSE-logos.txt` in the Second Life viewer
source distribution, as that licence requires.

> COPYRIGHT AND PERMISSION NOTICE
>
> Second Life(TM) Viewer Artwork.  Copyright (C) 2008 Linden Research, Inc.
>
> Linden Research, Inc. ("Linden Lab") licenses the Second Life viewer
> artwork and other works in the files distributed with this Notice under
> the Creative Commons Attribution-Share Alike 3.0 License, available at
> http://creativecommons.org/licenses/by-sa/3.0/legalcode.  For the license
> summary, see http://creativecommons.org/licenses/by-sa/3.0/.
>
> Notwithstanding the foregoing, all of Linden Lab's trademarks, including
> but not limited to the Second Life brand name and Second Life Eye-in-Hand
> logo, are subject to our trademark policy at
> http://secondlife.com/corporate/brand/trademark/.
>
> If you distribute any copies or adaptations of the Second Life viewer
> artwork or any other works in these files, you must include this Notice
> and clearly identify any changes made to the original works.  Include
> this Notice and information where copyright notices are usually included,
> for example, after your own copyright notice acknowledging your use of
> the Second Life viewer artwork, in a text file distributed with your
> program, in your application's About window, or on a credits page for
> your work.

---

## Second Life™ Viewer — Windlight preset files

Licensed under the **GNU Lesser General Public License, version 2.1**, as part of the Second Life
viewer source distribution (`indra/newview/app_settings/windlight`). Firestorm ships the same
files, which is why the preset names in this client's environment picker match the ones its users
already know.

### Files included

| Files in this repository | Source in the Second Life viewer | Changes made |
|---|---|---|
| `app/assets/windlight/skies/*.xml` (36 files) | `indra/newview/app_settings/windlight/skies/*.xml` | **File contents unchanged — byte-identical.** Only the file NAMES differ: the originals are URL-escaped (`Blue%20Midday.xml`), ours are decoded (`Blue Midday.xml`). |
| `app/assets/windlight/water/*.xml` (7 files) | `indra/newview/app_settings/windlight/water/*.xml` | Same: contents byte-identical, names URL-decoded. |

These are LLSD documents describing sky and water settings — the same format the legacy
`EnvironmentSettings` capability carries. This project reads them; it does not ship a modified
viewer.

---

## LibreMetaverse

The Second Life / OpenSimulator protocol implementation, used as a NuGet dependency. BSD 3-Clause.
See the package's own licence metadata for the full text.

## CoreJ2K

JPEG 2000 codec, used as a NuGet dependency for decoding grid textures. BSD 2-Clause / original
JJ2000 terms. See the package's own licence metadata for the full text.

## Godot Engine

The client runs on Godot Engine, MIT licensed. Godot's own notices ship with the engine runtime
in the exported binary.

---

## Reference source, not distributed

`scratch/` holds working copies of the Second Life viewer source (LGPL 2.1), LibreMetaverse and
OpenSimulator, kept for verifying behaviour against the real implementations. It is a scratch area
for development and is **not** part of any release of this project. Nothing in `scratch/` is
redistributed by us.
