# SQLite Workbench

SQLite Workbench is a complete cross-platform desktop utility (Windows, macOS, Linux) for creating, inspecting, querying and maintaining SQLite databases. The application is the single, standalone `sqlite_viewer.py` file. It is designed for restricted corporate computers and uses only the Python standard library.

There are no downloads, installers, `pip` packages, executable helpers or compiled extensions.

You can copy `sqlite_viewer.py` by itself to another permitted Windows folder; no other project file is required to run it.

## Start

Open PowerShell or Command Prompt in this folder and run:

```powershell
python .\sqlite_viewer.py
```

Open a database immediately by providing its path:

```powershell
python .\sqlite_viewer.py "C:\path\to\database.sqlite"
```

## Create a test database

The included generator creates a small database containing related tables, an index, a view, a trigger, constraints and sample records:

```powershell
python .\create_sample_database.py
python .\sqlite_viewer.py .\sample_company.sqlite
```

The generator asks before replacing an existing sample database.

Run the automated core tests with:

```powershell
python .\selftest.py
```

## Database browsing and editing

- Tables, views, indexes and triggers appear in the expandable object browser. Selecting a table or view expands it to show its columns and declared types.
- Selecting a table or view immediately displays its records.
- Recently opened databases are listed under **File ▸ Open recent**; **Reopen last database at startup** is optional.
- The browser shows the exact total row count and 500 records per page.
- Click a column heading for ascending or descending sorting, or use **Sort** for a three-column sort.
- Enter a trusted SQL expression in **SQL WHERE**, or build parameterized filters with **Add column filter**.
- Double-click any data row to open a complete, scrollable view of all its fields. Use **Edit cell...** or **Edit row** to change data.
- Use **Edit cell** for multiline text, NULL values and BLOB hexadecimal editing or file import/export.
- Add, edit, duplicate or delete rows. Multiple selected rows can be copied or deleted together.
- **Copy as...** places the selected rows on the clipboard as CSV, JSON, Markdown or SQL `INSERT` statements.
- Type part of a name in the box above the object browser to filter tables, views, indexes and triggers.
- **Search all tables** (`Ctrl+F`) finds a value in any column of any table; double-click a hit to open that table filtered to it.
- **Profile column...** shows row/null/distinct counts, min/max, average and sum for numeric data, and the ten most frequent values, honouring the current filter.
- Row counts are cached per table and filter and refreshed only when data or schema changes.
- Copy and paste tab-separated cell ranges with `Ctrl+C` and `Ctrl+V`.
- Changes remain reversible until **Commit** is selected. **Rollback** discards pending changes.

Direct editing requires a normal table with a primary key (single or composite) or an accessible SQLite `rowid`. Rows are matched on every primary-key column, so composite-key and `WITHOUT ROWID` tables can be edited and deleted directly.

## Database structure

The **Structure** workspace shows:

- column positions, names, declared types, defaults, NOT NULL flags and primary-key order;
- indexes, indexed columns, uniqueness, origin and partial-index state;
- foreign-key source/target columns and update/delete actions;
- the complete SQL definition for the selected object.

The visual structure tools can create tables, add/rename/drop columns, rename tables, create indexes and drop tables, views, indexes or triggers. The table designer previews its generated SQL and supports primary keys, uniqueness, defaults, NOT NULL, `STRICT` and `WITHOUT ROWID` tables. Use the SQL workspace for composite keys, generated columns or complex migrations.

## SQL workspace

- Maintain multiple query tabs.
- Open and save `.sql` files.
- Run selected SQL or the complete active tab with `F9`. Queries run in the background so the window stays responsive; use **Cancel** to interrupt a long query.
- Queries may use `:name`, `@name`, `$name` or `?` parameters; the workbench prompts for the values and binds them safely (typed as integer, decimal, text or `<NULL>`).
- Every run reports its execution time. Results are capped at 5,000 rows and the status bar says when a result was truncated.
- Inspect up to 5,000 rows from the final result set.
- Generate `EXPLAIN QUERY PLAN` output.
- Use lightweight SQL syntax highlighting, and **Format** to upper-case keywords and put one clause per line.
- Press `Ctrl+Space` to insert SQL keywords, schema objects or column names.
- Double-click an entry in **History** to restore it.
- Review every statement issued by the connection in **SQL Log**.

## Import, export and projects

- Import CSV into a new table. INTEGER and REAL affinities are inferred conservatively from the first 1,000 records.
- Import a JSON array of objects into a new table; nested objects and arrays are stored as JSON text.
- Export the selected table as a SQL script of `CREATE TABLE` plus `INSERT` statements.
- Export the displayed data grid or query results to UTF-8 CSV, JSON or Markdown.
- Export the complete selected table or filtered/sorted view to CSV, JSON or Markdown using streaming batches.
- Export a complete database as a standard SQL dump.
- Execute an existing SQL dump or migration script.
- Create a consistent SQLite backup using SQLite's online backup API.
- Save a workspace project as readable JSON, including the database path, read-only state, selected object, filters, sorting and every SQL tab.

## Administration and safety

- Open a database in read-only mode.
- Writable databases use WAL journal mode and a 5-second busy timeout by default, so other clients do not cause immediate lock errors.
- Dropping a table first shows how many rows will be lost, and foreign-key failures when deleting or editing name the tables that still reference the row.
- Files that are not writable open read-only automatically, and very large files prompt before opening.
- The toolbar shows **Uncommitted changes** while a transaction is pending.
- Unexpected errors are written to `sqlite_workbench_errors.log` beside the program and shown in a dialog instead of crashing the app.
- Attach another database and address it from SQL as `alias.table_name`.
- Review and change common PRAGMAs such as journal mode, synchronization, cache size and busy timeout.
- Run integrity and foreign-key checks.
- Run `VACUUM`, `ANALYZE`, `PRAGMA optimize` and `REINDEX`.
- Inspect the database file size, SQLite version, page information, journal mode and foreign-key state.
- Toggle a persistent dark/light theme, and change the text size with `Ctrl++`, `Ctrl+-` and `Ctrl+0`.
- **Commit** warns if another program has modified the database since it was opened or last committed.
- Plot a numeric column from the displayed data as a bar or line chart using Tkinter Canvas.

## Keyboard shortcuts

| Shortcut | Action |
|---|---|
| `Ctrl+O` | Open database |
| `Ctrl+N` | Create database |
| `Ctrl+S` | Commit changes |
| `F5` | Refresh schema |
| `F9` | Run selected or complete SQL |
| `Ctrl+Space` | SQL completion chooser |
| `Ctrl+C` | Copy selected data rows |
| `Ctrl+V` | Paste cells from the active cell |
| `Delete` | Delete selected rows |
| `Ctrl++` / `Ctrl+-` | Larger / smaller text |
| `Ctrl+0` | Reset text size |
| `Ctrl+F` | Search all tables |

## Important notes

- Always make a backup before destructive schema changes or importing an unfamiliar SQL script.
- SQL entered in the raw `WHERE` box and SQL workspace is executed as written. The column-filter builder uses bound parameters and is the safer option for ordinary searching.
- SQL-script import runs statement by statement inside a savepoint: if any statement fails, nothing from the script is kept and the failing statement is reported. `BEGIN`/`COMMIT` lines inside the script are ignored; choose **Commit** afterwards.
- SQLite features depend on the SQLite library bundled with the installed Python version. Errors from unsupported syntax are reported without requiring additional components.
- SQLCipher databases and external SQLite extensions are intentionally unsupported because they require compiled components that violate the restricted-machine requirement.
