# Einstellungen (Settings) Map & Status

Dieses Dokument listet alle geplanten und bereits vorhandenen Viewer-Einstellungen (Preferences) auf. Es orientiert sich an einem schlanken, platzsparenden Layout, ähnlich dem Firestorm Viewer, jedoch optimiert für eine moderne Benutzeroberfläche.

## Kategorien & Status

| Kategorie (Tab) | Gruppe | Einstellung | Status | Bemerkung |
| :--- | :--- | :--- | :--- | :--- |
| **Allgemein** | Sprache | UI-Sprache | ✅ Done | In `Anzeige` integriert |
| | | Inhalts-Einstufung (G/M/A) | ⏸️ Pending | |
| | Namen & Tags | Eigener Name / Gruppen-Titel | ⏸️ Pending | |
| | | Namen anzeigen (Komplexität etc.) | ⏸️ Pending | |
| | Abwesenheit | Zeit bis Away / Auto-Logout | ⏸️ Pending | |
| **Anzeige / UI** | Skalierung | UI-Skalierung (Slider) | ✅ Done | |
| | Toolbars | Toolbar (Fest unten) | ✅ Done | Docking-Optionen entfernt, Toolbar ist jetzt permanent am unteren Rand verankert |
| | Fenster | Transparenz / Hintergrund-Unschärfe | ⏸️ Pending | |
| | Info-Overlays | FPS / Netzwerk-Stats | ✅ Done | Im Top-Menü / Overlay |
| **Grafik** | Allgemein | Sichtweite (Draw Distance) | ⏸️ Pending | |
| | | Max. Partikel | ⏸️ Pending | |
| | | Avatar-Komplexität (JellyDolls) | ⏸️ Pending | |
| | Qualität | Schatten-Qualität | ✅ Done | In `Hardware / Qualität` |
| | | Wasser-Reflektionen | ⏸️ Pending | |
| | | Anti-Aliasing (MSAA) | ✅ Done | In `Hardware / Qualität` |
| | | Render-Thread (Haupt / eigener Thread) | 🧪 Review | In `Hardware`, wirkt erst nach Neustart; Standard: Haupt-Thread. Engine-Einstellung, deshalb nicht in `preferences.cfg`, sondern in `user://engine_overrides.cfg` (FEAT-PERF-13) |
| | | Teure Effekte bei kleinen Grafikkarten begrenzen | 🧪 Review | In `Hardware`, unter dem Texturspeicher; Standard an. Unter 6 GB Grafikspeicher-Budget: MSAA höchstens 2x (unter 4,5 GB aus), kein SSIL, Schattenatlas höchstens 2048, Reflection-Atlas 256 (für die Sitzung), kein Planarspiegel, keine Spiegel-Probe. `preferences.cfg` `low_vram_caps` (FEAT-PERF-25) |
| | Post-FX | Depth of Field (DoF) | ⏸️ Pending | |
| | | Umgebungsverdeckung (SSAO) | ⏸️ Pending | |
| | | Glow / Bloom | ⏸️ Pending | |
| **Kamera & Bewegen** | Kamera | Mouselook aktivieren/anpassen | ⏸️ Pending | |
| | | Sichtfeld (FOV) | ⏸️ Pending | |
| | Bewegen | Doppelklick zum Laufen | ⏸️ Pending | |
| **Sound & Medien** | Lautstärke | Master-Lautstärke | ⏸️ Pending | |
| | | UI-Sounds / Klicks | ⏸️ Pending | |
| | | Umgebungsgeräusche / Wind | ⏸️ Pending | |
| | | Medien & Musik-Stream | ⏸️ Pending | |
| **Chat** | Aussehen | Chat-Bubbles über Avataren | ⏸️ Pending | |
| | | Schriftgröße im Chat | ⏸️ Pending | |
| | Farben | Chat-Farben (Owner, System, Fehler) | ⏸️ Pending | |
| **Netzwerk & Cache**| Cache | Cache-Pfad | ✅ Done | In `Netzwerk` |
| | | Cache-Größe & Leeren | ✅ Done | In `Netzwerk`: ein Knopf für Asset- **und** Objekt-Cache (FEAT-NET-04), Größe getrennt angezeigt |
| | Bandbreite | Max. Bandbreite (UDP/HTTP) | ✅ Done | In `Netzwerk`, 500–10000 kbps, Standard 3000 wie der SL-Viewer (FEAT-NET-05); wirkt auf die UDP-Kategorien, nicht auf HTTP-Abrufe |

## Architektur & Platzersparnis (UI-Konzept)

Um Platz zu sparen und die Übersichtlichkeit zu maximieren (wie im bereitgestellten Firestorm-Beispiel):
- **Vertikale Tabs (Links):** Wie aktuell bereits in `PreferencesWindow.cs` umgesetzt.
- **Scrollbare Container:** Wenn Kategorien wie `Grafik` oder `Allgemein` zu viele Settings haben, wird der Inhalt vertikal gescrollt, um das Fenster nicht künstlich aufblähen zu müssen.
- **Kompakte Controls:** Dropdowns und Schalter (Toggle/Checkbox) statt ausladender Radio-Button-Listen, wo möglich.
- **Mehrspaltiges Layout:** Innerhalb eines Tabs (`HBoxContainer` / `GridContainer`) können Einstellungen nebeneinander angeordnet werden (z. B. Checkboxen untereinander in 2 Spalten, wie bei "Avatarnamen" in Firestorm).
