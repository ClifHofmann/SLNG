# Puris Viewer (SLNG) - Umfassendes Benutzerhandbuch

Willkommen beim Benutzerhandbuch für den **Puris Viewer** (internes Projekt: *SLNG*). Dieser Next-Generation-Viewer für Second Life und OpenSimulator kombiniert das bewährte LibreMetaverse-Protokoll mit der modernen Rendering-Engine von Godot 4.

> **Status:** Alpha. Es kommen kontinuierlich neue Funktionen hinzu.
>
> Dieses Handbuch beschreibt Version **v0.26.0-alpha**. Die Version steht oben rechts in der Menüleiste und auf dem Anmeldebildschirm. Was nicht funktioniert, steht in Abschnitt 11.

---

## 1. Installation & Start

### Systemanforderungen
- **Betriebssystem:** Windows (Linux/macOS experimentell).
- **Grafikkarte:** Vulkan-kompatibel (z. B. ab NVIDIA GTX 1000 Serie oder AMD Radeon RX 400 Serie) für PBR-Beleuchtung und Schatten.

### Installation
Wir stellen über unsere CI/CD-Pipelines automatisiert Windows-Installer bereit.
1. Laden Sie die Datei `PurisViewer_Setup_*.exe` aus unseren GitHub-Releases herunter.
2. Führen Sie den Installer aus. Er installiert den Viewer lokal auf Ihrem Rechner.
3. Starten Sie das Programm über die Desktop-Verknüpfung.

---

## 2. Login
Unser Login-Bildschirm nutzt das moderne "Puris Glassmorphism"-Design. 
- Geben Sie Ihren Benutzernamen und Ihr Passwort ein.
- Wählen Sie Ihr Ziel-Grid (z.B. Second Life Main Grid oder OSGrid) aus.
- Der Viewer merkt sich Ihre Login-Profile und Fenstergrößen und startet bei zukünftigen Sitzungen direkt mit Ihren persönlichen Einstellungen.

---

## 3. Benutzeroberfläche & Fensterverwaltung

### Das Glassmorphism-Design
Alle Menüs und schwebenden Fenster (UI-Fenster) bestehen aus ansprechenden, halbtransparenten Materialien im "Glassmorphism"-Look, die den Hintergrund dezent weichzeichnen.

### Fenster-Persistenz & Skalierung
- **Positionen merken:** Jedes schwebende Fenster (z.B. Chat, Freunde, Inventar) speichert beim Schließen oder beim Beenden des Viewers automatisch seine Größe und Position.
- **Sitzung wiederherstellen:** Welche der Werkzeug-Fenster (Chat, Kamera, Inventar, Schnappschuss, Umgebung, Minikarte, Weltkarte) beim letzten Beenden offen waren, werden bei der nächsten Anmeldung automatisch wieder geöffnet – an derselben Stelle und in derselben Größe. Das geschieht erst, nachdem der Ladebildschirm verschwunden ist, damit kein Fenster über dem Start-Overlay auftaucht.
- **Sichtbarkeit:** Sollte sich Ihre Bildschirmauflösung oder die Fenstergröße des Viewers ändern, zwingt der Viewer alle Fenster automatisch zurück in den sichtbaren Bereich. Ein versehentliches "Verschwinden" von Fenstern am Rand ist somit ausgeschlossen.
- **UI-Skalierung:** Unter `Preferences` (Einstellungen) -> `Anzeige` können Sie die Größe der gesamten Benutzeroberfläche stufenlos zwischen 80% und 160% anpassen.
- **Minimieren / Wiederherstellen:** Ein Doppelklick auf die Titelleiste eines Fensters (oder ein Klick auf `_`) klappt es auf die Titelleiste zusammen; ein erneuter Doppelklick klappt es wieder auf. Ein zusammengeklapptes Fenster lässt sich außerdem über seinen Knopf in der unteren Leiste oder im Schnellmenü wieder aufklappen.

### Inventar
- **Reiter:** Das Inventar-Fenster hat drei Reiter – *Inventar* (der komplette Ordnerbaum), *Angezogen* (was Ihr Avatar gerade trägt) und *Outfits* (gespeicherte Outfits). Im Reiter *Angezogen* legt ein Rechtsklick -> `Ablegen` oder ein Doppelklick genau das angeklickte Teil ab.
- **Filterleiste:** Das Such-/Filterfeld sitzt fest oben unter den Reitern und wirkt in jedem Reiter. Der eingegebene Text filtert jeweils die gerade sichtbare Liste; beim Wechsel des Reiters bleibt der Filter erhalten.
- **Schnellerer Start:** Der Viewer merkt sich Ihr Inventar zwischen den Sitzungen. Ab der zweiten Anmeldung sind Ordner, die sich nicht verändert haben, sofort da, statt erst vom Grid geladen zu werden. Gespeichert wird beim Abmelden, beim Beenden und auch dann, wenn das Grid die Sitzung beendet.

#### Ordner anlegen, verschieben, kopieren
- **Ordner-Menü (Rechtsklick auf einen Ordner):** `Neuer Ordner…`, `Umbenennen…` und `Ordner löschen`. Gelöscht wird nichts endgültig: Der Ordner samt Inhalt wandert in den Papierkorb (`In den Papierkorb`).
- **Verschieben per Ziehen:** Ziehen Sie eine Zeile auf einen Ordner, um sie dort hineinzulegen.
- **Ausschneiden, Kopieren, Einfügen:** Funktioniert mit der Tastatur und über das Menü. **Ganze Ordner** lassen sich kopieren, auch verschachtelte. Zusätzlich gibt es `Als Verknüpfung einfügen` – das ist wichtig, weil Outfits aus Verknüpfungen bestehen.
- **Original einer Verknüpfung:** Per Doppelklick auf eine Verknüpfung oder über Rechtsklick -> `Original anzeigen` springt das Inventar zum echten Gegenstand. Bei einer Ordner-Verknüpfung klappt es den Ordner auf und wählt ihn aus; ist das Original nicht mehr im Inventar, sagt der Viewer es Ihnen.
- **Schutzregeln:** Systemordner (z. B. „Objekte“) lassen sich weder umbenennen noch löschen noch ausschneiden, denn das Grid sortiert eingehende Gegenstände nach dem *Typ* des Ordners, nicht nach dem Namen. Ein Ordner kann nicht in sich selbst oder in einen eigenen Unterordner eingefügt werden. Aus `#Library` lässt sich nichts ausschneiden, denn das gehört Ihnen nicht – kopieren Sie es stattdessen.

#### Papierkorb
Was Sie löschen, landet zuerst im Papierkorb. Von dort holen Sie es zurück oder entfernen es endgültig:
- **Papierkorb leeren:** Rechtsklick auf den Papierkorb -> `Papierkorb leeren…`. Der Viewer zählt zuerst, was darin liegt, und fragt dann mit der Anzahl nach, z. B. „3 Objekte und 1 Ordner im Papierkorb endgültig löschen?“.
- **Rechtsklick auf etwas im Papierkorb** bietet zwei Dinge an:
  - `Wiederherstellen nach „…“` legt es zurück – allerdings nicht in den Ordner, aus dem es kam, denn das merkt sich weder das Grid noch ein anderer Viewer. Es landet im passenden Systemordner, z. B. „Objects“ oder „Clothing“; Schnappschüsse kommen ins Fotoalbum, Ordner ganz nach oben ins Inventar. Der Menüeintrag nennt das Ziel, bevor Sie klicken.
  - `Endgültig löschen…` entfernt ein einzelnes Teil, bei einem Ordner mit allem darin, nach einer eigenen Rückfrage.
- **Endgültig heißt endgültig:** Leeren und endgültiges Löschen lassen sich nicht rückgängig machen. Was Sie gerade tragen, wird nicht gelöscht: Der Viewer nennt dann das Teil – erst ausziehen, dann löschen.

#### Outfits
Rechtsklick auf ein gespeichertes Outfit im Reiter *Outfits*:
- `Dieses Outfit anziehen` ersetzt, was Sie tragen, durch dieses Outfit.
- `Zusätzlich anziehen` legt das Outfit zu dem, was Sie schon tragen.
- `Teile dieses Outfits ausziehen`, `Outfit umbenennen` und `Outfit löschen` tun, was sie sagen.
- `Mit aktuell Getragenem überschreiben` ändert den *gespeicherten* Ordner, nicht Ihren Avatar.

---

## 4. Kamera & Steuerung

### Bewegen & Interaktion
- **Tastatur:** Bewegen Sie sich wie gewohnt mit `W`, `A`, `S`, `D` oder den Pfeiltasten.
- **Kontext-Mauszeiger:** Der Mauszeiger ändert sich automatisch, wenn Sie über Objekte fahren:
  - *Stuhl-Symbol:* Ein Linksklick (oder Rechtsklick -> `Sit`) lässt Ihren Avatar Platz nehmen. Ein erneuter Druck auf eine Bewegungstaste lässt Sie aufstehen.
  - *Hand-Symbol:* Sie können das Objekt berühren (Touch).
- **Fliegen:** Aktivieren Sie den Flugmodus (Standard: Taste `F` oder über das Menü), um die Region von oben zu erkunden.
- **Klicken über Regionsgrenzen:** Objekte auf der anderen Seite einer Regionsgrenze lassen sich berühren, auswählen und kaufen wie Objekte in Ihrer eigenen Region.
- **Berühren mit Fläche:** Ein Klick meldet dem Objekt auch, *welche Fläche* Sie getroffen haben. Verkaufstafeln mit einem Produkt pro Fläche reagieren deshalb richtig.
- **Teleport im Sitzen:** Nach jedem Teleport steht Ihr Avatar am Ziel auf, statt in einer Sitzpose zu einem Stuhl zurückzufallen, der eine Region entfernt steht.

### Kamerasteuerung (Alt-Zoom & Co.)
- **Mauskamera (Alt+Zoom):** Halten Sie `Alt` gedrückt und klicken Sie mit der linken Maustaste auf ein Objekt oder einen Avatar, um diesen zu fokussieren. Mit Mausbewegungen können Sie dann darum kreisen oder hineinzoomen. Die Kamera **folgt dem Ziel**: geht der Avatar weiter oder fährt, fliegt oder bewegt sich das Objekt, bleibt es im Bild, auch wenn Sie selbst weiterlaufen. Verschwindet das Ziel (Objekt gelöscht, Avatar gegangen), bleibt die Kamera an der letzten Stelle. Mit `Esc` oder einem neuen Alt-Klick ist das Folgen beendet.
- **Sanfte Übergänge (Smooth Transitions):** Wenn Sie die Kamera zurücksetzen (z.B. mit Esc) oder ein neues Ziel anvisieren, fährt die Kamera weich an die neue Position, anstatt abrupt zu springen.
- **Kamera-Einstellungen:** Unter `Preferences` -> `Kamera` finden Sie detaillierte Kameraoptionen:
  - **Sichtfeld (FOV):** Ändern Sie den Blickwinkel (Standard: 75°).
  - **Kamera-Abstand (Rear Distance):** Wie weit die Kamera hinter Ihrem Avatar folgt.
  - **Fokus-Höhe:** Die Höhe der Kamera relativ zum Avatar.
  - **Geschwindigkeit:** Passen Sie die Dreh- und Zoom-Geschwindigkeit der Kamera ("Orbit", "Pan") individuell an (10% - 300%).

---

## 5. Kommunikation (Chat, Gruppen, Freunde)

Der Puris Viewer bündelt Ihre soziale Interaktion in einem kompakten Kommunikationsfenster.

- **Tabbed Chat:** Der lokale Umgebungs-Chat, private Instant Messages (IMs) und Gruppen-Chats werden übersichtlich in Reitern (Tabs) parallel gebündelt.
- **Freundesliste:** Sehen Sie auf einen Blick, wer online ist, und öffnen Sie per Rechtsklick das Avatar-Profil, um einen Teleport anzubieten oder eine Nachricht zu schreiben.
- **Teleport-Angebote und -Anfragen:** Bietet Ihnen jemand einen Teleport an, öffnet sich ein Fenster mit dem Namen und der Nachricht der Person; **Teleportieren** bringt Sie zu ihr, **Ablehnen** sagt ihr ab. Bittet jemand darum, zu Ihnen teleportiert zu werden, lautet die Antwort **Teleport anbieten** oder **Ablehnen**. Schließen Sie das Fenster mit dem Kreuz, wird nichts gesendet: Die Anfrage bleibt im Benachrichtigungsfenster unter „Einladungen“ stehen, und Sie können sie von dort wieder öffnen.
- **Freundschaftsangebote:** Bietet Ihnen jemand die Freundschaft an, öffnet sich ein Fenster mit dem Namen und der Nachricht der Person; **Annehmen** nimmt sie in Ihre Freundesliste auf, **Ablehnen** lehnt das Angebot ab, ohne dass die Person davon erfährt. Schließen Sie das Fenster mit dem Kreuz, wird nichts gesendet: Das Angebot bleibt im Benachrichtigungsfenster unter „Einladungen“ stehen, und Sie können es von dort wieder öffnen. Nimmt jemand Ihr eigenes Angebot an, erscheint ein Eintrag im Benachrichtigungsfenster, und die Person steht ohne Neuanmeldung in Ihrer Freundesliste.
- **Gruppen (Group Chat):** Treten Sie Gruppenchats bei, lesen Sie Nachrichten (die als eigene Chat-Tabs erscheinen) und verwalten Sie Ihre Mitgliedschaften.
  - *Stummschalten (Mute):* Wenn ein Gruppenchat zu viel spammt, können Sie ihn direkt stummschalten. Der Tab öffnet sich dann nicht mehr von selbst.
- **Skript-Dialoge (llDialog):** Popups von In-World-Skripten (z. B. Teleporter-Menüs oder Optionen von Möbeln) erscheinen als übersichtliche UI-Fenster mit interaktiven Buttons.

---

## 6. Weltkarte, Minimap & Teleport

Eines der Highlights des Viewers sind die modernen Navigations- und Karten-Tools.

### Minimap (Radar)
- Die Minimap zeigt Ihren Avatar im Zentrum und alle Avatare in Ihrer Nähe als Punkte. Sie können stufenlos mit dem Mausrad hinein- und herauszoomen.
- **Regionsbild:** Unter den Punkten sehen Sie das Kartenbild der Region, wie im Firestorm. Nachbarregionen erscheinen etwas dunkler, und wo keine Region liegt, bleibt der Hintergrund dunkel. Das Bild kommt vom Kartenserver des Grids und wird auf Ihrem Rechner zwischengespeichert; erscheint es nicht sofort, wird es im Hintergrund nachgeladen.
- **Aufbau:** Oben ein Namensfilter mit Zahnrad- und Sortier-Menü, darunter die Karte, darunter die Tabelle aller erfassten Avatare der Region. Die Trennlinie zwischen Karte und Tabelle lässt sich mit der Maus verschieben; die Höhe wird gemerkt.
- **Punktfarben:** Freunde sind grün, andere Avatare rot, stumm geschaltete grau. Der ausgewählte Avatar hat einen weißen Ring. Dieselbe Farbe steht als Punkt vor dem Namen in der Tabelle.
- **Höhe auf der Karte:** Ein Avatar, der mehr als 7 m über Ihnen ist, erscheint als Dreieck nach oben, einer mehr als 7 m unter Ihnen als Dreieck nach unten, auf gleicher Höhe als Punkt. Ist die Höhe nicht bekannt (nur grobe Ortung ganz oben), sehen Sie einen hohlen Ring.
- **Objekte:** Ihre eigenen Prims und die großen erscheinen als Quadrate auf der Karte: Ihre in Cyan, die anderer in Dunkelgrau, unter Wasser jeweils etwas dunkler, Phantom-Objekte noch etwas blasser. Die Quadrate sind halbtransparent mit feinem Rand, damit das Kartenbild der Region darunter sichtbar bleibt. Als "groß" zählt die Grundfläche (Länge und Breite zusammen), nicht die Höhe: ein Baum oder ein Laternenpfahl ist kein großes Objekt, eine Wand oder ein Boden schon. Wie groß ein fremdes Objekt sein muss, stellen Sie im Kartenmenü unter "Objekte" ein: ab 7,5 m (wie im Firestorm), ab 15 m (Standard) oder ab 30 m. Ihre eigenen Prims erscheinen immer, egal wie klein. Getragene Objekte, Bäume und Gras zählen nicht. Mit "Objekte anzeigen" im selben Untermenü schalten Sie die Quadrate ganz aus; dann wird auch nichts mehr berechnet.
- **Chat-Ringe:** Um Ihren Avatar liegen drei Ringe: blau für Flüstern (10 m), gelb für Sagen (20 m) und rot für Schreien (100 m). Sie lassen sich im Kartenmenü einzeln oder alle zusammen ausschalten.
- **Sichtkegel:** Ein heller Kegel zeigt, wohin Ihre Kamera schaut, so weit wie Ihre Sichtweite und so breit wie Ihr Sichtfeld.
- **Kartenmenü:** Ein Rechtsklick auf die Karte (oder das Zahnrad) öffnet das Menü: Zoom (Sehr nah 32 m, Nah 64 m, Mittel 128 m, Weit 256 m), "Norden oben" oder "Kamera oben" (dann dreht sich die Karte mit Ihrer Blickrichtung), "Karte automatisch zentrieren", "Karte zentrieren", "Chat-Ringe", "Objekte" und "Weltkarte". Ein Rechtsklick direkt auf einen Punkt öffnet stattdessen das Avatar-Menü. Alle Einstellungen bleiben beim nächsten Start erhalten.
- **Karte verschieben:** Mit gedrückter Umschalttaste (Shift) und gezogener linker Maustaste verschieben Sie die Ansicht. Ist "automatisch zentrieren" an, gleitet die Karte danach von selbst zurück.
- **Tooltip:** Über einem Punkt zeigt die Karte Name und Entfernung, sonst Region und die Koordinaten unter dem Mauszeiger.
- **Namen:** Der Standard-Nachname "Resident" wird weggelassen, wie im Firestorm: "Oz" statt "Oz Resident". Ein selbst gewählter Anzeigename bleibt unverändert.
- **Tabelle:** Die Spalten sind Name, Sprachchat (bleibt leer, bis es Sprachchat gibt), In Region, Sitzt, Zahlungsinfo (`$` hinterlegt, `$$` bereits benutzt), Notiz, Alter (Account-Alter in Tagen, rot unter 7 Tagen, "n.a." wenn versteckt; mit der Maus darüber sehen Sie es als Jahre, Monate und Tage sowie den Rezz-Tag, also das Datum, an dem das Konto erstellt wurde), Zeit (wie lange der Avatar schon in der Liste ist) und Distanz. Über dem Namen steht `[Gesamt/in der Region/in Chat-Reichweite]`. Die Distanz ist amber innerhalb der Sagen-Reichweite (20 m), normal bis zur Schrei-Reichweite (100 m) und darüber gedämpft, und fett, solange der Avatar sichtbar gezeichnet wird.
- **Notiz:** Hat der Avatar eine Notiz von Ihnen, steht in der Notiz-Spalte ein Symbol. Fahren Sie mit der Maus darüber, sehen Sie den Notiztext.
- **Spalten ein- und ausblenden:** Rechtsklick auf eine Spaltenüberschrift, oder Zahnrad, dann "Spalten". Das Menü bleibt offen, solange Sie mehrere Spalten anhaken. "Spalten zurücksetzen" stellt die Voreinstellung wieder her. Der Name lässt sich nicht ausblenden.
- **Sortieren:** Ein Klick auf eine Überschrift sortiert danach, ein zweiter Klick dreht die Richtung um. Alternativ über den Sortier-Knopf neben dem Zahnrad. Unbekannte Werte (zum Beispiel ein Alter, das noch nicht geladen ist) stehen immer am Ende. Spalten und Sortierung bleiben beim nächsten Start erhalten.
- **Filtern:** Das Feld oben filtert die Tabelle nach dem Namen.
- **Radar-Fokus:** Ein Klick auf eine Zeile markiert den Punkt des Avatars auf der Karte. Umgekehrt wählt ein Klick auf einen Punkt die Zeile aus.
- **Kamera-Fokus:** Ein Doppelklick auf eine Zeile zoomt die Karte auf den Avatar und dreht Ihre Kamera automatisch und sanft in eine frontale ("Portrait"-) Ansicht. Die Kamera folgt dem Avatar, auch wenn er weitergeht, bis Sie sie mit `Esc` zurücksetzen oder woanders hin ausrichten. Solange die Karte auf jemand anderen zentriert ist, steht neben dem Regionsnamen "zentriert auf Name". Zurück zu sich selbst kommen Sie mit einem zweiten Doppelklick auf dieselbe Zeile oder über das Kartenmenü (Rechtsklick auf die Karte, "Karte zentrieren").
- **Kontextmenü:** Rechtsklick auf eine Zeile öffnet das Avatar-Menü (Profil, IM, Teleport anbieten, Stummschalten).
- **Teleport per Doppelklick:** Ein Doppelklick auf die Karte teleportiert Sie an diese Stelle der Region – knapp über den Boden. Ein einfacher Klick tut nichts, und ein Doppelklick außerhalb der Region wird ignoriert.

### Weltkarte
- Über das Menü "World" öffnen Sie die große Weltkarte.
- **Suchen & Teleport:** Suchen Sie direkt nach Regionen (z.B. `Ahern`). Ein Doppelklick auf ein Quadrat auf der Karte löst sofort einen Teleport dorthin aus.
- Sie können die Weltkarte stufenlos verschieben und hineinzoomen, da Texturkacheln fließend nachgeladen werden.

### Landmarken
- Nutzen Sie Landmarken aus Ihrem Inventar per Doppelklick zum Teleportieren.
- Aktuelle Regionen lassen sich mühelos als neue Landmarke abspeichern.

### Land-Info (Über das Land)
Über das Menü **Welt → Über das Land …** öffnet sich ein Fenster mit den Angaben zur Parzelle, auf der Ihr Avatar gerade steht. Der Reiter **Allgemein** zeigt Name, Parzellen-ID (zum Markieren und Kopieren), Beschreibung, Typ, Einstufung, Besitzer, Gruppe, Beanspruchungsdatum (in Second-Life-Zeit, also US-Pazifikzeit; fehlt diese Zeitzone auf Ihrem Rechner, steht die Zeit in UTC und das Fenster sagt es), Preis, Fläche und Verkehr. Steht die Parzelle zum Verkauf, kommen der berechtigte Käufer und die Angabe hinzu, ob Objekte mitverkauft werden.
- Das Fenster folgt Ihnen: Betreten Sie eine andere Parzelle, aktualisiert es sich von selbst. Namen und der Verkehrswert erscheinen, sobald das Grid sie geliefert hat; bis dahin steht dort „(wird geladen …)“ bzw. „–“.
- Kann das Grid die Angaben nicht liefern, oder sind Sie nicht angemeldet, steht dort eine Zeile „Parzelleninformationen sind nicht verfügbar“ statt eines leeren Formulars.
- Das Fenster ist vorerst nur zum Lesen. Die Knöpfe für Kauf, Verkauf, Gruppe und Ähnliches sind sichtbar, aber ausgegraut („Noch nicht verfügbar“); weitere Reiter folgen.
Drei weitere Reiter zeigen, was die Sim über die Parzelle sagt – ebenfalls nur zum Lesen:
- **Optionen:** wer fliegen, bauen, Objekte hereinbringen und Skripte ausführen darf (bei „Bauen“, „Objekte“ und „Skripte“ ist „Gruppe“ immer mit angehakt, wenn „Jeder“ angehakt ist), „Sicher (kein Schaden)“, „Kein Schubsen“ (kommt der Zwang von der Region, steht dort „von der Region vorgegeben“), ob der Ort in der Suche erscheint und unter welcher Kategorie, „Moderate Inhalte“ (in einer Adult-Region „Adult-Inhalte“, immer angehakt; in einer allgemeinen Region nie), ob Avatare auf anderen Parzellen die Avatare hier sehen können, die Bild-ID des Schnappschusses (die Anzeige des Bildes selbst folgt später), der Landepunkt mit Blickrichtung („(keine)“, wenn keiner gesetzt ist) und das Teleport-Routing.
- **Medien:** Typ, Startseite, Beschreibung und Ersatztextur der Parzellenmedien sowie „Automatisch skalieren“. „Wiederholen“ gilt nur für Filme und Audio, die Größe nur für Webseiten; wo etwas nicht gilt, ist es abgeblendet bzw. mit „–“ gefüllt. Hat die Parzelle keine Medien, steht dort ein entsprechender Hinweis.
- **Sound:** Musik-URL, die Beschränkung von Gesten- und Objektsounds, wer Avatar-Sounds machen darf, die Voice-Einstellungen (hat der Grundbesitz Voice abgeschaltet, steht dort „vom Grundbesitz festgelegt“) und „MOAP auf diese Parzelle beschränken“. Letzteres kann der Viewer noch nicht aus der Sim lesen; es steht deshalb als „unbekannt“ da und nicht als nicht angehakt.
- Die Häkchen sind reine Anzeige und lassen sich nicht umschalten; ein Hinweistext sagt, dass das Ändern noch nicht verfügbar ist.

### Regions-Neustart
Kündigt das Grid einen Neustart der Region an, in der Sie stehen, öffnet sich das Fenster **Regions-Neustart**:
- Es nennt die Region und zählt die Zeit bis zum Neustart herunter (ab zehn Sekunden Restzeit wird die Zahl rot). Die Taskleiste blinkt, falls der Viewer im Hintergrund ist.
- Unter **Teleportieren nach** wählen Sie das Ziel: `Zuhause` (vorgewählt) oder eine Ihrer Landmarken. `Teleportieren` bringt Sie dorthin und schließt das Fenster. Was bei einem nicht gesetzten oder unbrauchbaren Zuhause geschieht, entscheidet das Grid.
- Kommt eine weitere Meldung ("noch eine Minute"), wird dasselbe Fenster aktualisiert – es öffnet sich kein zweites. Ein Regionswechsel schließt es. `Schließen` blendet es aus, ohne Sie zu teleportieren.
- Die Meldung bleibt zusätzlich im Fenster **Benachrichtigungen** (Reiter *System*) stehen.

**Wenn Sie bleiben:** Beim Neustart beendet das Grid Ihre Sitzung. Der Viewer zeigt dann **Du wurdest abgemeldet** mit dem Grund und zwei Knöpfen: `Zur Anmeldung` geht zum Anmeldebildschirm, `Hier bleiben` lässt Sie den Chat weiterlesen. Der Viewer sendet dabei ein ordentliches Abmelden an das Grid. Dasselbe passiert, wenn die Verbindung abläuft, eine Region nicht mehr antwortet oder Sie sich mit demselben Avatar anderswo anmelden. Die Welt bleibt dabei stehen, wie sie war – Landschaft, Objekte und Ihr Avatar mit allem, was er trägt; erst `Zur Anmeldung` räumt sie ab.

---

## 7. Profile

Das voll ausgestattete Benutzerprofil-Fenster ist zentral für Ihre SL-Identität:
- **Eigene Bearbeitung:** Bearbeiten Sie Ihre eigene *1st-Life-* und *2nd-Life-Beschreibung*, fügen Sie Ihre Webseite hinzu und setzen Sie Einstellungen (Mature Content, In Search). Alles wird über "Save Profile" live im Grid gespeichert.
- **Picks & Classifieds:** Bewundern Sie die Picks anderer Avatare. Der Puris Viewer zeigt vollständige Picks (inkl. Snapshot, Text und "Teleport"-Button zum Aufnahmeort) an.
- **Private Notizen:** Sie können zu jedem Avatar eine Notiz speichern, die nur Sie sehen. Sie wird mit Ihrem Konto auf dem Grid gespeichert und erscheint deshalb auch in anderen Viewern wie Firestorm, und umgekehrt. Notizen, die Sie früher nur auf diesem Computer gespeichert hatten, werden beim ersten Öffnen des Profils einmalig übernommen, sofern das Grid noch keine Notiz zu dieser Person hat. Antwortet ein Grid nicht (manche OpenSim-Server), bleiben die Notizen ausnahmsweise nur lokal.

---

## 8. Avatare & Rendering-Optionen

Der Puris Viewer legt höchsten Wert auf eine optisch ansprechende, korrekte Darstellung:
- **Bento & Bakes-on-Mesh (BoM):** Moderne Mesh-Körper, Bento-Skelette, Animationen, Alpha-Masken und klassische Systemkleidung (über BoM gebacken) werden vollständig unterstützt.
- **Grafikeinstellungen:** Im Einstellungsfenster (`Preferences`) -> `Grafik` können Sie Funktionen wie die Sichtweite (Draw Distance), Anti-Aliasing (MSAA) und die Qualität der dynamischen Echtzeit-Schatten (CSM) anpassen.
- **Objekt-Cache:** Der Viewer merkt sich die Objekte jeder Region, die Sie besucht haben, auf der Festplatte. Beim nächsten Besuch — auch nach einem Neustart oder wenn Sie schnell in eine Region zurückspringen — steht ein Großteil der Region sofort da, statt erst nach und nach vom Server zu kommen. Unter `Einstellungen` -> `Netzwerk` leert der Knopf `Asset- und Objekt-Cache leeren` beide Zwischenspeicher; daneben steht, wie viel Platz sie belegen. Wer den Objekt-Cache nicht nutzen will, startet den Viewer mit `--no-object-cache`.
- **Namensschilder:** Über dem Avatar steht der **Anzeigename**; darunter in Klammern der unveränderliche Benutzername, wenn beide verschieden sind. Avatare ohne eigenen Anzeigenamen zeigen nur ihren Namen. Darüber kann der Gruppentitel stehen. Unter `Einstellungen` -> `Anzeige` schalten Sie das ein oder aus: `Gruppentitel zeigen`, `Anzeigenamen verwenden`, `Benutzernamen unter dem Anzeigenamen zeigen` und `Meinen Gruppentitel vor allen verbergen` (das wirkt wirklich auf andere: Der Simulator sendet Ihren Titel dann an niemanden).
  - Der Viewer **merkt sich Anzeigenamen** zwischen den Sitzungen, sie stehen beim nächsten Login sofort da. Ein bereits gemerkter Name wird nach 24 Stunden im Hintergrund neu abgefragt.
  - Ändert jemand seinen Anzeigenamen und meldet das Grid es, aktualisiert sich das Namensschild, und im Fenster **Benachrichtigungen** (Reiter *System*) erscheint „*alter Name* (*Benutzername*) heißt jetzt *neuer Name*“.
- **Asynchrones Textur-Streaming:** Um Ruckler ("Stutter") beim Erkunden zu vermeiden, lädt und dekodiert der Viewer JPEG2000-Texturen asynchron im Hintergrund. Texturen, die näher an der Kamera sind, werden priorisiert geladen.

---

## 9. Geld & Zahlungen

- **Kontostand:** Ihr Guthaben in Linden-Dollar steht in der Menüleiste oben rechts (`L$`). Es aktualisiert sich von selbst.
- **Objekte kaufen:** Rechtsklick auf ein Objekt, das zum Verkauf steht -> `Kaufen (L$ …) …`. Das Fenster **Objekt kaufen** nennt nicht nur den Preis, sondern auch, *was* Sie kaufen: das **Original** (es wechselt den Besitzer und verschwindet von seinem Platz), eine **Kopie** (das Objekt bleibt stehen, die Kopie landet im Inventar) oder den **Inhalt**. Es zeigt Ihren Kontostand und, falls das Geld nicht reicht, *wie viel fehlt* – abgeschickt wird dann nichts.
- **Objekte bezahlen:** Rechtsklick -> `Bezahlen …` öffnet **Objekt bezahlen**. Die Beträge bestimmt das Objekt (z. B. ein Trinkgeldgeber oder ein Verkaufsautomat); gibt es keine Vorgabe, stehen L$ 1, 5, 10 und 20 zur Wahl, und `Anderer Betrag` erlaubt einen freien Betrag, sofern das Objekt ihn nicht ausgeschlossen hat.
- **Personen bezahlen:** Das Fenster **Person bezahlen** erreichen Sie per Rechtsklick auf die Person, aus ihrem Profil und aus der Freundesliste. Unter `Wofür? (optional)` können Sie einen Grund angeben, den der Empfänger sieht. **Über L$ 200** fragt der Viewer vorher nach und nennt Betrag und Empfänger: „L$ … an … senden. Das lässt sich nicht rückgängig machen.“
- **Wenn Geld ankommt oder abgeht:** Der Viewer meldet, wer wie viel gezahlt hat und wofür (eingehend, ausgehend oder abgelehnt). Sie steht außerdem im Fenster **Benachrichtigungen** (Reiter *Transaktionen*).
- **Nicht möglich:** Land kaufen ist noch nicht eingebaut.

> **Vorsicht:** Kauf und Zahlung sind neu. Prüfen Sie Betrag und Empfänger im Fenster sorgfältig, und melden Sie sich mit einem Konto an, dessen Guthaben Sie verschmerzen könnten.

---

## 10. Benachrichtigungen & Anfragen

### Das Fenster „Benachrichtigungen“
Alles, was bei Ihnen *ankommt* und nicht in den Chat gehört, landet hier: Zahlungen, Inventarangebote, Meldungen des Simulators (z. B. ein angekündigter Regions-Neustart oder ein geänderter Anzeigename), Gruppeneinladungen und Rechteanfragen von Skripten. Öffnen Sie es über das Glocken-Symbol in der unteren Leiste.
- **Vier Reiter:** *System*, *Transaktionen*, *Einladungen* und *Gruppe*. Die Zahl im Reiter sagt, was ungelesen ist.
- **Hinweis oben rechts:** Bei einer neuen Benachrichtigung erscheint kurz ein Hinweis, der von selbst wieder verschwindet. Ein Klick darauf öffnet den passenden Reiter. Wegklicken löscht nichts.
- **Ungelesen-Zähler** stehen an der Glocke und am Chat-Symbol. Gelesen wird pro Reiter gezählt: Wer *Transaktionen* öffnet, löscht nicht nebenbei den Zähler für ungesehene Gruppennachrichten.
- **Namen anklicken** öffnet das Profil, wo es eines gibt (ein Objekt hat keins).
- `Mehr anzeigen` klappt Einzelheiten auf, `Wegräumen` entfernt einen Eintrag, `Alle schließen` alle des Reiters.
- **Offene Entscheidungen:** Eine Gruppeneinladung oder ein Inventarangebot, das Sie mit dem × schließen, ist damit *nicht* beantwortet. Der Eintrag hat dann einen Knopf `Öffnen`, der das Fenster zurückholt. Nach der Antwort bleibt der Eintrag als Verlauf stehen („… – beigetreten“, „… – abgelehnt“), nur der Knopf verschwindet.
- Skript-Dialoge (`llDialog`) erscheinen **nicht** hier, sondern als eigene Fenster (siehe Abschnitt 5).

### Rechteanfragen von Skripten
Bittet ein Objekt um Erlaubnis (**Rechteanfrage**), nennt das Fenster das Objekt und seinen Besitzer und listet in Worten auf, was es möchte, z. B. *Deinen Avatar animieren* oder *Geld von deinem Konto abbuchen*. Die Zeile mit dem Geld ist hervorgehoben, denn sie ist die gefährlichste. `Ablehnen` steht links und hat den Fokus, `Erlauben` rechts. Das Schließen mit dem × zählt als Ablehnung.

### Einladungen und Angebote
Eine **Gruppeneinladung** zeigt den Einladenden und die Beitrittsgebühr (`Beitreten` / `Ablehnen`). Ein **Inventarangebot** zeigt, wer Ihnen was anbietet (`Annehmen` / `Ablehnen`).

---

## 11. Bekannte Probleme (Troubleshooting)

Da sich der Client in einer laufenden Alpha-Phase befindet, helfen Ihnen folgende Hinweise:
- **"Wolken-Avatare":** Wenn Avatare oder Objekte kurzzeitig grau/als Wolke dargestellt werden, warten Sie einen Augenblick. Das Hintergrund-Streaming (`CoreJ2K`) decodiert die Texturen gerade noch.
- **Abbrechen von Teleports:** Wenn Sie im Weltkarten-Menü "A teleport is already in progress" lesen, läuft im Hintergrund bereits ein Teleport-Vorgang. Der Puris Viewer blockiert hier gezielt Überlappungen, um korrupte Verbindungen (Race Conditions) zu vermeiden.
- **Objekt-Detach funktioniert nicht:** Ein Klick auf "Detach" im Inventar räumt nun automatisch kaputte Server-Verknüpfungen auf, falls ein Kleidungsstück fehlerhaft anhing. 
- **Avatar sieht unvollständig aus** (Kopf leer, Haare wie ein Helm): Menü `Avatar` -> `Avatar neu backen` (`Strg+Alt+R`) lädt die Körpertexturen neu.
- **Was noch fehlt (Alpha):**
  - **Gruppenmitteilungen** (Group Notices) kommen nicht an. Der Gruppen*chat* funktioniert.
  - **Freundesrechte** werden weder angezeigt noch lassen sie sich einstellen.
  - Die **Minikarte** listet nur Avatare Ihrer eigenen Region und zeigt keinen Regions- oder Parzellenhinweis.
  - **Land kaufen** gibt es nicht (Objekte kaufen und Zahlen schon).
  - **Ton:** Es gibt noch keinen Ton in der Welt; der Lautsprecher-Knopf oben ist dafür reserviert.
- **Neustart-Fenster:** Ob das Ziel *Zuhause* bei nicht gesetztem Zuhause an einem Hub landet, entscheidet das Grid; das ist noch nicht in allen Fällen erprobt.

---

## 12. Lizenzen & TPV

Dieser Viewer nutzt Texturen aus dem Bestand von Linden Lab (wie das Windlight Cloud Texture und den Terrain Blend Ramp). Details und rechtliche Rahmenbedingungen hierzu finden Sie unter dem Menüpunkt **Preferences → Licences** oder in der bereitgestellten `THIRD-PARTY-NOTICES.md`. 
Zudem hält sich der Puris Viewer strikt an die Third-Party Viewer (TPV) Policy, respektiert Berechtigungen (Permissions) und umgeht keine DRM-Schutzmechanismen des Grids.
