# Bug: sculpted tree linkset renders as disconnected parts, doesn't match Firestorm

Tracked as [GitHub issue #19](https://github.com/ClifHofmann/SLNG/issues/19). Status: **open,
paused** — every signal checkable from SLNG's own data/logs comes back correct; the remaining gap
needs an independent ground-truth read (see "Suggested next steps" below) or a fresh, self-owned
repro object. Full session-by-session investigation log: `docs/HANDOVER_TREE_BUG.md`.

## Summary

A sculpted-prim tree linkset renders visibly wrong in SLNG: the branch clusters appear as
separate, disconnected shapes floating apart from each other, instead of fusing into one
continuous tree the way the same object renders in Firestorm. Confirmed via a direct top-down
side-by-side comparison against Firestorm from the same viewpoint — not a camera-angle or
lighting illusion.

## Test object

OpenSim region "The Dangazi Forest" (via OSGrid hypergrid), tree linkset, 3 sculpted prims:

| Prim | UUID | Sculpt map | Scale |
|---|---|---|---|
| Root / trunk | `9ee5633a-b360-4d48-86c9-deaae89be0bb` | `18129dff-d039-4ac3-a7ce-bfc786341367` | 20/20/20 |
| Branch (unmirrored) | `caa20d9c-5617-4447-b204-a98e17dfb823` | `44af13fe-bd70-4ab4-bd7f-65fd848eec44` | 16/20/16 |
| Branch (mirrored) | `83d0c134-e575-420f-af51-1251703893ad` | `44af13fe-bd70-4ab4-bd7f-65fd848eec44` (mirror flag) | 8/10/8 |

The reporter does not have modify rights on this object, so its Position/Rotation can't be read
directly from a viewer's Edit/Build floater — all data below came from SLNG's own logging.

## What's already been ruled out

An earlier round on this object found and fixed a real bug: both the sculpt-mesh cache
(`AssetService._inflightSculptMeshes`) and the GPU mesh cache (`ObjectRenderer.KeyForSculpt`)
were keyed only by the sculpt map's UUID, not by `(mapId, sculptType)`. Since the mirrored and
unmirrored branch share one map, the second prim silently got the first prim's (wrong-handedness)
geometry. That fix is merged (PR #18) and confirmed working live (both `mirror=True` and
`mirror=False` now generate distinct geometry for map `44af13fe`, verified via `[SculptDebug]`
console output).

After that fix, the tree **still** renders as disconnected parts. Every other signal checked
comes back correct:

- **Position/rotation resolution**: hand-verified against logged raw/resolved values —
  `resolved_position = root_pos + root_rot.Rotate(child_local_pos)` and
  `resolved_rotation = root_rot * child_local_rot` both match to float precision, for both
  children.
- **Sculpt geometry**: verified byte-exact (max deviation 0.0000) against a ported reference of
  the real SL viewer's `LLVolume::sculptGenerateMapVertices`, including the mirrored branch
  (`ViewerSculptParityTests.OurSculptMesh_MatchesViewerAlgorithm`).
- **Mesh assignment**: no out-of-range indices, `Visible=True`, `Layers=1`, same opaque
  white-tinted texture on all 3 prims, no material/transparency issue.
- **World-space AABB overlap**: computed directly from each `MeshInstance.GlobalTransform` (not
  from the logged position/rotation/scale separately) — all 3 prims' bounding boxes substantially
  overlap; the root's box fully contains both children's.
- **Direct visual test**: forcing each of the 3 prims to a distinct unshaded debug color
  (bypassing texture/lighting entirely) still showed no contact between the three shapes, from a
  clean top-down view matched against the same angle in Firestorm (which shows all 3 fused into
  one tree).

So the bounding boxes overlap but the actual (thin, sparse) branch surfaces inside them don't —
this is a real discrepancy versus Firestorm at the rendered-geometry level, not a logging
artifact, not a caching bug, and not (as far as has been checked) a position/rotation bug.

## Repro

1. Log into OSGrid (`hg.osgrid.org`), region "The Dangazi Forest", near the tree at roughly
   SL-region coordinates `<463, 880, 40>`.
2. Compare the rendered tree against the same object/viewpoint in Firestorm.

## Suggested next steps

- Build a standalone LibreMetaverse-based ground-truth probe (bypassing SLNG's own event pipeline
  entirely) to read this object's raw Position/Rotation directly from the simulator, ruling out a
  LibreMetaverse-level decode issue.
- OR reproduce with a fresh, self-owned test linkset (2+ sculpted prims sharing a mirrored sculpt
  map, positioned to overlap) where the reporter has modify rights and can read exact
  Position/Rotation from Firestorm's own Edit tool for a true apples-to-apples comparison. This is
  the agreed next step regardless, independent of this specific object — see "Vorgehen" below.

---

## Vorgehen: Diagnose-Methodik für "Daten stimmen, Rendering nicht" (wiederverwendbar)

Diese Reihenfolge hat sich für Bugs bewährt, bei denen die Geometrie/Position rechnerisch korrekt
aussieht, das Ergebnis im Client aber trotzdem falsch aussieht. Jeder Schritt schließt eine ganze
Fehlerklasse aus, bevor man zum nächsten geht — nicht raten, sondern schrittweise eingrenzen:

1. **Rohdaten vs. verarbeitete Daten trennen.** Zuerst prüfen, was tatsächlich vom Server /
   von LibreMetaverse ankommt (`raw LocalPosition`/`raw LocalRotation` loggen), getrennt von dem,
   was die eigene Pipeline daraus berechnet (`RESOLVED Position`/`RESOLVED Rotation`). Beide
   Werte in einer Debug-Zeile gegenüberstellen, dann von Hand nachrechnen
   (`resolved = root_pos + root_rot.Rotate(child_local)`, `resolved_rot = root_rot * child_local_rot`)
   um zu bestätigen, dass die eigene Formel intern konsistent ist.

2. **Objekt-UUID, nicht interne IDs, zum Gaten benutzen.** `entity.Id`/ECS-Keys sind intern und
   session-abhängig — nie gegen eine vom User/Viewer kopierte UUID vergleichen. Immer
   `MetadataComponent.Id` (oder das protokoll-eigene Äquivalent) für Debug-Gates verwenden, sonst
   feuert das Logging nie.

3. **Geometrie unabhängig verifizieren.** Wenn möglich einen Referenz-Port des "echten" Algorithmus
   (hier: der reale SL-Viewer-Sourcecode) bauen und die eigene Ausgabe Byte-für-Byte dagegen
   vergleichen (Hausdorff-Abstand o.ä.), nicht nur "sieht plausibel aus".

4. **Render-Zeitpunkt vs. Resolve-Zeitpunkt trennen.** Transform-Resolve und Mesh-Zuweisung laufen
   oft asynchron zueinander (z.B. `CallDeferred`/`await`). Ein Wert, der beim Resolve korrekt
   geloggt wird, kann trotzdem falsch sein, wenn er erst SPÄTER (nach dem Log) nochmal verändert
   wird. Wo möglich, den tatsächlich gezeichneten Zustand direkt an der Zeichenstelle loggen, nicht
   nur den Zwischenwert.

5. **Weltraum-Bounding-Box direkt aus dem Node-Transform berechnen**, nicht aus separat geloggter
   Position/Rotation/Scale ableiten — das schließt aus, dass zwischen "was wir loggen" und "was
   die GPU zeichnet" eine Lücke klafft (z.B. `MeshInstance.GetAabb()` × `GlobalTransform`, alle 8
   Ecken transformieren, Min/Max bilden).

6. **Sichtbarkeits-/Material-Fakten separat prüfen**, auch wenn Geometrie/Position schon bestätigt
   sind: `Visible`, `Layers`, Textur-ID, Farbe/Alpha. Eine korrekte AABB beweist NICHT, dass etwas
   tatsächlich gezeichnet wird — nur dass die Mesh-Ressource die richtige Form/Position hätte.

7. **Der entscheidende Tiebreaker, wenn alles andere stimmt: unshaded Debug-Farben statt Textur.**
   Jedes fragliche Objekt kurzzeitig auf eine grelle, unshaded, eindeutige Farbe zwingen (Textur
   und Licht komplett umgehen) und einen Screenshot aus gleichem Winkel wie die Referenz
   (Firestorm o.ä.) machen. Das entscheidet unwiderlegbar "berühren sich die tatsächlich
   gezeichneten Flächen wirklich" — eine überlappende Bounding-Box bedeutet NICHT, dass sich dünne/
   sparsame Geometrie darin auch wirklich berührt.

8. **Wenn danach immer noch alles auf der eigenen Seite korrekt ist**, braucht es einen von der
   eigenen Pipeline unabhängigen Ground-Truth-Vergleich (z.B. ein eigenständiges Probe-Tool mit der
   gleichen Netzwerk-Bibliothek, das direkt vom Server liest, ohne die eigene Event-Pipeline zu
   durchlaufen) — oder, praktischer: ein selbst erstelltes Testobjekt mit vollen Edit-Rechten bauen,
   um jederzeit exakte Vergleichswerte im Ziel-Viewer ablesen zu können, statt an fremden Objekten
   ohne Modify-Recht zu rätseln.

Alle temporären Debug-Logs aus dieser Session (`TreeLinkDebug`, `TreeRenderDebug`,
`TreeAabbDebug`, `TreeVisDebug`, Debug-Farb-Override) wurden nach Abschluss wieder entfernt (siehe
`docs/HANDOVER_TREE_BUG.md`) — bei Bedarf die Technik neu anwenden, nicht die alten Code-Blöcke
wiederherstellen.
