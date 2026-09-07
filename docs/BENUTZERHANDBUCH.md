# Puris Viewer (SLNG) - Umfassendes Benutzerhandbuch

Willkommen beim Benutzerhandbuch für den **Puris Viewer** (internes Projekt: *SLNG*). Dieser Next-Generation-Viewer für Second Life und OpenSimulator kombiniert das bewährte LibreMetaverse-Protokoll mit der modernen Rendering-Engine von Godot 4.

> **Status:** Alpha. Es kommen kontinuierlich neue Funktionen hinzu.

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
- **Reiter:** Das Inventar-Fenster hat drei Reiter – *Inventar* (der komplette Ordnerbaum), *Angezogen* (was Ihr Avatar gerade trägt) und *Outfits* (gespeicherte Outfits).
- **Filterleiste:** Das Such-/Filterfeld sitzt fest oben unter den Reitern und wirkt in jedem Reiter. Der eingegebene Text filtert jeweils die gerade sichtbare Liste; beim Wechsel des Reiters bleibt der Filter erhalten.

---

## 4. Kamera & Steuerung

### Bewegen & Interaktion
- **Tastatur:** Bewegen Sie sich wie gewohnt mit `W`, `A`, `S`, `D` oder den Pfeiltasten.
- **Kontext-Mauszeiger:** Der Mauszeiger ändert sich automatisch, wenn Sie über Objekte fahren:
  - *Stuhl-Symbol:* Ein Linksklick (oder Rechtsklick -> `Sit`) lässt Ihren Avatar Platz nehmen. Ein erneuter Druck auf eine Bewegungstaste lässt Sie aufstehen.
  - *Hand-Symbol:* Sie können das Objekt berühren (Touch).
- **Fliegen:** Aktivieren Sie den Flugmodus (Standard: Taste `F` oder über das Menü), um die Region von oben zu erkunden.

### Kamerasteuerung (Alt-Zoom & Co.)
- **Mauskamera (Alt+Zoom):** Halten Sie `Alt` gedrückt und klicken Sie mit der linken Maustaste auf ein Objekt oder einen Avatar, um diesen zu fokussieren. Mit Mausbewegungen können Sie dann darum kreisen oder hineinzoomen.
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
- **Gruppen (Group Chat):** Treten Sie Gruppenchats bei, lesen Sie Nachrichten (die als eigene Chat-Tabs erscheinen) und verwalten Sie Ihre Mitgliedschaften.
  - *Stummschalten (Mute):* Wenn ein Gruppenchat zu viel spammt, können Sie ihn direkt stummschalten. Der Tab öffnet sich dann nicht mehr von selbst.
- **Skript-Dialoge (llDialog):** Popups von In-World-Skripten (z. B. Teleporter-Menüs oder Optionen von Möbeln) erscheinen als übersichtliche UI-Fenster mit interaktiven Buttons.

---

## 6. Weltkarte, Minimap & Teleport

Eines der Highlights des Viewers sind die modernen Navigations- und Karten-Tools.

### Minimap (Radar)
- Die Minimap zeigt Ihren Avatar im Zentrum und alle Avatare in Ihrer Nähe als Punkte. Sie können stufenlos mit dem Mausrad hinein- und herauszoomen.
- **Roster-Liste:** Neben der Karte sehen Sie eine Liste aller erfassten Avatare in der Region.
- **Radar-Fokus:** Ein Klick auf einen Eintrag in der Liste markiert den Punkt des Avatars auf dem Radar.
- **Kamera-Fokus:** Ein Doppelklick auf einen Namen dreht Ihre Kamera automatisch und sanft in eine frontale ("Portrait"-) Ansicht des entsprechenden Avatars.

### Weltkarte
- Über das Menü "World" öffnen Sie die große Weltkarte.
- **Suchen & Teleport:** Suchen Sie direkt nach Regionen (z.B. `Ahern`). Ein Doppelklick auf ein Quadrat auf der Karte löst sofort einen Teleport dorthin aus.
- Sie können die Weltkarte stufenlos verschieben und hineinzoomen, da Texturkacheln fließend nachgeladen werden.

### Landmarken
- Nutzen Sie Landmarken aus Ihrem Inventar per Doppelklick zum Teleportieren.
- Aktuelle Regionen lassen sich mühelos als neue Landmarke abspeichern.

---

## 7. Profile

Das voll ausgestattete Benutzerprofil-Fenster ist zentral für Ihre SL-Identität:
- **Eigene Bearbeitung:** Bearbeiten Sie Ihre eigene *1st-Life-* und *2nd-Life-Beschreibung*, fügen Sie Ihre Webseite hinzu und setzen Sie Einstellungen (Mature Content, In Search). Alles wird über "Save Profile" live im Grid gespeichert.
- **Picks & Classifieds:** Bewundern Sie die Picks anderer Avatare. Der Puris Viewer zeigt vollständige Picks (inkl. Snapshot, Text und "Teleport"-Button zum Aufnahmeort) an.
- **Lokale Notizen:** Sie können jedem Avatar lokale, nur für Sie sichtbare private Notizen hinzufügen.

---

## 8. Avatare & Rendering-Optionen

Der Puris Viewer legt höchsten Wert auf eine optisch ansprechende, korrekte Darstellung:
- **Bento & Bakes-on-Mesh (BoM):** Moderne Mesh-Körper, Bento-Skelette, Animationen, Alpha-Masken und klassische Systemkleidung (über BoM gebacken) werden vollständig unterstützt.
- **Grafikeinstellungen:** Im Einstellungsfenster (`Preferences`) -> `Grafik` können Sie Funktionen wie die Sichtweite (Draw Distance), Anti-Aliasing (MSAA) und die Qualität der dynamischen Echtzeit-Schatten (CSM) anpassen.
- **Asynchrones Textur-Streaming:** Um Ruckler ("Stutter") beim Erkunden zu vermeiden, lädt und dekodiert der Viewer JPEG2000-Texturen asynchron im Hintergrund. Texturen, die näher an der Kamera sind, werden priorisiert geladen.

---

## 9. Bekannte Probleme (Troubleshooting)

Da sich der Client in einer laufenden Alpha-Phase befindet, helfen Ihnen folgende Hinweise:
- **"Wolken-Avatare":** Wenn Avatare oder Objekte kurzzeitig grau/als Wolke dargestellt werden, warten Sie einen Augenblick. Das Hintergrund-Streaming (`CoreJ2K`) decodiert die Texturen gerade noch.
- **Abbrechen von Teleports:** Wenn Sie im Weltkarten-Menü "A teleport is already in progress" lesen, läuft im Hintergrund bereits ein Teleport-Vorgang. Der Puris Viewer blockiert hier gezielt Überlappungen, um korrupte Verbindungen (Race Conditions) zu vermeiden.
- **Objekt-Detach funktioniert nicht:** Ein Klick auf "Detach" im Inventar räumt nun automatisch kaputte Server-Verknüpfungen auf, falls ein Kleidungsstück fehlerhaft anhing. 

---

## 10. Lizenzen & TPV

Dieser Viewer nutzt Texturen aus dem Bestand von Linden Lab (wie das Windlight Cloud Texture und den Terrain Blend Ramp). Details und rechtliche Rahmenbedingungen hierzu finden Sie unter dem Menüpunkt **Preferences → Licences** oder in der bereitgestellten `THIRD-PARTY-NOTICES.md`. 
Zudem hält sich der Puris Viewer strikt an die Third-Party Viewer (TPV) Policy, respektiert Berechtigungen (Permissions) und umgeht keine DRM-Schutzmechanismen des Grids.
