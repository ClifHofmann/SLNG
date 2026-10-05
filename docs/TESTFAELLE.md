# Testfälle für die offenen Themen

Stand: v0.26.77-alpha (2026-10-05). Gilt für die Funktionen, die gebaut, aber noch **nicht im echten Betrieb
bestätigt** sind (Roadmap-Status 🧪 oder 🚧, oder ein „Not yet seen“ im Roadmap-Eintrag).

**Wie testen:** Der Build ist `PurisViewer_Setup_v0.26.77-alpha.exe` aus dem Release oder `build/windows/` (lokaler
Export). Das Protokoll liegt in `%APPDATA%\Godot\app_userdata\Puris Viewer\logs\godot.log` (die Datei der letzten
Sitzung heißt `godot<Zeitstempel>.log`). Ein Testfall ist bestanden, wenn **alle** Erwartungen stimmen. Trage das
Ergebnis in die Spalte *Ergebnis* ein (✅ / ❌ / ⏭ nicht getestet) und bei ❌ die Beobachtung, am besten mit den
letzten Zeilen des Protokolls und einem Bildschirmfoto.

**Konten:** Für TC-GRID und TC-LOG brauchst du denselben Namen auf **Second Life** und auf **OSGrid**
(z. B. „Clifton Howlett“ auf beiden). Für TC-ANGEBOT, TC-RECHTE und TC-PRAESENZ brauchst du ein zweites Konto oder eine zweite Person (am besten mit
Firestorm, damit du die andere Seite siehst).

| Gruppe | Thema | Roadmap | Status |
|---|---|---|---|
| TC-GRID | Daten je Grid getrennt | BUG-GRID-01 | 🚧 |
| TC-LOG | Chat-Protokolle wie Firestorm | FEAT-UI-41 | ✅ |
| TC-KEY | Tastenkürzel und Tastatur-Seite | FEAT-UI-43 | ✅ |
| TC-INV | Inventar öffnet bereit zum Suchen | FEAT-INV-14 | ✅ |
| TC-DPI | Skalierung auf hochauflösendem Bildschirm | FEAT-UI-42 | ✅ (Reste offen) |
| TC-LAND | Land-Info auf OpenSim und Sonderfälle | FEAT-LAND-01…05 | ✅ (Reste offen) |
| TC-ANGEBOT | Teleport- und Freundschaftsanfragen, Sonderfälle | BUG-NET-27/28 | ✅ (Reste offen) |
| TC-AVATAR | „Schaufensterpuppe“ beim Login | BUG-AVATAR-05 | ✅ (nicht nachgestellt) |
| TC-WARN | Engine-Warnungen im vollen Hub | BUG-RENDER-40 | 🚧 |
| TC-EQ | Tote EventQueue | BUG-NET-20 | 🧪 |
| TC-KAT | Freundeskategorien, Ansichts-Schalter, Online-Punkt | FEAT-UI-65 | 🧪 |
| TC-RECHTE | Freundesrechte als Symbole, Anmeldename hinter dem Namen | FEAT-UI-35 | 🧪 |
| TC-REITER | Reiter im Kommunikationsfenster, Ungelesen-Zähler am Chat-Reiter | FEAT-UI-66 | 🧪 |
| TC-CHATFOKUS | Eingabefeld bleibt nach Enter aktiv | BUG-UI-27 | 🧪 |
| TC-PRAESENZ | „… ist online“-Hinweise mit Namen | BUG-UI-28 | 🧪 |

---

## TC-GRID — Daten je Grid getrennt (BUG-GRID-01)

Hintergrund: Derselbe Kontoname auf zwei Grids teilte Dateien (Login-Bild, Objekt-Cache, Kartenkacheln,
Chat-Protokolle). Jetzt liegt alles unter `user://grids/<Grid>/…` (Windows:
`%APPDATA%\Godot\app_userdata\Puris Viewer\grids\`). Die Grid-Kennung für Second Life ist `agni`, für OSGrid
`hg.osgrid.org`.

| ID | Schritte | Erwartung | Ergebnis |
|---|---|---|---|
| TC-GRID-01 | Mit dem Konto auf **OSGrid** einloggen, kurz umsehen, **abmelden**. Danach im Login-Fenster dasselbe Konto auf **Second Life** wählen (Profil-Auswahl). | Das Login-Bild ist **nicht** das des OSGrid-Logins (anfangs das Standardbild, weil für Second Life noch keins gespeichert ist). | ☐ |
| TC-GRID-02 | Mit demselben Namen auf **Second Life** einloggen, umsehen, abmelden. Dann beide Profile abwechselnd im Login-Fenster wählen. | Jedes Profil zeigt **sein eigenes** zuletzt gespeichertes Bild. Es gibt zwei Dateien unter `grids\<Grid>\`. | ☐ |
| TC-GRID-03 | Auf Second Life einloggen, in eine Region wechseln, abmelden. Danach Ordner `grids\agni\cache\objects` und `grids\hg.osgrid.org\cache\objects` ansehen. | Die Objekt-Cache-Dateien liegen **je Grid in ihrem eigenen Ordner**. Der OSGrid-Ordner enthält keine Datei der anderen Seite und umgekehrt. | ☐ |
| TC-GRID-04 | Auf OSGrid die Standardregion (1000,1000) besuchen, dann auf Second Life „Da Boom“ (ebenfalls 1000,1000). Danach wieder OSGrid. | Beim Zurückkehren **verliert keine Seite** ihre gecachten Regionen (Protokoll: `[ObjectCache] … hit=` bei der Rückkehr, nicht „cache empty“). | ☐ |
| TC-GRID-05 | Auf beiden Grids die Weltkarte / Minikarte öffnen. | Die Kartenkacheln gehören zum jeweiligen Grid (kein Bild der anderen Welt). Ordner `grids\<Grid>\cache\maptiles`. | ☐ |
| TC-GRID-06 | Grid A einloggen, Himmel/Wetter beachten, abmelden, **ohne Neustart** Grid B einloggen (eines ohne eigene Umgebungsdaten, z. B. ein lokales OpenSim). | Der Himmel von Grid A bleibt **nicht** stehen; es gilt der Standard-Tageszyklus. | ☐ |
| TC-GRID-07 | Eine Unterhaltung auf Grid A führen, abmelden, auf Grid B einloggen (ohne Neustart). | Der Chat zeigt **nicht** die Reiter und Zeilen von Grid A. | ☐ |
| TC-GRID-08 | Einstellungen → Netzwerk: „Cache leeren“. | Die Größenanzeige umfasst **alle** Grids plus den alten gemeinsamen Ordner; nach dem Leeren zeigt sie ca. 0. | ☐ |

---

## TC-LOG — Chat-Protokolle wie Firestorm (FEAT-UI-41)

Vorbereitung: In Firestorm gibt es für das Konto einen Protokollordner (Beispiel
`…\Firestorm_x64\clifton_howlett` oder ein eigener Ordner wie OneDrive). **Firestorm darf während der Tests nicht
laufen.** Sichere vorher eine Kopie des Ordners.

| ID | Schritte | Erwartung | Ergebnis |
|---|---|---|---|
| TC-LOG-01 | Einstellungen → **Chat-Protokolle** öffnen. | Eine Kontoliste mit deinen gespeicherten Profilen („Name — Second Life“, „— OSGrid“). Für das gewählte Konto: Ordner (oder „SLNGs Standardordner“), „Wählen…“, „Standard verwenden“ und die Zeile „In Kraft“ mit Kontounterordner. | ☐ |
| TC-LOG-02 | Für dein Second-Life-Konto „Wählen…“ → den **Basisordner** wählen, den Firestorm für dieses Konto nutzt (der Ordner, **in dem** `clifton_howlett` liegt). | Der Dialog ist der normale Windows-Ordnerdialog; nach dem Wählen steht der Pfad im Feld und die Zeile „In Kraft“ zeigt `<Basis>\clifton_howlett`. | ☐ |
| TC-LOG-03 | Einloggen, im Chat-Fenster die Unterhaltung mit einer Person öffnen, mit der du in Firestorm geschrieben hast. | Der **alte Verlauf aus Firestorm** erscheint (die letzten Zeilen). Zeitstempel stimmen mit Firestorm überein (SL-Zeit). | ☐ |
| TC-LOG-04 | Verlauf-Fenster (History) für dieselbe Person öffnen. | Der Verlauf lässt sich lesen, auch bei einer großen Datei (kein längeres Einfrieren als etwa eine Sekunde). | ☐ |
| TC-LOG-05 | Einer Person eine Nachricht schicken (oder im Nahbereichs-Chat schreiben). Abmelden. Datei im Kontoordner öffnen (`<Name>.txt` bzw. `chat.txt`). | Die Zeile steht am **Ende** angehängt im Format `[2026/10/03 11:09]  Name: Text` (zwei Leerzeichen nach der Klammer, UTF-8, ohne die älteren Zeilen zu verändern). | ☐ |
| TC-LOG-06 | Danach **Firestorm** starten, mit demselben Konto einloggen, dieselbe Unterhaltung öffnen. | Die von SLNG geschriebene Zeile ist im Firestorm-Verlauf **sichtbar**; ältere Zeilen sind unverändert; Firestorm meldet keinen Fehler. Danach Firestorm schließen. | ☐ |
| TC-LOG-07 | Zurück in SLNG: Unterhaltung öffnen. | Die in Firestorm geschriebene Zeile erscheint im Verlauf von SLNG. | ☐ |
| TC-LOG-08 | Eine **Gruppen**-Unterhaltung führen; Dateiname im Ordner prüfen. | Datei heißt `<Gruppenname> (group).txt` (nicht eine UUID). Ist der Gruppenname beim Öffnen noch nicht bekannt, notiere es (bekannte Schwäche, trennt die Historie von Firestorm). | ☐ |
| TC-LOG-09 | Einstellungen → Chat-Protokolle: Stil der IM-Dateinamen wechseln (`Vorname Nachname.txt` ↔ `vorname_nachname.txt`), neu einloggen, einer Person schreiben. | Die neue Zeile geht in die Datei mit dem **zu Firestorms Einstellung passenden Namen** (bei dir: `vorname_nachname.txt`). Es entsteht keine zweite Datei für dieselbe Person. | ☐ |
| TC-LOG-10 | „Alte SLNG-Logs importieren…“ (nur eingeloggt möglich). | Ein Bestätigungsfenster mit Zahlen; nach „OK“ werden Zeilen **angehängt**, alte Dateien unter `%APPDATA%\SLNG\logs\chat` bleiben **unverändert** vorhanden. | ☐ |
| TC-LOG-11 | Den Import **ein zweites Mal** ausführen. | Es wird **nichts** mehr angehängt (keine doppelten Zeilen). | ☐ |
| TC-LOG-12 | Für die beiden Konten (Second Life / OSGrid) **verschiedene** Ordner wählen, beide einloggen. | Jedes Konto schreibt in **seinen** Ordner; `…\<name>.osgrid` für OSGrid. Der Kontounterordner bei OSGrid heißt `vorname_nachname.osgrid`. | ☐ |
| TC-LOG-13 | „Standard verwenden“ für ein Konto. | „In Kraft“ zeigt wieder `%APPDATA%\SLNG\logs\chat\<Konto>`; der zuvor gewählte Ordner bleibt unberührt. | ☐ |
| TC-LOG-14 | Den Protokollordner ändern und **abmelden**. | Das Abmelden kehrt zum Login-Fenster zurück und hängt nicht (der Fix BUG-NET-29 ist bestätigt; dieser Fall prüft die Kombination). Im Protokoll steht `[Logout] network logout finished …`. | ☐ |

---

## TC-KEY — Tastenkürzel und Tastatur-Seite (FEAT-UI-43)

Vorbereitung: eingeloggt, Fokus **nicht** in einem Textfeld (klick einmal auf die Welt). Gesamte Liste:
`docs/specs/FEAT-UI-43-keybindings.md`.

| ID | Schritte | Erwartung | Ergebnis |
|---|---|---|---|
| TC-KEY-01 | `Strg+I` | Inventar öffnet (siehe TC-INV). | ☐ |
| TC-KEY-02 | `F`, dann `Pos1`, dann `E` | `F` und `Pos1` schalten Fliegen um; `E` startet das Fliegen. | ☐ |
| TC-KEY-03 | `W`, `S`, `A`, `D`, Pfeiltasten | Gehen / Drehen wie bisher. `Umschalt` + `W` = Rennen. | ☐ |
| TC-KEY-04 | `Strg` gedrückt halten, `W` drücken | Der Avatar läuft **nicht** (Modifier müssen exakt passen). | ☐ |
| TC-KEY-05 | `Alt` halten, `A`/`D` | Kamera dreht sich um den Fokus, der Avatar bewegt sich nicht. | ☐ |
| TC-KEY-06 | `Alt` halten, `W`/`S` | Kamera zoomt hinein / hinaus. | ☐ |
| TC-KEY-07 | `Alt` halten, `E`, dann `C` | Kamera kreist über, dann unter den Fokus. Richtung wie erwartet? (sonst notieren) | ☐ |
| TC-KEY-08 | `Strg+Alt+Umschalt` halten, `W`/`A`/`S`/`D` oder Pfeile | Kamera verschiebt sich (Pan). Tempo angenehm? (geschätzt 1,5 m/s) | ☐ |
| TC-KEY-09 | `=` und `-` | Kamera-Abstand ändert sich. In einem **Textfeld** getippt zoomt `-` **nicht**. | ☐ |
| TC-KEY-10 | `Esc` | Kamera wird zurückgesetzt. | ☐ |
| TC-KEY-11 | Der Reihe nach: `Strg+P`, `Strg+T`, `Strg+H`, `Strg+Shift+F`, `Strg+Shift+G`, `Strg+M`, `Strg+Shift+M`, `Strg+Shift+S`, `Strg+K`, `Alt+Shift+N`, `Strg+Alt+H` | Es öffnen sich: Einstellungen, Kommunikation, Nahbereichs-Chat, Freunde, Gruppen, Weltkarte, Minikarte, Schnappschuss, Kamera-Steuerung, Benachrichtigungen, Höhenanpassung. Zweiter Druck schließt bzw. holt das Fenster nach vorn. | ☐ |
| TC-KEY-12 | Zwei Fenster öffnen, `Strg+W`, dann `Strg+Shift+W` | Zuerst das oberste Fenster zu, dann alle. | ☐ |
| TC-KEY-13 | `Strg+Shift+U` zweimal | Oberfläche wird ausgeblendet, dann wieder eingeblendet. | ☐ |
| TC-KEY-14 | `Strg+0`, `Strg+8`, `Strg+9` | Sichtfeld zoomt hinein / hinaus / zurück auf Standard. | ☐ |
| TC-KEY-15 | `Strg+R` | „Immer laufen“ schaltet um; die Meldung zeigt das **aktuelle** Kürzel (nicht fest „Strg+R“). | ☐ |
| TC-KEY-16 | `Alt+Shift+S`, `Alt+Shift+A` | Sitzen/Aufstehen; laufende Animationen stoppen. | ☐ |
| TC-KEY-17 | `Enter` (ohne Fokus im Feld) | Der Chat-Eingabe bekommt den Fokus. | ☐ |
| TC-KEY-18 | Im Chat-Eingabefeld tippen: `w a s d e c`, `Strg+C`, `Strg+V`, `Strg+A`, `Strg+Z` | Der Avatar läuft **nicht**; die Textbefehle wirken im Feld. `Strg+I` aus dem Feld öffnet weiterhin das Inventar. | ☐ |
| TC-KEY-19 | `Strg+Q` | Das Beenden-Verfahren startet (Abmelden-Bildschirm, Beenden). Teste erst, wenn alles andere erledigt ist. | ☐ |
| TC-KEY-20 | Einstellungen → **Tastatur** öffnen. | Aktionen nach Kategorie (Bewegung, Kamera, Fenster, Avatar, Ansicht, Chat, Bearbeiten, Entwickler), Suchfeld, aktuelle Tasten. Alle Texte in der gewählten Sprache. | ☐ |
| TC-KEY-21 | Suchfeld: „Inventar“ eingeben. | Nur passende Aktionen bleiben sichtbar. | ☐ |
| TC-KEY-22 | Bei „Inventar“ eine Taste anklicken, `Strg+B` drücken. | Ab sofort öffnet `Strg+B` das Inventar, `Strg+I` nicht mehr. Die Menü-Hinweise (Tooltips) zeigen die neue Taste. | ☐ |
| TC-KEY-23 | Bei einer anderen Aktion `Strg+B` vergeben. | Warnung mit Name der Aktion, die die Taste schon hat; Auswahl **Ersetzen / Abbrechen** funktioniert. | ☐ |
| TC-KEY-24 | Beim Umbelegen `Esc` drücken. | Das Zuhören wird abgebrochen; `Esc` selbst lässt sich nicht als Taste vergeben. Beim Umbelegen läuft der Avatar nicht los. | ☐ |
| TC-KEY-25 | `+` bei einer Aktion (zweite Taste), `×` (Taste entfernen), Pfeil „zurücksetzen“, „Alle zurücksetzen“ (zwei Klicks). | Jede Aktion verhält sich wie beschriftet; nach „Alle zurücksetzen“ gelten wieder die Standardtasten. | ☐ |
| TC-KEY-26 | Eine Taste ändern, SLNG **neu starten**. | Die Änderung gilt weiter. In `preferences.cfg` steht im Abschnitt `key_bindings` nur die Abweichung (z. B. `window.inventory="Ctrl+B"`). Nach „Alle zurücksetzen“ ist der Abschnitt leer. | ☐ |
| TC-KEY-27 | Ein Game-Controller (falls vorhanden): D-Pad / Stick. | Er läuft **nicht** mehr (bekannte Änderung, ungetestet). Nur notieren. | ☐ |
| TC-KEY-28 | Handbuch, Abschnitt „Tastenkürzel“ und „Noch nicht verfügbare Tastenkürzel“. | Die Liste entspricht dem, was in TC-KEY-01…19 funktioniert. Die Liste der fehlenden Kürzel nennt den Grund auf Deutsch. | ☐ |

---

## TC-INV — Inventar öffnet bereit zum Suchen (FEAT-INV-14)

| ID | Schritte | Erwartung | Ergebnis |
|---|---|---|---|
| TC-INV-01 | Inventar über das **Menü** öffnen (geschlossen). | Fenster **offen, nicht eingeklappt**, im Vordergrund, der **Cursor blinkt im Suchfeld**. Sofort tippen („hose“) füllt das Suchfeld. | ☐ |
| TC-INV-02 | Inventar über die **Werkzeugleiste** öffnen. | wie TC-INV-01 | ☐ |
| TC-INV-03 | Inventar schließen, `Strg+I`. | wie TC-INV-01 | ☐ |
| TC-INV-04 | Inventar mit dem „_“-Knopf **einklappen**, dann `Strg+I`. | Das Fenster klappt **aus**, kommt nach vorn, Cursor im Suchfeld (und schließt sich nicht). | ☐ |
| TC-INV-05 | Inventar einklappen, dann Menü / Werkzeugleiste. | wie TC-INV-04 | ☐ |
| TC-INV-06 | Inventar offen, Fokus im Suchfeld, `Strg+I`. | Das Fenster **schließt** sich. | ☐ |
| TC-INV-07 | Inventar offen, ein anderes Fenster liegt davor (Fokus dort), `Strg+I`. | Das Inventar kommt **nach vorn**, Cursor im Suchfeld; es schließt sich **nicht**. | ☐ |
| TC-INV-08 | `Strg+O` (Outfits), auch mit eingeklapptem Fenster. | Der Reiter „Outfits“ ist offen, Fenster ausgeklappt, Cursor im Suchfeld. | ☐ |
| TC-INV-09 | Inventar öffnen, im Suchfeld einen Begriff stehen lassen, schließen, wieder öffnen. | Verhalten stimmig (Begriff erhalten oder gelöscht – notiere, was passiert; Cursor im Feld in jedem Fall). | ☐ |
| TC-INV-10 | Beim Tippen im Suchfeld die Tasten `w a s d`. | Der Avatar läuft **nicht**. | ☐ |

---

## TC-DPI — Skalierung auf hochauflösendem Bildschirm (FEAT-UI-42, Reste)

Vorbereitung: Laptop mit Windows-Skalierung 150 % oder 200 %. Einstellungen → Anzeige.

| ID | Schritte | Erwartung | Ergebnis |
|---|---|---|---|
| TC-DPI-01 | Frische Einstellungen (oder Haken „Automatisch“ setzen), starten. | Login-Bildschirm, Menüs, Tooltips und Schrift sind **sofort in lesbarer Größe**; Anzeige „Display meldet 1.5x/2.0x“ und „Automatisch (folgt dem Display)“ ist angehakt. | ☐ |
| TC-DPI-02 | Login-Fenster verkleinern (kleines Fenster, z. B. 1152×648) bei Skalierung 2,0. | Das Login-Panel kann **abgeschnitten** sein (bekannte Grenze). Notiere, ob Anmelden trotzdem möglich ist. | ☐ |
| TC-DPI-03 | Eingeloggt: alle HUD-Teile ansehen (obere Leiste, untere Leiste, Statistik, Chat, Radar, Namensschilder). | Alles lesbar und **scharf**, nichts überlappt oder liegt außerhalb des Bildschirms. | ☐ |
| TC-DPI-04 | Ein **getragenes HUD** (Attachment am HUD-Punkt) ansehen, darauf klicken. | Es ist scharf (kein Verwaschen); ein Klick trifft das richtige Element. | ☐ |
| TC-DPI-05 | Das 3D-Bild beachten. | Die 3D-Welt ist weiterhin **in voller Auflösung** (nicht weicher als vorher). | ☐ |
| TC-DPI-06 | Einstellungen → Anzeige: Haken „Automatisch“ entfernen, Regler ziehen (Loslassen wendet an). | Die Oberfläche skaliert live; die gespeicherte Skalierung bleibt nach Neustart. Dein früherer Wert (125 %) bleibt als **manueller** Wert erhalten. | ☐ |
| TC-DPI-07 | Fenster auf einen **zweiten Bildschirm mit anderer Skalierung** ziehen (falls vorhanden), „Automatisch“ an. | Nach bis zu etwa einer Sekunde passt sich die Skalierung an. (nur notieren, falls es nicht klappt) | ☐ |
| TC-DPI-08 | Fenster einer **Spiegel/Mini-Karte/Schnappschuss**-Funktion benutzen. | Größenangaben im Schnappschuss entsprechen **physischen Pixeln**; keine verschobene Mausauswahl in der 3D-Welt (Klick auf Objekt trifft das richtige Objekt). | ☐ |

---

## TC-LAND — Land-Info auf OpenSim und Sonderfälle (FEAT-LAND-01…05)

Menü Welt → „Über das Land…“. Beim Wechsel der Parzelle folgt das Fenster.

| ID | Schritte | Erwartung | Ergebnis |
|---|---|---|---|
| TC-LAND-01 | **OpenSim:** auf einer Parzelle, **Allgemein**-Tab. | Name, Besitzer, Beschreibung, Fläche stimmen. Verkehr zeigt „0“ oder „–“ (OpenSim liefert ggf. 0). Kein Absturz, keine leere Maske. | ☐ |
| TC-LAND-02 | **OpenSim:** Tab **Vertrag**. | Entweder der Vertragstext, „Kein Vertrag“ oder eine klar benannte Störung („konnte nicht geladen werden“); **nie** ein stilles Leer. | ☐ |
| TC-LAND-03 | **OpenSim:** Tab **Objekte** auf **eigenem** Land, dann auf **fremdem**. | Eigenes Land: Zähler und Besitzerliste. Fremdes Land: nach ca. 10 s „Sim hat nicht geantwortet“ (OpenSim antwortet ohne Recht gar nicht). | ☐ |
| TC-LAND-04 | **OpenSim:** Tab **Optionen**, **Medien**, **Sound**. | Die Häkchen entsprechen den Einstellungen im Besitzer-Fenster (Firestorm); alles schreibgeschützt. | ☐ |
| TC-LAND-05 | **Second Life:** Parzelle mit **Medien** und **Musik** (z. B. Club, Strand). Tabs Medien und Sound. | Typ, Adresse, Beschreibung, Größe (nur bei Webinhalt), Wiederholen (nur bei Video/Audio) und die Musik-URL erscheinen. | ☐ |
| TC-LAND-06 | **Second Life:** Parzelle mit **Gruppen-Objekten** (Tab Objekte). | Gruppen-Besitzer haben die Art „Gruppe“. Die Sortierung nach Anzahl ist absteigend. | ☐ |
| TC-LAND-07 | **Second Life:** Tab Objekte auf Land, wo du **kein Recht** hast. | Entweder die Liste, „nicht aufgelistet“ oder „nicht geantwortet“ – **nicht** „keine Objekte“, wenn die Zähler Objekte zeigen. | ☐ |
| TC-LAND-08 | Eine **große** Parzelle mit mehr als ca. 54 Besitzern. | Die Liste ist vollständig oder wächst nach (mehrere Antwortpakete). Notiere, ob Besitzer fehlen. | ☐ |
| TC-LAND-09 | Tab **Sound**, Zeile „MOAP auf diese Parzelle beschränken“. | Steht als „unbekannt“ **ohne Häkchenfeld** (kann nicht gelesen werden), nie als „aus“. Kein senkrecht umbrechender Text. | ☐ |
| TC-LAND-10 | Zwischen Parzellen laufen, Fenster offen lassen. | Die Anzeige folgt der Parzelle; Tab **Objekte** fragt nur neu, wenn er gerade sichtbar ist. | ☐ |
| TC-LAND-11 | Region mit **Vertrag** auf Second Life, Tab Vertrag. | Text, „zuletzt geändert“ in **deiner Ortszeit** (so macht es Firestorm), Estate-Name und Besitzer. | ☐ |

---

## TC-ANGEBOT — Teleport- und Freundschaftsanfragen, Sonderfälle (BUG-NET-27 / -28)

Zweites Konto oder eine zweite Person nötig.

| ID | Schritte | Erwartung | Ergebnis |
|---|---|---|---|
| TC-ANGEBOT-01 | Teleport-**Angebot** schicken lassen, annehmen, dann ein zweites Mal ablehnen. | Fenster „Teleport-Angebot“ mit den Knöpfen *Teleportieren* / *Ablehnen*; Annehmen teleportiert mit normaler Fortschrittsanzeige. | ☐ |
| TC-ANGEBOT-02 | Ein Teleport-Angebot **erst nach einiger Zeit** (z. B. nach 10 Minuten oder nachdem der Anbieter gegangen ist) annehmen. | Eine klare Fehlermeldung, **kein Hängen**, keine zweite Teleport-Anzeige. | ☐ |
| TC-ANGEBOT-03 | Teleport-**Anfrage** (jemand möchte zu dir) bekommen. | Fenster mit *Teleport anbieten* / *Ablehnen*; „Anbieten“ schickt der Person einen Teleport. | ☐ |
| TC-ANGEBOT-04 | Freundschaftsanfrage erhalten, annehmen. | Der Freund erscheint **ohne Neustart** in der Freundesliste. | ☐ |
| TC-ANGEBOT-05 | Anfrage erhalten, ablehnen. | Sie verschwindet; der Absender erfährt nichts (so macht es der Viewer). | ☐ |
| TC-ANGEBOT-06 | Anfrage **an eine offline Person**, die später annimmt (oder umgekehrt: eine Anfrage erhalten, während du offline warst, dann einloggen und annehmen). | Ablauf funktioniert. Wenn Annehmen scheitert: Protokoll notieren (Verdacht: fehlender Parameter `agent_name` bei der Offline-Route). | ☐ |
| TC-ANGEBOT-07 | Eigene Anfrage an jemand, der annimmt. | Eintrag in Benachrichtigungen („… hat dein Freundschaftsangebot angenommen“). | ☐ |
| TC-ANGEBOT-08 | Jemand entfernt dich in Firestorm als Freund. | Der Freund **verschwindet** aus deiner Liste; Eintrag unter **System** („… hat dich als Freund entfernt“). Entfernst du selbst jemanden, kommt **keine** Meldung. | ☐ |
| TC-ANGEBOT-09 | Profil eines Freundes öffnen. | Knopf „Freund entfernen“ (statt „Befreundet ✓“); nach Bestätigung ändert er sich auf „Freund hinzufügen“. | ☐ |
| TC-ANGEBOT-10 | Von einer **stummgeschalteten** Person ein Angebot erhalten. | Bekannte Lücke: SLNG lehnt nicht automatisch ab, wie es der Viewer täte. Nur notieren, was passiert. | ☐ |
| TC-ANGEBOT-11 | IM an einen **Offline-Freund** schicken (Second Life). | Genau **eine** Meldung („User not online…“ vom Grid). Bei einem Grid, das schweigt (OpenSim ohne Offline-Modul), erscheint nach etwa 2,5 s SLNGs eigene Meldung. | ☐ |

---

## TC-AVATAR — „Schaufensterpuppe“ beim Login (BUG-AVATAR-05)

Der Fehler wurde nicht nachgestellt, nur aus dem Protokoll erschlossen. Beobachten, nicht erzwingen.

| ID | Schritte | Erwartung | Ergebnis |
|---|---|---|---|
| TC-AVATAR-01 | **Mehrfach** (5-mal) auf einer vollen Region (Millenium-Hub) einloggen, besonders mit leerem Objekt-Cache (Einstellungen → Netzwerk: Cache leeren) und mit einer langsamen Verbindung. | Der eigene Avatar hat **Haut, Augen und Haare** (keine weiße Puppe). | ☐ |
| TC-AVATAR-02 | Bei Fehlschlag: Das Protokoll nach `[SelfBake] channels` durchsuchen. | Soll nach der Zeile mit `(null)` eine Zeile **mit Kanalnummern** (`8=…`) folgen. Fehlt sie, Protokoll und Zeit bis zum Erscheinen des Avatars notieren und das Ticket neu öffnen. | ☐ |

---

## TC-WARN — Engine-Warnungen im vollen Hub (BUG-RENDER-40)

Ziel ist ein Protokoll, das die Ursache nennt.

| ID | Schritte | Erwartung | Ergebnis |
|---|---|---|---|
| TC-WARN-01 | Auf Millenium am **Ankunftspunkt** (Kamera bei etwa 133/120/28, viele Avatare) einloggen und etwa eine Minute stehen bleiben. | Das Protokoll enthält **keine** oder nur wenige Zeilen „Vector3 cannot be normalized“. Ist ein Schwall da, steht dazu `[NonFiniteScan]` (mit Zählung nach Knotenklasse). | ☐ |
| TC-WARN-02 | Falls Warnungen auftraten: aus dem Protokoll die Zeilen `[EngineWarnings]`, `[NonFiniteScan]`, `[MeshGuard]` und `[ParticlesGuard]` kopieren und senden. | Damit ist die Ursache bestimmbar („viele CpuParticles3D“ = Partikel; „Skeleton3D“ = Knochen; „nichts gefunden“ = Tangenten in der Engine). | ☐ |

---

## TC-EQ — Tote EventQueue (BUG-NET-20)

Tritt nur auf, wenn eine **Nachbarregion** neu startet, während du verbunden bleibst (oder eine eigene Region
mit Estate-Rechten ohne Abmeldung). Kein künstlich herstellbarer Test; nur beobachten.

| ID | Schritte | Erwartung | Ergebnis |
|---|---|---|---|
| TC-EQ-01 | Bei einem passenden Ereignis das Protokoll nach `[EventQueue] … not recovering` suchen. | Genau **eine** solche Zeile (kein Schwall von 65 Wiederholungen) und im Client eine Meldung, dass die Ereigniswarteschlange tot ist. | ☐ |

---

## TC-KAT — Freundeskategorien, Ansichts-Schalter, Online-Punkt (FEAT-UI-65)

Reiter **Freunde** im Kommunikationsfenster. Am besten mit mindestens fünf Freunden, davon zwei online.

| ID | Schritte | Erwartung | Ergebnis |
|---|---|---|---|
| TC-KAT-01 | Ohne vorhandene Kategorie die Liste ansehen. Dann das **+** neben dem Filterfeld drücken, „Familie“ eingeben, bestätigen. | Vorher eine einfache Liste ohne Überschriften. Danach gibt es die Überschrift **Familie** mit „(0/0)“, darunter (neu) **Ohne Kategorie** mit allen Freunden. | ☐ |
| TC-KAT-02 | Rechtsklick auf einen Freund → **Familie**. Dasselbe über den Knopf **Kategorie…** rechts. Dann Rechtsklick → **Neue Kategorie…** → „Arbeit“. | Der Freund steht unter der gewählten Kategorie (im Menü ist die aktuelle abgehakt). „Neue Kategorie…“ legt „Arbeit“ an und sortiert den Freund **gleich** hinein. **Keine Kategorie** nimmt ihn wieder heraus. | ☐ |
| TC-KAT-03 | Auf die Überschrift einer Kategorie klicken. Erneut klicken. Abmelden, wieder einloggen. | Klick klappt zu (▶) und auf (▼); hinter dem Namen steht „(online/gesamt)“. Der Zustand ist nach dem Neustart **derselbe**. | ☐ |
| TC-KAT-04 | Rechtsklick auf die Überschrift → **Umbenennen…** (neuer Name), dann nochmal mit dem Namen einer **anderen** Kategorie. | Der erste Name wird übernommen, die Freunde bleiben darin. Beim doppelten Namen kommt die Abfrage **zurück** und sagt, dass es den Namen schon gibt. | ☐ |
| TC-KAT-05 | Rechtsklick auf die Überschrift → **Kategorie löschen**, erst abbrechen, dann bestätigen. | Abbrechen ändert nichts. Bestätigen entfernt die Kategorie; ihre Freunde bleiben in der Liste unter **Ohne Kategorie**. | ☐ |
| TC-KAT-06 | Drei Kategorien anlegen. Eine Überschrift am **Griff** (sechs Punkte links) auf eine andere ziehen — einmal nach oben, einmal nach unten. Auch am Namen ziehen. | Der Griff ist sichtbar, der Mauszeiger wird zur Hand. Beim Ziehen hängt eine kleine blaue Karte am Zeiger, und an der Zielüberschrift zeigt ein heller Balken **oben** (landet darüber) oder **unten** (landet darunter). Nach dem Loslassen steht die Kategorie dort; die Reihenfolge bleibt nach einem Neustart. | ☐ |
| TC-KAT-07 | Eine Kategorie auf **Ohne Kategorie** ziehen. | Sie rutscht an das **Ende** der Kategorien. Ist sie schon die letzte, passiert nichts und es erscheint kein Balken. | ☐ |
| TC-KAT-08 | Rechtsklick auf eine Überschrift → **Nach oben** / **Nach unten**. | Verschiebt um einen Platz. Bei der obersten ist „Nach oben“, bei der untersten „Nach unten“ ausgegraut. | ☐ |
| TC-KAT-09 | Kästchen **Kategorien anzeigen** ausschalten, wieder einschalten. Ausgeschaltet eine neue Kategorie mit **+** anlegen. | Aus: eine einfache Liste ohne Überschriften (online zuerst), die Einteilung bleibt erhalten. Beim Anlegen einer Kategorie schaltet sich das Kästchen **von selbst** wieder ein. | ☐ |
| TC-KAT-10 | Kästchen **Nur Online anzeigen** einschalten. Eine Kategorie zuklappen, dann das Kästchen aus- und wieder einschalten. Danach (mit einem zweiten Konto) alle Freunde offline lassen. | Offline-Freunde und Kategorien ohne jemanden online verschwinden; zugeklappte bleiben zugeklappt. Ist niemand online, steht dort „Gerade ist keiner deiner Freunde online.“ Der Zustand bleibt nach einem Neustart. | ☐ |
| TC-KAT-11 | Etwas ins **Filterfeld** tippen (Teil eines Namens), auch mit einer zugeklappten Kategorie, die einen Treffer enthält. | Nur Kategorien mit Treffern sind sichtbar, **alle aufgeklappt**. Nach dem Löschen des Textes ist der alte Zustand zurück. Ein Klick auf eine Überschrift tut währenddessen nichts. | ☐ |
| TC-KAT-12 | Mit einem **zweiten Konto** auf demselben Rechner einloggen. | Dieses Konto hat **keine** Kategorien des ersten; was dort angelegt wird, erscheint beim ersten nicht. | ☐ |
| TC-KAT-13 | Den **Online-Punkt** vor einem Freund ansehen, die Maus darüber halten. | Ein deutlich sichtbarer Kreis (grün = online, grau = offline) in der Größe der Symbole daneben; der Hinweis sagt „Online“ / „Offline“. | ☐ |

---

## TC-RECHTE — Freundesrechte als Symbole, Anmeldename hinter dem Namen (FEAT-UI-35)

Zweites Konto nötig, am besten mit **Firestorm**, um die Gegenseite zu sehen. Die drei Rechte heißen: **Online** (Auge),
**Karte** (Pin), **Edit** (Stift). Blau = was **du** dem Freund erlaubst (anklickbar), grün = was der Freund **dir**
erlaubt (nicht anklickbar). Ein eingeschaltetes Recht ist farbig, ein ausgeschaltetes ein blasses Symbol.

| ID | Schritte | Erwartung | Ergebnis |
|---|---|---|---|
| TC-RECHTE-01 | Die Freundesliste ansehen, mit der Maus über die Spaltenköpfe fahren; viele Freunde, sodass die Liste scrollt. | Fünf Spalten rechts: unter **Freund darf…** Online/Karte/Edit, unter **Ich darf…** Karte/Edit. Die Symbole stehen genau **unter** ihren Köpfen, auch mit Scrollleiste. Jeder Kopf hat einen Hinweis. | ☐ |
| TC-RECHTE-02 | Das blaue **Karte**-Symbol eines Freundes anklicken. In Firestorm beim Freund nachsehen (Freundesliste → dein Name → Rechte). | Das Symbol wird blau, und beim Freund steht, dass er dich **auf der Karte** sehen darf. Nochmal anklicken nimmt es zurück. | ☐ |
| TC-RECHTE-03 | Dasselbe mit **Online** (Auge). | Der Freund darf deinen Online-Status sehen / nicht mehr sehen. (Nicht das „Karte“-Recht verwechseln.) | ☐ |
| TC-RECHTE-04 | Das blaue **Edit**-Symbol anklicken, erst **abbrechen**, dann nochmal und bestätigen. | Es kommt eine Rückfrage („… erlauben, deine Objekte zu bearbeiten …“). Abbrechen lässt das Symbol blass. Bestätigen färbt es, und beim Freund steht, dass er deine Objekte bearbeiten darf. **Zurücknehmen fragt nicht.** | ☐ |
| TC-RECHTE-05 | **Richtung prüfen:** Einem Freund nur das Recht **Karte** geben, sonst nichts. In Firestorm bei diesem Freund nachsehen. | Dort ist **nur** „darf mich auf der Karte sehen“ gesetzt — nicht Online, nicht Edit. In **deiner** Liste ist nur das blaue Pin an. | ☐ |
| TC-RECHTE-06 | Der Freund gibt dir in Firestorm das Recht, ihn auf der Karte zu sehen. | Das grüne **Karte**-Symbol unter **Ich darf…** geht **ohne Neustart** an, und im Benachrichtigungsfenster steht unter **System** „… : Du siehst jetzt auf der Karte, wo diese Person ist.“ | ☐ |
| TC-RECHTE-07 | Der Freund nimmt dir ein Recht wieder weg. | Das grüne Symbol wird blass; Eintrag unter **System** („… nicht mehr …“). Eigene Änderungen an den blauen Symbolen lösen **keinen** solchen Eintrag aus. | ☐ |
| TC-RECHTE-08 | Ein **grünes** Symbol anklicken, mit der Maus darüber halten. | Klick tut nichts; der Hinweis erklärt das Recht und sagt, dass nur der Freund es ändern kann. | ☐ |
| TC-RECHTE-09 | Zwei verschiedene Rechte desselben Freundes kurz hintereinander umstellen. Abmelden, wieder einloggen. | Nach dem Neustart sind **beide** Änderungen noch da (keine hebt die andere auf). | ☐ |
| TC-RECHTE-10 | Einen Freund mit Anzeigenamen ansehen; dann die Maus über den Namen halten; dann unter `Einstellungen` → `Anzeige` die Option für Benutzernamen ausschalten. | Der Anmeldename steht **hinter** dem Anzeigenamen, blass, in Klammern, auf derselben Zeile. Ausgeschaltet steht er nur im Hinweis. Eckige Klammern im Namen werden **nicht** als Formatierung gelesen. | ☐ |

---

## TC-REITER — Reiter im Kommunikationsfenster, Ungelesen-Zähler am Chat-Reiter (FEAT-UI-66)

| ID | Schritte | Erwartung | Ergebnis |
|---|---|---|---|
| TC-REITER-01 | Nacheinander auf **Symbol**, **Namen** und den **Rand** eines Reiters (Chat / Freunde / Gruppen / Verlauf) klicken. | Jeder Klick wechselt den Reiter. Unter der Maus wird der Reiter heller, der Mauszeiger ist die Hand. | ☐ |
| TC-REITER-02 | Reiter **Freunde** öffnen. Jemand schreibt im **Umgebungschat** (Main ausgewählt). | Am Reiter **Chat** erscheint eine **rote Zahl**; Symbol und Name sind warm gefärbt. | ☐ |
| TC-REITER-03 | Zurück auf **Chat** klicken. | Die Zahl verschwindet, sobald die Nachricht zu sehen ist. | ☐ |
| TC-REITER-04 | Eine IM-Unterhaltung auswählen, dann **Freunde** öffnen; die Person schreibt. | Der Chat-Reiter zählt die Nachricht (auch für die ausgewählte Unterhaltung). Beim Zurückwechseln ist sie gelesen. | ☐ |
| TC-REITER-05 | Mehr als neun ungelesene Nachrichten sammeln. | Es steht „9+“. Die Zahl entspricht der am Chat-Knopf in der Symbolleiste. | ☐ |
| TC-REITER-06 | Das Fenster **schließen** (oder minimieren), währenddessen schreibt jemand in der **ausgewählten** Unterhaltung. | Unverändert zu früher: Diese Zeile zählt nicht als ungelesen (bekannte Lücke, siehe Roadmap `FEAT-UI-66`). Nur beobachten. | ☐ |

---

## TC-CHATFOKUS — Eingabefeld bleibt nach Enter aktiv (BUG-UI-27)

| ID | Schritte | Erwartung | Ergebnis |
|---|---|---|---|
| TC-CHATFOKUS-01 | Im Chat eine Zeile tippen, **Enter**, sofort die nächste tippen — mindestens zehnmal hintereinander, nur mit der Tastatur. | Jede Zeile landet im Eingabefeld; der Cursor blinkt weiter; der Avatar läuft **nicht** los. Man muss nie neu hineinklicken. | ☐ |
| TC-CHATFOKUS-02 | Dasselbe in einer **IM**-Unterhaltung und im Gruppenchat. | Wie oben. | ☐ |
| TC-CHATFOKUS-03 | **Escape** drücken, dann W/A/S/D. | Escape verlässt die Chat-Leiste; der Avatar läuft wieder. | ☐ |
| TC-CHATFOKUS-04 | Senden mit dem **Senden-Knopf**, danach tippen. | Funktioniert wie vorher. | ☐ |
| TC-CHATFOKUS-05 | In der **Weltkarte** einen Namen ins Suchfeld tippen, **Enter**, dann sofort einen anderen Namen tippen. | Die zweite Eingabe landet im Suchfeld, ohne vorher hineinzuklicken. | ☐ |

---

## TC-PRAESENZ — „… ist online“-Hinweise mit Namen (BUG-UI-28)

Zweites Konto nötig; für TC-PRAESENZ-01 mindestens zwei Freunde, die beim Login online sind.

| ID | Schritte | Erwartung | Ergebnis |
|---|---|---|---|
| TC-PRAESENZ-01 | Einloggen, während mindestens zwei Freunde online sind. | Die Hinweise oben rechts nennen **Namen** und nie „Someone“. Hat der Freund einen **Anzeigenamen**, steht **dieser** da, nicht der Anmeldename (z. B. „Zora“ statt „anna.resident“). Sie erscheinen bis zu ein paar Sekunden verzögert, wenn die Namen erst eintreffen müssen (der Anzeigename wartet höchstens 2 s). | ☐ |
| TC-PRAESENZ-02 | Auf einen Hinweis klicken. | Der IM mit diesem Freund öffnet sich. | ☐ |
| TC-PRAESENZ-03 | Ein Freund meldet sich später an und ab (nach dem Login, die Namen sind längst bekannt). | Hinweise „… ist online.“ / „… ist offline.“ sofort und mit Namen. | ☐ |
| TC-PRAESENZ-04 | Unter `Einstellungen` → `Anzeige` den Hinweis abschalten, ein Freund meldet sich an. | Kein Hinweis; die graue Zeile im offenen IM-Reiter bleibt. | ☐ |

---

## Nach dem Test

Pro Gruppe: Wenn alle Fälle ✅ sind, kann das Roadmap-Ticket auf ✅ gesetzt werden (die Zeile in `docs/ROADMAP.md`
bekommt den Satz „Confirmed in-world <Datum>“). Bei ❌: Protokoll, Beobachtung und, wenn möglich, Bildschirmfoto an
den Entwickler; das Ticket bleibt 🧪 / 🚧.
