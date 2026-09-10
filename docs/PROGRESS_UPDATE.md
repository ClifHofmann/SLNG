# Fortschritt aktualisieren — „Puris Viewer — Fortschritt"

Handlungsanweisung für das Fortschritts-Dashboard. Gilt gleichermaßen für Claude Code,
Gemini CLI und den Menschen davor.

## Was das Dashboard ist

Die öffentliche Statusseite liegt auf **GitHub Pages: <https://clifhofmann.github.io/SLNG/>**
und wird **automatisch** erzeugt und veröffentlicht — vom Workflow
`.github/workflows/dashboard.yml`, bei jedem Push auf `main`, der `docs/ROADMAP.md`,
`docs/specs/**` oder `tools/roadmap-dashboard.py` berührt (oder manuell über
Actions → „Progress dashboard" → „Run workflow").

`tools/roadmap-dashboard.py` baut die Seite aus:

| Quelle | Was daraus wird |
|---|---|
| `docs/ROADMAP.md` | Meilensteine, Aufgaben, Statusflags, Track, „Done when" |
| Git-Historie | Commit-Zahl, letzter Commit, Commits pro Woche nach Commit-Typ |
| `app/scripts/Boot.cs` | die Versionsnummer im Kopf (`AppVersion`) |
| `dotnet test` im Workflow | die Kachel „Tests grün" |

Daraus folgen drei Regeln:

1. **`docs/dashboard.html` niemals von Hand bearbeiten.** Die Datei steht in `.gitignore`,
   ist also ein Build-Ergebnis. Der Workflow erzeugt sie frisch in `_site/index.html` und
   committet sie nie.
2. **Es gibt keine zweite Liste.** Ändert sich der Fortschritt, ändert sich `ROADMAP.md`
   — sonst nichts. (Das frühere Claude-Artifact ist abgelöst und zeigt nur noch auf die
   Pages-URL.)
3. **Das Dashboard erfindet nichts.** Steht eine fertige Aufgabe noch auf 🚧, zeigt das
   Dashboard 🚧. Schritt 1 unten ist deshalb der einzige, bei dem wirklich nachgedacht wird.

## Ablauf

### 1 — `docs/ROADMAP.md` auf den Stand bringen

Für jede Aufgabe, die sich seit dem letzten Mal bewegt hat, das Statusflag in der
**Task-Spalte** setzen. Erlaubt sind genau vier, festgelegt in `AGENTS.md`
(„Feature Tracking & ID Convention"):

| Flag | Bedeutung | Dashboard |
|---|---|---|
| `⏸️` | Offen, nicht begonnen | Offen |
| `🚧` | In Arbeit | In Arbeit |
| `🧪` | Implementiert, wartet auf Review/Verifikation | Review |
| `✅` | Fertig | Fertig |

Der Parser liest das Flag als **letztes Zeichen der Task-Zelle** (`strip_flag()`,
`tools/roadmap-dashboard.py`). Ein Flag mitten im Text zählt nicht. Am sichersten ist es,
ein vorhandenes Flag aus einer Nachbarzeile zu kopieren, statt eines zu tippen — `⏸️`
enthält einen unsichtbaren Variation Selector.

**Wann darf ein Flag auf ✅?** Nur mit Beleg, nicht nach Gefühl:

- Die Acceptance Criteria in `docs/specs/<ID>-*.md` sind alle abgehakt, **und**
- der Code liegt auf `main` (`git log --oneline --grep "<ID>"`), **und**
- `dotnet build` + `dotnet test` sind grün.

Fehlt eines davon, ist der ehrliche Status 🧪, nicht ✅.

Beim Umstellen auf ✅ zusätzlich:

- Die Zeile `**Status:**` in der zugehörigen Spec-Datei mitziehen — sonst widersprechen
  sich Roadmap und Spec.
- Die „Done when"-Spalte um einen Satz ergänzen, **was** gelandet ist und **womit** es
  belegt ist (Commit-Hashes, Live-Verifikation). Diese Spalte ist das Logbuch des
  Projekts; das Dashboard zeigt nur die erste Zeile davon.

Aufgaben eines anderen Owners dürfen hier mit Beleg umgestellt werden — es ist eine
Dokumentationsänderung, keine Übernahme der Aufgabe. Im Commit erwähnen.

### 2 — (optional) lokal ansehen

```bash
python tools/roadmap-dashboard.py --tests $(dotnet test SLNG.sln --nologo | \
  grep -oE 'Passed:[[:space:]]+[0-9]+' | grep -oE '[0-9]+' | awk '{s+=$1} END{print s}')
```

Öffnet man `docs/dashboard.html` im Browser zum Gegenlesen. `--tests` ist nur für die
Kachel; ohne den Schalter fehlt sie kommentarlos. Bricht der Parser mit
`no milestones parsed` ab, ist die Roadmap-Tabelle kaputt (weniger als 8 Spalten in
einer Zeile).

### 3 — Committen und pushen

```bash
git add docs/ROADMAP.md docs/specs
git commit -m "docs: update roadmap status"
git push origin main
```

`docs/dashboard.html` taucht dabei nicht auf — sie ist ignoriert. Der Push löst den
`dashboard.yml`-Workflow aus; nach ~2–3 min (Build + Test) steht der neue Stand auf
<https://clifhofmann.github.io/SLNG/>. Status des Laufs: Repo → Actions → „Progress
dashboard".

## Fallstricke

- **Handänderung am HTML** — beim nächsten Lauf weg, und die Datei ist ohnehin ignoriert.
- **Flag nicht am Zellenende** — die Aufgabe landet stillschweigend im falschen Eimer
  (ohne erkanntes Flag: Tabellenzeilen zählen als „Offen", Checkboxlisten als „Fertig").
- **Meilenstein-Überschriften tragen ebenfalls Flags** (`## MVP 1.5 — … 🚧`). Die zählen
  nicht als Aufgaben; deshalb ist die Zahl der ✅ in der Datei größer als die Kachel
  „Fertig".
- **Version im Kopf ist alt** — kein Dashboard-Problem, sondern ein vergessener
  `AppVersion`-Bump im vorangegangenen Feature-Commit.
- **Workflow lief nicht** — der Pfad-Filter deckt nur `docs/ROADMAP.md`, `docs/specs/**`
  und das Script ab. Andere Commits aktualisieren die Seite nicht; dann von Hand über
  Actions → „Run workflow".

## Kurzfassung

```bash
# docs/ROADMAP.md: Statusflags + Spec-Status + "Done when" nachziehen
git add docs/ROADMAP.md docs/specs && git commit -m "docs: update roadmap status"
git push origin main
# -> .github/workflows/dashboard.yml baut + deployt https://clifhofmann.github.io/SLNG/
```
