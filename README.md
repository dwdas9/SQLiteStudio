# SQLiteStudio

SQLiteStudio is a single Python file. Run `python studio.py` and it opens a desktop app for browsing, editing and querying SQLite databases.

There are many SQLite tools. What makes this one different is its simplicity: no installer, no admin rights and no extra libraries. That makes it useful on locked-down corporate computers where installing software is not allowed.

## See it in action

### Browse and edit data

Explore tables, views, indexes and triggers, then filter, sort or edit records without writing SQL.

![SQLiteStudio browsing the employees table](docs/images/sqlite-studio-browse.png)

### Inspect the schema

Review columns, types, constraints, indexes, foreign keys and SQL definitions.

![SQLiteStudio showing the employees table schema](docs/images/sqlite-studio-schema.png)

### Run SQL

Write queries and inspect their results, history and execution log in the same workspace.

![SQLiteStudio executing an aggregate query and showing its results](docs/images/sqlite-studio-sql.png)

## Start

Open PowerShell, Command Prompt or Terminal in the folder containing `studio.py`.

Run:

```powershell
python studio.py
```

SQLiteStudio will open.

To open a database immediately, add its path:

```powershell
python studio.py "C:\path\to\database.sqlite"
```

If your computer uses `python3` instead of `python`, replace `python` with `python3`.

### Sample database

Create and open a sample database containing related tables, an index, a view, a trigger, constraints and records:

```powershell
python .\create_sample_database.py
python .\studio.py .\sample_company.sqlite
```

The generator asks before replacing an existing sample database.

## Browse and edit data

- Browse tables and views from the expandable object tree. Columns and declared types appear beneath each object.
- View 500 records per page with an exact total row count.
- Sort by one column from its heading or configure a three-column sort.
- Filter with a SQL `WHERE` expression or the parameterized column-filter builder.
- Search every table, profile column values and plot numeric data.
- Add, edit, duplicate or delete rows. Copy selected rows as CSV, JSON, Markdown or SQL `INSERT` statements.
- Copy and paste tab-separated cell ranges.
- Keep changes pending until **Commit**, or discard them with **Rollback**.

Direct editing requires a primary key or accessible SQLite `rowid`. Composite keys and `WITHOUT ROWID` tables are supported.

## Inspect and change the schema

The **Schema** workspace shows:

- columns, types, defaults, nullability and primary-key order;
- indexes, uniqueness, origin and partial-index state;
- foreign-key columns and update/delete actions;
- the complete SQL definition for the selected object.

Create tables and indexes, add or rename columns, rename tables, and drop schema objects. The table designer previews its SQL and supports `STRICT` and `WITHOUT ROWID` tables. Use the SQL workspace for composite keys, generated columns and complex migrations.

## Run SQL

- Maintain multiple query tabs and open or save `.sql` files.
- Run selected SQL or the complete active tab. Queries run in the background and can be cancelled.
- Bind `:name`, `@name`, `$name` and `?` parameters as integers, decimals, text or `NULL`.
- Inspect up to 5,000 result rows with execution time and truncation status.
- Generate `EXPLAIN QUERY PLAN` output.
- Format SQL and complete keywords, schema objects and column names.
- Restore previous statements from **History** and review issued statements in **SQL Log**.

## Import, export and projects

- Import CSV or JSON into a new table. CSV column affinities are inferred from the first 1,000 records.
- Export displayed results or a complete filtered and sorted table as CSV, JSON or Markdown.
- Export a table as `CREATE TABLE` plus `INSERT` statements.
- Export a database as a SQL dump, or execute a dump or migration script.
- Create a consistent backup with SQLite's online backup API.
- Save the database path, selected object, filters, sorting and SQL tabs as a readable JSON project.

## Database tools and safety

- Open a database read-only; non-writable files switch to read-only automatically.
- Use WAL journal mode and a five-second busy timeout for writable databases.
- Attach another database and query it as `alias.table_name`.
- Review common PRAGMAs and database file information.
- Run integrity and foreign-key checks, `VACUUM`, `ANALYZE`, `PRAGMA optimize` and `REINDEX`.
- See the affected row count before dropping a table and the referencing tables when a foreign-key operation fails.
- Receive a warning if another program changes the database before you commit.
- Switch between light and dark themes and adjust the text size.

## Keyboard shortcuts

| Shortcut | Action |
|---|---|
| `Ctrl+O` | Open database |
| `Ctrl+N` | Create database |
| `Ctrl+S` | Commit changes |
| `F5` | Refresh schema |
| `F9` | Run selected or complete SQL |
| `Ctrl+Space` | SQL completion chooser |
| `Ctrl+C` | Copy selected rows |
| `Ctrl+V` | Paste cells |
| `Delete` | Delete selected rows |
| `Ctrl++` / `Ctrl+-` | Change text size |
| `Ctrl+0` | Reset text size |
| `Ctrl+F` | Search all tables |

## Notes

- Back up a database before destructive schema changes or importing an unfamiliar SQL script.
- SQL entered in the raw `WHERE` box or SQL workspace runs as written. Use the parameterized column-filter builder for ordinary filtering.
- SQL-script imports run inside a savepoint. If a statement fails, the complete import is rolled back. Choose **Commit** after a successful import.
- Available SQLite features depend on the library bundled with Python.
- SQLCipher databases and external SQLite extensions are not supported because they require compiled components.
