# [BUG-INV-14] A refused folder request reads as an empty folder

- **Feature ID:** `BUG-INV-14`
- **Track:** `net` / `ui`
- **Status:** `🧪 Review`
- **Owner:** `claude`
- **Spec / Roadmap:** [ROADMAP.md](file:///E:/Git/SLNG/docs/ROADMAP.md)

## Overview & Goal
After a login that restored the inventory cache, the window showed "My Inventory" with "(empty)"
under it, Library fine, and nothing in the log. The cache was suspected first — the first session
on a new path builds one — and cleared: a restore cannot empty the root (the login skeleton lists
every folder before the restore runs, a restore only adds items, and the root's own flag is never
touched). `InventoryRootTests` runs no cache → restore → restore again against a local stand-in
for `FetchInventoryDescendents2` and the root is complete in every generation.

What does empty it: LibreMetaverse's `HttpCapsClient` never reads the HTTP status, so a refused
POST comes back from `RequestFolderContentsAsync` as an empty list — no exception, no log line,
the folder's `NeedsUpdate` untouched — and `FolderContentsAsync` falls back to the store only for
`null`. SLNG drew it as an empty folder and the panel never asked again.

## Acceptance Criteria
- [x] A folder the grid did not fill is never returned as empty: it is asked for again, then the
      local contents are returned and the folder keeps its flag.
- [x] The panel does not mark such a folder loaded; collapse/expand asks the grid again.
- [x] A restore that leaves My Inventory without a folder is discarded and logged.
- [x] Tests reproduce the empty root (they fail on the old fetch) and pin the cache cycle.
- [ ] Seen in-world: three logins in a row (no cache, restore, restore) show the whole inventory.

## Technical Specs & Affected Files
- `src/SLNG.Net/GridSession.Inventory.cs` — `FetchFolderFromGridAsync`, `RootHasFolders`,
  `DiscardRestoredContents`, `InventoryFetchUriOverride` (test seam).
- `app/scripts/UI/InventoryPanel.cs` — `FetchAsync` keeps a partial folder retryable.
- `tests/SLNG.Net.Tests/InventoryRootTests.cs`, `FakeInventoryCaps.cs`.

## Not proven
The report's log shows no failed request (that is the point: a refusal raises nothing), so the
refusal itself was not seen on the wire. If the root is empty again with `[Inventory] the grid did
not fill` absent from the log, the cause is something else.
