# Übergabe-Prompt: BUG-RENDER-16 — dichtes Gras flackert beim Laufen

> **Stand v0.22.24-alpha (2026-09-10, Claude):** siehe „## Runde 6" direkt unten. Der Rest des
> Dokuments ist die Übergabe aus Runde 5 und bleibt als Historie stehen.

## Runde 6 — die Sortierung ist nicht *neu entschieden*, sie ist *falsch* (v0.22.24)

**Neue Lesart des Freeze-Tests.** Eingefrorene Reihenfolge + trotzdem Flackern heißt nicht „die
Reihenfolge ist egal". Es heißt: die Reihenfolge ist **falsch**, und beim Gehen macht die
**Parallaxe** jede falsche Überdeckung sichtbar — ein hinterer Halm, der über einen vorderen
gezeichnet wird, *wandert* beim Gehen über den vorderen. Beim Drehen um das eigene Auge bleibt
jede Überdeckung exakt gleich (derselbe Sehstrahl trifft dieselben Punkte), deshalb ruhig. Das
erklärt jede Beobachtung, inklusive „scissor/hash ruhig" (echte Tiefe pro Pixel) und die vier
wirkungslosen Sortier-Fixes (keiner konnte die Reihenfolge *richtig* machen, weil es pro Objekt
keine richtige gibt).

**Vorher an der Godot-4.7-Quelle ausgeschlossen (nicht mehr prüfen):**
- SSAO/SSIL/SSR erreichen den Transparent-Pass nicht (`_setup_environment(..., p_opaque_render_buffers=false)`,
  `render_forward_clustered.cpp:2415` → `ss_effects_flags = 0`).
- DoF war in der Session aus (`preferences.cfg [dof] enabled=false`).
- Ein Blend-Material wirft keinen Schatten und ist in keinem Prepass (`:4191-4200`).
- Godots `depth_prepass_alpha`-Schwelle ist fest 0.99 (`:1823`), nicht einstellbar — darum hat
  `prepass` nichts gebracht.

**Der Viewer-Mechanismus** (`lldrawpoolalpha.cpp:212-227` und `:240-248`): ein **Depth-only-Pass
über den Alpha-Pool mit `setMinimumAlpha(0.33f)`** — Kern schreibt Tiefe, Saum nicht.

**Der Port — `--foliage-alpha=blendcore`, seit v0.22.24 Default:**
`app/materials/prim/prim_depth_core.gdshader` hängt als `next_pass` am normalen `prim_blend`-Material
(`ObjectRenderer.AttachDepthCore`), `blend_mix + depth_draw_always`, `discard` unter der Schwelle,
`ALPHA = 0` (Farbe unberührt), **`RenderPriority = -2`** → der Godot-Alpha-Komparator sortiert
Priority *vor* Tiefe, also liegt jeder Kern im Depth-Buffer, bevor irgendein Blend-Pass läuft.
Ergebnis: ein Saum hinter einem fremden Kern wird in *jeder* Zeichenreihenfolge verworfen; nur noch
Saum-über-Saum entscheidet die Sortierung, und das ist kontrastarm.

**A/B:**
```
pwsh tools/run-client.ps1 -Diag                        # blendcore (Default)
pwsh tools/run-client.ps1 -Diag -FoliageAlpha blend    # Kontrolle: das flackernde v0.22.16-23
pwsh tools/run-client.ps1 -Diag -FoliageCoreAlpha 0.5  # Schwelle (Default 0.33 = Viewer)
```
**Zuerst im Log lesen:** `[AlphaSort] … depthCore=<n>@0.33 foliage=BlendCore` (Logger.Info, alle
15 s). `depthCore=0` bei `foliage=BlendCore` = Mechanismus nicht aktiv (alte DLL? anderer Zweig?),
dann nichts aus dem Bild schließen. Bekannter Preis: wo Rispen überlappen, blendet ein Kern mit
Alpha 0.33–1 seinen (1−α)-Anteil mit dem *Hintergrund* statt mit dem verdeckten Halm — ein
**statischer** Halo, kein wandernder. Höhere Schwelle = weicher, mehr Saum der Sortierung
überlassen.

**Haare (getragen):** separater Pfad (`AvatarRenderer`). Der Viewer schreibt für *rigged* Alpha die
Tiefe **jedes** Fragments, in Attachment-Reihenfolge, vor dem unrigged Alpha
(`lldrawpoolalpha.cpp:240-248`). Derselbe `DepthCore`-next_pass an einem `cull_disabled`-Zwilling
wäre der naheliegende Port. Nicht in dieser Runde.

Verifiziert: beide Builds, 683 Tests, `dotnet format SLNG.sln` clean, Shader-Globals, Selftest
36/36 (`prim_depth_core.gdshader: 33 uniforms`). `AppVersion` v0.22.24-alpha. Nicht committet.

---

> Kopiere alles ab „## Aufgabe" in die andere KI. Der Teil davor ist nur für dich.
>
> **Wichtigste Botschaft an die nächste KI:** die Alpha-**Sortierreihenfolge** ist als Ursache
> *experimentell widerlegt*. Vier Fixes in diese Richtung sind gescheitert. Wer dort weitermacht,
> verbrennt Zeit.

---

## Aufgabe

Du arbeitest am Second-Life-Viewer **SLNG** (C#, Godot 4.7 .NET/Vulkan, Forward+). Repo:
`E:\Git\SLNG`, Branch `fix/BUG-RENDER-16-hifreq-foliage-alpha`.

**Symptom:** Dichtes Ziergras auf der SL-Region *Millenium* (rosa Rispen + grüne Halme) **flackert**
— Halme/Flächen zappeln sichtbar. Firestorm zeigt dieselbe Szene ruhig. Zusätzlich flackern
„manche Haare" (getragene Mesh-Haare, anderer Codepfad).

**Präzises Timing, in-world verifiziert (das ist die wichtigste Beobachtung):**

| Situation | Verhalten |
|---|---|
| Avatar steht still, keine Eingabe | **ruhig** |
| Nur Kamera drehen / orbiten | **ruhig** |
| Avatar **bewegt sich** durch die Szene | **flackert** |

**Der eine harte Hinweis, der übrig ist:** derselbe Content ist flackerfrei, wenn seine Faces auf
`prim_scissor` oder `prim_hash` laufen, und flackert auf `prim_blend`. Umschaltbar zur Laufzeit
per `--foliage-alpha=scissor|blend|hash|prepass|edge|blenddepth`.

Ziel: weiche Firestorm-Kante **und** kein Flackern. `scissor` erfüllt „kein Flackern", wurde aber
in-world abgelehnt, weil die Kante hart/blockig ist und dünne grüne Halme ganz wegfallen.

---

## Was BEWIESEN ausgeschlossen ist — bitte nicht wiederholen

### 1. Die transparente Zeichenreihenfolge ist NICHT die Ursache

Das ist kein Verdacht, das ist ein Experiment. `--alpha-sort-freeze` friert die Sortiertiefe jedes
transparenten Objekts beim ersten Sichtkontakt ein (`ObjectRenderer.TickAlphaSortHysteresis`,
schreibt `GeometryInstance3D.SortingOffset = radial − eingefrorenerWert`). Danach ist die
Reihenfolge **vollständig kameraunabhängig und unveränderlich**.

Log belegt den aktiven Modus: `alphaSortFreeze=True` und
`[AlphaSort] ... 1645 objects / 2323 surfaces ... FROZEN(debug)`.

**Ergebnis: flackert unverändert weiter.** Wenn sich die Reihenfolge nachweislich nicht ändern
kann und es trotzdem flackert, liegt es nicht an der Reihenfolge.

### 2. Vier gescheiterte Fixes, alle in dieser Richtung, alle noch im Code

| Fix | Was er tat | Ergebnis |
|---|---|---|
| Same-Material-Surface-Merge (v0.22.17) | Aufeinanderfolgende Submeshes mit identischem `FaceTexture` → eine Godot-Surface. 195 von 1848 Meshes gemergt (`8->1`, `6->1`) | kein Effekt |
| Alpha-Sort-Hysterese pro Objekt (v0.22.20) | Port von `ALPHA_DIRTY` | kein Effekt (und Designfehler: pro Objekt statt pro Group entsynchronisiert) |
| Alpha-Sort-Hysterese pro 16-m-Zelle (v0.22.21) | Korrigiert, geteilter Blickwinkel pro Zelle | kein Effekt. `refreezes` fällt auf 0–3 pro 15 s, die Reihenfolge war also faktisch eingefroren |
| Planare Tiefe (v0.22.22) | Sortierung nach Blickachsen-Projektion statt radialer Distanz, wie der Viewer | kein Effekt |

Alle vier sind per Flag zuschaltbar und stehen im Code. Sie sind **nicht** der Fix, aber die
Hysterese und die planare Tiefe sind belegte Viewer-Parität und dürfen bleiben.

### 3. Kein Churn im Log

- `GpuUpload`: 4359 Zeilen, **4359 verschiedene** Ids → keine Upload-Schleife.
- `GpuSharpen`: 613 verschiedene Texturen, einzelne bis 6× → normale LOD-Nachlieferung.
- `material.surface`: n=17–45 pro Reporting-Intervall bei 1851 Surfaces → kein szenenweiter
  Materialneubau.

### 4. Bereits früher in-world durchprobiert und verworfen (Details in der Spec)

`hash` (ALPHA_HASH_SCALE 1→4, körnig), `prepass` (`depth_prepass_alpha`, Godots Schwelle ~0.99,
mittlere Alphas erreichen den Prepass nie), `edge` (`ALPHA_ANTIALIASING_EDGE`), `blenddepth`
(`depth_draw_always`, flackert stärker), **TAA** (auf `hash` getestet, verworfen), **FXAA**
(`screen_space_aa=1`, ist als Default drin).

---

## Verifizierte Engine- und Viewer-Fakten (aus der Quelle geholt, nicht geraten)

Damit du das nicht neu herleiten musst:

**Godot 4.7-stable**
- Alpha-Komparator, `servers/rendering/renderer_rd/forward_clustered/render_forward_clustered.h:722-726`:
  `(A->sort.priority == B->sort.priority) ? (A->owner->depth > B->owner->depth) : (A->sort.priority < B->sort.priority)`
  → nur Material-`render_priority` und Instanz-Tiefe. Das Material selbst kommt nicht vor.
- `render_forward_clustered.cpp:961-966`: `inst->depth = cam_origin.distance_to(center)` — **pro
  Instanz**, radial, aus dem AABB-Zentrum. Alle Surfaces einer Instanz vergleichen sich **exakt
  gleich**.
- `core/templates/sort_array.h:202-235`: instabiler Median-of-3-Introsort.

**Second-Life-Viewer** (`scratch/slviewer`, shallow clone — nie `git pull`, nur `fetch --depth 1`)
- `pipeline.cpp:3732`: Alpha-Gruppen werden mit `CompareDepthGreater` über `mDepth` sortiert.
- `llspatialpartition.cpp:684-692`: `mDepth` ist die **Projektion auf die Blickachse** (planar),
  und nur für Gruppen mit `PASS_ALPHA`; der `else`-Zweig behält radial.
- `llspatialpartition.cpp:657-676`: `ALPHA_DIRTY`-Hysterese. `eye` wird **normalisiert**, `0.64`
  ist also eine Sehne auf der Einheitskugel ≈ **37°**, nicht Radiant.
- `pipeline.cpp:3734`: getragenes (rigged) Alpha wird mit `CompareRenderOrder` sortiert — nach
  Attachment-Reihenfolge, **gar nicht nach Tiefe**. Relevant für die Haare.

---

## Meine beste verbleibende Hypothese (ungetestet)

Der Unterschied zwischen den beiden Shadern ist **nicht nur** die Sortierung:

```
prim_blend.gdshader:   render_mode blend_mix, depth_draw_opaque, cull_back, ...;
prim_scissor.gdshader: render_mode blend_mix, depth_draw_opaque, cull_back, ..., alpha_to_coverage;
                       ALPHA_SCISSOR_THRESHOLD = alpha_scissor_threshold;
```

`ALPHA_SCISSOR_THRESHOLD` hält das Material in der **Opaque-Queue mit Depth-Write**, und
`alpha_to_coverage` verwandelt Alpha in eine **MSAA-Coverage-Maske**. Das Projekt läuft mit
`msaa_3d=2` (4×).

**MSAA antialiast bei geblendeter Geometrie praktisch nichts** — sie wirkt auf geometrische Kanten,
nicht auf das Blending-Ergebnis im Inneren. Bei ~2300 transparenten Surfaces mit dünnen,
hochfrequenten Halmen ohne Depth-Write akkumuliert jedes Pixel viele Schichten; schon
Subpixel-Bewegung ändert das Ergebnis stark. Das wäre **temporales Aliasing**, kein Sortierproblem
— und es würde jede Beobachtung erklären, inklusive „ruhig im Stand, flackert beim Laufen" und
„`scissor`/`hash` sind ruhig" (beide gehen über die Opaque-Queue, wo MSAA greift).

Naheliegende Tests in dieser Richtung:
1. **TAA auf dem `blend`-Pfad.** TAA wurde bisher nur auf `hash` getestet und dort zu Recht
   verworfen (Godots Alpha-Hash ist räumlich stabil, TAA hat im Stand nichts zu mitteln). Für
   *bewegte* geblendete Foliage ist TAA aber genau das Standardmittel. **Nie getestet.**
2. `alpha_to_coverage` zusätzlich auf einer Blend-Variante.
3. `msaa_3d` hochdrehen und schauen, ob sich das Flackern proportional ändert — falls ja, ist es
   Sampling, nicht Ordnung.
4. Prüfen, ob Firestorm hier mit anderem AA läuft (FXAA/SMAA/Deferred) — der Vergleich lief bisher
   nicht kontrolliert.

Wenn auch das nichts bringt: die Alternativen sind Mesh-LOD-Wechsel und das
Release/Reload an der Draw-Distance-Grenze. Beide passieren **nur beim Bewegen** und sind noch
nicht untersucht (`visual.update n=851` pro Intervall im Log ist auffällig hoch —
`ObjectRenderer.ReleaseResources` / der Cull-Sweep ab Zeile ~453 wären der Startpunkt).

---

## Repo-Regeln (verbindlich, stehen in `AGENTS.md`)

- `src/` bleibt engine-agnostisch — **kein `using Godot;`** außerhalb von `app/`.
- **`dotnet build SLNG.sln` baut `app/` NICHT.** Beide bauen:
  `dotnet build SLNG.sln` **und** `dotnet build app/SLNG.App.csproj`. Sonst läuft der Client
  gegen die alte DLL.
- .NET 8 SDK liegt unter `%USERPROFILE%\.dotnet` (ein Runtime-only `dotnet` gewinnt sonst den
  PATH). Godot ist als `godot` im PATH.
- Vor jedem Commit: `.claude/skills/slng-verify/SKILL.md` — beide Builds, `dotnet test`,
  `dotnet format SLNG.sln`, `python tools/check_shader_globals.py`,
  `godot --headless --path app -- --selftest` (35/35).
- **`godot --headless` schreibt `app/project.godot` um** und verliert dabei jedes Mal
  `lights_and_shadows/directional_shadow/size=4096`. `git diff app/project.godot` prüfen und den
  Hunk zurücknehmen.
- `AppVersion` in `app/scripts/Boot.cs` bei jeder Änderung patch-bumpen. Stand: `v0.22.23-alpha`.
- Conventional Commits mit Feature-Id: `fix(render): [BUG-RENDER-16] <desc>`.

---

## Werkzeuge, die schon da sind

**Starten:**
```
pwsh tools/run-client.ps1 -Diag [-FoliageAlpha blend|scissor|hash|prepass|edge|blenddepth]
                                [-AlphaSortHysteresis 0.64] [-AlphaSortPlanar] [-AlphaSortFreeze]
```

**Log:** `client-output.log` im Repo-Root. Ohne `-Diag` ist es sehr kurz (die meisten Zeilen sind
`Logger.Debug`) — für jede Diagnose `-Diag` mitgeben.

**Diagnosezeilen:**
- `[AlphaSort] N objects / M surfaces multiSurface=… singleSurface=… within32m=… <modus>`
  — alle 15 s, `Logger.Info`. Zählt live an den Surfaces
  (`GetSurfaceOverrideMaterial(i).Shader`), nicht zur Material-Bauzeit.
- `[PrimMesh] mesh=… N submeshes -> M surface(s): <verdict>` — Surface-Merge pro Mesh.
- `[FaceAlpha] <texid> … -> <Shader>` — pro Textur, welcher Alpha-Pfad gewählt wurde.
- `[WorkCost] <label> n=… avgMs=…` — Main-Thread-Kosten pro Lane.

**Falle, die zwei Runden gekostet hat:** `ApplyAlphaCutout` (entscheidet Blend vs. Scissor) läuft
in einer **nicht abgewarteten** Continuation
(`_ = GetOrCreateGpuTextureAsync(...).ContinueWith(...)` → `MainThreadWorkQueue`), also **nach**
`BuildFaceMaterialAsync`. Wer den Shader zur Material-Bauzeit ausliest, bekommt überall `Opaque`.

---

## Relevante Dateien

| Datei | Inhalt |
|---|---|
| `docs/specs/BUG-RENDER-16-hifreq-foliage-alpha.md` | Vollständige Historie, alle Sackgassen mit Begründung |
| `docs/specs/BUG-RENDER-12-worn-mesh-hair-sort-flicker.md` | Dieselbe Problemklasse am Avatar, dort mit Face-Merge gelöst |
| `app/scripts/ObjectRenderer.cs` | `ApplyAlphaCutout`, `BuildArrayMesh`, `TickAlphaSortCensus`, `TickAlphaSortHysteresis` |
| `app/scripts/PrimShaderFamily.cs` | Shader-Varianten und `Select` |
| `app/materials/prim/prim_blend.gdshader`, `prim_scissor.gdshader`, `prim_hash.gdshader` | die drei Pfade |
| `app/scripts/RenderConfig.cs` | `HighFrequencyFoliageAlpha`, `AlphaSortHysteresis`, `AlphaSortPlanarDepth`, `AlphaSortFreezeDebug` |
| `src/SLNG.Core/FaceSurfaceMerge.cs` + `tests/SLNG.Core.Tests/FaceSurfaceMergeTests.cs` | reiner Merge-Planer, 26 Tests |
| `scratch/slviewer/` | echte Viewer-Quelle zum Nachschlagen |

---

## Was ich mir von dir wünsche

1. **Fang nicht bei der Sortierung an.** Sie ist widerlegt (Punkt 1 oben).
2. **Miss, bevor du baust.** Diese Bug-Historie besteht aus vier quellenbelegten Fixes, die alle
   an der falschen Schicht ansetzten, weil niemand vorher geprüft hat, ob die vermutete Ursache
   überhaupt zutrifft. Der `--alpha-sort-freeze`-Test hat mehr geklärt als drei Fixes zusammen.
3. **Ein Mechanismus pro Runde**, hinter einem Flag, damit in-world A/B'd werden kann. Genau so
   ist die `--foliage-alpha=`-Harness entstanden.
4. Jede Diagnosezeile, die eine Entscheidung tragen soll, gehört auf `Logger.Info` — nicht hinter
   `--diag`. Auch das hat hier eine Runde gekostet.
