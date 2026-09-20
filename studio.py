"""SQLiteStudio - a dependency-free SQLite desktop utility for Windows.

Requires only the Python standard library (tkinter and sqlite3).
Architect: Das
"""

from __future__ import annotations

import csv
import io
import json
import os
import queue
import re
import sqlite3
import sys
import threading
import time
import traceback
from pathlib import Path
import tkinter as tk
from tkinter import filedialog, messagebox, simpledialog, ttk

APP_NAME = "SQLiteStudio"
PAGE_SIZE = 500
SQLITE_TYPES = ("INTEGER", "TEXT", "REAL", "NUMERIC", "BLOB", "DATE", "DATETIME", "BOOLEAN")
CONFIG_PATH = Path(__file__).with_name("sqlite_workbench_settings.json")
ERROR_LOG_PATH = Path(__file__).with_name("sqlite_workbench_errors.log")
LARGE_FILE_BYTES = 2 * 1024 ** 3
if sys.platform == "darwin":
    MONO_FONT, UI_FONT = "Menlo", "Helvetica Neue"
elif sys.platform.startswith("win"):
    MONO_FONT, UI_FONT = "Consolas", "Segoe UI"
else:
    MONO_FONT, UI_FONT = "DejaVu Sans Mono", "DejaVu Sans"
SQL_KEYWORDS = tuple(sorted(set("""ABORT ACTION ADD AFTER ALL ALTER ANALYZE AND AS ASC ATTACH AUTOINCREMENT
BEFORE BEGIN BETWEEN BY CASCADE CASE CAST CHECK COLLATE COLUMN COMMIT CONFLICT CONSTRAINT CREATE CROSS CURRENT_DATE
CURRENT_TIME CURRENT_TIMESTAMP DATABASE DEFAULT DEFERRABLE DEFERRED DELETE DESC DETACH DISTINCT DO DROP EACH ELSE END
ESCAPE EXCEPT EXCLUDE EXCLUSIVE EXISTS EXPLAIN FAIL FILTER FIRST FOLLOWING FOR FOREIGN FROM FULL GENERATED GLOB GROUP
GROUPS HAVING IF IGNORE IMMEDIATE IN INDEX INDEXED INITIALLY INNER INSERT INSTEAD INTERSECT INTO IS ISNULL JOIN KEY
LAST LEFT LIKE LIMIT MATCH MATERIALIZED NATURAL NO NOT NOTHING NOTNULL NULL NULLS OF OFFSET ON OR ORDER OTHERS OUTER
OVER PARTITION PLAN PRAGMA PRECEDING PRIMARY QUERY RAISE RANGE RECURSIVE REFERENCES REGEXP REINDEX RELEASE RENAME
REPLACE RESTRICT RIGHT ROLLBACK ROW ROWS SAVEPOINT SELECT SET TABLE TEMP TEMPORARY THEN TIES TO TRANSACTION TRIGGER
UNBOUNDED UNION UNIQUE UPDATE USING VACUUM VALUES VIEW VIRTUAL WHEN WHERE WINDOW WITH WITHOUT""".split())))


def quote_identifier(name: str) -> str:
    """Quote an SQLite identifier safely."""
    return '"' + name.replace('"', '""') + '"'


def split_sql(script: str) -> list[str]:
    """Split a script using sqlite3's parser rather than splitting on semicolons."""
    statements: list[str] = []
    buffer = ""
    for character in script:
        buffer += character
        if character == ";" and sqlite3.complete_statement(buffer):
            statement = buffer.strip()
            if statement:
                statements.append(statement)
            buffer = ""
    if buffer.strip():
        statements.append(buffer.strip())
    return statements


def infer_column_types(rows: list[list[str]], width: int) -> list[str]:
    """Infer conservative SQLite affinities for CSV data."""
    types = []
    for index in range(width):
        values = [row[index].strip() for row in rows if index < len(row) and row[index].strip()]
        if values and all(re.fullmatch(r"[+-]?\d+", value) for value in values):
            types.append("INTEGER")
        elif values:
            try:
                for value in values:
                    float(value)
                types.append("REAL")
            except ValueError:
                types.append("TEXT")
        else:
            types.append("TEXT")
    return types


def markdown_row(values) -> str:
    """One Markdown table row; pipes and newlines inside cells are escaped."""
    return "| " + " | ".join(str(v).replace("|", "\\|").replace("\n", " ") for v in values) + " |\n"


def markdown_table(columns, rows) -> str:
    """Render a Markdown table."""
    header = markdown_row(columns) + "| " + " | ".join("---" for _ in columns) + " |\n"
    return header + "".join(markdown_row(row) for row in rows)


_CLAUSE_START = ("INSERT INTO", "DELETE FROM", "GROUP BY", "ORDER BY", "UNION ALL", "LEFT OUTER JOIN", "LEFT JOIN",
                 "RIGHT JOIN", "INNER JOIN", "CROSS JOIN", "NATURAL JOIN", "WITH", "SELECT", "FROM", "WHERE", "HAVING",
                 "LIMIT", "OFFSET", "UNION", "EXCEPT", "INTERSECT", "VALUES", "SET", "UPDATE", "JOIN", "RETURNING")
_CLAUSE_INDENT = ("AND", "OR", "ON")


def format_sql(sql: str) -> str:
    """Lightweight formatter: upper-case keywords, one clause per line, AND/OR/ON indented. Strings/comments untouched."""
    pieces, last = [], 0
    for match in _SQL_NOISE.finditer(sql):
        pieces.append((False, sql[last:match.start()]))
        pieces.append((True, match.group(0)))
        last = match.end()
    pieces.append((False, sql[last:]))
    out = []
    for protected, text in pieces:
        if protected:
            out.append(text)
            continue
        text = re.sub(r"\s+", " ", text)
        text = re.sub(r"(?<![:@$.\w])[A-Za-z_]+\b",
                      lambda m: m.group(0).upper() if m.group(0).upper() in SQL_KEYWORDS else m.group(0), text)
        for clause in _CLAUSE_START:
            text = re.sub(r" ?\b" + re.escape(clause) + r"\b", "\n" + clause, text)
        for word in _CLAUSE_INDENT:
            text = re.sub(r" ?\b" + word + r"\b", "\n  " + word, text)
        out.append(text)
    lines = [line.rstrip() for line in "".join(out).splitlines()]
    return "\n".join(line for line in lines if line.strip())


def like_pattern(needle: str) -> str:
    """Escape LIKE wildcards in needle and wrap it for a contains-match; pair with ESCAPE '\\'."""
    escaped = needle.replace("\\", "\\\\").replace("%", "\\%").replace("_", "\\_")
    return f"%{escaped}%"


def search_database(connection, needle: str, limit: int = 200):
    """Find cells whose text contains needle in any column of any table: (table, column, value, row) tuples."""
    if not needle:
        return []
    results, pattern, lowered = [], like_pattern(needle), needle.casefold()
    tables = [row[0] for row in connection.execute(
        "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name")]
    for table in tables:
        columns = [row[1] for row in connection.execute(f"PRAGMA table_info({quote_identifier(table)})")]
        if not columns:
            continue
        clause = " OR ".join(f"CAST({quote_identifier(c)} AS TEXT) LIKE ? ESCAPE '\\'" for c in columns)
        cursor = connection.execute(f"SELECT * FROM {quote_identifier(table)} WHERE {clause} LIMIT ?",
                                    (*[pattern] * len(columns), limit - len(results)))
        for row in cursor:
            values = tuple(row)
            for column, value in zip(columns, values):
                if value is not None and not isinstance(value, bytes) and lowered in str(value).casefold():
                    results.append((table, column, value, values))
            if len(results) >= limit:
                break
        if len(results) >= limit:
            break
    return results[:limit]


def profile_column(connection, table: str, column: str, where: str = "", params=()):
    """Summarise one column: counts, nulls, distinct, min/max, average/sum for numeric data, and top-10 values."""
    q, t = quote_identifier(column), quote_identifier(table)
    suffix = f" WHERE {where}" if where else ""
    total, non_null, distinct, minimum, maximum, numeric = tuple(connection.execute(
        f"SELECT COUNT(*), COUNT({q}), COUNT(DISTINCT {q}), MIN({q}), MAX({q}), "
        f"SUM(CASE WHEN typeof({q}) IN ('integer','real') THEN 1 ELSE 0 END) FROM {t}{suffix}", params).fetchone())
    stats = {"rows": total, "non-null": non_null, "null": total - non_null, "distinct": distinct,
             "min": minimum, "max": maximum}
    if non_null and numeric == non_null:
        average, total_sum = tuple(connection.execute(f"SELECT AVG({q}), SUM({q}) FROM {t}{suffix}", params).fetchone())
        stats["average"], stats["sum"] = average, total_sum
    top = connection.execute(f"SELECT {q}, COUNT(*) AS n FROM {t}{suffix} GROUP BY {q} ORDER BY n DESC, {q} LIMIT 10",
                             params).fetchall()
    return stats, [tuple(row) for row in top]


class ProfileDialog(tk.Toplevel):
    """Show column statistics and the most frequent values."""

    def __init__(self, parent, title, stats, top):
        super().__init__(parent)
        self.title(f"Profile: {title}")
        self.transient(parent)
        self.geometry("520x460")
        body = ttk.Frame(self, padding=10)
        body.pack(fill="both", expand=True)
        ttk.Label(body, text="Statistics", font=(UI_FONT, 10, "bold")).pack(anchor="w")
        stats_grid = ttk.Treeview(body, columns=("k", "v"), show="headings", height=8)
        stats_grid.heading("k", text="Measure")
        stats_grid.heading("v", text="Value")
        stats_grid.column("k", width=140, stretch=False)
        for key, value in stats.items():
            stats_grid.insert("", "end", values=(key, "<NULL>" if value is None else value))
        stats_grid.pack(fill="x", pady=(2, 10))
        ttk.Label(body, text="Most frequent values", font=(UI_FONT, 10, "bold")).pack(anchor="w")
        top_grid = ttk.Treeview(body, columns=("value", "count"), show="headings")
        top_grid.heading("value", text="Value")
        top_grid.heading("count", text="Rows")
        top_grid.column("count", width=90, stretch=False)
        for value, count in top:
            top_grid.insert("", "end", values=("<NULL>" if value is None else value, count))
        top_grid.pack(fill="both", expand=True, pady=2)
        ttk.Button(body, text="Close", command=self.destroy).pack(anchor="e", pady=(8, 0))
        self.bind("<Escape>", lambda _e: self.destroy())


class SearchDialog(tk.Toplevel):
    """Search every table for a value; double-click a hit to open that table filtered to it."""

    def __init__(self, app):
        super().__init__(app)
        self.app = app
        self.title("Search all tables")
        self.transient(app)
        self.geometry("760x480")
        body = ttk.Frame(self, padding=10)
        body.pack(fill="both", expand=True)
        row = ttk.Frame(body)
        row.pack(fill="x")
        ttk.Label(row, text="Find text:").pack(side="left")
        self.needle_var = tk.StringVar()
        entry = ttk.Entry(row, textvariable=self.needle_var)
        entry.pack(side="left", fill="x", expand=True, padx=6)
        entry.bind("<Return>", lambda _e: self.run())
        ttk.Button(row, text="Search", command=self.run).pack(side="left")
        self.grid = ttk.Treeview(body, columns=("table", "column", "value"), show="headings")
        for column, label, width in (("table", "Table", 160), ("column", "Column", 160), ("value", "Value", 380)):
            self.grid.heading(column, text=label)
            self.grid.column(column, width=width, stretch=True)
        self.grid.pack(fill="both", expand=True, pady=8)
        self.grid.bind("<Double-1>", lambda _e: self.open_hit())
        self.summary = ttk.Label(body, text="Matches up to 200 cells across all tables (case-insensitive).")
        self.summary.pack(anchor="w")
        self.bind("<Escape>", lambda _e: self.destroy())
        entry.focus_set()

    def run(self):
        needle = self.needle_var.get().strip()
        self.grid.delete(*self.grid.get_children())
        if not needle:
            return
        try:
            hits = search_database(self.app.connection, needle)
        except sqlite3.Error as exc:
            messagebox.showerror("Search failed", str(exc), parent=self)
            return
        for table, column, value, _row in hits:
            self.grid.insert("", "end", values=(table, column, str(value)[:200]))
        self.summary.config(text=f"{len(hits)} matching cell(s)" + (" (limit reached)" if len(hits) >= 200 else ""))

    def open_hit(self):
        selection = self.grid.selection()
        if not selection:
            return
        table, column, _value = self.grid.item(selection[0], "values")
        self.app.open_table_with_filter(table, column, self.needle_var.get().strip())


def sql_literal(value) -> str:
    """Render a Python value as an SQLite literal."""
    if value is None:
        return "NULL"
    if isinstance(value, bool):
        return "1" if value else "0"
    if isinstance(value, int):
        return str(value)
    if isinstance(value, float):
        return repr(value) if value == value and value not in (float("inf"), float("-inf")) else "NULL"
    if isinstance(value, (bytes, bytearray, memoryview)):
        return "X'" + bytes(value).hex() + "'"
    return "'" + str(value).replace("'", "''") + "'"


def sql_insert_statements(table: str, columns, rows) -> str:
    """Build one INSERT statement per row."""
    head = f"INSERT INTO {quote_identifier(table)} ({', '.join(quote_identifier(c) for c in columns)}) VALUES "
    return "".join(head + "(" + ", ".join(sql_literal(v) for v in row) + ");\n" for row in rows)


def json_ready(value):
    """Convert a database value into something json.dumps accepts."""
    if isinstance(value, (bytes, bytearray, memoryview)):
        return bytes(value).hex()
    return value


def json_records_to_table(records):
    """Turn a JSON array of objects into (columns, affinities, rows) for a new table."""
    if not isinstance(records, list) or not all(isinstance(r, dict) for r in records):
        raise ValueError("The JSON file must contain an array of objects.")
    columns: list[str] = []
    for record in records:
        for key in record:
            if key not in columns:
                columns.append(str(key))
    if not columns:
        raise ValueError("No columns were found in the JSON records.")

    def normalise(value):
        if isinstance(value, (dict, list)):
            return json.dumps(value, ensure_ascii=False)
        if isinstance(value, bool):
            return int(value)
        return value

    rows = [[normalise(record.get(column)) for column in columns] for record in records]
    types = []
    for index in range(len(columns)):
        values = [row[index] for row in rows if row[index] is not None]
        if values and all(isinstance(v, int) for v in values):
            types.append("INTEGER")
        elif values and all(isinstance(v, (int, float)) for v in values):
            types.append("REAL")
        else:
            types.append("TEXT")
    return columns, types, rows


_SQL_NOISE = re.compile(r"'(?:''|[^'])*'|\"(?:\"\"|[^\"])*\"|--[^\n]*|/\*[\s\S]*?\*/")
_TRANSACTION_CONTROL = re.compile(r"^\s*(BEGIN|COMMIT|END)\b", re.IGNORECASE)


def find_parameters(sql: str) -> tuple[list[str], int]:
    """Return (named parameters in order of first use, number of plain '?' markers), ignoring strings and comments."""
    bare = _SQL_NOISE.sub(" ", sql)
    named: list[str] = []
    for match in re.finditer(r"[:@$]([A-Za-z_][A-Za-z0-9_]*)", bare):
        if match.group(1) not in named:
            named.append(match.group(1))
    return named, len(re.findall(r"\?(?!\d)", bare))


def coerce_parameter(text):
    """Interpret a typed parameter: <NULL>, integers and decimals become typed values; anything else stays text."""
    if not isinstance(text, str) or text == "<NULL>":
        return None if text == "<NULL>" else text
    if re.fullmatch(r"[+-]?\d+", text):
        return int(text)
    if re.fullmatch(r"[+-]?(\d+\.\d*|\.\d+|\d+)([eE][+-]?\d+)?", text):
        return float(text)
    return text


def bind_arguments(statement: str, named: dict, positional):
    """Select the arguments one statement needs from the collected values (positional is an iterator)."""
    names, count = find_parameters(statement)
    if names:
        return {name: named.get(name) for name in names}
    values = []
    for _ in range(count):
        try:
            values.append(next(positional))
        except StopIteration:
            raise sqlite3.ProgrammingError("Not enough values were supplied for the '?' parameters.") from None
    return tuple(values)


def run_script_atomically(connection, script: str) -> int:
    """Run a script inside a savepoint; if any statement fails, nothing from the script is kept.

    BEGIN/COMMIT/END lines inside the script are skipped because the savepoint already makes it atomic.
    """
    statements = [item for item in split_sql(script) if not _TRANSACTION_CONTROL.match(item)]
    connection.execute("SAVEPOINT workbench_import")
    try:
        for number, statement in enumerate(statements, start=1):
            try:
                connection.execute(statement)
            except sqlite3.Error as exc:
                snippet = " ".join(statement.split())[:160]
                raise sqlite3.OperationalError(
                    f"Statement {number} of {len(statements)} failed: {exc}\n\n{snippet}"
                ) from exc
    except sqlite3.Error:
        connection.execute("ROLLBACK TO workbench_import")
        connection.execute("RELEASE workbench_import")
        raise
    connection.execute("RELEASE workbench_import")
    return len(statements)


def describe_constraint_error(connection, table: str, exc) -> str:
    """Turn a bare SQLite constraint error into guidance, naming the tables that reference `table`."""
    message = str(exc)
    if "FOREIGN KEY constraint failed" not in message:
        return message
    referencing = set()
    try:
        children = [row[0] for row in connection.execute(
            "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%'"
        )]
        for child in children:
            for fk in connection.execute(f"PRAGMA foreign_key_list({quote_identifier(child)})"):
                if fk[2] == table:
                    referencing.add(f"{child}.{fk[3]}")
    except sqlite3.Error:
        pass
    if referencing:
        return (f"{message}.\n\nRows in {', '.join(sorted(referencing))} still reference this table. "
                "Delete or update those rows first, or define the foreign key with ON DELETE CASCADE.")
    return f"{message}.\n\nOther rows still reference this row, or this row references a missing parent row."


def file_is_writable(path: Path) -> bool:
    """True when the database file and its folder (needed for -journal/-wal files) are both writable."""
    return os.access(path, os.W_OK) and os.access(path.parent, os.W_OK)


class ColumnDialog(tk.Toplevel):
    """Collect one SQLite column definition."""

    def __init__(self, parent, title="Column", initial=None):
        super().__init__(parent)
        initial = initial or {}
        self.title(title)
        self.transient(parent)
        self.resizable(False, False)
        self.result = None
        self.name_var = tk.StringVar(value=initial.get("name", ""))
        self.type_var = tk.StringVar(value=initial.get("type", "TEXT"))
        self.pk_var = tk.BooleanVar(value=initial.get("pk", False))
        self.notnull_var = tk.BooleanVar(value=initial.get("notnull", False))
        self.unique_var = tk.BooleanVar(value=initial.get("unique", False))
        self.default_var = tk.StringVar(value=initial.get("default", ""))
        body = ttk.Frame(self, padding=14)
        body.pack(fill="both", expand=True)
        ttk.Label(body, text="Column name").grid(row=0, column=0, sticky="w", pady=4)
        name_entry = ttk.Entry(body, textvariable=self.name_var, width=34)
        name_entry.grid(row=0, column=1, sticky="ew", pady=4)
        ttk.Label(body, text="Data type").grid(row=1, column=0, sticky="w", pady=4)
        ttk.Combobox(body, textvariable=self.type_var, values=SQLITE_TYPES, width=31).grid(row=1, column=1, sticky="ew", pady=4)
        ttk.Label(body, text="Default SQL value").grid(row=2, column=0, sticky="w", pady=4)
        ttk.Entry(body, textvariable=self.default_var, width=34).grid(row=2, column=1, sticky="ew", pady=4)
        flags = ttk.Frame(body)
        flags.grid(row=3, column=0, columnspan=2, sticky="w", pady=8)
        ttk.Checkbutton(flags, text="Primary key", variable=self.pk_var).pack(side="left", padx=(0, 12))
        ttk.Checkbutton(flags, text="Not NULL", variable=self.notnull_var).pack(side="left", padx=(0, 12))
        ttk.Checkbutton(flags, text="Unique", variable=self.unique_var).pack(side="left")
        buttons = ttk.Frame(body)
        buttons.grid(row=4, column=0, columnspan=2, sticky="e", pady=(10, 0))
        ttk.Button(buttons, text="Cancel", command=self.destroy).pack(side="right")
        ttk.Button(buttons, text="OK", command=self.accept).pack(side="right", padx=8)
        self.bind("<Escape>", lambda _e: self.destroy())
        self.bind("<Return>", lambda _e: self.accept())
        self.grab_set()
        name_entry.focus_set()

    def accept(self):
        name = self.name_var.get().strip()
        if not name:
            messagebox.showwarning("Column", "Enter a column name.", parent=self)
            return
        self.result = {"name": name, "type": self.type_var.get().strip() or "TEXT", "pk": self.pk_var.get(),
                       "notnull": self.notnull_var.get(), "unique": self.unique_var.get(),
                       "default": self.default_var.get().strip()}
        self.destroy()


class CreateTableDialog(tk.Toplevel):
    """Visual CREATE TABLE designer with generated-SQL preview."""

    def __init__(self, parent):
        super().__init__(parent)
        self.title("Create table")
        self.transient(parent)
        self.geometry("760x530")
        self.result = None
        self.columns = []
        self.table_var = tk.StringVar()
        self.without_rowid_var = tk.BooleanVar()
        self.strict_var = tk.BooleanVar()
        body = ttk.Frame(self, padding=12)
        body.pack(fill="both", expand=True)
        top = ttk.Frame(body)
        top.pack(fill="x")
        ttk.Label(top, text="Table name").pack(side="left")
        table_entry = ttk.Entry(top, textvariable=self.table_var, width=32)
        table_entry.pack(side="left", padx=8)
        ttk.Checkbutton(top, text="WITHOUT ROWID", variable=self.without_rowid_var, command=self.preview).pack(side="left")
        ttk.Checkbutton(top, text="STRICT", variable=self.strict_var, command=self.preview).pack(side="left", padx=10)
        columns_frame = ttk.LabelFrame(body, text="Columns", padding=5)
        columns_frame.pack(fill="both", expand=True, pady=10)
        self.grid = ttk.Treeview(columns_frame, columns=("name", "type", "pk", "notnull", "unique", "default"), show="headings")
        for column, label, width in (("name", "Name", 150), ("type", "Type", 90), ("pk", "PK", 45),
                                     ("notnull", "Not NULL", 70), ("unique", "Unique", 60), ("default", "Default", 150)):
            self.grid.heading(column, text=label)
            self.grid.column(column, width=width, stretch=True)
        self.grid.pack(side="left", fill="both", expand=True)
        bar = ttk.Frame(columns_frame)
        bar.pack(side="right", fill="y", padx=(6, 0))
        ttk.Button(bar, text="Add...", command=self.add_column).pack(fill="x", pady=2)
        ttk.Button(bar, text="Edit...", command=self.edit_column).pack(fill="x", pady=2)
        ttk.Button(bar, text="Remove", command=self.remove_column).pack(fill="x", pady=2)
        ttk.Button(bar, text="Move up", command=lambda: self.move(-1)).pack(fill="x", pady=(14, 2))
        ttk.Button(bar, text="Move down", command=lambda: self.move(1)).pack(fill="x", pady=2)
        ttk.Label(body, text="Generated SQL").pack(anchor="w")
        self.preview_text = tk.Text(body, height=6, wrap="word", font=(MONO_FONT, 9), state="disabled")
        self.preview_text.pack(fill="x")
        buttons = ttk.Frame(body)
        buttons.pack(fill="x", pady=(10, 0))
        ttk.Button(buttons, text="Cancel", command=self.destroy).pack(side="right")
        ttk.Button(buttons, text="Create table", command=self.accept).pack(side="right", padx=8)
        self.table_var.trace_add("write", lambda *_: self.preview())
        self.bind("<Escape>", lambda _e: self.destroy())
        self.grab_set()
        table_entry.focus_set()
        self.preview()

    def add_column(self):
        dialog = ColumnDialog(self, "Add column")
        self.wait_window(dialog)
        if dialog.result:
            if any(c["name"].casefold() == dialog.result["name"].casefold() for c in self.columns):
                messagebox.showwarning("Duplicate column", "Column names must be unique.", parent=self)
                return
            self.columns.append(dialog.result)
            self.refresh()

    def edit_column(self):
        selection = self.grid.selection()
        if not selection:
            return
        index = self.grid.index(selection[0])
        dialog = ColumnDialog(self, "Edit column", self.columns[index])
        self.wait_window(dialog)
        if dialog.result:
            if any(i != index and c["name"].casefold() == dialog.result["name"].casefold() for i, c in enumerate(self.columns)):
                messagebox.showwarning("Duplicate column", "Column names must be unique.", parent=self)
                return
            self.columns[index] = dialog.result
            self.refresh()

    def remove_column(self):
        selection = self.grid.selection()
        if selection:
            del self.columns[self.grid.index(selection[0])]
            self.refresh()

    def move(self, amount):
        selection = self.grid.selection()
        if not selection:
            return
        index = self.grid.index(selection[0])
        target = index + amount
        if 0 <= target < len(self.columns):
            self.columns[index], self.columns[target] = self.columns[target], self.columns[index]
            self.refresh(select=target)

    def refresh(self, select=None):
        self.grid.delete(*self.grid.get_children())
        for column in self.columns:
            self.grid.insert("", "end", values=(column["name"], column["type"], "Yes" if column["pk"] else "",
                                                 "Yes" if column["notnull"] else "", "Yes" if column["unique"] else "",
                                                 column["default"]))
        if select is not None and self.grid.get_children():
            self.grid.selection_set(self.grid.get_children()[select])
        self.preview()

    def generated_sql(self):
        name = self.table_var.get().strip() or "new_table"
        definitions = []
        for column in self.columns:
            part = f"{quote_identifier(column['name'])} {column['type']}"
            if column["pk"]:
                part += " PRIMARY KEY"
            if column["notnull"]:
                part += " NOT NULL"
            if column["unique"]:
                part += " UNIQUE"
            if column["default"]:
                part += " DEFAULT " + column["default"]
            definitions.append(part)
        suffix = []
        if self.without_rowid_var.get():
            suffix.append("WITHOUT ROWID")
        if self.strict_var.get():
            suffix.append("STRICT")
        return f"CREATE TABLE {quote_identifier(name)} (\n    " + ",\n    ".join(definitions) + "\n)" + (" " + ", ".join(suffix) if suffix else "") + ";"

    def preview(self):
        if not hasattr(self, "preview_text"):
            return
        self.preview_text.configure(state="normal")
        self.preview_text.delete("1.0", "end")
        self.preview_text.insert("1.0", self.generated_sql())
        self.preview_text.configure(state="disabled")

    def accept(self):
        if not self.table_var.get().strip() or not self.columns:
            messagebox.showwarning("Create table", "Enter a table name and add at least one column.", parent=self)
            return
        if sum(1 for c in self.columns if c["pk"]) > 1:
            messagebox.showwarning("Create table", "Use SQL for a composite primary key.", parent=self)
            return
        if self.without_rowid_var.get() and not any(c["pk"] for c in self.columns):
            messagebox.showwarning("Create table", "A WITHOUT ROWID table must have a primary key.", parent=self)
            return
        if self.strict_var.get() and any(c["type"].upper() not in ("INTEGER", "INT", "REAL", "TEXT", "BLOB", "ANY") for c in self.columns):
            messagebox.showwarning("Create table", "STRICT permits only INTEGER, INT, REAL, TEXT, BLOB or ANY.", parent=self)
            return
        self.result = self.generated_sql()
        self.destroy()


class FilterDialog(tk.Toplevel):
    OPERATORS = ("contains", "equals", "not equal", "greater than", "less than", "is NULL", "is not NULL")

    def __init__(self, parent, columns):
        super().__init__(parent)
        self.title("Add column filter")
        self.transient(parent)
        self.resizable(False, False)
        self.result = None
        self.column_var = tk.StringVar(value=columns[0] if columns else "")
        self.operator_var = tk.StringVar(value="contains")
        self.value_var = tk.StringVar()
        body = ttk.Frame(self, padding=14)
        body.pack(fill="both", expand=True)
        for row, label in enumerate(("Column", "Condition", "Value")):
            ttk.Label(body, text=label).grid(row=row, column=0, sticky="w", pady=4)
        ttk.Combobox(body, textvariable=self.column_var, values=columns, state="readonly", width=28).grid(row=0, column=1, pady=4)
        ttk.Combobox(body, textvariable=self.operator_var, values=self.OPERATORS, state="readonly", width=28).grid(row=1, column=1, pady=4)
        ttk.Entry(body, textvariable=self.value_var, width=31).grid(row=2, column=1, pady=4)
        buttons = ttk.Frame(body)
        buttons.grid(row=3, column=0, columnspan=2, sticky="e", pady=(10, 0))
        ttk.Button(buttons, text="Cancel", command=self.destroy).pack(side="right")
        ttk.Button(buttons, text="Add filter", command=self.accept).pack(side="right", padx=8)
        self.bind("<Escape>", lambda _e: self.destroy())
        self.bind("<Return>", lambda _e: self.accept())
        self.grab_set()

    def accept(self):
        operator = self.operator_var.get()
        if not self.column_var.get():
            return
        if operator not in ("is NULL", "is not NULL") and not self.value_var.get():
            messagebox.showwarning("Filter", "Enter a filter value.", parent=self)
            return
        self.result = (self.column_var.get(), operator, self.value_var.get())
        self.destroy()


class SortDialog(tk.Toplevel):
    def __init__(self, parent, columns, initial=None):
        super().__init__(parent)
        self.title("Sort rows")
        self.transient(parent)
        self.resizable(False, False)
        self.result = None
        initial = initial or []
        self.column_vars, self.direction_vars = [], []
        body = ttk.Frame(self, padding=14)
        body.pack(fill="both", expand=True)
        ttk.Label(body, text="Column").grid(row=0, column=0, sticky="w")
        ttk.Label(body, text="Direction").grid(row=0, column=1, sticky="w")
        for index in range(3):
            column = tk.StringVar(value=initial[index][0] if index < len(initial) else "")
            direction = tk.StringVar(value="Descending" if index < len(initial) and initial[index][1] else "Ascending")
            self.column_vars.append(column)
            self.direction_vars.append(direction)
            ttk.Combobox(body, textvariable=column, values=("", *columns), state="readonly", width=27).grid(row=index + 1, column=0, pady=4, padx=(0, 8))
            ttk.Combobox(body, textvariable=direction, values=("Ascending", "Descending"), state="readonly", width=14).grid(row=index + 1, column=1, pady=4)
        ttk.Label(body, text="Rows are sorted from the first selection to the third.", foreground="#555").grid(row=4, column=0, columnspan=2, sticky="w", pady=(8, 0))
        buttons = ttk.Frame(body)
        buttons.grid(row=5, column=0, columnspan=2, sticky="e", pady=(12, 0))
        ttk.Button(buttons, text="Clear sorting", command=self.clear).pack(side="left", padx=(0, 70))
        ttk.Button(buttons, text="Cancel", command=self.destroy).pack(side="right")
        ttk.Button(buttons, text="Apply", command=self.accept).pack(side="right", padx=8)
        self.grab_set()

    def clear(self):
        self.result = []
        self.destroy()

    def accept(self):
        result = [(column.get(), direction.get() == "Descending") for column, direction in zip(self.column_vars, self.direction_vars) if column.get()]
        if len({item[0] for item in result}) != len(result):
            messagebox.showwarning("Sort rows", "Choose each sort column only once.", parent=self)
            return
        self.result = result
        self.destroy()


class ChoiceDialog(tk.Toplevel):
    def __init__(self, parent, title, prompt, choices):
        super().__init__(parent)
        self.title(title)
        self.transient(parent)
        self.geometry("420x390")
        self.result = None
        self.all_choices = list(choices)
        body = ttk.Frame(self, padding=10)
        body.pack(fill="both", expand=True)
        ttk.Label(body, text=prompt).pack(anchor="w")
        self.search_var = tk.StringVar()
        entry = ttk.Entry(body, textvariable=self.search_var)
        entry.pack(fill="x", pady=6)
        self.listbox = tk.Listbox(body)
        self.listbox.pack(fill="both", expand=True)
        buttons = ttk.Frame(body)
        buttons.pack(fill="x", pady=(8, 0))
        ttk.Button(buttons, text="Cancel", command=self.destroy).pack(side="right")
        ttk.Button(buttons, text="Choose", command=self.accept).pack(side="right", padx=8)
        self.search_var.trace_add("write", lambda *_: self.refresh())
        self.listbox.bind("<Double-1>", lambda _e: self.accept())
        self.bind("<Escape>", lambda _e: self.destroy())
        self.bind("<Return>", lambda _e: self.accept())
        self.grab_set()
        self.refresh()
        entry.focus_set()

    def refresh(self):
        query = self.search_var.get().casefold()
        self.listbox.delete(0, "end")
        for choice in self.all_choices:
            if query in choice.casefold():
                self.listbox.insert("end", choice)
        if self.listbox.size():
            self.listbox.selection_set(0)

    def accept(self):
        selection = self.listbox.curselection()
        if selection:
            self.result = self.listbox.get(selection[0])
            self.destroy()


class CellEditorDialog(tk.Toplevel):
    def __init__(self, parent, column, value):
        super().__init__(parent)
        self.title(f"Edit cell — {column}")
        self.transient(parent)
        self.geometry("680x470")
        self.result = None
        self.blob_value = value if isinstance(value, bytes) else None
        self.mode_var = tk.StringVar(value="BLOB" if isinstance(value, bytes) else ("NULL" if value is None else "Text"))
        top = ttk.Frame(self, padding=10)
        top.pack(fill="x")
        ttk.Label(top, text=f"Column: {column}").pack(side="left")
        ttk.Label(top, text="Value type:").pack(side="left", padx=(25, 5))
        ttk.Combobox(top, textvariable=self.mode_var, values=("Text", "NULL", "BLOB"), state="readonly", width=10).pack(side="left")
        ttk.Button(top, text="Load BLOB from file...", command=self.load_blob).pack(side="right")
        ttk.Button(top, text="Save BLOB to file...", command=self.save_blob).pack(side="right", padx=5)
        self.text = tk.Text(self, wrap="none", font=(MONO_FONT, 10), undo=True)
        self.text.pack(fill="both", expand=True, padx=10)
        if isinstance(value, bytes):
            self.text.insert("1.0", value.hex(" "))
        elif value is not None:
            self.text.insert("1.0", str(value))
        self.info_var = tk.StringVar()
        ttk.Label(self, textvariable=self.info_var, padding=(10, 5)).pack(fill="x")
        buttons = ttk.Frame(self, padding=10)
        buttons.pack(fill="x")
        ttk.Button(buttons, text="Cancel", command=self.destroy).pack(side="right")
        ttk.Button(buttons, text="Apply", command=self.accept).pack(side="right", padx=8)
        self.mode_var.trace_add("write", lambda *_: self.update_info())
        self.bind("<Escape>", lambda _e: self.destroy())
        self.grab_set()
        self.update_info()

    def update_info(self):
        if self.mode_var.get() == "NULL":
            self.info_var.set("The SQL NULL value will be stored.")
        elif self.mode_var.get() == "BLOB":
            self.info_var.set(f"Binary value: {len(self.blob_value or b''):,} byte(s). Hexadecimal is displayed.")
        else:
            self.info_var.set("Text is submitted as a bound value; SQLite applies the column affinity.")

    def load_blob(self):
        path = filedialog.askopenfilename(title="Load BLOB from file", parent=self)
        if path:
            try:
                with open(path, "rb") as handle:
                    self.blob_value = handle.read()
                self.mode_var.set("BLOB")
                self.text.delete("1.0", "end")
                self.text.insert("1.0", self.blob_value.hex(" "))
                self.update_info()
            except OSError as exc:
                messagebox.showerror("Load BLOB failed", str(exc), parent=self)

    def save_blob(self):
        if self.blob_value is None:
            messagebox.showinfo("Save BLOB", "There is no binary value to save.", parent=self)
            return
        path = filedialog.asksaveasfilename(title="Save BLOB to file", parent=self)
        if path:
            try:
                with open(path, "wb") as handle:
                    handle.write(self.blob_value)
            except OSError as exc:
                messagebox.showerror("Save BLOB failed", str(exc), parent=self)

    def accept(self):
        mode = self.mode_var.get()
        if mode == "NULL":
            value = None
        elif mode == "Text":
            value = self.text.get("1.0", "end-1c")
        else:
            try:
                value = bytes.fromhex(self.text.get("1.0", "end-1c"))
            except ValueError:
                messagebox.showerror("Invalid BLOB", "BLOB text must contain hexadecimal byte pairs.", parent=self)
                return
        self.result = (True, value)
        self.destroy()


class ChartDialog(tk.Toplevel):
    COLORS = ("#3478c7", "#2a9d8f", "#e76f51", "#7b61a8", "#e9a23b")

    def __init__(self, parent, columns, rows):
        super().__init__(parent)
        self.title("Plot displayed data")
        self.geometry("900x570")
        self.transient(parent)
        self.columns, self.rows = columns, rows
        self.x_var = tk.StringVar(value=columns[0] if columns else "")
        self.y_var = tk.StringVar(value=columns[1] if len(columns) > 1 else (columns[0] if columns else ""))
        self.kind_var = tk.StringVar(value="Bar")
        topbar = ttk.Frame(self, padding=8)
        topbar.pack(fill="x")
        ttk.Label(topbar, text="Labels").pack(side="left")
        ttk.Combobox(topbar, textvariable=self.x_var, values=columns, state="readonly", width=20).pack(side="left", padx=5)
        ttk.Label(topbar, text="Values").pack(side="left", padx=(10, 0))
        ttk.Combobox(topbar, textvariable=self.y_var, values=columns, state="readonly", width=20).pack(side="left", padx=5)
        ttk.Combobox(topbar, textvariable=self.kind_var, values=("Bar", "Line"), state="readonly", width=8).pack(side="left", padx=10)
        ttk.Button(topbar, text="Draw", command=self.draw).pack(side="left")
        ttk.Label(topbar, text="First 100 displayed rows", foreground="#555").pack(side="right")
        self.canvas = tk.Canvas(self, background="white", highlightthickness=1, highlightbackground="#aaa")
        self.canvas.pack(fill="both", expand=True, padx=8, pady=(0, 8))
        self.canvas.bind("<Configure>", lambda _e: self.draw())
        self.after(100, self.draw)

    def draw(self):
        self.canvas.delete("all")
        if not self.columns or not self.rows:
            self.canvas.create_text(30, 30, text="No displayed data to plot", anchor="nw")
            return
        xi, yi = self.columns.index(self.x_var.get()), self.columns.index(self.y_var.get())
        points = []
        for row in self.rows[:100]:
            try:
                points.append((str(row[xi]), float(row[yi])))
            except (TypeError, ValueError):
                continue
        if not points:
            self.canvas.create_text(30, 30, text="The selected value column has no numeric data", anchor="nw")
            return
        width, height = max(self.canvas.winfo_width(), 500), max(self.canvas.winfo_height(), 300)
        left, top, right, bottom = 70, 30, width - 25, height - 70
        values = [point[1] for point in points]
        low, high = min(0, min(values)), max(0, max(values))
        span = high - low or 1
        zero_y = bottom - (0 - low) / span * (bottom - top)
        self.canvas.create_line(left, top, left, bottom, fill="#666")
        self.canvas.create_line(left, zero_y, right, zero_y, fill="#666")
        self.canvas.create_text(left - 8, top, text=f"{high:g}", anchor="e")
        self.canvas.create_text(left - 8, bottom, text=f"{low:g}", anchor="e")
        step = (right - left) / max(len(points), 1)
        coords = []
        for index, (label, value) in enumerate(points):
            x = left + (index + 0.5) * step
            y = bottom - (value - low) / span * (bottom - top)
            coords.extend((x, y))
            if self.kind_var.get() == "Bar":
                half = max(1, step * .35)
                self.canvas.create_rectangle(x - half, min(y, zero_y), x + half, max(y, zero_y), fill=self.COLORS[index % len(self.COLORS)], outline="")
            if index % max(1, len(points) // 12) == 0:
                short = label if len(label) < 12 else label[:10] + "…"
                self.canvas.create_text(x, bottom + 10, text=short, angle=35, anchor="nw")
        if self.kind_var.get() == "Line" and len(coords) >= 4:
            self.canvas.create_line(*coords, fill=self.COLORS[0], width=2, smooth=True)
            for x, y in zip(coords[::2], coords[1::2]):
                self.canvas.create_oval(x - 3, y - 3, x + 3, y + 3, fill=self.COLORS[0], outline="")


class PragmaDialog(tk.Toplevel):
    DEFINITIONS = (
        ("foreign_keys", ("0", "1")),
        ("journal_mode", ("DELETE", "TRUNCATE", "PERSIST", "MEMORY", "WAL", "OFF")),
        ("synchronous", ("0", "1", "2", "3")),
        ("temp_store", ("0", "1", "2")),
        ("cache_size", ()),
        ("busy_timeout", ()),
        ("auto_vacuum", ("0", "1", "2")),
    )

    def __init__(self, parent, connection, readonly=False):
        super().__init__(parent)
        self.title("Database pragmas")
        self.transient(parent)
        self.resizable(False, False)
        self.connection = connection
        self.vars = {}
        body = ttk.Frame(self, padding=14)
        body.pack(fill="both", expand=True)
        ttk.Label(body, text="Setting", font=(UI_FONT, 9, "bold")).grid(row=0, column=0, sticky="w")
        ttk.Label(body, text="Value", font=(UI_FONT, 9, "bold")).grid(row=0, column=1, sticky="w")
        for row, (name, choices) in enumerate(self.DEFINITIONS, 1):
            variable = tk.StringVar(value=str(connection.execute(f"PRAGMA {name}").fetchone()[0]))
            self.vars[name] = variable
            ttk.Label(body, text=name).grid(row=row, column=0, sticky="w", padx=(0, 20), pady=4)
            widget = (ttk.Combobox(body, textvariable=variable, values=choices, state="readonly", width=22)
                      if choices else ttk.Entry(body, textvariable=variable, width=25))
            widget.grid(row=row, column=1, sticky="ew", pady=4)
        ttk.Label(body, text="Some settings take effect after reopening the database.", foreground="#555").grid(row=len(self.DEFINITIONS) + 1, column=0, columnspan=2, sticky="w", pady=(8, 0))
        buttons = ttk.Frame(body)
        buttons.grid(row=len(self.DEFINITIONS) + 2, column=0, columnspan=2, sticky="e", pady=(12, 0))
        ttk.Button(buttons, text="Close", command=self.destroy).pack(side="right")
        ttk.Button(buttons, text="Apply", command=self.apply, state="disabled" if readonly else "normal").pack(side="right", padx=8)
        self.grab_set()

    def apply(self):
        try:
            for name, choices in self.DEFINITIONS:
                value = self.vars[name].get().strip()
                if choices and value.upper() not in choices:
                    raise ValueError(f"Unsupported value for {name}")
                if not choices:
                    int(value)
                self.connection.execute(f"PRAGMA {name}={value}")
            self.destroy()
        except Exception as exc:
            messagebox.showerror("Pragma error", str(exc), parent=self)


class RecordDialog(tk.Toplevel):
    def __init__(self, parent: tk.Widget, title: str, columns: list[str], values=None, allow_default=False):
        super().__init__(parent)
        self.title(title)
        self.transient(parent)
        self.resizable(True, True)
        self.result = None
        self.allow_default = allow_default
        self.vars: list[tk.StringVar] = []
        values = values or [""] * len(columns)
        self.original_values = list(values)

        frame = ttk.Frame(self, padding=12)
        frame.pack(fill="both", expand=True)
        for row, (column, value) in enumerate(zip(columns, values)):
            ttk.Label(frame, text=column).grid(row=row, column=0, sticky="nw", padx=(0, 10), pady=4)
            if isinstance(value, bytes):
                shown = f"<BLOB {len(value)} bytes>"
            else:
                shown = "<NULL>" if value is None else str(value)
            var = tk.StringVar(value=shown)
            self.vars.append(var)
            ttk.Entry(frame, textvariable=var, width=56).grid(row=row, column=1, sticky="ew", pady=4)
        frame.columnconfigure(1, weight=1)
        note = "Use <NULL> for SQL NULL" + (" or <DEFAULT> to use the column default." if allow_default else ".")
        ttk.Label(frame, text=note, foreground="#555").grid(
            row=len(columns), column=0, columnspan=2, sticky="w", pady=(8, 4)
        )
        buttons = ttk.Frame(frame)
        buttons.grid(row=len(columns) + 1, column=0, columnspan=2, sticky="e", pady=(8, 0))
        ttk.Button(buttons, text="Cancel", command=self.destroy).pack(side="right")
        ttk.Button(buttons, text="Save", command=self._save).pack(side="right", padx=8)
        self.bind("<Escape>", lambda _e: self.destroy())
        self.bind("<Return>", lambda _e: self._save())
        self.grab_set()
        self.wait_visibility()
        self.focus_set()

    def _save(self):
        result = []
        for var, original in zip(self.vars, self.original_values):
            value = var.get()
            if value == "<NULL>":
                result.append(None)
            elif isinstance(original, bytes) and value == f"<BLOB {len(original)} bytes>":
                result.append(original)
            else:
                result.append(value)
        self.result = result
        self.destroy()


class RowDetailsDialog(tk.Toplevel):
    """Show every value in one record without relying on grid column widths."""

    def __init__(self, parent: tk.Widget, columns: list[str], values):
        super().__init__(parent)
        self.title("Row details")
        self.geometry("760x520")
        self.minsize(480, 300)
        self.transient(parent)

        frame = ttk.Frame(self, padding=10)
        frame.pack(fill="both", expand=True)
        text = tk.Text(frame, wrap="word", font=(MONO_FONT, 10), padx=8, pady=8)
        scroll = ttk.Scrollbar(frame, orient="vertical", command=text.yview)
        text.configure(yscrollcommand=scroll.set)
        text.pack(side="left", fill="both", expand=True)
        scroll.pack(side="right", fill="y")
        for column, value in zip(columns, values):
            shown = f"<BLOB {len(value)} bytes>" if isinstance(value, bytes) else ("<NULL>" if value is None else str(value))
            text.insert("end", f"{column}\n", "field")
            text.insert("end", f"{shown}\n\n")
        text.tag_configure("field", font=(UI_FONT, 10, "bold"))
        text.configure(state="disabled")
        ttk.Button(self, text="Close", command=self.destroy).pack(pady=(0, 10))
        self.bind("<Escape>", lambda _e: self.destroy())
        self.grab_set()


class SQLiteStudio(tk.Tk):
    def __init__(self, initial_path: str | None = None):
        super().__init__()
        self.title(APP_NAME)
        self.geometry("1380x850")
        self.minsize(980, 640)
        self.protocol("WM_DELETE_WINDOW", self.close_app)

        self.connection: sqlite3.Connection | None = None
        self.database_path: Path | None = None
        self.readonly = False
        self.selected_object: tuple[str, str] | None = None
        self.page = 0
        self.total_rows = 0
        self.sort_column: str | None = None
        self.sort_descending = False
        self.sort_terms: list[tuple[str, bool]] = []
        self.advanced_filters: list[tuple[str, str, str]] = []
        self.current_columns: list[str] = []
        self.current_row_keys: dict[str, tuple[tuple[str, ...], tuple] | None] = {}
        self.current_raw_rows: dict[str, tuple] = {}
        self.active_cell: tuple[str, int] | None = None
        self.sql_editors: list[tk.Text] = []
        self.sql_paths: dict[tk.Text, str | None] = {}
        self.sql_history: list[str] = []
        self._highlight_job = None
        self._ui_queue: queue.Queue = queue.Queue()
        self._db_lock = threading.Lock()
        self._sql_running = False
        self._data_version = None
        self._schema_filter_job = None
        self._row_count_cache: dict = {}
        self._schema_generation = 0
        self.settings = self._load_settings()

        self._create_menu()
        self._create_widgets()
        self.apply_theme()
        self._set_connected_state(False)
        self.after(100, self._process_ui_queue)
        self.after(100, lambda: self._open_initial(initial_path))

    def _create_menu(self):
        menu = tk.Menu(self)
        file_menu = tk.Menu(menu, tearoff=False)
        file_menu.add_command(label="Open database...", accelerator="Ctrl+O", command=self.open_database)
        file_menu.add_command(label="Open database read-only...", command=lambda: self.open_database(readonly=True))
        self.recent_menu = tk.Menu(file_menu, tearoff=False)
        file_menu.add_cascade(label="Open recent", menu=self.recent_menu)
        self._refresh_recent_menu()
        self.reopen_last_var = tk.BooleanVar(value=bool(self.settings.get("reopen_last", False)))
        file_menu.add_checkbutton(label="Reopen last database at startup", variable=self.reopen_last_var,
                                  command=self._save_reopen_setting)
        file_menu.add_command(label="New database...", accelerator="Ctrl+N", command=self.new_database)
        file_menu.add_command(label="Attach database...", command=self.attach_database)
        file_menu.add_separator()
        file_menu.add_command(label="Backup database...", command=self.backup_database)
        file_menu.add_command(label="Export displayed results to CSV...", command=self.export_csv)
        file_menu.add_command(label="Export displayed results to JSON...", command=self.export_json)
        file_menu.add_command(label="Export displayed results to Markdown...", command=self.export_markdown)
        file_menu.add_command(label="Export selected table as SQL INSERT script...", command=self.export_table_sql)
        file_menu.add_command(label="Export selected table to CSV...", command=self.export_table_csv)
        file_menu.add_command(label="Export selected table to JSON...", command=self.export_table_json)
        file_menu.add_command(label="Export selected table to Markdown...", command=self.export_table_markdown)
        file_menu.add_command(label="Import CSV into new table...", command=self.import_csv)
        file_menu.add_command(label="Import JSON into new table...", command=self.import_json)
        file_menu.add_separator()
        file_menu.add_command(label="Export database as SQL dump...", command=self.export_sql_dump)
        file_menu.add_command(label="Import SQL script...", command=self.import_sql_dump)
        file_menu.add_separator()
        file_menu.add_command(label="Save workspace project...", command=self.save_project)
        file_menu.add_command(label="Open workspace project...", command=self.open_project)
        file_menu.add_separator()
        file_menu.add_command(label="Close database", command=self.close_database)
        file_menu.add_command(label="Exit", command=self.close_app)
        menu.add_cascade(label="File", menu=file_menu)

        structure_menu = tk.Menu(menu, tearoff=False)
        structure_menu.add_command(label="Create table...", command=self.create_table)
        structure_menu.add_command(label="Add column...", command=self.add_column)
        structure_menu.add_command(label="Rename selected table...", command=self.rename_table)
        structure_menu.add_command(label="Rename column...", command=self.rename_column)
        structure_menu.add_command(label="Drop column...", command=self.drop_column)
        structure_menu.add_separator()
        structure_menu.add_command(label="Create index...", command=self.create_index)
        structure_menu.add_command(label="Drop selected object...", command=self.drop_object)
        menu.add_cascade(label="Structure", menu=structure_menu)

        db_menu = tk.Menu(menu, tearoff=False)
        db_menu.add_command(label="Refresh schema", accelerator="F5", command=self.refresh_schema)
        db_menu.add_command(label="Database information", command=self.show_database_info)
        db_menu.add_command(label="Search all tables...", accelerator="Ctrl+F", command=self.search_all_tables)
        db_menu.add_command(label="Pragma settings...", command=self.edit_pragmas)
        db_menu.add_separator()
        db_menu.add_command(label="Integrity check", command=self.integrity_check)
        db_menu.add_command(label="Foreign-key check", command=self.foreign_key_check)
        db_menu.add_command(label="Vacuum / compact", command=self.vacuum_database)
        db_menu.add_command(label="Analyze", command=self.analyze_database)
        db_menu.add_command(label="Optimize", command=self.optimize_database)
        db_menu.add_command(label="Reindex", command=self.reindex_database)
        db_menu.add_separator()
        db_menu.add_command(label="Commit", accelerator="Ctrl+S", command=self.commit)
        db_menu.add_command(label="Rollback", command=self.rollback)
        menu.add_cascade(label="Database", menu=db_menu)

        view_menu = tk.Menu(menu, tearoff=False)
        view_menu.add_command(label="Plot displayed data...", command=self.plot_data)
        view_menu.add_command(label="Toggle dark theme", command=self.toggle_theme)
        view_menu.add_separator()
        view_menu.add_command(label="Larger text", accelerator="Ctrl++", command=lambda: self.change_font_size(1))
        view_menu.add_command(label="Smaller text", accelerator="Ctrl+-", command=lambda: self.change_font_size(-1))
        view_menu.add_command(label="Reset text size", accelerator="Ctrl+0", command=lambda: self.change_font_size(None))
        menu.add_cascade(label="View", menu=view_menu)

        help_menu = tk.Menu(menu, tearoff=False)
        help_menu.add_command(label="Keyboard shortcuts", command=self.show_shortcuts)
        help_menu.add_command(label="About", command=lambda: messagebox.showinfo(
            "About", f"{APP_NAME}\n\nA standard-library SQLite viewer and editor.\nSQLite {sqlite3.sqlite_version}"
        ))
        menu.add_cascade(label="Help", menu=help_menu)
        self.config(menu=menu)
        self.bind("<Control-o>", lambda _e: self.open_database())
        self.bind("<Control-n>", lambda _e: self.new_database())
        self.bind("<Control-s>", lambda _e: self.commit())
        self.bind("<F5>", lambda _e: self.refresh_schema())
        self.bind("<Control-f>", lambda _e: self.search_all_tables())
        for sequence, delta in (("<Control-equal>", 1), ("<Control-plus>", 1), ("<Control-minus>", -1), ("<Control-0>", None)):
            self.bind(sequence, lambda _e, d=delta: self.change_font_size(d))

    def _create_widgets(self):
        toolbar = ttk.Frame(self, padding=(6, 5))
        toolbar.pack(fill="x")
        ttk.Label(toolbar, text=APP_NAME, font=(UI_FONT, 13, "bold")).pack(side="left", padx=(2, 14))
        for text, command in (("Open", self.open_database), ("New", self.new_database),
                              ("Refresh", self.refresh_schema), ("Commit", self.commit),
                              ("Rollback", self.rollback)):
            ttk.Button(toolbar, text=text, command=command).pack(side="left", padx=2)
        self.path_label = ttk.Label(toolbar, text="No database open", anchor="e")
        self.path_label.pack(side="right", fill="x", expand=True, padx=8)
        self.tx_label = ttk.Label(toolbar, text="", foreground="#b25a00")
        self.tx_label.pack(side="right", padx=6)

        pane = ttk.Panedwindow(self, orient="horizontal")
        pane.pack(fill="both", expand=True)
        left = ttk.Frame(pane, padding=(6, 2, 2, 4))
        right = ttk.Frame(pane, padding=(2, 2, 6, 4))
        pane.add(left, weight=1)
        pane.add(right, weight=4)

        ttk.Label(left, text="Database objects").pack(anchor="w")
        self.schema_filter_var = tk.StringVar()
        filter_entry = ttk.Entry(left, textvariable=self.schema_filter_var)
        filter_entry.pack(fill="x", pady=(2, 4))
        filter_entry.bind("<KeyRelease>", lambda _e: self._schedule_schema_filter())
        ttk.Label(
            left,
            text="Select a table to view its rows",
            foreground="#555",
        ).pack(anchor="w", pady=(0, 5))
        self.schema_tree = ttk.Treeview(left, show="tree", selectmode="browse")
        schema_scroll = ttk.Scrollbar(left, orient="vertical", command=self.schema_tree.yview)
        self.schema_tree.configure(yscrollcommand=schema_scroll.set)
        schema_scroll.pack(side="right", fill="y")
        self.schema_tree.pack(fill="both", expand=True)
        self.schema_tree.bind("<<TreeviewSelect>>", self.on_schema_select)
        self.schema_tree.bind("<Double-1>", lambda _e: self.open_selected_object())
        ttk.Button(left, text="Browse selected table", command=self.open_selected_object).pack(
            fill="x", pady=(5, 0)
        )

        self.notebook = ttk.Notebook(right)
        self.notebook.pack(fill="both", expand=True)
        self.data_tab = ttk.Frame(self.notebook)
        self.sql_tab = ttk.Frame(self.notebook)
        self.schema_tab = ttk.Frame(self.notebook)
        self.notebook.add(self.data_tab, text="Browse Data")
        self.notebook.add(self.sql_tab, text="Execute SQL")
        self.notebook.add(self.schema_tab, text="Schema")
        self._create_data_tab()
        self._create_sql_tab()
        self._create_schema_tab()

        self.status = tk.StringVar(value="Ready")
        ttk.Label(self, textvariable=self.status, relief="sunken", anchor="w", padding=(6, 3)).pack(fill="x")

    def _create_data_tab(self):
        controls = ttk.Frame(self.data_tab, padding=5)
        controls.pack(fill="x")
        ttk.Button(controls, text="Add row", command=self.add_row).pack(side="left", padx=2)
        ttk.Button(controls, text="Edit row", command=self.edit_row).pack(side="left", padx=2)
        ttk.Button(controls, text="Edit cell...", command=self.edit_cell_dialog).pack(side="left", padx=2)
        ttk.Button(controls, text="Delete row", command=self.delete_row).pack(side="left", padx=2)
        ttk.Button(controls, text="Duplicate", command=self.duplicate_row).pack(side="left", padx=2)
        ttk.Separator(controls, orient="vertical").pack(side="left", fill="y", padx=6)
        ttk.Button(controls, text="Copy", command=self.copy_rows).pack(side="left", padx=2)
        ttk.Button(controls, text="Copy as...", command=self.copy_rows_as).pack(side="left", padx=2)
        ttk.Button(controls, text="Paste", command=self.paste_cells).pack(side="left", padx=2)
        ttk.Button(controls, text="Plot...", command=self.plot_data).pack(side="left", padx=2)
        ttk.Button(controls, text="Profile column...", command=self.profile_column_dialog).pack(side="left", padx=2)

        filters = ttk.Frame(self.data_tab, padding=(7, 2, 7, 5))
        filters.pack(fill="x")
        ttk.Label(filters, text="SQL WHERE:").pack(side="left", padx=(0, 3))
        self.filter_var = tk.StringVar()
        filter_entry = ttk.Entry(filters, textvariable=self.filter_var)
        filter_entry.pack(side="left", fill="x", expand=True)
        filter_entry.bind("<Return>", lambda _e: self.load_table(0))
        ttk.Button(filters, text="Apply", command=lambda: self.load_table(0)).pack(side="left", padx=3)
        ttk.Button(filters, text="Add column filter...", command=self.add_filter).pack(side="left", padx=3)
        ttk.Button(filters, text="Sort...", command=self.choose_sort).pack(side="left", padx=3)
        ttk.Button(filters, text="Clear filters", command=self.clear_filter).pack(side="left")
        self.filter_summary = ttk.Label(self.data_tab, text="", foreground="#555", padding=(7, 0, 7, 4))
        self.filter_summary.pack(fill="x")

        grid_frame = ttk.Frame(self.data_tab)
        grid_frame.pack(fill="both", expand=True)
        self.data_grid = ttk.Treeview(grid_frame, show="headings", selectmode="extended")
        yscroll = ttk.Scrollbar(grid_frame, orient="vertical", command=self.data_grid.yview)
        xscroll = ttk.Scrollbar(grid_frame, orient="horizontal", command=self.data_grid.xview)
        self.data_grid.configure(yscrollcommand=yscroll.set, xscrollcommand=xscroll.set)
        self.data_grid.grid(row=0, column=0, sticky="nsew")
        yscroll.grid(row=0, column=1, sticky="ns")
        xscroll.grid(row=1, column=0, sticky="ew")
        grid_frame.rowconfigure(0, weight=1)
        grid_frame.columnconfigure(0, weight=1)
        self.data_grid.bind("<Button-1>", self.remember_active_cell, add=True)
        self.data_grid.bind("<Double-1>", self.show_row_details)
        self.data_grid.bind("<Control-c>", lambda _e: self.copy_rows())
        self.data_grid.bind("<Control-v>", lambda _e: self.paste_cells())
        self.data_grid.bind("<Delete>", lambda _e: self.delete_row())

        pager = ttk.Frame(self.data_tab, padding=5)
        pager.pack(fill="x")
        ttk.Button(pager, text="Previous", command=self.previous_page).pack(side="left")
        self.page_label = ttk.Label(pager, text="Page 1")
        self.page_label.pack(side="left", padx=8)
        ttk.Button(pager, text="Next", command=self.next_page).pack(side="left")
        ttk.Label(pager, text="Go to page:").pack(side="left", padx=(16, 3))
        self.goto_page_var = tk.StringVar(value="1")
        goto = ttk.Entry(pager, textvariable=self.goto_page_var, width=7)
        goto.pack(side="left")
        goto.bind("<Return>", lambda _e: self.go_to_page())
        self.row_count_label = ttk.Label(pager, text="0 rows")
        self.row_count_label.pack(side="right")

    def _create_sql_tab(self):
        controls = ttk.Frame(self.sql_tab, padding=5)
        controls.pack(fill="x")
        self.run_button = ttk.Button(controls, text="Run selected / all (F9)", command=self.execute_sql)
        self.run_button.pack(side="left", padx=2)
        self.cancel_button = ttk.Button(controls, text="Cancel", command=self.cancel_sql, state="disabled")
        self.cancel_button.pack(side="left", padx=2)
        ttk.Button(controls, text="Query plan", command=self.explain_query).pack(side="left", padx=2)
        ttk.Button(controls, text="Format", command=self.format_current_sql).pack(side="left", padx=2)
        ttk.Separator(controls, orient="vertical").pack(side="left", fill="y", padx=6)
        ttk.Button(controls, text="New SQL tab", command=self.add_sql_tab).pack(side="left", padx=2)
        ttk.Button(controls, text="Open SQL...", command=self.open_sql_file).pack(side="left", padx=2)
        ttk.Button(controls, text="Save SQL...", command=self.save_sql_file).pack(side="left", padx=2)
        ttk.Button(controls, text="Close tab", command=self.close_sql_tab).pack(side="left", padx=2)
        ttk.Button(controls, text="Complete (Ctrl+Space)", command=self.sql_autocomplete).pack(side="left", padx=8)
        ttk.Label(controls, text="Changes remain pending until Commit.", foreground="#555").pack(side="right")
        sql_pane = ttk.Panedwindow(self.sql_tab, orient="vertical")
        sql_pane.pack(fill="both", expand=True)
        editor_frame = ttk.Frame(sql_pane)
        output_frame = ttk.Frame(sql_pane)
        sql_pane.add(editor_frame, weight=2)
        sql_pane.add(output_frame, weight=3)
        self.sql_notebook = ttk.Notebook(editor_frame)
        self.sql_notebook.pack(fill="both", expand=True)
        self.add_sql_tab("-- Enter SQL here, select part of it if desired, then press F9.\nSELECT sqlite_version();", "Query 1")

        self.output_notebook = ttk.Notebook(output_frame)
        self.output_notebook.pack(fill="both", expand=True)
        result_frame = ttk.Frame(self.output_notebook)
        history_frame = ttk.Frame(self.output_notebook)
        log_frame = ttk.Frame(self.output_notebook)
        self.output_notebook.add(result_frame, text="Results")
        self.output_notebook.add(history_frame, text="History")
        self.output_notebook.add(log_frame, text="SQL Log")
        self.result_grid = ttk.Treeview(result_frame, show="headings")
        ry = ttk.Scrollbar(result_frame, orient="vertical", command=self.result_grid.yview)
        rx = ttk.Scrollbar(result_frame, orient="horizontal", command=self.result_grid.xview)
        self.result_grid.configure(yscrollcommand=ry.set, xscrollcommand=rx.set)
        self.result_grid.grid(row=0, column=0, sticky="nsew")
        ry.grid(row=0, column=1, sticky="ns")
        rx.grid(row=1, column=0, sticky="ew")
        result_frame.rowconfigure(0, weight=1)
        result_frame.columnconfigure(0, weight=1)

        self.history_list = tk.Listbox(history_frame, font=(MONO_FONT, 9))
        self.history_list.pack(fill="both", expand=True)
        self.history_list.bind("<Double-1>", lambda _e: self.restore_history())
        self.log_text = tk.Text(log_frame, wrap="none", font=(MONO_FONT, 9), state="disabled")
        self.log_text.pack(fill="both", expand=True)

    def _create_schema_tab(self):
        toolbar = ttk.Frame(self.schema_tab, padding=5)
        toolbar.pack(fill="x")
        ttk.Button(toolbar, text="Create table...", command=self.create_table).pack(side="left", padx=2)
        ttk.Button(toolbar, text="Add column...", command=self.add_column).pack(side="left", padx=2)
        ttk.Button(toolbar, text="Rename table...", command=self.rename_table).pack(side="left", padx=2)
        ttk.Button(toolbar, text="Create index...", command=self.create_index).pack(side="left", padx=2)
        ttk.Button(toolbar, text="Drop object...", command=self.drop_object).pack(side="left", padx=2)
        self.structure_notebook = ttk.Notebook(self.schema_tab)
        self.structure_notebook.pack(fill="both", expand=True)
        columns_frame = ttk.Frame(self.structure_notebook)
        indexes_frame = ttk.Frame(self.structure_notebook)
        foreign_frame = ttk.Frame(self.structure_notebook)
        definition_frame = ttk.Frame(self.structure_notebook)
        self.structure_notebook.add(columns_frame, text="Columns")
        self.structure_notebook.add(indexes_frame, text="Indexes")
        self.structure_notebook.add(foreign_frame, text="Foreign Keys")
        self.structure_notebook.add(definition_frame, text="Definition")
        self.columns_grid = ttk.Treeview(columns_frame, columns=("cid", "name", "type", "notnull", "default", "pk"), show="headings")
        for column, label, width in (("cid", "#", 35), ("name", "Name", 170), ("type", "Type", 100),
                                     ("notnull", "Not NULL", 70), ("default", "Default", 170), ("pk", "Primary key", 80)):
            self.columns_grid.heading(column, text=label)
            self.columns_grid.column(column, width=width, stretch=True)
        self.columns_grid.pack(fill="both", expand=True)
        self.indexes_grid = ttk.Treeview(indexes_frame, columns=("name", "unique", "origin", "partial", "columns"), show="headings")
        for column, label, width in (("name", "Name", 190), ("unique", "Unique", 70), ("origin", "Origin", 70),
                                     ("partial", "Partial", 70), ("columns", "Columns", 280)):
            self.indexes_grid.heading(column, text=label)
            self.indexes_grid.column(column, width=width, stretch=True)
        self.indexes_grid.pack(fill="both", expand=True)
        self.foreign_grid = ttk.Treeview(foreign_frame, columns=("id", "from", "table", "to", "update", "delete", "match"), show="headings")
        for column, label, width in (("id", "#", 35), ("from", "From", 130), ("table", "References table", 150),
                                     ("to", "References column", 150), ("update", "On update", 90),
                                     ("delete", "On delete", 90), ("match", "Match", 70)):
            self.foreign_grid.heading(column, text=label)
            self.foreign_grid.column(column, width=width, stretch=True)
        self.foreign_grid.pack(fill="both", expand=True)
        self.schema_text = tk.Text(definition_frame, wrap="none", font=(MONO_FONT, 10), state="disabled")
        self.schema_text.pack(fill="both", expand=True)

    def _set_connected_state(self, connected: bool):
        if not connected:
            self.title(APP_NAME)
            self.path_label.config(text="No database open")

    def _load_settings(self):
        try:
            with open(CONFIG_PATH, "r", encoding="utf-8") as handle:
                data = json.load(handle)
                return data if isinstance(data, dict) else {}
        except (OSError, ValueError):
            return {}

    def _save_settings(self):
        try:
            with open(CONFIG_PATH, "w", encoding="utf-8") as handle:
                json.dump(self.settings, handle, indent=2)
        except OSError:
            pass

    def apply_theme(self):
        dark = self.settings.get("dark_theme", False)
        style = ttk.Style(self)
        if dark:
            style.theme_use("clam")
            style.configure(".", background="#252526", foreground="#f0f0f0", fieldbackground="#333337")
            style.configure("Treeview", background="#2d2d30", foreground="#f0f0f0", fieldbackground="#2d2d30")
            style.map("Treeview", background=[("selected", "#0e639c")])
            style.configure("TNotebook", background="#252526")
            style.configure("TNotebook.Tab", background="#333337", foreground="#f0f0f0")
            text_bg, text_fg, insert = "#1e1e1e", "#d4d4d4", "white"
        else:
            try:
                style.theme_use("vista")
            except tk.TclError:
                style.theme_use("clam")
            text_bg, text_fg, insert = "white", "black", "black"
        size = int(self.settings.get("font_size", 10))
        style.configure("TButton", padding=(8, 5))
        style.configure("Treeview", rowheight=size + 15, font=(UI_FONT, size))
        style.configure("Treeview.Heading", font=(UI_FONT, max(8, size - 1), "bold"))
        style.configure("TNotebook.Tab", padding=(12, 6))
        for widget in getattr(self, "sql_editors", []):
            widget.configure(background=text_bg, foreground=text_fg, insertbackground=insert, font=(MONO_FONT, size))
            widget.tag_configure("keyword", font=(MONO_FONT, size, "bold"))
        for name in ("schema_text", "log_text"):
            widget = getattr(self, name, None)
            if widget:
                widget.configure(background=text_bg, foreground=text_fg, insertbackground=insert, font=(MONO_FONT, size))
        history = getattr(self, "history_list", None)
        if history:
            history.configure(font=(MONO_FONT, max(7, size - 1)))

    def toggle_theme(self):
        self.settings["dark_theme"] = not self.settings.get("dark_theme", False)
        self._save_settings()
        self.apply_theme()

    def new_database(self):
        path = filedialog.asksaveasfilename(title="Create SQLite database", defaultextension=".db",
                                            filetypes=[("SQLite database", "*.db *.sqlite *.sqlite3"), ("All files", "*.*")])
        if path:
            self.open_database(path)

    def open_database(self, path: str | None = None, readonly: bool = False):
        if not path:
            path = filedialog.askopenfilename(title="Open SQLite database",
                                              filetypes=[("SQLite database", "*.db *.sqlite *.sqlite3"), ("All files", "*.*")])
        if not path:
            return False
        try:
            resolved = Path(path).resolve()
            size = resolved.stat().st_size if resolved.exists() else 0
            if size > LARGE_FILE_BYTES and not messagebox.askyesno(
                "Large database",
                f"This file is {size / 1024 ** 3:.1f} GiB. Row counts and page loads may be slow. Open it anyway?",
            ):
                return False
            if not readonly and resolved.exists() and not file_is_writable(resolved):
                messagebox.showinfo("Read-only file", "The file or its folder is not writable, so it will be opened read-only.")
                readonly = True
            if readonly:
                new_connection = sqlite3.connect(resolved.as_uri() + "?mode=ro", uri=True, check_same_thread=False)
            else:
                new_connection = sqlite3.connect(resolved, check_same_thread=False)
            new_connection.execute("PRAGMA busy_timeout = 5000")
            if not readonly:
                new_connection.execute("PRAGMA foreign_keys = ON")
                try:
                    new_connection.execute("PRAGMA journal_mode = WAL")
                except sqlite3.Error:
                    pass
            new_connection.execute("SELECT name FROM sqlite_master LIMIT 1").fetchone()
        except sqlite3.Error as exc:
            messagebox.showerror("Cannot open database", str(exc))
            return False
        if not self.close_database(prompt=True):
            new_connection.close()
            return False
        self.connection = new_connection
        self.connection.row_factory = sqlite3.Row
        self.connection.set_trace_callback(self._trace_sql)
        self._data_version = self._read_data_version()
        self._row_count_cache.clear()
        self.database_path = resolved
        self.readonly = readonly
        suffix = " [READ-ONLY]" if readonly else ""
        self.title(f"{APP_NAME} — {self.database_path.name}{suffix}")
        self.path_label.config(text=str(self.database_path) + suffix)
        self.status.set(f"Opened {self.database_path}{suffix}")
        self._remember_recent(resolved)
        self.refresh_schema()
        return True

    def close_database(self, prompt=True):
        if self._sql_running:
            messagebox.showinfo(APP_NAME, "A query is still running. Cancel it or wait before closing.")
            return False
        if self.connection:
            if prompt and self.connection.in_transaction:
                choice = messagebox.askyesnocancel("Pending changes", "Commit pending changes before closing?")
                if choice is None:
                    return False
                self.connection.commit() if choice else self.connection.rollback()
            self.connection.close()
        self.connection = None
        self.database_path = None
        self.readonly = False
        self.selected_object = None
        self.schema_tree.delete(*self.schema_tree.get_children())
        self._clear_grid(self.data_grid)
        self._set_schema_text("")
        self._set_connected_state(False)
        self.status.set("Database closed")
        return True

    def _trace_sql(self, statement):
        # May run on the SQL worker thread: never touch Tk widgets here.
        stamp = time.strftime("%H:%M:%S")
        line = f"[{stamp}] {statement.strip()}\n"
        self._ui_queue.put(lambda: self._append_log(line))

    def _append_log(self, line: str):
        if hasattr(self, "log_text"):
            self.log_text.configure(state="normal")
            self.log_text.insert("end", line)
            self.log_text.see("end")
            self.log_text.configure(state="disabled")

    def _process_ui_queue(self):
        """Run callbacks posted from worker threads on the Tk main thread."""
        try:
            while True:
                self._ui_queue.get_nowait()()
        except queue.Empty:
            pass
        if hasattr(self, "tx_label"):
            pending = bool(self.connection and self.connection.in_transaction)
            self.tx_label.config(text="\u25cf Uncommitted changes" if pending else "")
        self.after(100, self._process_ui_queue)

    def report_callback_exception(self, exc, value, tb):
        """Global handler for uncaught errors in Tk callbacks: log to file, show a friendly dialog."""
        details = "".join(traceback.format_exception(exc, value, tb))
        try:
            with open(ERROR_LOG_PATH, "a", encoding="utf-8") as handle:
                handle.write(f"[{time.strftime('%Y-%m-%d %H:%M:%S')}]\n{details}\n")
        except OSError:
            pass
        messagebox.showerror("Unexpected error", f"{value}\n\nDetails were written to:\n{ERROR_LOG_PATH}")

    def _refresh_recent_menu(self):
        self.recent_menu.delete(0, "end")
        recent = [path for path in self.settings.get("recent_files", []) if isinstance(path, str)]
        if not recent:
            self.recent_menu.add_command(label="(no recent databases)", state="disabled")
        for path in recent:
            self.recent_menu.add_command(label=path, command=lambda p=path: self.open_database(p))
        self.recent_menu.add_separator()
        self.recent_menu.add_command(label="Clear list", command=self._clear_recent)

    def _remember_recent(self, path: Path):
        recent = [item for item in self.settings.get("recent_files", []) if item != str(path)]
        self.settings["recent_files"] = [str(path)] + recent[:9]
        self._save_settings()
        self._refresh_recent_menu()

    def _clear_recent(self):
        self.settings["recent_files"] = []
        self._save_settings()
        self._refresh_recent_menu()

    def close_app(self):
        if self.close_database():
            self.destroy()

    def require_connection(self) -> bool:
        if not self.connection:
            messagebox.showinfo(APP_NAME, "Open or create a database first.")
            return False
        if self._sql_running:
            messagebox.showinfo(APP_NAME, "A query is still running. Wait for it to finish or cancel it.")
            return False
        return True

    def refresh_schema(self):
        if not self.require_connection():
            return
        self._schema_generation += 1
        previous = self.selected_object
        self.schema_tree.delete(*self.schema_tree.get_children())
        roots = {kind: self.schema_tree.insert("", "end", text=kind.title(), open=True)
                 for kind in ("table", "view", "index", "trigger")}
        rows = self.connection.execute(
            "SELECT type, name FROM sqlite_master WHERE name NOT LIKE 'sqlite_%' "
            "AND type IN ('table','view','index','trigger') ORDER BY type, name COLLATE NOCASE"
        ).fetchall()
        needle = self.schema_filter_var.get().strip().casefold() if hasattr(self, "schema_filter_var") else ""
        restore_item = None
        for row in rows:
            if needle and needle not in row["name"].casefold():
                continue
            item = self.schema_tree.insert(roots[row["type"]], "end", text=row["name"], values=(row["type"], row["name"]))
            if row["type"] in ("table", "view"):
                try:
                    for column in self.connection.execute(f"PRAGMA table_info({quote_identifier(row['name'])})"):
                        declared_type = column[2] or "untyped"
                        self.schema_tree.insert(item, "end", text=f"{column[1]}  ({declared_type})")
                except sqlite3.Error:
                    pass
            if previous == (row["type"], row["name"]):
                restore_item = item
        for kind, root in roots.items():
            count = len(self.schema_tree.get_children(root))
            self.schema_tree.item(root, text=f"{kind.title()} ({count})")
        if restore_item:
            self.schema_tree.selection_set(restore_item)
            self.schema_tree.focus(restore_item)
        self.status.set(f"Schema refreshed: {len(rows)} objects")

    def on_schema_select(self, _event=None):
        if self._sql_running:
            return
        selection = self.schema_tree.selection()
        if not selection:
            return
        item = selection[0]
        values = self.schema_tree.item(item, "values")
        if len(values) != 2:
            return
        self.selected_object = (values[0], values[1])
        if values[0] in ("table", "view"):
            self.schema_tree.item(item, open=True)
        row = self.connection.execute("SELECT sql FROM sqlite_master WHERE type=? AND name=?", values).fetchone()
        self._set_schema_text(row[0] if row and row[0] else "-- No SQL definition available")
        self.load_structure(values[0], values[1])
        if values[0] in ("table", "view"):
            self.load_table(0)

    def open_selected_object(self):
        if self.selected_object and self.selected_object[0] in ("table", "view"):
            self.notebook.select(self.data_tab)
            self.load_table(0)

    def _set_schema_text(self, text: str):
        self.schema_text.config(state="normal")
        self.schema_text.delete("1.0", "end")
        self.schema_text.insert("1.0", text)
        self.schema_text.config(state="disabled")

    def load_structure(self, kind: str, name: str):
        for grid in (self.columns_grid, self.indexes_grid, self.foreign_grid):
            grid.delete(*grid.get_children())
        if kind not in ("table", "view"):
            return
        try:
            for row in self.connection.execute(f"PRAGMA table_info({quote_identifier(name)})"):
                self.columns_grid.insert("", "end", values=(row[0], row[1], row[2], "Yes" if row[3] else "",
                                                                  row[4] if row[4] is not None else "", row[5] or ""))
            for row in self.connection.execute(f"PRAGMA index_list({quote_identifier(name)})"):
                columns = self.connection.execute(f"PRAGMA index_info({quote_identifier(row[1])})").fetchall()
                self.indexes_grid.insert("", "end", values=(row[1], "Yes" if row[2] else "", row[3],
                                                              "Yes" if row[4] else "", ", ".join(c[2] for c in columns)))
            for row in self.connection.execute(f"PRAGMA foreign_key_list({quote_identifier(name)})"):
                self.foreign_grid.insert("", "end", values=(row[0], row[3], row[2], row[4], row[5], row[6], row[7]))
        except sqlite3.Error as exc:
            self.status.set(f"Could not load structure: {exc}")

    def load_table(self, page=None):
        if not self.connection or not self.selected_object or self.selected_object[0] not in ("table", "view"):
            return
        if self._sql_running:
            self.status.set("Wait for the running query to finish before loading data.")
            return
        if page is not None:
            self.page = max(0, page)
        kind, name = self.selected_object
        where, params = self._build_where()
        # Fetch the row-key columns in the SAME query as the data so each displayed
        # row is paired with its own key, even when the sort column has ties.
        key_columns = self._row_key_columns(name) if kind == "table" else None
        select_list = "*"
        if key_columns:
            select_list += "".join(f", {quote_identifier(column)}" for column in key_columns)
        sql = f"SELECT {select_list} FROM {quote_identifier(name)}"
        if where:
            sql += f" WHERE {where}"
        order_terms = self.sort_terms or ([(self.sort_column, self.sort_descending)] if self.sort_column else [])
        if order_terms:
            sql += " ORDER BY " + ", ".join(
                f"{quote_identifier(column)} {'DESC' if descending else 'ASC'}" for column, descending in order_terms
            )
        sql += " LIMIT ? OFFSET ?"
        try:
            count_sql = f"SELECT COUNT(*) FROM {quote_identifier(name)}" + (f" WHERE {where}" if where else "")
            self.total_rows = self._cached_row_count(name, count_sql, params)
            last_page = max(0, (self.total_rows - 1) // PAGE_SIZE)
            self.page = min(self.page, last_page)
            cursor = self.connection.execute(sql, (*params, PAGE_SIZE, self.page * PAGE_SIZE))
            raw_rows = cursor.fetchall()
            all_columns = [description[0] for description in cursor.description or []]
        except sqlite3.Error as exc:
            messagebox.showerror("Cannot load data", str(exc))
            return
        key_count = len(key_columns) if key_columns else 0
        if key_count:
            columns = all_columns[:-key_count]
            display_rows = [tuple(row)[:-key_count] for row in raw_rows]
            row_key_values = [tuple(row)[-key_count:] for row in raw_rows]
        else:
            columns = all_columns
            display_rows = [tuple(row) for row in raw_rows]
            row_key_values = None
        self.current_columns = columns
        self._fill_grid(self.data_grid, columns, display_rows, sortable=True)
        self.current_row_keys.clear()
        children = self.data_grid.get_children()
        self.current_raw_rows = {item: row for item, row in zip(children, display_rows)}
        if key_columns:
            key_tuple = tuple(key_columns)
            for item, values in zip(children, row_key_values):
                self.current_row_keys[item] = (key_tuple, values)
        self.page_label.config(text=f"Page {self.page + 1} of {max(1, last_page + 1)}")
        self.goto_page_var.set(str(self.page + 1))
        self.row_count_label.config(text=f"{self.total_rows:,} total row(s); showing {len(display_rows):,}")
        self.status.set(f"{name}: {len(display_rows)} row(s) displayed")

    def _build_where(self):
        clauses, params = [], []
        raw = self.filter_var.get().strip()
        if raw:
            clauses.append(f"({raw})")
        templates = {
            "contains": "CAST({column} AS TEXT) LIKE ?",
            "equals": "{column} = ?",
            "not equal": "{column} <> ?",
            "greater than": "{column} > ?",
            "less than": "{column} < ?",
            "is NULL": "{column} IS NULL",
            "is not NULL": "{column} IS NOT NULL",
        }
        for column, operator, value in self.advanced_filters:
            clauses.append(templates[operator].format(column=quote_identifier(column)))
            if operator not in ("is NULL", "is not NULL"):
                params.append(f"%{value}%" if operator == "contains" else value)
        return " AND ".join(clauses), tuple(params)

    def _row_key_columns(self, table: str) -> list[str] | None:
        info = self.connection.execute(f"PRAGMA table_info({quote_identifier(table)})").fetchall()
        primary_keys = sorted((row for row in info if row[5]), key=lambda row: row[5])
        if primary_keys:
            return [row[1] for row in primary_keys]
        try:
            self.connection.execute(f"SELECT rowid FROM {quote_identifier(table)} LIMIT 0")
            return ["rowid"]
        except sqlite3.Error:
            return None

    def _key_predicate(self, key_columns) -> str:
        return " AND ".join(f"{quote_identifier(column)} IS ?" for column in key_columns)

    def sort_data(self, column: str):
        if self.sort_column == column:
            self.sort_descending = not self.sort_descending
        else:
            self.sort_column, self.sort_descending = column, False
        self.sort_terms = [(self.sort_column, self.sort_descending)]
        self.load_table(0)

    def choose_sort(self):
        if not self.current_columns:
            messagebox.showinfo("Sort rows", "Select a table or view first.")
            return
        dialog = SortDialog(self, self.current_columns, self.sort_terms)
        self.wait_window(dialog)
        if dialog.result is not None:
            self.sort_terms = dialog.result
            if self.sort_terms:
                self.sort_column, self.sort_descending = self.sort_terms[0]
            else:
                self.sort_column, self.sort_descending = None, False
            self.load_table(0)

    def clear_filter(self):
        self.filter_var.set("")
        self.advanced_filters.clear()
        self.filter_summary.config(text="")
        self.load_table(0)

    def add_filter(self):
        if not self.current_columns:
            messagebox.showinfo("Column filter", "Select a table or view first.")
            return
        dialog = FilterDialog(self, self.current_columns)
        self.wait_window(dialog)
        if dialog.result:
            self.advanced_filters.append(dialog.result)
            labels = [f"{column} {operator}" + (f" {value!r}" if value else "")
                      for column, operator, value in self.advanced_filters]
            self.filter_summary.config(text="Column filters: " + "  AND  ".join(labels))
            self.load_table(0)

    def previous_page(self):
        if self.page:
            self.load_table(self.page - 1)

    def next_page(self):
        if (self.page + 1) * PAGE_SIZE < self.total_rows:
            self.load_table(self.page + 1)

    def go_to_page(self):
        try:
            page = max(1, int(self.goto_page_var.get()))
        except ValueError:
            page = 1
        self.load_table(page - 1)

    def _editable_table(self) -> str | None:
        if self._sql_running:
            messagebox.showinfo(APP_NAME, "A query is still running. Wait for it to finish or cancel it.")
            return None
        if self.readonly:
            messagebox.showinfo("Read-only database", "This database was opened read-only.")
            return None
        if not self.selected_object or self.selected_object[0] != "table":
            messagebox.showinfo("Edit rows", "Select a table (not a view) first.")
            return None
        return self.selected_object[1]

    def create_table(self):
        if not self.require_connection() or self.readonly:
            if self.readonly:
                messagebox.showinfo("Read-only database", "This database was opened read-only.")
            return
        dialog = CreateTableDialog(self)
        self.wait_window(dialog)
        if dialog.result:
            try:
                self.connection.execute(dialog.result)
                self.refresh_schema()
                self.status.set("Table created; choose Commit to save")
            except sqlite3.Error as exc:
                messagebox.showerror("Create table failed", str(exc))

    def add_column(self):
        table = self._editable_table()
        if not table:
            return
        dialog = ColumnDialog(self, f"Add column to {table}")
        self.wait_window(dialog)
        column = dialog.result
        if not column:
            return
        if column["pk"] or column["unique"]:
            messagebox.showwarning("Add column", "SQLite cannot add PRIMARY KEY or UNIQUE using ALTER TABLE. Use a new table or SQL migration.")
            return
        definition = f"{quote_identifier(column['name'])} {column['type']}"
        if column["notnull"]:
            definition += " NOT NULL"
        if column["default"]:
            definition += " DEFAULT " + column["default"]
        try:
            self.connection.execute(f"ALTER TABLE {quote_identifier(table)} ADD COLUMN {definition}")
            self.refresh_schema()
            self.load_structure("table", table)
            self.status.set("Column added; choose Commit to save")
        except sqlite3.Error as exc:
            messagebox.showerror("Add column failed", str(exc))

    def rename_table(self):
        table = self._editable_table()
        if not table:
            return
        new_name = simpledialog.askstring("Rename table", "New table name:", initialvalue=table, parent=self)
        if not new_name or new_name == table:
            return
        try:
            self.connection.execute(f"ALTER TABLE {quote_identifier(table)} RENAME TO {quote_identifier(new_name.strip())}")
            self.selected_object = ("table", new_name.strip())
            self.refresh_schema()
            self.status.set("Table renamed; choose Commit to save")
        except sqlite3.Error as exc:
            messagebox.showerror("Rename table failed", str(exc))

    def _choose_column(self, title):
        table = self._editable_table()
        if not table:
            return None, None
        columns = [row[1] for row in self.connection.execute(f"PRAGMA table_info({quote_identifier(table)})")]
        dialog = ChoiceDialog(self, title, "Choose a column", columns)
        self.wait_window(dialog)
        return table, dialog.result

    def rename_column(self):
        table, column = self._choose_column("Rename column")
        if not column:
            return
        new_name = simpledialog.askstring("Rename column", "New column name:", initialvalue=column, parent=self)
        if not new_name or new_name == column:
            return
        try:
            self.connection.execute(
                f"ALTER TABLE {quote_identifier(table)} RENAME COLUMN {quote_identifier(column)} TO {quote_identifier(new_name.strip())}"
            )
            self.refresh_schema()
            self.load_table(0)
            self.status.set("Column renamed; choose Commit to save")
        except sqlite3.Error as exc:
            messagebox.showerror("Rename column failed", str(exc))

    def drop_column(self):
        table, column = self._choose_column("Drop column")
        if not column or not messagebox.askyesno("Drop column", f"Permanently remove column {column!r} from {table!r}?", parent=self):
            return
        try:
            self.connection.execute(f"ALTER TABLE {quote_identifier(table)} DROP COLUMN {quote_identifier(column)}")
            self.refresh_schema()
            self.load_table(0)
            self.status.set("Column dropped; choose Commit to save")
        except sqlite3.Error as exc:
            messagebox.showerror("Drop column failed", str(exc))

    def create_index(self):
        table = self._editable_table()
        if not table:
            return
        index_name = simpledialog.askstring("Create index", "Index name:", initialvalue=f"idx_{table}_", parent=self)
        if not index_name:
            return
        available = [row[1] for row in self.connection.execute(f"PRAGMA table_info({quote_identifier(table)})")]
        columns_text = simpledialog.askstring(
            "Create index", "Columns, comma-separated:\nAvailable: " + ", ".join(available), parent=self
        )
        if not columns_text:
            return
        columns = [item.strip() for item in columns_text.split(",") if item.strip()]
        if not columns or any(column not in available for column in columns):
            messagebox.showerror("Create index", "Use only the listed column names.")
            return
        unique = messagebox.askyesno("Create index", "Should the index enforce unique values?", parent=self)
        try:
            self.connection.execute(
                f"CREATE {'UNIQUE ' if unique else ''}INDEX {quote_identifier(index_name.strip())} "
                f"ON {quote_identifier(table)} ({','.join(map(quote_identifier, columns))})"
            )
            self.refresh_schema()
            self.status.set("Index created; choose Commit to save")
        except sqlite3.Error as exc:
            messagebox.showerror("Create index failed", str(exc))

    def drop_object(self):
        if not self.selected_object:
            messagebox.showinfo("Drop object", "Select a database object first.")
            return
        if self.readonly:
            messagebox.showinfo("Read-only database", "This database was opened read-only.")
            return
        kind, name = self.selected_object
        if kind not in ("table", "view", "index", "trigger"):
            return
        detail = ""
        if kind == "table":
            try:
                count = self.connection.execute(f"SELECT COUNT(*) FROM {quote_identifier(name)}").fetchone()[0]
                detail = f"\n\nThis table contains {count:,} row(s) that will be lost."
            except sqlite3.Error:
                pass
        if not messagebox.askyesno("Drop object", f"Drop {kind} {name!r}? This is destructive after Commit.{detail}"):
            return
        try:
            self.connection.execute(f"DROP {kind.upper()} {quote_identifier(name)}")
            self.selected_object = None
            self.refresh_schema()
            self.status.set(f"{kind.title()} dropped; choose Commit to save")
        except sqlite3.Error as exc:
            messagebox.showerror("Drop failed", str(exc))

    def duplicate_row(self):
        table = self._editable_table()
        selection = self.data_grid.selection()
        if not table or not selection:
            return
        values = list(self.current_raw_rows.get(selection[0], ()))
        if not values:
            return
        for key_column in self._row_key_columns(table) or []:
            if key_column in self.current_columns:
                values[self.current_columns.index(key_column)] = None
        dialog = RecordDialog(self, f"Duplicate row in {table}", self.current_columns, values)
        self.wait_window(dialog)
        if dialog.result is None:
            return
        placeholders = ",".join("?" for _ in self.current_columns)
        sql = f"INSERT INTO {quote_identifier(table)} ({','.join(map(quote_identifier, self.current_columns))}) VALUES ({placeholders})"
        try:
            self.connection.execute(sql, dialog.result)
            self.load_table(self.page)
            self.status.set("Row duplicated; choose Commit to save")
        except sqlite3.Error as exc:
            messagebox.showerror("Cannot duplicate row", str(exc))

    def remember_active_cell(self, event):
        item = self.data_grid.identify_row(event.y)
        column_id = self.data_grid.identify_column(event.x)
        if item and column_id:
            self.active_cell = (item, int(column_id[1:]) - 1)

    def show_row_details(self, event):
        """Open the complete record when any of its visible cells is double-clicked."""
        self.remember_active_cell(event)
        item = self.data_grid.identify_row(event.y)
        if not item:
            return
        self.data_grid.selection_set(item)
        self.data_grid.focus(item)
        values = self.current_raw_rows.get(item, self.data_grid.item(item, "values"))
        RowDetailsDialog(self, self.current_columns, values)

    def begin_inline_edit(self, event):
        self.remember_active_cell(event)
        table = self._editable_table()
        if not table or not self.active_cell:
            return
        item, column_index = self.active_cell
        if item not in self.current_raw_rows or not (0 <= column_index < len(self.current_columns)):
            return
        value = self.current_raw_rows[item][column_index]
        if isinstance(value, bytes):
            self.edit_cell_dialog()
            return
        bbox = self.data_grid.bbox(item, f"#{column_index + 1}")
        if not bbox:
            return
        x, y, width, height = bbox
        editor = ttk.Entry(self.data_grid)
        editor.insert(0, "<NULL>" if value is None else str(value))
        editor.place(x=x, y=y, width=width, height=height)
        editor.focus_set()
        editor.select_range(0, "end")
        done = {"value": False}

        def finish(save=True):
            if done["value"]:
                return
            done["value"] = True
            new_value = None if editor.get() == "<NULL>" else editor.get()
            editor.destroy()
            if save and new_value != value and self._update_cell(item, column_index, new_value):
                self.load_table(self.page)

        editor.bind("<Return>", lambda _e: finish(True))
        editor.bind("<Escape>", lambda _e: finish(False))
        editor.bind("<FocusOut>", lambda _e: finish(True))

    def edit_cell_dialog(self):
        table = self._editable_table()
        if not table or not self.active_cell:
            messagebox.showinfo("Edit cell", "Click a cell first.")
            return
        item, column_index = self.active_cell
        row = self.current_raw_rows.get(item)
        if row is None or not (0 <= column_index < len(self.current_columns)):
            return
        dialog = CellEditorDialog(self, self.current_columns[column_index], row[column_index])
        self.wait_window(dialog)
        if dialog.result and dialog.result[0] and self._update_cell(item, column_index, dialog.result[1]):
            self.load_table(self.page)

    def _update_cell(self, item, column_index, value):
        if not self.selected_object or not self.current_row_keys.get(item):
            return False
        table = self.selected_object[1]
        key_columns, key_values = self.current_row_keys[item]
        column = self.current_columns[column_index]
        try:
            self.connection.execute(
                f"UPDATE {quote_identifier(table)} SET {quote_identifier(column)}=? "
                f"WHERE {self._key_predicate(key_columns)}", (value, *key_values)
            )
            if column in key_columns:
                self.current_row_keys[item] = (
                    key_columns,
                    tuple(value if c == column else v for c, v in zip(key_columns, key_values)),
                )
            self.status.set("Cell updated; choose Commit to save")
            return True
        except sqlite3.Error as exc:
            messagebox.showerror("Cannot update cell", describe_constraint_error(self.connection, table, exc))
            return False

    def _open_initial(self, initial_path):
        if initial_path:
            self.open_database(initial_path)
            return
        if self.settings.get("reopen_last"):
            recent = self.settings.get("recent_files") or []
            if recent and Path(recent[0]).exists():
                self.open_database(recent[0])

    def _save_reopen_setting(self):
        self.settings["reopen_last"] = bool(self.reopen_last_var.get())
        self._save_settings()

    def _schedule_schema_filter(self):
        if self._schema_filter_job:
            self.after_cancel(self._schema_filter_job)
        self._schema_filter_job = self.after(250, lambda: self.refresh_schema() if self.connection else None)

    def change_font_size(self, delta):
        size = 10 if delta is None else int(self.settings.get("font_size", 10)) + delta
        self.settings["font_size"] = max(7, min(20, size))
        self._save_settings()
        self.apply_theme()
        self.status.set(f"Text size {self.settings['font_size']}")

    def _read_data_version(self):
        try:
            return self.connection.execute("PRAGMA data_version").fetchone()[0]
        except (sqlite3.Error, AttributeError):
            return None

    def _external_change_detected(self) -> bool:
        current = self._read_data_version()
        return self._data_version is not None and current is not None and current != self._data_version

    def copy_rows_as(self):
        selection = self.data_grid.selection()
        if not selection:
            messagebox.showinfo("Copy as", "Select one or more rows first.")
            return
        dialog = ChoiceDialog(self, "Copy as", "Copy the selected rows as:", ["CSV", "JSON", "Markdown", "SQL INSERT"])
        self.wait_window(dialog)
        fmt = dialog.result
        if not fmt:
            return
        columns = list(self.current_columns)
        rows = [list(self.current_raw_rows.get(item, ())) for item in selection]
        if fmt == "CSV":
            buffer = io.StringIO()
            writer = csv.writer(buffer)
            writer.writerow(columns)
            writer.writerows([["" if v is None else self._display_value(v) for v in row] for row in rows])
            text = buffer.getvalue()
        elif fmt == "JSON":
            text = json.dumps([{c: json_ready(v) for c, v in zip(columns, row)} for row in rows], indent=2, ensure_ascii=False)
        elif fmt == "Markdown":
            text = markdown_table(columns, [[self._display_value(v) for v in row] for row in rows])
        else:
            table = self.selected_object[1] if self.selected_object else "table"
            text = sql_insert_statements(table, columns, rows)
        self.clipboard_clear()
        self.clipboard_append(text)
        self.status.set(f"Copied {len(rows)} row(s) as {fmt}")

    def export_table_sql(self):
        if not self.connection or not self.selected_object or self.selected_object[0] != "table":
            messagebox.showinfo("Export table", "Select a table first.")
            return
        name = self.selected_object[1]
        path = filedialog.asksaveasfilename(title="Export table as SQL INSERT script", defaultextension=".sql",
                                            initialfile=f"{name}.sql", filetypes=[("SQL files", "*.sql"), ("All files", "*.*")])
        if not path:
            return
        try:
            ddl = self.connection.execute("SELECT sql FROM sqlite_master WHERE type='table' AND name=?", (name,)).fetchone()
            cursor = self.connection.execute(f"SELECT * FROM {quote_identifier(name)}")
            columns = [item[0] for item in cursor.description]
            count = 0
            with open(path, "w", encoding="utf-8") as handle:
                if ddl and ddl[0]:
                    handle.write(ddl[0].rstrip().rstrip(";") + ";\n\n")
                handle.write("BEGIN TRANSACTION;\n")
                while True:
                    rows = cursor.fetchmany(1000)
                    if not rows:
                        break
                    handle.write(sql_insert_statements(name, columns, [tuple(row) for row in rows]))
                    count += len(rows)
                handle.write("COMMIT;\n")
            self.status.set(f"Exported {count:,} row(s) from {name} as SQL to {path}")
        except (OSError, sqlite3.Error) as exc:
            messagebox.showerror("Export failed", str(exc))

    def import_json(self):
        if not self.require_connection():
            return
        if self.readonly:
            messagebox.showinfo("Read-only database", "This database was opened read-only.")
            return
        path = filedialog.askopenfilename(title="Import JSON", filetypes=[("JSON files", "*.json"), ("All files", "*.*")])
        if not path:
            return
        default_name = Path(path).stem.replace(" ", "_")
        table = simpledialog.askstring("Import JSON", "New table name:", initialvalue=default_name, parent=self)
        if not table:
            return
        try:
            with open(path, "r", encoding="utf-8-sig") as handle:
                records = json.load(handle)
            columns, types, rows = json_records_to_table(records)
            columns_sql = ",".join(f"{quote_identifier(c)} {t}" for c, t in zip(columns, types))
            self.connection.execute(f"CREATE TABLE {quote_identifier(table)} ({columns_sql})")
            placeholders = ",".join("?" for _ in columns)
            self.connection.executemany(f"INSERT INTO {quote_identifier(table)} VALUES ({placeholders})", rows)
            self.refresh_schema()
            self.status.set(f"Imported {len(rows)} rows into {table}; choose Commit to save")
        except (OSError, ValueError, sqlite3.Error) as exc:
            messagebox.showerror("Import failed", str(exc))

    def _cached_row_count(self, name, count_sql, params):
        """COUNT(*) is expensive on big tables; reuse it until data or schema changes."""
        key = (name, count_sql, tuple(params))
        stamp = (self.connection.total_changes, self._read_data_version(), self._schema_generation)
        cached = self._row_count_cache.get(key)
        if cached and cached[0] == stamp:
            return cached[1]
        count = self.connection.execute(count_sql, params).fetchone()[0]
        self._row_count_cache[key] = (stamp, count)
        return count

    def format_current_sql(self):
        editor = self.current_sql_editor()
        if not editor:
            return
        try:
            start, end = editor.index("sel.first"), editor.index("sel.last")
        except tk.TclError:
            start, end = "1.0", "end-1c"
        text = editor.get(start, end)
        if not text.strip():
            return
        editor.delete(start, end)
        editor.insert(start, format_sql(text))
        self.highlight_sql(editor)

    def profile_column_dialog(self):
        if not self.connection or not self.selected_object or self.selected_object[0] not in ("table", "view") \
                or not self.current_columns:
            messagebox.showinfo("Profile column", "Select a table or view first.")
            return
        chooser = ChoiceDialog(self, "Profile column", "Column to profile:", self.current_columns)
        self.wait_window(chooser)
        if not chooser.result:
            return
        where, params = self._build_where()
        try:
            stats, top = profile_column(self.connection, self.selected_object[1], chooser.result, where, params)
        except sqlite3.Error as exc:
            messagebox.showerror("Profile failed", str(exc))
            return
        ProfileDialog(self, f"{self.selected_object[1]}.{chooser.result}", stats, top)

    def search_all_tables(self):
        if not self.require_connection():
            return
        SearchDialog(self)

    def open_table_with_filter(self, table: str, column: str, needle: str):
        self.selected_object = ("table", table)
        self.refresh_schema()
        self.advanced_filters = []
        pattern = like_pattern(needle).replace("'", "''")
        self.filter_var.set(f"CAST({quote_identifier(column)} AS TEXT) LIKE '{pattern}' ESCAPE '\\'")
        self.notebook.select(self.data_tab)
        self.load_table(0)

    def copy_rows(self):
        selection = self.data_grid.selection()
        if not selection:
            return
        lines = []
        for item in selection:
            row = self.current_raw_rows.get(item, ())
            lines.append("\t".join("\\N" if value is None else self._display_value(value) for value in row))
        self.clipboard_clear()
        self.clipboard_append("\n".join(lines))
        self.status.set(f"Copied {len(selection)} row(s) as tab-separated text")

    def paste_cells(self):
        table = self._editable_table()
        if not table or not self.active_cell:
            messagebox.showinfo("Paste", "Click the starting cell first.")
            return
        try:
            matrix = [line.split("\t") for line in self.clipboard_get().splitlines()]
        except tk.TclError:
            return
        items = list(self.data_grid.get_children())
        start_item, start_column = self.active_cell
        if start_item not in items:
            return
        start_row = items.index(start_item)
        changed = 0
        for row_offset, values in enumerate(matrix):
            if start_row + row_offset >= len(items):
                break
            item = items[start_row + row_offset]
            for column_offset, value in enumerate(values):
                column_index = start_column + column_offset
                if column_index >= len(self.current_columns):
                    break
                if self._update_cell(item, column_index, None if value == "\\N" else value):
                    changed += 1
        if changed:
            self.load_table(self.page)
            self.status.set(f"Pasted {changed} cell(s); choose Commit to save")

    def add_row(self):
        table = self._editable_table()
        if not table:
            return
        info = self.connection.execute(f"PRAGMA table_info({quote_identifier(table)})").fetchall()
        columns = [row[1] for row in info]
        initial = ["<NULL>" if row[5] else ("<DEFAULT>" if row[4] is not None else "") for row in info]
        dialog = RecordDialog(self, f"Add row to {table}", columns, initial, allow_default=True)
        self.wait_window(dialog)
        if dialog.result is None:
            return
        included = [(column, value) for column, value in zip(columns, dialog.result) if value != "<DEFAULT>"]
        if included:
            placeholders = ",".join("?" for _ in included)
            sql = (f"INSERT INTO {quote_identifier(table)} ({','.join(quote_identifier(item[0]) for item in included)}) "
                   f"VALUES ({placeholders})")
            parameters = [item[1] for item in included]
        else:
            sql = f"INSERT INTO {quote_identifier(table)} DEFAULT VALUES"
            parameters = []
        try:
            self.connection.execute(sql, parameters)
            self.load_table(self.page)
            self.status.set("Row added; choose Commit to save permanently")
        except sqlite3.Error as exc:
            messagebox.showerror("Cannot add row", str(exc))

    def edit_row(self):
        table = self._editable_table()
        selection = self.data_grid.selection()
        if not table or not selection:
            if table:
                messagebox.showinfo("Edit row", "Select a row first.")
            return
        item = selection[0]
        key = self.current_row_keys.get(item)
        if not key:
            messagebox.showerror("Edit row", "This table needs a primary key or rowid for safe editing.")
            return
        values = self.current_raw_rows.get(item, self.data_grid.item(item, "values"))
        dialog = RecordDialog(self, f"Edit row in {table}", self.current_columns, values)
        self.wait_window(dialog)
        if dialog.result is None:
            return
        key_columns, key_values = key
        assignments = ",".join(f"{quote_identifier(c)}=?" for c in self.current_columns)
        sql = f"UPDATE {quote_identifier(table)} SET {assignments} WHERE {self._key_predicate(key_columns)}"
        try:
            self.connection.execute(sql, dialog.result + list(key_values))
            self.load_table(self.page)
            self.status.set("Row updated; choose Commit to save permanently")
        except sqlite3.Error as exc:
            messagebox.showerror("Cannot update row", describe_constraint_error(self.connection, table, exc))

    def delete_row(self):
        table = self._editable_table()
        selection = self.data_grid.selection()
        if not table or not selection:
            if table:
                messagebox.showinfo("Delete row", "Select a row first.")
            return
        keys = [self.current_row_keys.get(item) for item in selection]
        if any(not key for key in keys):
            messagebox.showerror("Delete row", "This table needs a primary key or rowid for safe deletion.")
            return
        if not messagebox.askyesno("Delete row", f"Delete {len(selection)} selected row(s)? You can still Rollback before committing."):
            return
        try:
            for key_columns, key_values in keys:
                self.connection.execute(
                    f"DELETE FROM {quote_identifier(table)} WHERE {self._key_predicate(key_columns)}",
                    tuple(key_values),
                )
            self.load_table(self.page)
            self.status.set(f"{len(keys)} row(s) deleted; choose Commit to save permanently")
        except sqlite3.Error as exc:
            messagebox.showerror("Cannot delete row", describe_constraint_error(self.connection, table, exc))

    def add_sql_tab(self, content="", title=None):
        frame = ttk.Frame(self.sql_notebook)
        text = tk.Text(frame, undo=True, wrap="none", font=(MONO_FONT, 10))
        yscroll = ttk.Scrollbar(frame, orient="vertical", command=text.yview)
        xscroll = ttk.Scrollbar(frame, orient="horizontal", command=text.xview)
        text.configure(yscrollcommand=yscroll.set, xscrollcommand=xscroll.set)
        text.grid(row=0, column=0, sticky="nsew")
        yscroll.grid(row=0, column=1, sticky="ns")
        xscroll.grid(row=1, column=0, sticky="ew")
        frame.rowconfigure(0, weight=1)
        frame.columnconfigure(0, weight=1)
        number = len(self.sql_editors) + 1
        self.sql_notebook.add(frame, text=title or f"Query {number}")
        self.sql_editors.append(text)
        self.sql_paths[text] = None
        text.insert("1.0", content)
        text.tag_configure("keyword", foreground="#0066cc", font=(MONO_FONT, 10, "bold"))
        text.tag_configure("comment", foreground="#3a8a3a")
        text.tag_configure("string", foreground="#a31515")
        text.bind("<F9>", lambda _e: self.execute_sql())
        text.bind("<Control-space>", lambda _e: self.sql_autocomplete())
        text.bind("<KeyRelease>", lambda _e, editor=text: self.schedule_highlight(editor))
        self.sql_notebook.select(frame)
        self.after_idle(lambda: self.highlight_sql(text))
        if hasattr(self, "settings"):
            self.apply_theme()
        return text

    def current_sql_editor(self):
        selected = self.sql_notebook.select()
        if not selected:
            return None
        frame = self.nametowidget(selected)
        for editor in self.sql_editors:
            if editor.master == frame:
                return editor
        return None

    def close_sql_tab(self):
        if len(self.sql_editors) <= 1:
            self.current_sql_editor().delete("1.0", "end")
            return
        editor = self.current_sql_editor()
        if editor:
            self.sql_paths.pop(editor, None)
            self.sql_editors.remove(editor)
            self.sql_notebook.forget(editor.master)

    def schedule_highlight(self, editor):
        if self._highlight_job:
            self.after_cancel(self._highlight_job)
        self._highlight_job = self.after(250, lambda: self.highlight_sql(editor))

    def highlight_sql(self, editor):
        if not editor.winfo_exists():
            return
        content = editor.get("1.0", "end-1c")
        for tag in ("keyword", "comment", "string"):
            editor.tag_remove(tag, "1.0", "end")
        for match in re.finditer(r"--[^\n]*|/\*[\s\S]*?\*/", content):
            editor.tag_add("comment", f"1.0+{match.start()}c", f"1.0+{match.end()}c")
        for match in re.finditer(r"'(?:''|[^'])*'", content):
            editor.tag_add("string", f"1.0+{match.start()}c", f"1.0+{match.end()}c")
        keyword_pattern = r"\b(?:" + "|".join(SQL_KEYWORDS) + r")\b"
        for match in re.finditer(keyword_pattern, content, re.IGNORECASE):
            editor.tag_add("keyword", f"1.0+{match.start()}c", f"1.0+{match.end()}c")

    def sql_autocomplete(self):
        editor = self.current_sql_editor()
        if not editor:
            return
        choices = list(SQL_KEYWORDS)
        if self.connection:
            choices += [row[0] for row in self.connection.execute(
                "SELECT name FROM sqlite_master WHERE name NOT LIKE 'sqlite_%' ORDER BY name"
            )]
            for row in self.connection.execute("PRAGMA table_list"):
                try:
                    choices += [c[1] for c in self.connection.execute(f"PRAGMA table_info({quote_identifier(row[1])})")]
                except sqlite3.Error:
                    pass
        dialog = ChoiceDialog(self, "SQL completion", "Choose a keyword or database object", sorted(set(choices), key=str.casefold))
        self.wait_window(dialog)
        if dialog.result:
            editor.insert("insert", dialog.result)

    def open_sql_file(self):
        path = filedialog.askopenfilename(title="Open SQL file", filetypes=[("SQL scripts", "*.sql"), ("All files", "*.*")])
        if not path:
            return
        try:
            with open(path, "r", encoding="utf-8-sig") as handle:
                editor = self.add_sql_tab(handle.read(), Path(path).name)
            self.sql_paths[editor] = path
        except OSError as exc:
            messagebox.showerror("Open SQL failed", str(exc))

    def save_sql_file(self):
        editor = self.current_sql_editor()
        if not editor:
            return
        path = self.sql_paths.get(editor) or filedialog.asksaveasfilename(
            title="Save SQL file", defaultextension=".sql", filetypes=[("SQL scripts", "*.sql"), ("All files", "*.*")]
        )
        if not path:
            return
        try:
            with open(path, "w", encoding="utf-8", newline="\n") as handle:
                handle.write(editor.get("1.0", "end-1c"))
            self.sql_paths[editor] = path
            self.sql_notebook.tab(editor.master, text=Path(path).name)
            self.status.set(f"Saved SQL to {path}")
        except OSError as exc:
            messagebox.showerror("Save SQL failed", str(exc))

    def _selected_sql(self):
        editor = self.current_sql_editor()
        if not editor:
            return ""
        try:
            return editor.get("sel.first", "sel.last")
        except tk.TclError:
            return editor.get("1.0", "end-1c")

    def restore_history(self):
        selection = self.history_list.curselection()
        if selection:
            editor = self.current_sql_editor()
            editor.delete("1.0", "end")
            editor.insert("1.0", self.sql_history[selection[0]])
            self.notebook.select(self.sql_tab)

    def explain_query(self):
        if not self.require_connection():
            return
        statements = split_sql(self._selected_sql())
        if not statements:
            return
        statement = statements[0].rstrip().rstrip(";")
        bindings = self._collect_parameters([statement])
        if bindings is None:
            return
        try:
            cursor = self.connection.execute("EXPLAIN QUERY PLAN " + statement,
                                             bind_arguments(statement, bindings[0], iter(bindings[1])))
            rows = cursor.fetchall()
            self._fill_grid(self.result_grid, [d[0] for d in cursor.description], rows)
            self.output_notebook.select(0)
            self.status.set(f"Query plan: {len(rows)} step(s)")
        except sqlite3.Error as exc:
            messagebox.showerror("Query-plan error", str(exc))

    def execute_sql(self):
        if not self.require_connection():
            return
        script = self._selected_sql()
        statements = split_sql(script)
        if not statements:
            return
        bindings = self._collect_parameters(statements)
        if bindings is None:
            return
        self.sql_history.insert(0, script)
        self.sql_history = self.sql_history[:100]
        self.history_list.insert(0, " ".join(script.split())[:180])
        if self.history_list.size() > 100:
            self.history_list.delete(100, "end")
        self._set_sql_running(True)
        self.status.set("Running SQL... (use Cancel to interrupt)")
        threading.Thread(target=self._sql_worker, args=(statements, bindings), daemon=True).start()

    def _collect_parameters(self, statements):
        """Prompt for :name / ? parameters used by the statements. Returns (named, positional) or None if cancelled."""
        names: list[str] = []
        positional_total = 0
        for statement in statements:
            found, count = find_parameters(statement)
            names.extend(name for name in found if name not in names)
            positional_total += count
        if not names and not positional_total:
            return {}, []
        labels = names + [f"?{index}" for index in range(1, positional_total + 1)]
        dialog = RecordDialog(self, "Query parameters", labels, [""] * len(labels))
        self.wait_window(dialog)
        if dialog.result is None:
            return None
        values = [coerce_parameter(value) for value in dialog.result]
        return dict(zip(names, values[:len(names)])), values[len(names):]

    def _sql_worker(self, statements, bindings=None):
        """Run statements off the UI thread; the outcome is posted back through the UI queue."""
        result = {"rows": None, "columns": None, "affected": 0, "elapsed_ms": 0.0, "error": None, "cancelled": False}
        start = time.perf_counter()
        try:
            with self._db_lock:
                named, positional = bindings or ({}, [])
                positional_iter = iter(positional)
                last_cursor = None
                for statement in statements:
                    last_cursor = self.connection.execute(statement, bind_arguments(statement, named, positional_iter))
                    if last_cursor.rowcount > 0:
                        result["affected"] += last_cursor.rowcount
                result["elapsed_ms"] = (time.perf_counter() - start) * 1000
                if last_cursor and last_cursor.description:
                    result["columns"] = [item[0] for item in last_cursor.description]
                    result["rows"] = [tuple(row) for row in last_cursor.fetchmany(5000)]
        except Exception as exc:  # report anything, so the UI never stays in the "running" state
            result["error"] = str(exc)
            result["cancelled"] = "interrupt" in str(exc).lower()
        self._ui_queue.put(lambda: self._finish_sql(result))

    def _finish_sql(self, result):
        self._set_sql_running(False)
        if result["error"]:
            if result["cancelled"]:
                self.status.set("Query cancelled")
            else:
                messagebox.showerror("SQL error", result["error"])
                self.status.set(f"SQL error: {result['error']}")
            return
        elapsed_ms = result["elapsed_ms"]
        if result["columns"] is not None:
            rows = result["rows"]
            self._fill_grid(self.result_grid, result["columns"], rows)
            truncated = " (first 5,000 shown)" if len(rows) == 5000 else ""
            status = f"SQL succeeded in {elapsed_ms:.0f} ms: {len(rows):,} result row(s){truncated}"
        else:
            self._fill_grid(self.result_grid, ["Result"], [[f"Success. {result['affected']} row(s) affected."]])
            status = f"SQL succeeded in {elapsed_ms:.0f} ms; changes are pending until Commit"
        self.refresh_schema()
        self.output_notebook.select(0)
        self.status.set(status)

    def _set_sql_running(self, running: bool):
        self._sql_running = running
        if hasattr(self, "run_button"):
            self.run_button.state(["disabled"] if running else ["!disabled"])
            self.cancel_button.state(["!disabled"] if running else ["disabled"])

    def cancel_sql(self):
        if self._sql_running and self.connection:
            self.connection.interrupt()
            self.status.set("Cancelling query...")

    def _clear_grid(self, grid: ttk.Treeview):
        grid.delete(*grid.get_children())
        grid["columns"] = ()

    def _fill_grid(self, grid: ttk.Treeview, columns, rows, sortable=False):
        grid.delete(*grid.get_children())
        safe_columns = [str(c) for c in columns]
        grid["columns"] = safe_columns
        displayed_rows = [[self._display_value(value) for value in row] for row in rows]
        for index, column in enumerate(safe_columns):
            if sortable:
                grid.heading(column, text=column, command=lambda c=column: self.sort_data(c))
            else:
                # Passing command=None is not accepted by some Tk versions and
                # caused successful SQL scripts to stop before schema refresh.
                grid.heading(column, text=column)
            sample = [column, *(row[index] for row in displayed_rows[:200] if index < len(row))]
            longest = max((max((len(line) for line in value.splitlines()), default=0) for value in sample), default=0)
            grid.column(column, width=max(80, min(360, longest * 8 + 24)), minwidth=60, stretch=True)
        for values in displayed_rows:
            grid.insert("", "end", values=values)

    @staticmethod
    def _display_value(value):
        if value is None:
            return "<NULL>"
        if isinstance(value, bytes):
            return f"<BLOB {len(value)} bytes>"
        return str(value)

    def commit(self):
        if self.connection:
            if self._external_change_detected() and not messagebox.askyesno(
                "Database changed",
                "Another program modified this database since you opened it or last committed.\n\n"
                "Commit your pending changes anyway?",
            ):
                return
            try:
                self.connection.commit()
                self._data_version = self._read_data_version()
                self.status.set("Changes committed")
            except sqlite3.Error as exc:
                messagebox.showerror("Commit failed", str(exc))

    def rollback(self):
        if self.connection:
            self.connection.rollback()
            self._row_count_cache.clear()
            self.load_table(self.page)
            self.refresh_schema()
            self.status.set("Pending changes rolled back")

    def export_csv(self):
        self._export_grid("csv")

    def export_json(self):
        self._export_grid("json")

    def export_markdown(self):
        self._export_grid("markdown")

    def _export_grid(self, fmt: str):
        grid = self.result_grid if self.notebook.select() == str(self.sql_tab) else self.data_grid
        columns = [str(column) for column in grid["columns"]]
        if not columns:
            messagebox.showinfo("Export", "There are no displayed results to export.")
            return
        title, extension, filetypes = {
            "csv": ("Export CSV", ".csv", [("CSV files", "*.csv"), ("All files", "*.*")]),
            "json": ("Export JSON", ".json", [("JSON files", "*.json"), ("All files", "*.*")]),
            "markdown": ("Export Markdown", ".md", [("Markdown files", "*.md"), ("All files", "*.*")]),
        }[fmt]
        path = filedialog.asksaveasfilename(title=title, defaultextension=extension, filetypes=filetypes)
        if not path:
            return
        rows = [list(grid.item(item, "values")) for item in grid.get_children()]
        try:
            if fmt == "csv":
                with open(path, "w", newline="", encoding="utf-8-sig") as handle:
                    writer = csv.writer(handle)
                    writer.writerow(columns)
                    writer.writerows(rows)
            elif fmt == "json":
                records = [{column: (None if value == "<NULL>" else value) for column, value in zip(columns, row)}
                           for row in rows]
                with open(path, "w", encoding="utf-8") as handle:
                    json.dump(records, handle, indent=2, ensure_ascii=False)
            else:
                with open(path, "w", encoding="utf-8") as handle:
                    handle.write(markdown_table(columns, rows))
            self.status.set(f"Exported {len(rows)} rows to {path}")
        except OSError as exc:
            messagebox.showerror("Export failed", str(exc))

    def export_table_csv(self):
        self._export_table("csv")

    def export_table_json(self):
        self._export_table("json")

    def export_table_markdown(self):
        self._export_table("markdown")

    def _export_table(self, fmt: str):
        """Stream the selected table or view (honouring filters and sorting) to CSV, JSON or Markdown."""
        if not self.connection or not self.selected_object or self.selected_object[0] not in ("table", "view"):
            messagebox.showinfo("Export table", "Select a table or view first.")
            return
        name = self.selected_object[1]
        title, extension, filetypes = {
            "csv": ("Export complete table to CSV", ".csv", [("CSV files", "*.csv"), ("All files", "*.*")]),
            "json": ("Export complete table to JSON", ".json", [("JSON files", "*.json"), ("All files", "*.*")]),
            "markdown": ("Export complete table to Markdown", ".md", [("Markdown files", "*.md"), ("All files", "*.*")]),
        }[fmt]
        path = filedialog.asksaveasfilename(title=title, defaultextension=extension, initialfile=f"{name}{extension}",
                                            filetypes=filetypes)
        if not path:
            return
        where, params = self._build_where()
        sql = f"SELECT * FROM {quote_identifier(name)}" + (f" WHERE {where}" if where else "")
        order_terms = self.sort_terms or ([(self.sort_column, self.sort_descending)] if self.sort_column else [])
        if order_terms:
            sql += " ORDER BY " + ", ".join(
                f"{quote_identifier(column)} {'DESC' if descending else 'ASC'}" for column, descending in order_terms
            )
        try:
            cursor = self.connection.execute(sql, params)
            columns = [item[0] for item in cursor.description]
            count = 0
            with open(path, "w", newline="" if fmt == "csv" else None,
                      encoding="utf-8-sig" if fmt == "csv" else "utf-8") as handle:
                writer = csv.writer(handle) if fmt == "csv" else None
                if fmt == "csv":
                    writer.writerow(columns)
                elif fmt == "json":
                    handle.write("[")
                else:
                    handle.write(markdown_table(columns, []))
                while True:
                    rows = cursor.fetchmany(1000)
                    if not rows:
                        break
                    if fmt == "csv":
                        writer.writerows(rows)
                    elif fmt == "json":
                        for row in rows:
                            record = {c: json_ready(v) for c, v in zip(columns, row)}
                            handle.write(("," if count else "") + "\n" + json.dumps(record, ensure_ascii=False))
                            count += 1
                        continue
                    else:
                        for row in rows:
                            handle.write(markdown_row(self._display_value(v) for v in row))
                    count += len(rows)
                if fmt == "json":
                    handle.write("\n]\n")
            self.status.set(f"Exported {count:,} row(s) from {name} to {path}")
        except (OSError, sqlite3.Error) as exc:
            messagebox.showerror("Export failed", str(exc))

    def import_csv(self):
        if not self.require_connection():
            return
        if self.readonly:
            messagebox.showinfo("Read-only database", "This database was opened read-only.")
            return
        path = filedialog.askopenfilename(title="Import CSV", filetypes=[("CSV files", "*.csv"), ("All files", "*.*")])
        if not path:
            return
        default_name = Path(path).stem.replace(" ", "_")
        table = simpledialog.askstring("Import CSV", "New table name:", initialvalue=default_name, parent=self)
        if not table:
            return
        try:
            with open(path, "r", newline="", encoding="utf-8-sig") as handle:
                reader = csv.reader(handle)
                headers = next(reader)
                if not headers or any(not h for h in headers) or len(set(headers)) != len(headers):
                    raise ValueError("The CSV header must contain unique, non-empty column names.")
                rows = list(reader)
            if any(len(row) != len(headers) for row in rows):
                raise ValueError("Some CSV rows have a different number of fields than the header.")
            types = infer_column_types(rows[:1000], len(headers))
            columns_sql = ",".join(f"{quote_identifier(h)} {kind}" for h, kind in zip(headers, types))
            self.connection.execute(f"CREATE TABLE {quote_identifier(table)} ({columns_sql})")
            placeholders = ",".join("?" for _ in headers)
            self.connection.executemany(f"INSERT INTO {quote_identifier(table)} VALUES ({placeholders})", rows)
            self.refresh_schema()
            self.status.set(f"Imported {len(rows)} rows into {table}; choose Commit to save")
        except (OSError, csv.Error, ValueError, sqlite3.Error) as exc:
            messagebox.showerror("Import failed", str(exc))

    def export_sql_dump(self):
        if not self.require_connection():
            return
        path = filedialog.asksaveasfilename(title="Export SQL dump", defaultextension=".sql",
                                            filetypes=[("SQL scripts", "*.sql"), ("All files", "*.*")])
        if not path:
            return
        try:
            with open(path, "w", encoding="utf-8", newline="\n") as handle:
                for line in self.connection.iterdump():
                    handle.write(line + "\n")
            self.status.set(f"SQL dump exported to {path}")
        except (OSError, sqlite3.Error) as exc:
            messagebox.showerror("SQL dump failed", str(exc))

    def import_sql_dump(self):
        if not self.require_connection() or self.readonly:
            if self.readonly:
                messagebox.showinfo("Read-only database", "This database was opened read-only.")
            return
        path = filedialog.askopenfilename(title="Import SQL script", filetypes=[("SQL scripts", "*.sql"), ("All files", "*.*")])
        if not path:
            return
        if not messagebox.askyesno("Import SQL", "Execute the complete script? If any statement fails, nothing from the script is kept. "
                                   "Make a backup first for important databases."):
            return
        try:
            with open(path, "r", encoding="utf-8-sig") as handle:
                script = handle.read()
            count = run_script_atomically(self.connection, script)
            self.refresh_schema()
            self.status.set(f"Imported SQL script {path}: {count} statement(s); choose Commit to save")
        except (OSError, sqlite3.Error) as exc:
            messagebox.showerror("SQL import failed", str(exc))

    def attach_database(self):
        if not self.require_connection():
            return
        path = filedialog.askopenfilename(title="Attach SQLite database", filetypes=[("SQLite database", "*.db *.sqlite *.sqlite3"), ("All files", "*.*")])
        if not path:
            return
        default_alias = re.sub(r"\W+", "_", Path(path).stem).strip("_") or "attached"
        alias = simpledialog.askstring("Attach database", "Schema alias:", initialvalue=default_alias, parent=self)
        if not alias or not re.fullmatch(r"[A-Za-z_]\w*", alias):
            if alias:
                messagebox.showerror("Attach database", "Alias must contain only letters, numbers and underscores and cannot start with a number.")
            return
        try:
            self.connection.execute(f"ATTACH DATABASE ? AS {quote_identifier(alias)}", (path,))
            self.status.set(f"Attached {path} as {alias}; access it in SQL as {alias}.table_name")
        except sqlite3.Error as exc:
            messagebox.showerror("Attach failed", str(exc))

    def save_project(self):
        if not self.require_connection():
            return
        path = filedialog.asksaveasfilename(title="Save workspace project", defaultextension=".sqlw.json",
                                            filetypes=[("SQLiteStudio projects", "*.sqlw.json"), ("JSON files", "*.json")])
        if not path:
            return
        data = {
            "version": 1,
            "database": str(self.database_path),
            "readonly": self.readonly,
            "selected_object": self.selected_object,
            "where": self.filter_var.get(),
            "column_filters": self.advanced_filters,
            "sort_column": self.sort_column,
            "sort_descending": self.sort_descending,
            "sort_terms": self.sort_terms,
            "sql_tabs": [{"title": self.sql_notebook.tab(editor.master, "text"),
                          "content": editor.get("1.0", "end-1c"), "path": self.sql_paths.get(editor)}
                         for editor in self.sql_editors],
        }
        try:
            with open(path, "w", encoding="utf-8") as handle:
                json.dump(data, handle, indent=2)
            self.status.set(f"Workspace saved to {path}")
        except OSError as exc:
            messagebox.showerror("Save project failed", str(exc))

    def open_project(self):
        path = filedialog.askopenfilename(title="Open workspace project", filetypes=[("SQLiteStudio projects", "*.sqlw.json"), ("JSON files", "*.json")])
        if not path:
            return
        try:
            with open(path, "r", encoding="utf-8") as handle:
                data = json.load(handle)
            database = data["database"]
            if not Path(database).exists():
                raise ValueError(f"Database file not found: {database}")
            if not self.open_database(database, bool(data.get("readonly"))):
                return
            for editor in list(self.sql_editors)[1:]:
                self.sql_notebook.forget(editor.master)
                self.sql_editors.remove(editor)
                self.sql_paths.pop(editor, None)
            first = self.sql_editors[0]
            tabs = data.get("sql_tabs") or []
            if tabs:
                first.delete("1.0", "end")
                first.insert("1.0", tabs[0].get("content", ""))
                self.sql_notebook.tab(first.master, text=tabs[0].get("title", "Query 1"))
                self.sql_paths[first] = tabs[0].get("path")
                for tab in tabs[1:]:
                    editor = self.add_sql_tab(tab.get("content", ""), tab.get("title"))
                    self.sql_paths[editor] = tab.get("path")
            self.filter_var.set(data.get("where", ""))
            self.advanced_filters = [tuple(item) for item in data.get("column_filters", [])]
            self.sort_column = data.get("sort_column")
            self.sort_descending = bool(data.get("sort_descending"))
            self.sort_terms = [(item[0], bool(item[1])) for item in data.get("sort_terms", [])]
            selected = data.get("selected_object")
            if selected:
                self.selected_object = tuple(selected)
                self.refresh_schema()
                self.load_table(0)
            self.status.set(f"Workspace opened from {path}")
        except (OSError, ValueError, KeyError, json.JSONDecodeError) as exc:
            messagebox.showerror("Open project failed", str(exc))

    def backup_database(self):
        if not self.require_connection():
            return
        path = filedialog.asksaveasfilename(title="Backup database", defaultextension=".db",
                                            filetypes=[("SQLite database", "*.db"), ("All files", "*.*")])
        if not path:
            return
        try:
            destination = sqlite3.connect(path)
            with destination:
                self.connection.backup(destination)
            destination.close()
            self.status.set(f"Backup saved to {path}")
        except sqlite3.Error as exc:
            messagebox.showerror("Backup failed", str(exc))

    def edit_pragmas(self):
        if not self.require_connection():
            return
        dialog = PragmaDialog(self, self.connection, self.readonly)
        self.wait_window(dialog)
        self.status.set("Pragma settings reviewed")

    def integrity_check(self):
        if not self.require_connection():
            return
        try:
            rows = self.connection.execute("PRAGMA integrity_check").fetchall()
            messagebox.showinfo("Integrity check", "\n".join(str(row[0]) for row in rows))
        except sqlite3.Error as exc:
            messagebox.showerror("Integrity check failed", str(exc))

    def foreign_key_check(self):
        if not self.require_connection():
            return
        try:
            rows = self.connection.execute("PRAGMA foreign_key_check").fetchall()
            if not rows:
                messagebox.showinfo("Foreign-key check", "OK — no foreign-key violations were found.")
            else:
                self._fill_grid(self.result_grid, ("table", "rowid", "parent", "foreign_key"), rows)
                self.notebook.select(self.sql_tab)
                self.output_notebook.select(0)
                messagebox.showwarning("Foreign-key check", f"Found {len(rows)} violation(s). They are shown in SQL Results.")
        except sqlite3.Error as exc:
            messagebox.showerror("Foreign-key check failed", str(exc))

    def vacuum_database(self):
        if not self.require_connection():
            return
        if self.connection.in_transaction:
            messagebox.showinfo("Vacuum", "Commit or Rollback pending changes before vacuuming.")
            return
        try:
            self.connection.execute("VACUUM")
            self.status.set("Database vacuum completed")
        except sqlite3.Error as exc:
            messagebox.showerror("Vacuum failed", str(exc))

    def analyze_database(self):
        if not self.require_connection():
            return
        try:
            self.connection.execute("ANALYZE")
            self.status.set("Database statistics updated; choose Commit to save")
        except sqlite3.Error as exc:
            messagebox.showerror("Analyze failed", str(exc))

    def optimize_database(self):
        if not self.require_connection() or self.readonly:
            return
        try:
            self.connection.execute("PRAGMA optimize")
            self.status.set("Database optimization completed")
        except sqlite3.Error as exc:
            messagebox.showerror("Optimize failed", str(exc))

    def reindex_database(self):
        if not self.require_connection() or self.readonly:
            return
        try:
            self.connection.execute("REINDEX")
            self.status.set("All indexes rebuilt; choose Commit to save")
        except sqlite3.Error as exc:
            messagebox.showerror("Reindex failed", str(exc))

    def plot_data(self):
        if self.notebook.select() == str(self.data_tab):
            columns = list(self.current_columns)
            rows = [self.current_raw_rows[item] for item in self.data_grid.get_children() if item in self.current_raw_rows]
        else:
            columns = list(self.result_grid["columns"])
            rows = [self.result_grid.item(item, "values") for item in self.result_grid.get_children()]
        if not columns or not rows:
            messagebox.showinfo("Plot data", "Display table data or SQL results first.")
            return
        ChartDialog(self, columns, rows)

    def show_database_info(self):
        if not self.require_connection():
            return
        page_count = self.connection.execute("PRAGMA page_count").fetchone()[0]
        page_size = self.connection.execute("PRAGMA page_size").fetchone()[0]
        fk = self.connection.execute("PRAGMA foreign_keys").fetchone()[0]
        journal = self.connection.execute("PRAGMA journal_mode").fetchone()[0]
        attached = self.connection.execute("PRAGMA database_list").fetchall()
        text = (f"File: {self.database_path}\n"
                f"Mode: {'Read-only' if self.readonly else 'Read/write'}\n"
                f"File size: {os.path.getsize(self.database_path):,} bytes\n"
                f"SQLite version: {sqlite3.sqlite_version}\n"
                f"Page count: {page_count:,}\nPage size: {page_size:,} bytes\n"
                f"Journal mode: {journal}\nForeign keys: {'On' if fk else 'Off'}\n"
                f"Databases: " + ", ".join(row[1] for row in attached))
        messagebox.showinfo("Database information", text)

    def show_shortcuts(self):
        messagebox.showinfo("Keyboard shortcuts", "Ctrl+O  Open database\nCtrl+N  New database\n"
                            "Ctrl+S  Commit changes\nF5  Refresh schema\nF9  Run selected SQL or all SQL\n"
                            "Ctrl+Space  SQL completion\nCtrl+C  Copy selected rows\nCtrl+V  Paste cells\nDelete  Delete selected rows\n"
                            "Ctrl++ / Ctrl+-  Larger / smaller text\nCtrl+0  Reset text size\nCtrl+F  Search all tables")


def main():
    initial_path = sys.argv[1] if len(sys.argv) > 1 else None
    try:
        SQLiteStudio(initial_path).mainloop()
    except Exception:
        messagebox.showerror("Unexpected error", traceback.format_exc())


if __name__ == "__main__":
    main()
