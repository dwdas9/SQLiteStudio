# SQLiteStudio Roadmap

This roadmap tracks two source-only desktop applications in the same repository:

- **Python studio** — `studio.py`, cross-platform, Python standard library only.
- **C# studio** — `SQLiteStudio.cs`, Windows, .NET 10 file-based WinForms app using the system `winsqlite3.dll` with no NuGet packages.

The editions share product goals but have different constraints. The Python studio prioritizes broad features, cross-platform use and zero third-party Python packages. The C# studio prioritizes a native Windows interface, Visual Studio use and stricter core workflows while remaining a single source file with no checked-in executable or project file.

- **Priority:** P0 = correctness or safety, P1 = high user value, P2 = polish or differentiation.
- **Effort:** S = hours, M = a day or two, L = several days, XL = a project.
- **Status:** Done applies to the edition named in the item. A feature completed in one edition is not automatically complete in the other.

## Current edition status

| Area | Python studio | C# studio |
|---|---|---|
| Core browse and edit | Mature | Implemented |
| Stable row identity and composite keys | Implemented | Implemented |
| SQL execution, history and cancellation | Implemented | Implemented |
| Schema inspection | Implemented | Implemented |
| Visual schema editing | Implemented | Planned; use SQL today |
| Import and full-table export | Implemented | Planned |
| Projects, backup and SQL dumps | Implemented | Planned |
| Search, profiling and charts | Implemented | Planned |
| Cross-platform support | Windows, macOS, Linux | Windows only by design |
| Automated verification | Python core tests need continued maintenance | Clean .NET build and provider smoke coverage; automated tests planned |

The numbered backlog below originated from the Python studio and still uses Python/Tk terminology where appropriate. Items that also apply to the C# edition say so explicitly. A dedicated C# parity section follows the shared backlog.

## 1. Correctness and data safety P0

| # | Item | Effort | Why |
|---|---|---:|---|
| 1.1 | **Done in both editions — composite and `WITHOUT ROWID` key support** for edit and delete. | M | Prevents incomplete key predicates. |
| 1.2 | **Done in both editions — stable row identity under non-unique sorting.** Display values and keys are fetched together, and keys provide deterministic sort tiebreakers. | M | Prevents silent wrong-row edits. |
| 1.3 | **Done in both editions — guarded key loading.** Failed reads do not leave partially aligned key state. | S | Makes failure predictable. |
| 1.4 | **Done in both editions — external-change warning before commit** using `PRAGMA data_version`. | M | Reduces stale-write risk. |
| 1.5 | **Partly done — clear uncommitted-change state.** Both editions expose pending state and explicit commit/rollback. An optional autocommit mode remains open. | M | Makes transaction state visible. |
| 1.6 | **Done in Python — destructive schema confirmations include affected rows.** Add equivalent visual schema actions before marking this complete in C#. | S | Prevents accidental loss. |
| 1.7 | **Done in Python — atomic SQL-script import with failing-statement reporting.** Add a dedicated import path in C#. | M | Makes imports recoverable. |

## 2. Performance and responsiveness P0 P1

| # | Item | Effort | Why |
|---|---|---:|---|
| 2.1 | **Done in both editions — SQL work runs away from the UI thread.** | L | Keeps the window responsive. |
| 2.2 | **Done in both editions — cancellable SQL.** | M | Required for long-running queries. |
| 2.3 | **Done in both editions — query timing and SQL log status.** | S | Makes query cost visible. |
| 2.4 | **Done in both editions — five-second busy timeout.** | S | Handles concurrent access more gracefully. |
| 2.5 | **Done in both editions — WAL requested for writable databases when supported.** | S | Improves ordinary concurrency. |
| 2.6 | **Shared — streaming or virtual grid.** Render only visible rows for very wide or tall results. | L | Current GUI grids degrade on large result sets. |
| 2.7 | **Shared — optional keyset pagination** instead of high `LIMIT/OFFSET` pages. | M | Scales to millions of rows. |
| 2.8 | **Done in Python; planned in C# — row-count cache** invalidated by data and schema changes. | M | Avoids repeated expensive counts. |

## 3. SQL workspace power features P1

| # | Item | Effort | Why |
|---|---|---:|---|
| 3.1 | **Done in both editions — named and positional parameter prompts.** | M | Enables safe reusable queries. |
| 3.2 | **Shared — per-statement result tabs.** The current workspaces show the final result set. | M | Improves script analysis. |
| 3.3 | **Shared — result paging and export beyond 5,000 displayed rows.** Both editions report truncation. | M | Avoids ambiguity on large results. |
| 3.4 | **Shared — editable query results** when a result maps safely to one keyed table. | L | Extends safe editing beyond table browse. |
| 3.5 | **Python and C# — context-aware autocomplete** for tables, aliases and columns. | M | Speeds query authoring. |
| 3.6 | **Done in both editions — lightweight SQL formatter.** Continue improving comment, string and nested-query preservation. | M | Improves readability. |
| 3.7 | **Shared — named query library** with descriptions. | M | Supports reuse beyond history. |
| 3.8 | **Shared — query-plan tree** with index-use highlighting. | M | Makes optimization approachable. |
| 3.9 | **Shared — editor find, replace, bookmarks and go-to-line.** | S | Adds expected editor basics. |
| 3.10 | **Shared — crash recovery and SQL-tab persistence.** | M | Prevents lost work. |

## 4. Data grid and editing UX P1

| # | Item | Effort | Why |
|---|---|---:|---|
| 4.1 | **Done in C#; planned for full consistency in Python — distinct `NULL` rendering.** | S | Prevents confusion with empty strings. |
| 4.2 | **Shared — richer type-aware editors:** dates, booleans, numeric validation and JSON formatting. | L | Reduces entry errors. |
| 4.3 | **Partly done in C# — BLOB hex editing and preview text.** Add image thumbnails and richer Python parity. | M | Makes binary data understandable. |
| 4.4 | **Shared — inline multi-row editing and fill-down.** | M | Speeds repetitive edits. |
| 4.5 | **Shared — remembered column visibility, order, freeze state and widths.** | M | Improves wide-table use. |
| 4.6 | **Shared — quick filter row below column headings.** | M | Makes common filtering immediate. |
| 4.7 | **Done in Python; partial in C# — copy/export formats.** C# currently copies TSV and exports visible CSV/JSON. | S | Improves interoperability. |
| 4.8 | **Shared — paste creates rows or a new table.** | M | Speeds ingest. |
| 4.9 | **Shared — navigable row-detail dialog and foreign-key links.** | M | Supports relational exploration. |
| 4.10 | **Shared — edit-level undo/redo inside a transaction.** | L | Adds confidence beyond full rollback. |

## 5. Schema and structure tooling P1

| # | Item | Effort | Why |
|---|---|---:|---|
| 5.1 | **Python — composite primary keys and foreign keys in the visual designer.** | L | Completes common table design. |
| 5.2 | **Shared — full ALTER support through safe table rebuilds.** | L | Handles SQLite's ALTER limitations. |
| 5.3 | **Shared — CHECK, UNIQUE, DEFAULT and generated-column editors.** | M | Supports realistic schemas. |
| 5.4 | **Shared — index advisor** based on query plans and slow queries. | L | Turns plans into practical guidance. |
| 5.5 | **Shared — trigger and view editors with preview.** | M | Avoids raw-SQL-only management. |
| 5.6 | **Shared — schema diff and migration SQL generation.** | L | Supports team and deployment workflows. |
| 5.7 | **Done in both editions — object filter above the schema tree.** | S | Improves large-schema navigation. |

## 6. Import export and interoperability P1

| # | Item | Effort | Why |
|---|---|---:|---|
| 6.1 | **Done in Python; C# parity planned — JSON import and export.** C# currently exports visible rows only. | M | JSON is a common interchange format. |
| 6.2 | **Shared — XLSX or spreadsheet-compatible HTML export.** | L | Supports Excel-oriented workflows without bundling Excel. |
| 6.3 | **Python partial; C# planned — Markdown and HTML export.** | S | Supports reports and documentation. |
| 6.4 | **Python — advanced CSV import:** delimiter, encoding, header mapping, append, type override and NULL token. | M | Handles real-world files. |
| 6.5 | **Python partial; C# planned — SQL `INSERT` and clipboard-to-table workflows.** | M | Improves database-to-database transfer. |
| 6.6 | **Python partial; C# planned — streaming full-table export and compression.** | S | Handles data beyond the visible page. |
| 6.7 | **Shared — selected-table copy to another SQLite database.** | M | Enables subsets and anonymized sharing. |

## 7. Visualization and analysis P2

| # | Item | Effort | Why |
|---|---|---:|---|
| 7.1 | **Python — richer charts:** scatter, histogram, pie, grouped bars, labels and image export. | M | Expands analysis without another tool. |
| 7.2 | **Done in Python; C# planned — column profile dialog.** | M | Provides quick distributions and quality checks. |
| 7.3 | **Shared — table statistics dashboard.** | M | Shows database health at a glance. |
| 7.4 | **Shared — relationship diagram.** | L | Makes foreign-key structure visible. |
| 7.5 | **Shared — pivot and group-by builder.** | L | Helps analysts work without hand-written SQL. |

## 8. Navigation and discoverability P1

| # | Item | Effort | Why |
|---|---|---:|---|
| 8.1 | **Done in Python; C# planned — recent files and reopen-last.** | S | Improves daily startup. |
| 8.2 | **Done in Python; C# planned — cross-table search.** | M | Answers where a value appears. |
| 8.3 | **Shared — command palette.** | M | Speeds keyboard navigation. |
| 8.4 | **Shared — multiple databases as tabs.** | L | Supports comparison and migration work. |
| 8.5 | **Done in both editions — object filter for large schemas.** | S | Reduces navigation time. |
| 8.6 | **Shared — favorite or pinned objects.** | S | Keeps active tables close. |

## 9. Reliability session and recovery P1

| # | Item | Effort | Why |
|---|---|---:|---|
| 9.1 | **Shared — automatic workspace recovery.** | M | Restores state after restart or crash. |
| 9.2 | **Done in Python; C# planned — persistent friendly exception log.** C# already presents operation errors without raw console output. | S | Makes diagnosis easier. |
| 9.3 | **Python partial; C# planned — plain-language SQLite errors with affected object names.** | M | Makes constraints understandable. |
| 9.4 | **Done in both editions — read-only fallback and visible mode.** | S | Prevents confusing write failures. |

## 10. Security and robustness P1

| # | Item | Effort | Why |
|---|---|---:|---|
| 10.1 | **Shared — restricted mode** for `ATTACH`, file-writing PRAGMAs and extension-related operations. | M | Helps in shared or corporate environments. |
| 10.2 | **Shared — guard dangerous PRAGMAs** such as `writable_schema` behind an expert mode. | S | Reduces corruption risk. |
| 10.3 | **Shared — redaction-aware exports.** | M | Protects sensitive data when sharing. |
| 10.4 | **Done in Python; C# planned — warning and streaming policy for very large database files.** | S | Avoids surprise resource use. |

## 11. Platforms packaging and source distribution P1

| # | Item | Effort | Why |
|---|---|---:|---|
| 11.1 | **Done in Python — platform-aware fonts and cross-platform execution.** Keep platform language in the README accurate. | S | Preserves Windows, macOS and Linux support. |
| 11.2 | **Done in C#; Python planned — explicit high-DPI scaling.** | S | Keeps the GUI readable on modern displays. |
| 11.3 | **Optional executable documentation only.** Source execution remains the primary supported path; do not commit generated executables. | M | Preserves the source-only promise. |
| 11.4 | **Shared — automatic OS-theme detection.** Both editions currently support manual theme switching. | S | Matches user preferences. |
| 11.5 | **Shared — optional file-association helpers.** | S | Enables opening a database from the shell. |

## 12. Accessibility and polish P2

| # | Item | Effort | Why |
|---|---|---:|---|
| 12.1 | **Shared — full keyboard and screen-reader audit.** | M | Makes both studios broadly usable. |
| 12.2 | **Done in Python; C# uses Windows scaling — text zoom controls.** Consider explicit C# zoom for parity. | S | Improves readability. |
| 12.3 | **Shared — richer status details:** selected value type, encoding, transaction state and database size. | S | Adds useful context. |
| 12.4 | **Done in C#; Python planned — consistent grouped toolbar spacing.** | M | Improves scanability. |
| 12.5 | **Shared — localization scaffolding.** | M | Enables translated interfaces. |

## 13. Testing and quality engineering P1

| # | Item | Effort | Why |
|---|---|---:|---|
| 13.1 | **Python — repair and expand the core test entry point.** Keep imports aligned with `studio.py`, then cover edits, composite keys, `WITHOUT ROWID`, CSV, scripts and exports. | M | Protects the mature edition. |
| 13.2 | **Shared — headless GUI smoke tests.** Use platform-appropriate UI automation. | L | Catches wiring and layout regressions. |
| 13.3 | **Shared — golden-file tests** for dumps and exports. | M | Stabilizes interchange formats. |
| 13.4 | **Shared — CI matrix.** Run Python on Windows, macOS and Linux; build and test C# on Windows with .NET 10. | M | Makes release status trustworthy. |
| 13.5 | **Shared — fuzz SQL parsing, parameter detection and CSV inference.** | M | Finds unusual input failures. |
| 13.6 | **C# — extract testable core helpers or add a self-test mode** while keeping the shipped application in one source file. | M | Adds regression coverage without a permanent project. |

## 14. Architecture and code health P1

| # | Item | Effort | Why |
|---|---|---:|---|
| 14.1 | **Python — separate database service and Tk views within the single file.** | L | Improves testability. |
| 14.2 | **Both editions — central guarded connection path.** C# has a common command factory; continue consolidating maintenance and background operations. | M | Keeps transaction behavior consistent. |
| 14.3 | **Done in Python — remove duplicate status work and report result truncation.** C# also reports its 5,000-row limit. | S | Avoids ambiguous results. |
| 14.4 | **Python — type hints, docstrings and static checks.** | M | Improves maintainability. |
| 14.5 | **Shared — stable internal event layer.** | M | Makes features easier to compose. |
| 14.6 | **C# — split large UI construction methods into focused regions or nested components** without introducing required project files. | M | Keeps the single-file app understandable. |

## 15. World class differentiators P2

| # | Item | Effort | Why |
|---|---|---:|---|
| 15.1 | **Shared — FTS5 management and ranked search UI.** | M | Exposes a powerful SQLite feature. |
| 15.2 | **Shared — JSON1 browsing and path builders.** | M | Supports modern SQLite data. |
| 15.3 | **Shared — snapshots and one-click restore.** | M | Makes experimentation safer. |
| 15.4 | **Shared — test-data generator.** | M | Helps development and demos. |
| 15.5 | **Shared — searchable, pinnable and comparable query history.** | M | Helps power users. |
| 15.6 | **Shared — optional table and row metadata.** | M | Keeps documentation near data. |
| 15.7 | **Out of scope by default — hosted natural-language-to-SQL.** It conflicts with the offline, local-first posture. | — | Keeps the trust boundary clear. |
| 15.8 | **Python — opt-in plugin hooks.** Reassess a C# equivalent only after feature parity work. | L | Adds extensibility without changing the core. |

## 16. C# edition parity backlog P1

The C# studio is an additional edition, not a replacement for the Python studio. These items close the most visible gaps while preserving its source-only file-based-app model.

| # | Item | Effort | Why |
|---|---|---:|---|
| C1 | CSV and JSON import with preview, column mapping, type overrides and explicit NULL handling. | L | Restores the most common ingest workflow. |
| C2 | Full-table streaming export to CSV, JSON, Markdown and SQL rather than visible-page export only. | M | Prevents accidental partial exports. |
| C3 | Open and save `.sql` files plus multiple query tabs. | M | Brings the SQL workspace to daily-use parity. |
| C4 | Visual table and index creation, common rename/add/drop operations and guarded destructive confirmations. | L | Avoids requiring raw SQL for ordinary schema work. |
| C5 | Online backup, SQL dump import/export and project save/restore. | L | Supports recovery and repeatable workspaces. |
| C6 | Cross-table search, column profiling and basic charts. | L | Restores exploration tools. |
| C7 | Recent files, reopen-last and per-database grid preferences. | M | Improves repeated use. |
| C8 | Windows UI automation tests for launch, open, browse, edit, rollback, SQL cancellation and read-only mode. | L | Verifies the native interface end to end. |
| C9 | Screenshot set and a short Windows demonstration recording. | M | Documents the additional edition accurately. |

## Suggested sequencing

1. **Protect both editions:** repair the Python test entry point, add Windows C# UI smoke tests and establish CI.
2. **Close C# workflow gaps:** implement C1 through C5 before expanding differentiators.
3. **Improve scale:** add virtual grids, keyset pagination and result paging to both editions.
4. **Improve daily use:** add recovery, recent files, richer editors and accessibility checks.
5. **Add differentiators:** relationship diagrams, schema diff, FTS5 and JSON1 tools.

Preserve the edition contracts throughout: the Python studio remains standard-library-only and cross-platform; the C# studio remains a single .NET file-based app with no checked-in executable or project file.
