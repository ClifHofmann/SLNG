# Fortschritt aktualisieren — "Puris Viewer — Fortschritt"

Handlungsanweisung für das Fortschritts-Dashboard. Gilt gleichermaßen für Claude Code,
Gemini CLI und den Menschen davor. Der Ablauf ist bewusst kurz, weil das Dashboard
**erzeugt** und nicht gepflegt wird.

## Was das Dashboard ist

`docs/dashboard.html` wird von `tools/roadmap-dashboard.py` aus zwei Quellen erzeugt:

| Quelle | Was daraus wird |
|---|---|
| `docs/ROADMAP.md` | Meilensteine, Aufgaben, Statusflags, Track, Owner, "Done when" |
| Git-Historie | Commit-Zahl, letzter Commit, Commits pro Woche nach Commit-Typ |
| `app/scripts/Boot.cs` | die Versionsnummer im Kopf (`AppVersion`) |
| `--tests N` | die Kachel "Tests grün" |

Daraus folgen drei Regeln:

1. **`docs/dashboard.html` niemals von Hand bearbeiten.** Die Datei steht in `.gitignore`
   (Zeile 60), ist also ein Build-Ergebnis und kein Dokument. Jede Handänderung ist beim
   nächsten Lauf weg.
2. **Es gibt keine zweite Liste.** Ändert sich der Fortschritt, ändert sich `ROADMAP.md`
   — sonst nichts.
3. **Das Dashboard erfindet nichts.** Steht eine fertige Aufgabe noch auf 🚧, zeigt das
   Dashboard 🚧. Schritt 1 ist deshalb der einzige, bei dem wirklich nachgedacht wird.

## Ablauf

### 0 — Ausgangslage

Auf `main`, aktueller Stand, sauberer Arbeitsbaum:

```bash
git switch main && git pull --ff-only && git status --short
```

### 1 — `docs/ROADMAP.md` auf den Stand bringen

Für jede Aufgabe, die sich seit dem letzten Lauf bewegt hat, das Statusflag in der
**Task-Spalte** setzen. Erlaubt sind genau vier, festgelegt in `AGENTS.md`
("Feature Tracking & ID Convention"):

| Flag | Bedeutung | Dashboard |
|---|---|---|
| `⏸️` | Offen, nicht begonnen | Offen |
| `🚧` | In Arbeit | In Arbeit |
| `🧪` | Implementiert, wartet auf Review/Verifikation | Review |
| `✅` | Fertig | Fertig |

Der Parser liest das Flag als **letztes Zeichen der Task-Zelle** (`strip_flag()`,
`tools/roadmap-dashboard.py:55`). Ein Flag mitten im Text zählt nicht. Am sichersten ist
es, ein vorhandenes Flag aus einer Nachbarzeile zu kopieren, statt eines zu tippen — `⏸️`
enthält einen unsichtbaren Variation Selector.

**Wann darf ein Flag auf ✅?** Nur mit Beleg, nicht nach Gefühl:

- Die Acceptance Criteria in `docs/specs/<ID>-*.md` sind alle abgehakt, **und**
- der Code liegt auf `main` (`git log --oneline --grep "<ID>"`), **und**
- `dotnet build` + `dotnet test` sind grün (Schritt 2 erledigt das ohnehin).

Fehlt eines davon, ist der ehrliche Status 🧪, nicht ✅.

Beim Umstellen auf ✅ zusätzlich:

- Die Zeile `**Status:**` in der zugehörigen Spec-Datei mitziehen — sonst widersprechen
  sich Roadmap und Spec.
- Die "Done when"-Spalte um einen Satz ergänzen, **was** gelandet ist und **womit** es
  belegt ist (Commit-Hashes, Live-Verifikation). Diese Spalte ist das Logbuch des
  Projekts; sie ist der Grund, warum das Dashboard nur die erste Zeile davon zeigt.

Aufgaben eines anderen Owners (`gemini` vs. `claude`) dürfen hier mit Beleg umgestellt
werden — es ist eine Dokumentationsänderung, keine Übernahme der Aufgabe. Im Commit
erwähnen.

### 2 — Testzahl ermitteln

```bash
dotnet test SLNG.sln --nologo
```

Die Zahl für `--tests` ist die **Summe** der drei "erfolgreich"-Werte aus
`SLNG.Core.Tests`, `SLNG.Assets.Tests` und `SLNG.Net.Tests`. Sind Tests rot, erst die
Tests reparieren — eine grüne Kachel über einer roten Suite ist eine Falschaussage.

> Dev-Maschine: ein Runtime-only `dotnet` in `C:\Program Files\dotnet` gewinnt die
> PATH-Reihenfolge und meldet "no SDK". Dann `. tools/dev-env.ps1` sourcen oder direkt
> `%USERPROFILE%\.dotnet\dotnet.exe` aufrufen (siehe `AGENTS.md`).

### 3 — Dashboard erzeugen

```bash
python tools/roadmap-dashboard.py --tests 322
```

`--tests` immer mitgeben — ohne den Schalter verschwindet die Kachel "Tests grün"
kommentarlos aus dem Kopf. Optional: `-o <pfad>`, `--roadmap <pfad>`, `--weeks 18`.

Das Skript gibt eine Zeile aus, z. B. `docs/dashboard.html — 7 milestones, 71 tasks`.
Bricht der Parser mit `no milestones parsed` ab, ist die Roadmap-Tabelle kaputt (weniger
als 8 Spalten in einer Zeile).

### 4 — Gegenlesen

Kopfzeile prüfen: Version = `AppVersion` aus `app/scripts/Boot.cs`, "Zuletzt" = der
tatsächlich letzte Commit, Aufgaben-/Fertig-Zahlen plausibel gegenüber dem letzten Lauf.

```bash
grep -o "<dt>[^<]*</dt><dd>[^<]*</dd>" docs/dashboard.html
```

Stimmt die Version nicht, wurde `AppVersion` bei der eigentlichen Änderung vergessen —
das gehört in den Feature-Commit, nicht hierher (Versioning-Regel in `AGENTS.md`).

### 5 — Veröffentlichen

**Claude Code** publiziert die Datei als Artifact — immer unter derselben, festen URL:

```
https://claude.ai/code/artifact/41601392-9588-4f97-b111-7b39bdc94291
```

Aufruf: das `Artifact`-Tool mit `file_path: docs/dashboard.html` **und** `url:` der
obigen Adresse. Ohne `url` entsteht ein zweites, konkurrierendes Artifact. Wurde die
Live-Fassung in dieser Sitzung noch nicht gelesen, verlangt das Tool zuerst ein
`action: "read"` auf dieselbe URL — das ist erwartet und kein Fehler.

**Gemini CLI** kann keine Artifacts veröffentlichen. Gemini beendet den Ablauf nach
Schritt 4 und übergibt: `docs/dashboard.html` liegt lokal fertig vor und lässt sich im
Browser öffnen; das Publizieren übernimmt Claude Code oder der Mensch in einer eigenen
Sitzung. Der inhaltliche Teil — Schritte 1 bis 4 — ist der Teil, auf den es ankommt, und
den kann Gemini vollständig.

### 6 — Committen

```bash
git add docs/ROADMAP.md docs/specs
git commit -m "docs: update roadmap status and progress dashboard"
```

`docs/dashboard.html` taucht dabei nicht auf — sie ist ignoriert. Wer sie im
`git status` sucht, sucht vergeblich; das ist Absicht.

## Fallstricke

- **Handänderung am HTML** — beim nächsten Lauf weg, und die Datei ist ohnehin ignoriert.
- **Flag nicht am Zellenende** — die Aufgabe landet stillschweigend im falschen Eimer
  (ohne erkanntes Flag: Tabellenzeilen zählen als "Offen", Checkboxlisten als "Fertig").
- **Meilenstein-Überschriften tragen ebenfalls Flags** (`## MVP 1.5 — … 🚧`). Die zählen
  nicht als Aufgaben; deshalb ist die Zahl der ✅ in der Datei größer als die Kachel
  "Fertig".
- **`--tests` vergessen** — Kachel fehlt, ohne Fehlermeldung.
- **Zweites Artifact angelegt** — passiert, sobald ohne `url` publiziert wird. Dann hat
  der Nutzer zwei Links, von denen einer einfriert.
- **Version im Kopf ist alt** — kein Dashboard-Problem, sondern ein vergessener
  `AppVersion`-Bump im vorangegangenen Feature-Commit.

## Kurzfassung

```bash
git switch main && git pull --ff-only
# docs/ROADMAP.md: Statusflags + Spec-Status + "Done when" nachziehen
dotnet test SLNG.sln --nologo          # Summe der drei "erfolgreich"-Werte merken
python tools/roadmap-dashboard.py --tests <Summe>
# Claude Code: Artifact publizieren mit url=…/41601392-9588-4f97-b111-7b39bdc94291
git add docs/ROADMAP.md docs/specs && git commit -m "docs: update roadmap status and progress dashboard"
```
