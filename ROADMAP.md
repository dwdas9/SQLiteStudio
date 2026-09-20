# SQLiteStudio — Improvement Roadmap

A complete backlog for turning the current single-file viewer into a world-class SQLite
workbench. Grounded in the present code (`sqlite_viewer.py`, ~2,255 lines, standard library
only). Each item has a priority and rough effort.

- **Priority:** P0 = correctness/safety, do first · P1 = high user value · P2 = polish/differentiators
- **Effort:** S = hours · M = a day or two · L = several days · XL = a project

Constraint to preserve throughout: **standard library only, single file, runs on locked-down
machines.** Every idea below is achievable without third-party packages.

---

## 1. Correctness and data safety (P0)

| # | Item | Effort | Why |
|---|------|--------|-----|
| 1.1 | **DONE — composite / WITHOUT ROWID key support** for edit and delete. | M | Was the reported delete bug. |
| 1.2 | **DONE — Fixed wrong-row edits under non-unique sort.** `load_table` fetches display rows and key values in *two separate queries* and zips by position. Under a tie in the sort column the two orderings can diverge, so an edit/delete can hit the wrong row. Select the key columns together with the data in one query, or append the key columns as a stable tiebreaker to every `ORDER BY`. | M | Silent data corruption; highest-severity latent bug. |
| 1.3 | **DONE — key columns now fetched in the same guarded query** as the data load. Today an error there raises unhandled and leaves `current_row_keys` half-populated. | S | Predictable failure. |
| 1.4 | **DONE — Commit warns when `PRAGMA data_version` shows another connection wrote.** If another process writes the DB, warn before committing stale edits. Track file mtime / `PRAGMA data_version`. | M | Prevents lost updates. |
| 1.5 | **PARTLY DONE — toolbar shows an Uncommitted-changes indicator.** Remaining: autocommit toggle. Surface a clear "in transaction / N pending changes" indicator, and let the user choose autocommit vs manual. Currently relies on implicit transactions and `in_transaction`. | M | Clarity and safety. |
| 1.6 | **DONE — Drop table confirmation shows the row count.** (drop table shows "12,340 rows will be lost"). | S | Avoids accidents. |
| 1.7 | **DONE — SQL-script import runs inside a savepoint and reports the failing statement.** (SQL dump) behind a savepoint where possible, and report which statement failed with line number. | M | Recoverable imports. |

## 2. Performance and responsiveness (P0/P1)

| # | Item | Effort | Why |
|---|------|--------|-----|
| 2.1 | **DONE — SQL workspace queries run on a worker thread**; results and the SQL log return to Tk through a polled UI queue. Use a worker thread with its own connection (or a queue to a single DB thread) so the window never freezes. Post results back via `after()`. | L | The single biggest quality gap. |
| 2.2 | **DONE — Cancel button calls `connection.interrupt()`.** Use `connection.interrupt()` from a Cancel button while a long query runs. | M | Table-stakes for a serious tool. |
| 2.3 | **DONE — Query timing.** Show wall-clock ms for every executed statement in the status bar and SQL log. | S | Users expect it. |
| 2.4 | **DONE — `busy_timeout = 5000` set on connect.** (e.g. 5000 ms) so concurrent access retries instead of erroring immediately. | S | Robustness with WAL / other clients. |
| 2.5 | **DONE — WAL journal mode by default** for writable databases (offer opt-out). Faster, better concurrency. | S | Modern default. |
| 2.6 | **Streaming/virtual grid.** For very wide or very tall result sets, render only visible rows. `ttk.Treeview` degrades past a few thousand rows. | L | Handles big tables smoothly. |
| 2.7 | **Keyset pagination option** (`WHERE key > ?`) for huge tables instead of `LIMIT/OFFSET`, which slows at high offsets. | M | Scales to millions of rows. |
| 2.8 | **DONE — row counts cached per table/filter, invalidated by data_version, total_changes and schema changes.** `SELECT COUNT(*)` on every page load is expensive on large tables; compute once, show "~N" estimate from `sqlite_stat1` when available, refresh on demand. | M | Snappier navigation. |

## 3. SQL workspace power features (P1)

| # | Item | Effort | Why |
|---|------|--------|-----|
| 3.1 | **DONE — `:name`/`?` parameters are prompted for and bound.** Detect `:name` / `?` and prompt for values, bound safely. | M | Safe, reusable queries. |
| 3.2 | **Per-statement results.** Running a multi-statement script shows only the last result set; give each `SELECT` its own result tab. | M | Real scripting. |
| 3.3 | **Result-set paging and export beyond 5,000 rows.** Currently `fetchmany(5000)` truncates silently — at least warn, ideally page. | M | No silent truncation. |
| 3.4 | **Editable query results** when the result maps to one table with a key (reuse the row-key logic). | L | Edit from arbitrary queries. |
| 3.5 | **Smarter autocomplete.** Context-aware (columns of the table in `FROM`), not just a flat keyword list; snippet expansion. | M | Faster authoring. |
| 3.6 | **DONE — Format button (keywords, one clause per line).** A standard-library formatter (indent, keyword case). | M | Readability. |
| 3.7 | **Named/saved queries library** with descriptions, separate from history. | M | Reuse. |
| 3.8 | **`EXPLAIN QUERY PLAN` as a tree** with index-usage highlighting, plus a "no index used" warning. | M | Teaches optimization. |
| 3.9 | **Bookmarks / go-to-line / find-replace** in the editor. | S | Editor basics. |
| 3.10 | **Persist open SQL tabs across sessions** (crash recovery / autosave). | M | Never lose work. |

## 4. Data grid and editing UX (P1)

| # | Item | Effort | Why |
|---|------|--------|-----|
| 4.1 | **Distinct NULL vs empty-string rendering** (grey italic `NULL`), consistently across grid, details, and copy. | S | Correctness of perception. |
| 4.2 | **Type-aware cell editors:** date picker, boolean toggle, numeric validation, JSON pretty-printer for TEXT that parses as JSON. | L | Feels modern. |
| 4.3 | **BLOB previews:** detect image blobs (PNG/JPEG magic bytes) and show a thumbnail in the cell editor; hex + text views for others. | M | Common need. |
| 4.4 | **Inline multi-row editing and fill-down** (Excel-style). | M | Speed. |
| 4.5 | **Column show/hide, reorder, freeze, and remembered widths** per table. | M | Wide tables. |
| 4.6 | **Quick per-column filter row** under headers (in addition to the filter builder). | M | Fast exploration. |
| 4.7 | **DONE — Copy as...** button: TSV (current), CSV, JSON, Markdown table, SQL `INSERT`s. | S | Interop. |
| 4.8 | **Paste creates rows / new table** from clipboard TSV. | M | Fast ingest. |
| 4.9 | **Row-detail dialog: edit in place, navigate prev/next**, and show foreign-key link-throughs ("jump to referenced row"). | M | Relational browsing. |
| 4.10 | **Undo/redo stack for data edits** within a transaction (beyond all-or-nothing rollback). | L | Confidence. |

## 5. Schema and structure tooling (P1)

| # | Item | Effort | Why |
|---|------|--------|-----|
| 5.1 | **Composite primary keys and foreign keys in the visual table designer** (today it defers to SQL). | L | Completeness. |
| 5.2 | **Full ALTER support via table rebuild** (change type, drop/reorder columns, add constraints) using the safe 12-step `CREATE new / copy / drop / rename` recipe inside a transaction. | L | SQLite's biggest gap, handled well. |
| 5.3 | **CHECK / UNIQUE / DEFAULT / generated-column editors** in the designer. | M | Real schema work. |
| 5.4 | **Index advisor:** suggest indexes from `EXPLAIN QUERY PLAN` scans and slow queries. | L | Differentiator. |
| 5.5 | **Trigger and view editors** with SQL preview (currently create via SQL only). | M | Parity. |
| 5.6 | **Schema diff** between two databases; generate migration SQL. | L | Team workflows. |
| 5.7 | **DONE — filter box above the object browser.** find a column/table/index by name across the database. | S | Navigation. |

## 6. Import, export, and interoperability (P1)

| # | Item | Effort | Why |
|---|------|--------|-----|
| 6.1 | **DONE — JSON export of the displayed grid and JSON import into a new table.** (array-of-objects ⇄ table). Standard-library `json`. | M | Ubiquitous format. |
| 6.2 | **Excel-free XLSX export** by writing the OOXML zip with `zipfile` (no dependency) — or at minimum a `.xls`-compatible HTML table. | L | Requested constantly. |
| 6.3 | **PARTLY DONE — Markdown export.** Remaining: HTML | S | Docs/reports. |
| 6.4 | **CSV import upgrades:** delimiter/quote/encoding chooser, header mapping, append-to-existing-table, type override, NULL token. Current inference is first-1,000-rows only. | M | Robust ingest. |
| 6.5 | **PARTLY DONE — SQL `INSERT` script export and Copy-as-INSERT.** Remaining: clipboard-to-table of a table or selection. | M | Interop. |
| 6.6 | **PARTLY DONE — full-table streaming export to JSON and Markdown.** Remaining: compressed output. | S | Consistency. |
| 6.7 | **SQLite → SQLite copy** of selected tables into a new file (subset/anonymized export). | M | Sharing subsets. |

## 7. Visualization and analysis (P2)

| # | Item | Effort | Why |
|---|------|--------|-----|
| 7.1 | **Upgrade the chart tool:** scatter, histogram, pie, grouped bars, multiple series, axis labels, save-as-PNG (Canvas `postscript` → convert, or draw to a PPM). Current tool is single-column bar/line. | M | Insight without leaving the app. |
| 7.2 | **DONE — Profile column dialog:** min/max/avg/nulls/distinct/histogram for the selected column, computed in SQL. | M | Instant data understanding. |
| 7.3 | **Table statistics dashboard:** row counts, sizes (`dbstat`), index sizes, largest tables. | M | Health at a glance. |
| 7.4 | **Relationship diagram (ERD):** draw tables and FK links on a Canvas, auto-laid-out. | L | Flagship feature. |
| 7.5 | **Pivot / group-by builder** without writing SQL. | L | Analyst appeal. |

## 8. Navigation and discoverability (P1)

| # | Item | Effort | Why |
|---|------|--------|-----|
| 8.1 | **DONE — Recent files menu and optional reopen-last-at-startup.** | S | Everyday convenience. |
| 8.2 | **DONE — Search all tables (Ctrl+F):** find a value across all tables/columns (with a size guard). | M | "Where is this record?" |
| 8.3 | **Command palette** (`Ctrl+P`) for actions and objects. | M | Speed for power users. |
| 8.4 | **Multiple databases as tabs**, not just `ATTACH`. | L | Real multi-DB work. |
| 8.5 | **Breadcrumb / object filter box** above the schema tree for large schemas. | S | Big databases. |
| 8.6 | **Favorites / pinned tables.** | S | Focus. |

## 9. Reliability, session, and recovery (P1)

| # | Item | Effort | Why |
|---|------|--------|-----|
| 9.1 | **Autosave workspace** (open DB, tabs, filters) and restore on next launch / after crash. Projects exist but are manual. | M | Never lose context. |
| 9.2 | **DONE — global Tk exception handler** that logs to a file and shows a friendly dialog instead of a stack trace to stderr. `traceback` is already imported. | S | Professional feel. |
| 9.3 | **PARTLY DONE — foreign-key failures name the referencing tables.** Remaining: other error classes that explain SQLite errors in plain language (e.g. FK constraint → name the referencing table). | M | Approachability. |
| 9.4 | **DONE — non-writable files open read-only with a notice.** for databases on read-only media / locked files, with a clear banner. | S | Fewer confusing errors. |

## 10. Security and robustness (P1)

| # | Item | Effort | Why |
|---|------|--------|-----|
| 10.1 | **Sandbox `ATTACH` and URI opens.** Raw `WHERE`, SQL workspace, and `ATTACH` execute arbitrary SQL/paths; that is fine for a trusted local tool but document it and consider a "restricted mode" that disables `ATTACH`, file-writing PRAGMAs, and `load_extension`. | M | Safe in shared/corporate use. |
| 10.2 | **Confirm `PRAGMA writable_schema` and other footguns** behind an "expert" toggle. | S | Prevent corruption. |
| 10.3 | **Redaction-aware export** (hash or blank chosen columns) for sharing data safely. | M | Privacy. |
| 10.4 | **DONE — files over 2 GiB prompt before opening.**, and stream where possible. | S | Stability. |

## 11. Cross-platform and packaging (P1)

| # | Item | Effort | Why |
|---|------|--------|-----|
| 11.1 | **PARTLY DONE — per-platform `MONO_FONT`/`UI_FONT` added (Menlo/Consolas/DejaVu).** Remaining: update Windows-only wording in README. The app is pure Tkinter and runs on macOS/Linux unchanged. Fix hardcoded `Consolas` font with a per-platform monospace fallback (`Menlo`, `DejaVu Sans Mono`, `Consolas`). | S | Triples the audience. |
| 11.2 | **High-DPI / scaling** support (`tk scaling`, larger default fonts on hi-res displays). | S | Looks right everywhere. |
| 11.3 | **Optional single-file executable** builds (documented `pyinstaller`/`--onefile`) while keeping the no-dependency source path. | M | Easy distribution. |
| 11.4 | **Respect OS theme** (auto dark/light) where detectable. | S | Native feel. |
| 11.5 | **`.desktop` / file-association** helpers so double-clicking a `.sqlite` opens the app. | S | Discoverability. |

## 12. Accessibility and polish (P2)

| # | Item | Effort | Why |
|---|------|--------|-----|
| 12.1 | **Full keyboard operability** and visible focus rings for every control; screen-reader-friendly labels. | M | Inclusivity. |
| 12.2 | **DONE — text zoom** for grid and editor (`Ctrl+=/-`). | S | Readability. |
| 12.3 | **Status-bar affordances:** selected-cell type, encoding, transaction state, DB size. | S | Context. |
| 12.4 | **Consistent iconography and spacing**; a proper toolbar with grouped actions. | M | First impression. |
| 12.5 | **Localization scaffolding** (extract UI strings). | M | Global reach. |

## 13. Testing and quality engineering (P1)

| # | Item | Effort | Why |
|---|------|--------|-----|
| 13.1 | **IN PROGRESS — `selftest.py` grew from 5 to 22 tests** (composite keys, sort-tie alignment, interrupt, Markdown). Continue to cover edit/delete key logic, composite keys, WITHOUT ROWID, CSV round-trips, SQL splitter edge cases, export formats. | M | Confidence to refactor. |
| 13.2 | **Headless GUI smoke tests** driving the Tk app without a display (virtual events, `update_idletasks`). | L | Catch UI regressions. |
| 13.3 | **Golden-file tests** for SQL dump and CSV export. | M | Stable outputs. |
| 13.4 | **CI** (GitHub Actions) running the suite on Windows/macOS/Linux and multiple Python versions. | M | Trustworthy releases. |
| 13.5 | **Fuzz the SQL splitter and CSV inference** with random inputs. | M | Robustness. |

## 14. Architecture and code health (P1)

| # | Item | Effort | Why |
|---|------|--------|-----|
| 14.1 | **Separate concerns** inside the one file: a `Database` service (all SQL, threading, transactions) distinct from the Tk views. Keeps the single-file constraint while making it testable. | L | Enables everything above. |
| 14.2 | **Centralize connection access** so every query goes through one guarded, timed, cancellable path (foundation for §2). | M | Consistency. |
| 14.3 | **DONE — removed the duplicated status-set block** in `execute_sql`; also added a silent-truncation warning at 5,000 rows. | S | Small dead code. |
| 14.4 | **Type hints and docstrings** across public methods; run `mypy`/`ruff` in CI. | M | Maintainability. |
| 14.5 | **A stable internal event/callback layer** rather than direct cross-method calls, to make features composable. | M | Scales the codebase. |

## 15. World-class differentiators (P2)

| # | Item | Effort | Why |
|---|------|--------|-----|
| 15.1 | **Full-text search UI** over FTS5 tables (create, populate, query with ranking). | M | Power feature, built into SQLite. |
| 15.2 | **JSON1 helpers:** browse/query JSON columns with path pickers and `json_extract` builders. | M | Modern data. |
| 15.3 | **Time-travel / snapshots:** quick backup-before-change with one-click restore (backup API already used). | M | Fearless editing. |
| 15.4 | **Data generator / faker** for populating test tables (standard-library only). | M | Testing convenience. |
| 15.5 | **Query history search and diff**; pin and annotate queries. | M | Power users. |
| 15.6 | **Row-level and table-level comments/metadata** stored in a side table. | M | Documentation in place. |
| 15.7 | **Natural-language-to-SQL is intentionally out of scope** (needs network/models, violates the offline constraint) — note it and move on. | — | Keep the offline promise. |
| 15.8 | **Plugin hooks** (drop a `.py` into a folder to add an export format or action), respecting the restricted-machine posture with an explicit enable. | L | Extensibility without bloat. |

---

## Suggested sequencing

1. **Now (P0):** 1.2 wrong-row-under-sort, 1.3 key-query guard, 2.1 threading + 2.2 cancel, 2.4 busy_timeout. These remove the two remaining data-integrity risks and the UI-freeze that most damages the "world-class" impression.
2. **Next (P1 foundations):** 14.1/14.2 split the DB service, 13.1 grow the test suite, 11.1 cross-platform. Everything else gets easier and safer after these.
3. **Then (P1 value):** §3 SQL power features, §4 grid/editing UX, §6 import/export, §8 navigation.
4. **Later (P2 differentiators):** §7 visualization, §5.4/5.6 index advisor & schema diff, §15 FTS/JSON/ERD.

Nothing here requires leaving the standard library or the single-file model.
