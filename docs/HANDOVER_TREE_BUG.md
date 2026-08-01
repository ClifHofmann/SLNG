# Handover: Tree linkset "looks like 3 parts, not 1 tree" (fix/broken-mesh-objects)

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

## Offenes Problem: sieht in SLNG wie 3 lose Teile aus, nicht wie 1 Baum

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

## Nächster Schritt (billigster, definitiver Test zuerst)

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
