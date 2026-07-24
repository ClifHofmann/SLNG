# Handover: Tree linkset "looks like 3 parts, not 1 tree" (fix/broken-mesh-objects)

## Update (v0.3.29-alpha) — paused, not solved

A later session (2026-07-24) picked this back up and did two things:

1. **Implemented the hollow-prim end-cap gap** noted as open below (`RepairMissingEndCap`) —
   hollow box/cylinder/prism now get a correctly-triangulated annulus cap at both ends, splitting
   the gathered ring into outer/inner loops by distance-from-centroid and stitching them as a
   strip. Covered by new cases in `PrimBoxFaceDumpTests`. This is DONE and unrelated to the tree.
2. **Re-opened and re-verified the tree case live**, since the user reported it still looked wrong
   after the v0.3.27/v0.3.28 fixes below. Findings, most-recent-first:
   - **The sculpt cache-key fix (item 5 below) IS active and working** — confirmed live via a
     fresh `[SculptDebug]` capture showing BOTH `mirror=True` and `mirror=False` generated
     separately for map `44af13fe` (previously only one line ever appeared).
   - **Every other geometric signal also checks out**: position and rotation resolution
     hand-verified against the logged raw/resolved values (root_pos + root_rot·child_local,
     root_rot·child_rot — both match to float precision); sculpt geometry re-verified byte-exact
     against the ported real-viewer algorithm (`ViewerSculptParityTests`, incl. the mirrored
     branch, max deviation 0.0000); mesh assignment has no out-of-range indices; the three
     prims' world-space AABBs (computed straight from each `MeshInstance.GlobalTransform`,
     `[TreeAabbDebug]`) substantially overlap; `Visible=True`, same opaque white-tinted texture,
     no material/transparency issue (`[TreeVisDebug]`).
   - **Despite all of that, a direct visual test (forcing each of the 3 prims to an unshaded
     debug color — red/green/blue, bypassing texture and lighting) showed the three shapes
     rendered with NO overlap/contact**, confirmed from a clean top-down comparison shot against
     Firestorm (which shows all 3 fused into one tree from the same angle). So the AABBs overlap
     but the actual thin/sparse branch surfaces inside them don't — real discrepancy vs.
     Firestorm, not a logging or camera-angle artifact.
   - Next step proposed but not taken: an independent LibreMetaverse-based probe (bypassing
     SLNG's own event pipeline entirely) to get ground-truth position/rotation for these 3
     UUIDs, since the user doesn't have modify rights on this object and can't read them via
     Firestorm's edit tool. **Decision: paused rather than built** — the user reports fewer
     problems with other content on other sims, so this may be specific to this object/region
     rather than a general SLNG bug. Resume with the probe-tool approach if it recurs elsewhere.
   - All temporary debug logging added during this round (`TreeLinkDebug`, `TreeRenderDebug`,
     `TreeAabbDebug`, `TreeVisDebug`, the debug-color override, plus the pre-existing
     `DebugRenderer`/`DebugSculptMesh`/`DebugPrimMesh`/`DebugMeshAsset`/`SculptDebug`/`AvatarMove`
     from the round below) has been **removed entirely**, not just gated off, for the merge to
     `main`. If this needs re-investigating, re-add targeted logging rather than trying to
     resurrect the old blocks — the technique (gate on the object's real UUID via
     `MetadataComponent.Id`, print resolve-time vs. render-time vs. world-AABB) is documented
     above and worth repeating, not the exact code.

## Historical session (Claude ran out of tokens mid-investigation)

Branch `fix/broken-mesh-objects`, nichts committed. Version
`v0.3.26-alpha`. Build/Test sauber (nur der bekannte, unrelated `RepairMissingEndCap`-Test-Fail).

## Fixed & verifiziert diese Session (nicht anfassen ohne neuen Grund)

1. **Sculpt-Textur-Resize** (`AssetService.DecodeTexture`) — kein Nearest-Neighbor-Preresize mehr
   auf 64×64; native Auflösung geht an den Mesher.
2. **Whole-Mesh-Reject** (`PrimMeshService.Convert`) — ein einzelner Garbage-Vertex verwirft nicht
   mehr das ganze Mesh (verursachte `MESH NULL` bei großen Maps), nur die betroffenen Dreiecke.
3. **Mirror/Invert-Sculpt-Flags** (`GridSession.cs`) — wurden komplett verworfen, jetzt korrekt
   durchgereicht. Hat die Linkset-Zusammensetzung sichtbar repariert.
4. **Wrap-Seam-Bug** (`src/SLNG.Assets/PrimMesher/SculptMesh.cs`) — gegen echten Viewer-Code
   (`scratch/slviewer/indra/llmath/llvolume.cpp:3044`, `sculptGenerateMapVertices`) verifiziert:
   die vendorte Klasse schloss den horizontalen Wrap-Rand abhängig von gerade/ungerader Zeilenzahl
   falsch (überschrieb Spalte 0 mit dem falschen Rand). Gefixt + `PrimMeshService.GenerateSculpt`
   nutzt jetzt diese lokale Klasse statt der nicht patchbaren NuGet-`MeshFoundry`.
5. **Debug-Gate-Bug** — `entity.Id` ist ein interner ECS-Key, NICHT die SL-Objekt-UUID. Immer
   `entity.GetComponent<MetadataComponent>()?.Id` für UUID-Vergleiche nutzen.

## Ergebnis: die "hässlichen Knicke" in den Ästen sind AUTHENTISCH

3 unabhängige Mesh-Implementierungen (alte NuGet, alte lokale, neue gefixte lokale) erzeugen
denselben Knick aus derselben Sculpt-Map. User hat bestätigt: Firestorm zeigt denselben Knick.
**Kein Bug — nicht weiter verfolgen.**

## ✅ GELÖST (v0.3.27-alpha): Sculpt-Mesh-Cache ignorierte den Sculpt-Type

**Ursache: zwei Cache-Kollisionen.** Beide Mesh-Caches waren nur mit der Sculpt-Map-UUID
geschlüsselt, obwohl das Type-Byte (Stitching-Modus + Invert 0x40 + Mirror 0x80) die Geometrie
verändert. Eine Map mit unterschiedlichen Flags mehrfach zu benutzen ist normales SL-Authoring —
genau das tut das Baum-Linkset: `caa20d9c` und `83d0c134` teilen sich Map `44af13fe`, eines
gespiegelt, eines nicht.

1. `AssetService._inflightSculptMeshes` war auf `Guid` geschlüsselt → der zweite Anforderer hängte
   sich an den Task des ersten und bekam dessen Geometrie (Race).
2. `ObjectRenderer.AssignSharedMesh(state, sculptId, …)` benutzte die blanke Map-UUID als
   GpuCache-Key → **deterministisch**: das erste hochgeladene Prim gewann, jedes weitere Prim mit
   derselben Map bekam dessen ArrayMesh, egal welche Flags es selbst hatte. `AssignSharedMesh`
   liefert den Cache-Treffer zurück und schaut die gerade übergebene `MeshData` nie an.
   (Exakt der Fehler, den `KeyForShape` für Prim-LOD bereits im Doc-Kommentar beschreibt.)

**Beweis im Log:** `[SculptDebug]` zeigt 85 Generierungen für nur 70 verschiedene Maps, und für
`44af13fe` **genau eine** Zeile (`mirror=True`) — obwohl zwei Prims sie mit unterschiedlichen
Mirror-Flags benutzen. Das ungespiegelte Prim rendert also die gespiegelte Geometrie.
`MirrorFlag_ProducesDifferentGeometry…` misst dafür 1.0 Einheiten Versatz pro Vertex = die volle
Prim-Breite; bei Scale 16/20/16 sind das bis zu 16 m. Daher „3 lose Teile".

**Fix:** beide Caches auf `(sculptId, sculptType)` geschlüsselt, plus `VisualState.LoadedSculptType`,
damit ein Flag-Wechsel ohne UUID-Wechsel (Build-Tool) einen Reload auslöst.

## ✅ GELÖST (v0.3.28-alpha): jedes Box-Prim war eine offene Schale

Nach dem Cache-Fix sahen Baum **und Mauern** immer noch falsch aus — zu Recht, denn das war ein
zweiter, unabhängiger Bug im Prim-Pfad (nicht Sculpt).

`PrimMesher.cs:2057` erzeugt den End-Cap nur für den **letzten** Path-Node
(`nodeIndex == path.pathNodes.Count - 1`); der erste bekommt nie einen. Messung mit
`tests/SLNG.Assets.Tests/PrimBoxFaceDumpTests.cs`:

| Prim | vorher | nachher |
|---|---|---|
| Box | 5 von 6 Flächen, Face 5 entartet (4 Vertices auf einer Linie, Fläche 0, Normale (0,0,0)) | 6 Flächen, Fläche **6.000** |
| Zylinder | 2 Submeshes — Boden-Cap fehlte **ganz** | 3 Submeshes, Fläche 4.686 = π + 2·π/4 ✓ |
| Prisma | Boden-Cap entartet | beide Caps ✓ |
| Hollow-Box | Boden-Cap fehlt | ✅ gelöst (v0.3.29-alpha), siehe Update oben |

Welche Fläche in Weltkoordinaten fehlt, hängt von der Prim-Rotation ab: eine als dünne Box gebaute
Mauer kann ihre zur Kamera zeigende Fläche verlieren, und mit `CullMode.Disabled` sieht man dann
ins Prim-Innere. Das betrifft **Mauern, Böden, Plattformen, Stufen** — alles was eine Box ist.

`RepairMissingEndCap` war ein deaktivierter Stub (in 6ee14fe gezogen, weil die damalige
Nearest-Vertex-Ring-Suche NaNs produzierte). Die neue Fassung sucht nichts: sie nimmt die
Seitenwand-Vertices, die bereits exakt in der Cap-Ebene liegen, dedupliziert sie wertgleich,
sortiert sie um den Schwerpunkt und triangluiert als Fächer. Jede Koordinate stammt aus einem
bereits vom Mesher emittierten Vertex — es gibt keine Division und keine Normalisierung, aus der
ein NaN entstehen könnte. Abgelehnt wird: Hollow-Prims (Cap ist ein Ring), < 3 Ringpunkte,
entartete Dreiecke.

**Update (v0.3.29-alpha):** Hollow-Prims sind jetzt auch gelöst — `RepairMissingEndCap` bekam
einen zweiten Pfad (`RepairHollowCap`), der den Ring per Abstand-vom-Schwerpunkt in Außen-/
Innenschleife trennt und als Streifen (nicht Fächer) trianguliert. Getestet für Hollow-Box,
-Zylinder, -Prisma in `PrimBoxFaceDumpTests`.

## Ebenfalls geklärt: die Sculpt-Geometrie selbst ist exakt viewer-konform

`tests/SLNG.Assets.Tests/ViewerSculptParityTests.cs` portiert `sculpt_calc_mesh_resolution` +
`LLVolume::sculptGenerateMapVertices` (`llvolume.cpp:3044/3164`) 1:1 nach C# und vergleicht gegen
unseren Mesher auf den **echten gecachten Maps**. Hausdorff-Abweichung **0.0000** für Cylinder-
(inkl. Mirror) und Plane-Sculpts, 0.036 für Sphere (nur Pol-Sampling). Fix 4 (Wrap-Seam) war
richtig. **Die Vertex-Positionen sind kein Bug mehr — nicht erneut untersuchen.**

## Historie: sah in SLNG wie 3 lose Teile aus, nicht wie 1 Baum

Test-Objekt: Baum-Linkset in "The Dangazi Forest", 3 Prims:
- Root `9ee5633a-b360-4d48-86c9-deaae89be0bb` (sculpt `18129dff`, scale 20/20/20)
- `caa20d9c-5617-4447-b204-a98e17dfb823` (sculpt `44af13fe`, scale 16/20/16)
- `83d0c134-e575-420f-af51-1251703893ad` (sculpt `44af13fe`, mirror, scale 8/10/8)

**Verifiziert (4 unabhängige Checks, alle stimmen überein):** Transform-Pipeline ist korrekt.
`[TreeLinkDebug]`-Log (Resolve-Zeit in `WorldSimulation.ApplyObjectUpdate`) und
`[TreeRenderDebug]`-Log (tatsächlicher Godot-Node in `ObjectRenderer.UpdateVisual`) stimmen exakt
überein, stabil über mehrere Frames (kein Drift/Race). Eine manuelle Godot-Nachbildung mit exakt
diesen Zahlen (`scratch`-Ordner, siehe unten) zeigt die 3 Teile **überlappend**, nicht getrennt.

**Also: kein bestätigter Positions-/Rotations-Bug.** Die im Screenshot sichtbare Lücke ist entweder
(a) eine Perspektiven-Täuschung aus diesem einen Kamerawinkel, oder (b) ein echtes, aber sehr
lokales Silhouetten-Mismatch am Berührungspunkt.

## Nächster Schritt: visuelle Bestätigung

Im nächsten Client-Lauf muss `[SculptDebug]` für `44af13fe` **zwei** Zeilen zeigen —
`mirror=True` UND `mirror=False`. Dann erst das temporäre Debug-Logging (unten) entfernen.

## Historische Analyse vor dem Fix (Positions-Pipeline — war korrekt)

1. **AABB-Overlap-Check statt Kamerawinkel-Raten:** In `ObjectRenderer.UpdateVisual`, direkt nach
   `state.MeshInstance.Scale`/Position/Quaternion gesetzt sind, für die 3 UUIDs
   `state.MeshInstance.GetAabb()` (transformiert in Weltkoordinaten) loggen und prüfen, ob sich die
   3 Boxen wirklich schneiden. Rein numerisch, kein Kamerawinkel nötig — sollte in 5 Minuten die
   Frage "berühren sie sich wirklich" endgültig klären.
2. Falls sie sich NICHT schneiden: Scale/Position ist numerisch korrekt (verifiziert), also läge es
   an der tatsächlichen Mesh-GRÖSSE (Bounding-Radius) — z.B. ob `PrimMeshService.GenerateSculpt`
   wirklich einen Unit-Cube [-0.5,0.5] liefert wie dokumentiert, oder ob der lokale `SculptMesh`-Pfad
   (neu seit Fix 4) eine andere Größenordnung erzeugt als die alte NuGet-Version. Schneller Check:
   `mesh.coords` Min/Max in allen 3 Achsen dumpen für beide sculpt-map-Instanzen.
3. Falls sie sich SCHNEIDEN: Es ist eine Kamerawinkel-/Silhouetten-Sache, kein Rendering-Bug —
   dem User das mit einem klar erklärten Fazit zurückmelden statt weiterzusuchen.

## Wichtige Fallstricke (kostet sonst eine ganze Runde)

- `client-output.log` ist **UTF-16** (PowerShell `Tee-Object`). `Get-Content -Encoding Unicode`
  benutzen, kein `cat`/Bash-`grep` — das gibt entweder Fehler oder unlesbaren Text.
- `entity.Id` (ECS-Key) ≠ echte SL-UUID. Immer `MetadataComponent.Id` für UUID-Vergleiche.
- Bash-`grep` wird gelegentlich vom `rtk`-Hook abgeschnitten/umgeleitet — bei verdächtig leerer
  Ausgabe lieber den `Grep`-Tool-Aufruf nutzen statt Bash-`grep`.
- Reproduce-Workflow: `tools\run-client.ps1` baut sauber neu (kein Stale-Assembly-Risiko).

## Aufräumen nicht vergessen, sobald der Fall gelöst ist

`[DebugRenderer]`, `[DebugPrimMesh]`, `[DebugMeshAsset]`, `[DebugSculptMesh]`, `[TreeLinkDebug]`,
`[TreeRenderDebug]`, `[SculptDebug]` — alle klar `TEMPORARY` markiert, in `ObjectRenderer.cs`,
`WorldSimulation.cs`, `AssetService.cs`. `app/assets/images/PurisHG.png` ist gewollt (Boot-Screen-
Hintergrund), nicht anfassen.
