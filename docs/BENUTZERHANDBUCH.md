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
- **Jedes Grid für sich:** Das Hintergrundbild Ihres letzten Besuchs, der Objekt- und der Kartenspeicher sowie die Chat-Protokolle werden für jedes Grid (und jedes Konto) getrennt abgelegt (die Protokolle in der Ordnerstruktur von Firestorm, siehe Abschnitt 5). Ein Konto mit demselben Namen auf Second Life und auf OSGrid sieht dadurch nie die Daten des jeweils anderen Grids. Was frühere Versionen gemeinsam gespeichert haben, wird nicht mehr benutzt und nicht gelöscht: Das Hintergrundbild beginnt pro Grid neu, der Objekt-Cache füllt sich beim nächsten Besuch einer Region von selbst wieder. Für die Chat-Protokolle gilt seit Version 0.26.14 die Ordnerstruktur von Firestorm (siehe Abschnitt 5).

---

## 3. Benutzeroberfläche & Fensterverwaltung

### Das Glassmorphism-Design
Alle Menüs und schwebenden Fenster (UI-Fenster) bestehen aus ansprechenden, halbtransparenten Materialien im "Glassmorphism"-Look, die den Hintergrund dezent weichzeichnen.

### Fenster-Persistenz & Skalierung
- **Positionen merken:** Jedes schwebende Fenster (z.B. Chat, Freunde, Inventar) speichert beim Schließen oder beim Beenden des Viewers automatisch seine Größe und Position.
- **Sitzung wiederherstellen:** Welche der Werkzeug-Fenster (Chat, Kamera, Inventar, Schnappschuss, Umgebung, Minikarte, Weltkarte) beim letzten Beenden offen waren, werden bei der nächsten Anmeldung automatisch wieder geöffnet – an derselben Stelle und in derselben Größe. Das geschieht erst, nachdem der Ladebildschirm verschwunden ist, damit kein Fenster über dem Start-Overlay auftaucht.
- **Sichtbarkeit:** Sollte sich Ihre Bildschirmauflösung oder die Fenstergröße des Viewers ändern, zwingt der Viewer alle Fenster automatisch zurück in den sichtbaren Bereich. Ein versehentliches "Verschwinden" von Fenstern am Rand ist somit ausgeschlossen.
- **UI-Skalierung:** Unter `Preferences` (Einstellungen) -> `Anzeige` stellen Sie die Größe der gesamten Benutzeroberfläche ein: Menüleiste, Anmelde- und Ladebildschirm, alle Fenster, Menüs, Kurzinfos und Namensschilder. Die 3D-Ansicht bleibt dabei unverändert und wird nicht unschärfer.
  - **Automatisch (folgt der Anzeige):** Das ist die Voreinstellung. Der Viewer übernimmt die Skalierung, die Windows für Ihren Bildschirm eingestellt hat (z. B. 150 % auf einem hochauflösenden Laptop), damit die Schrift dort nicht winzig ist. Zieht man das Fenster auf einen anderen Bildschirm, folgt die Größe nach. Darunter steht, welche Anzeigeskalierung erkannt wurde.
  - **Eigene Größe:** Nehmen Sie das Häkchen bei `Automatisch` heraus, dann bestimmt der Regler die Größe, stufenlos von 80 % bis 400 %, auf jedem Bildschirm gleich. Der Wert gilt, sobald Sie den Regler loslassen, und bleibt gespeichert. Mit dem Häkchen kehren Sie jederzeit zur automatischen Größe zurück.
  - Hatten Sie die Größe schon früher selbst gewählt, bleibt dieser Wert erhalten; Fensterpositionen bleiben an derselben Stelle auf dem Bildschirm.
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

### Chat-Protokolle (gemeinsam mit Firestorm)

Der Puris Viewer schreibt Ihre Chats im Format und in der Ordnerstruktur von Firestorm. Dadurch können Sie zwischen beiden Viewern wechseln und sehen in beiden denselben Verlauf: gleicher Ordner, gleiche Dateinamen, gleiche Zeilen.

- **Wo die Protokolle liegen:** Pro Konto und Grid gibt es einen eigenen Unterordner, benannt wie bei Firestorm, zum Beispiel `clifton_howlett` (Second Life) oder `clifton_howlett.osgrid` (OSGrid). Darin liegt eine Datei je Unterhaltung: `chat.txt` für den lokalen Chat, `<Name>.txt` für Instant Messages und `<Gruppenname> (group).txt` für Gruppenchats. Die Zeilen sehen aus wie bei Firestorm (`[2026/04/11 08:07]  Name: Text`); die Uhrzeit ist, wie bei Firestorm, die Second-Life-Zeit (Pazifische Zeit), nicht Ihre Ortszeit.
- **Einstellung (pro Konto):** Unter `Preferences` -> `Chat-Protokolle` wählen Sie zuerst ein gespeichertes Konto aus der Liste (wie auf dem Login-Bildschirm, zum Beispiel „Clifton Howlett — Second Life“; ein Konto, mit dem Sie gerade angemeldet sind, steht auch dann dort, wenn Sie es nicht gespeichert haben). Für dieses Konto legen Sie mit `Wählen...` den Ordner fest, in dem die Protokolle liegen sollen; `Standard verwenden` stellt den Standardordner von SLNG wieder her (`%APPDATA%\SLNG\logs\chat`). Darunter sehen Sie, welcher Ordner tatsächlich gilt, einschließlich des Konto-Unterordners, der darin angelegt wird. Der Puris Viewer sucht nicht nach Firestorm und liest dessen Einstellungen nicht: Wenn beide Viewer einen gemeinsamen Verlauf haben sollen, wählen Sie hier für das Konto den Ordner, den Firestorm für dasselbe Konto benutzt. Dort stellen Sie auch ein, wie die Dateien der Instant Messages heißen (`Vorname Nachname.txt` oder `vorname_nachname.txt`); diese Einstellung gilt für alle Konten und sollte zu der in Firestorm passen. Eine Änderung gilt ab der nächsten Anmeldung.
- **Alte SLNG-Protokolle übernehmen:** Protokolle früherer Versionen werden nie gelöscht oder verschoben. Mit `Alte SLNG-Protokolle importieren...` (nach der Anmeldung) hängen Sie sie ans Ende der passenden Dateien im Ordner des angemeldeten Kontos an. Vor dem Import wird Ihnen angezeigt, wie viele Nachrichten übernommen werden, und Sie müssen bestätigen. Der Import fügt nichts doppelt ein: Er kann gefahrlos wiederholt werden. Weil die alten Protokolle nicht pro Konto geführt wurden, landet alles unter dem Konto, mit dem Sie angemeldet sind. Ob eine Datei eine private Unterhaltung oder ein Gruppenchat war, lässt sich nur aus ihrem Inhalt erraten; Dateien, die nur aus einer Kennung bestehen, werden übersprungen.
- **Nicht gleichzeitig:** Lassen Sie Firestorm und den Puris Viewer nicht gleichzeitig mit demselben Protokollordner laufen. Jeder Viewer hängt an seine Dateien an, ohne vom anderen zu wissen; nacheinander ist es unproblematisch.
- **Unterhaltungsliste von Firestorm:** Der Puris Viewer ändert die Datei `conversation.log` nicht. Unterhaltungen, die Sie nur im Puris Viewer geführt haben, erscheinen deshalb nicht in der Unterhaltungsliste von Firestorm. Die Protokolldateien selbst liegen im gemeinsamen Ordner.

---

## 6. Weltkarte, Minimap & Teleport

Eines der Highlights des Viewers sind die modernen Navigations- und Karten-Tools.

### Minimap (Radar)
- Die Minimap zeigt Ihren Avatar im Zentrum und alle Avatare in Ihrer Nähe als Punkte. Sie können stufenlos mit dem Mausrad hinein- und herauszoomen.
- **Regionsbild:** Unter den Punkten sehen Sie das Kartenbild der Region, wie im Firestorm. Nachbarregionen erscheinen etwas dunkler, und wo keine Region liegt, bleibt der Hintergrund dunkel. Das Bild kommt vom Kartenserver des Grids und wird auf Ihrem Rechner zwischengespeichert; erscheint es nicht sofort, wird es im Hintergrund nachgeladen.
- **Aufbau:** Oben ein Namensfilter mit Zahnrad- und Sortier-Menü, darunter die Karte, darunter die Tabelle aller erfassten Avatare der Region. Die Trennlinie zwischen Karte und Tabelle lässt sich mit der Maus verschieben; die Höhe wird gemerkt.
- **Punktfarben:** Freunde sind grün, andere Avatare rot, stumm geschaltete grau. Der ausgewählte Avatar hat einen weißen Ring. Dieselbe Farbe steht als Punkt vor dem Namen in der Tabelle.
- **Höhe auf der Karte:** Ein Avatar, der mehr als 7 m über Ihnen ist, erscheint als Dreieck nach oben, einer mehr als 7 m unter Ihnen als Dreieck nach unten, auf gleicher Höhe als Punkt. Ist die Höhe nicht bekannt (nur grobe Ortung ganz oben), sehen Sie einen hohlen Ring.
- **Objekte:** die Objektzahlen der Parzelle und der Region sowie die Liste der **Objektbesitzer** mit der Anzahl ihrer Objekte (meiste zuerst). Die Liste wird erst abgefragt, wenn Sie den Reiter öffnen, und danach nur bei einem Parzellenwechsel oder mit **Aktualisieren**; die Sim liefert sie nur an Einwohner, die die Parzelle verwalten dürfen, und verweigert sie die Liste oder schweigt, steht dort ein Hinweis (in Orange) statt „Nichts gefunden“. Zurückgeben, Anzeigen und die automatische Rückgabe sind sichtbar, aber ausgegraut.
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
