"""Automated standard-library tests for SQLiteStudio Python core behavior."""

import os
from pathlib import Path
import sqlite3
import sys
import tempfile
import threading
import time
import unittest

sys.dont_write_bytecode = True
sys.path.insert(0, str(Path(__file__).parent))

from sqlite_viewer import (bind_arguments, coerce_parameter, describe_constraint_error, file_is_writable,
                           find_parameters, format_sql, infer_column_types, json_records_to_table,
                           like_pattern, markdown_row, markdown_table, profile_column, quote_identifier,
                           run_script_atomically, search_database, split_sql, sql_insert_statements,
                           sql_literal)


class WorkbenchCoreTests(unittest.TestCase):
    def test_identifier_quoting(self):
        self.assertEqual(quote_identifier('weird"name'), '"weird""name"')

    def test_script_splitter_handles_same_line_and_semicolons_in_text(self):
        script = "CREATE TABLE t(v TEXT); INSERT INTO t VALUES('a;b'); SELECT * FROM t;"
        self.assertEqual(len(split_sql(script)), 3)

    def test_csv_type_inference(self):
        rows = [["1", "2.5", "Alice"], ["2", "3", "Bob"]]
        self.assertEqual(infer_column_types(rows, 3), ["INTEGER", "REAL", "TEXT"])

    def test_database_lifecycle_and_foreign_keys(self):
        handle, path = tempfile.mkstemp(suffix=".sqlite")
        os.close(handle)
        try:
            connection = sqlite3.connect(path)
            connection.execute("PRAGMA foreign_keys=ON")
            connection.executescript("""
                CREATE TABLE parent(id INTEGER PRIMARY KEY, name TEXT);
                CREATE TABLE child(id INTEGER PRIMARY KEY, parent_id INTEGER REFERENCES parent(id));
                INSERT INTO parent VALUES(1, 'sample');
                INSERT INTO child VALUES(1, 1);
            """)
            connection.commit()
            self.assertEqual(connection.execute("SELECT COUNT(*) FROM child").fetchone()[0], 1)
            self.assertEqual(connection.execute("PRAGMA integrity_check").fetchone()[0], "ok")
            self.assertEqual(connection.execute("PRAGMA foreign_key_check").fetchall(), [])
            connection.close()
        finally:
            os.remove(path)

    def test_sql_dump_round_trip(self):
        source = sqlite3.connect(":memory:")
        source.executescript("CREATE TABLE item(id INTEGER PRIMARY KEY, value TEXT); INSERT INTO item(value) VALUES('one'),('two');")
        dump = "\n".join(source.iterdump())
        restored = sqlite3.connect(":memory:")
        restored.executescript(dump)
        self.assertEqual(restored.execute("SELECT value FROM item ORDER BY id").fetchall(), [("one",), ("two",)])
        source.close()
        restored.close()

    def test_markdown_table_escapes_pipes_and_newlines(self):
        text = markdown_table(["a", "b"], [["x|y", "line1\nline2"]])
        self.assertEqual(text.splitlines(), ["| a | b |", "| --- | --- |", "| x\\|y | line1 line2 |"])

    def test_composite_key_predicate_targets_exact_row(self):
        # Mirrors the workbench's delete on a WITHOUT ROWID table with a composite key.
        connection = sqlite3.connect(":memory:")
        connection.executescript("""
            CREATE TABLE m(emp INTEGER, dept INTEGER, role TEXT, PRIMARY KEY(emp, dept)) WITHOUT ROWID;
            INSERT INTO m VALUES (1, 1, 'lead'), (1, 2, 'aux'), (2, 2, 'member');
        """)
        info = connection.execute("PRAGMA table_info(m)").fetchall()
        keys = [row[1] for row in sorted((r for r in info if r[5]), key=lambda r: r[5])]
        self.assertEqual(keys, ["emp", "dept"])
        predicate = " AND ".join(f"{quote_identifier(k)} IS ?" for k in keys)
        connection.execute(f"DELETE FROM m WHERE {predicate}", (1, 1))
        self.assertEqual(connection.execute("SELECT emp, dept FROM m ORDER BY emp, dept").fetchall(), [(1, 2), (2, 2)])
        connection.close()

    def test_row_keys_fetched_with_data_stay_aligned_under_sort_ties(self):
        # The grid selects "*, key" in ONE query, so each row carries its own key even when the sort column ties.
        connection = sqlite3.connect(":memory:")
        connection.execute("CREATE TABLE t(id INTEGER PRIMARY KEY, grp TEXT)")
        connection.executemany("INSERT INTO t VALUES (?, 'same')", [(i,) for i in range(1, 101)])
        cursor = connection.execute('SELECT *, "id" FROM t ORDER BY "grp" LIMIT 500 OFFSET 0')
        columns = [d[0] for d in cursor.description]
        id_index = columns[:-1].index("id")
        for raw in cursor.fetchall():
            display, key = raw[:-1], raw[-1:]
            self.assertEqual(display[id_index], key[0])
        connection.close()

    def test_query_can_be_interrupted_from_another_thread(self):
        connection = sqlite3.connect(":memory:", check_same_thread=False)
        outcome = {}

        def worker():
            try:
                connection.execute(
                    "WITH RECURSIVE c(x) AS (SELECT 1 UNION ALL SELECT x + 1 FROM c) SELECT COUNT(*) FROM c"
                ).fetchone()
                outcome["error"] = None
            except sqlite3.Error as exc:
                outcome["error"] = str(exc)

        thread = threading.Thread(target=worker, daemon=True)
        thread.start()
        time.sleep(0.2)
        connection.interrupt()
        thread.join(5)
        self.assertFalse(thread.is_alive())
        self.assertIn("interrupt", (outcome.get("error") or "").lower())
        connection.close()

    def test_sql_insert_script_round_trips_all_value_types(self):
        self.assertEqual(sql_literal(None), "NULL")
        self.assertEqual(sql_literal("it's"), "'it''s'")
        self.assertEqual(sql_literal(b"\x00\xff"), "X'00ff'")
        rows = [(1, "it's", 2.5, b"\x01", None)]
        script = sql_insert_statements("t", ["a", "b", "c", "d", "e"], rows)
        connection = sqlite3.connect(":memory:")
        connection.execute("CREATE TABLE t(a INTEGER, b TEXT, c REAL, d BLOB, e)")
        connection.executescript(script)
        self.assertEqual(connection.execute("SELECT a, b, c, d, e FROM t").fetchall(), [(1, "it's", 2.5, b"\x01", None)])
        connection.close()

    def test_json_records_infer_columns_and_affinities(self):
        records = [{"id": 1, "name": "a", "score": 1.5, "flag": True, "meta": {"k": 1}},
                   {"id": 2, "name": None, "score": 2, "extra": "x"}]
        columns, types, rows = json_records_to_table(records)
        self.assertEqual(columns, ["id", "name", "score", "flag", "meta", "extra"])
        self.assertEqual(types, ["INTEGER", "TEXT", "REAL", "INTEGER", "TEXT", "TEXT"])
        self.assertEqual(rows[0][3], 1)
        self.assertEqual(rows[0][4], '{"k": 1}')
        self.assertIsNone(rows[1][1])
        with self.assertRaises(ValueError):
            json_records_to_table({"not": "a list"})

    def test_data_version_reveals_commits_from_other_connections(self):
        handle, path = tempfile.mkstemp(suffix=".sqlite")
        os.close(handle)
        try:
            ours = sqlite3.connect(path)
            ours.execute("CREATE TABLE t(v)")
            ours.commit()
            before = ours.execute("PRAGMA data_version").fetchone()[0]
            ours.execute("INSERT INTO t VALUES (1)")
            ours.commit()
            self.assertEqual(ours.execute("PRAGMA data_version").fetchone()[0], before, "own commits must not bump it")
            other = sqlite3.connect(path)
            other.execute("INSERT INTO t VALUES (2)")
            other.commit()
            other.close()
            self.assertNotEqual(ours.execute("PRAGMA data_version").fetchone()[0], before)
            ours.close()
        finally:
            os.remove(path)

    def test_find_parameters_ignores_strings_and_comments(self):
        sql = "SELECT * FROM t WHERE a = :name AND b = @name AND c = ? AND d = 'x:not' -- :no\n AND e = ?"
        self.assertEqual(find_parameters(sql), (["name"], 2))

    def test_coerce_parameter_types(self):
        self.assertIsNone(coerce_parameter("<NULL>"))
        self.assertEqual(coerce_parameter("42"), 42)
        self.assertEqual(coerce_parameter("2.5"), 2.5)
        self.assertEqual(coerce_parameter("abc"), "abc")
        self.assertIsNone(coerce_parameter(None))

    def test_bind_arguments_named_positional_and_shortfall(self):
        self.assertEqual(bind_arguments("SELECT :a, :b", {"a": 1, "b": 2}, iter([])), {"a": 1, "b": 2})
        positional = iter([7, 8, 9])
        self.assertEqual(bind_arguments("SELECT ?, ?", {}, positional), (7, 8))
        with self.assertRaises(sqlite3.ProgrammingError):
            bind_arguments("SELECT ?, ?", {}, positional)
        connection = sqlite3.connect(":memory:")
        self.assertEqual(connection.execute("SELECT :a + ?", {"a": 1}) if False else
                         connection.execute("SELECT :a * 2", bind_arguments("SELECT :a * 2", {"a": 21}, iter([]))).fetchone()[0], 42)
        connection.close()

    def test_run_script_atomically_rolls_back_everything_on_failure(self):
        connection = sqlite3.connect(":memory:")
        connection.execute("CREATE TABLE t(v INTEGER UNIQUE)")
        good = "BEGIN TRANSACTION; INSERT INTO t VALUES (1); INSERT INTO t VALUES (2); COMMIT;"
        self.assertEqual(run_script_atomically(connection, good), 2)
        self.assertEqual(connection.execute("SELECT COUNT(*) FROM t").fetchone()[0], 2)
        bad = "INSERT INTO t VALUES (3); INSERT INTO t VALUES (1); INSERT INTO t VALUES (4);"
        with self.assertRaises(sqlite3.OperationalError) as caught:
            run_script_atomically(connection, bad)
        self.assertIn("Statement 2 of 3 failed", str(caught.exception))
        self.assertEqual(connection.execute("SELECT COUNT(*) FROM t").fetchone()[0], 2, "partial script must not persist")
        connection.close()

    def test_describe_constraint_error_names_referencing_tables(self):
        connection = sqlite3.connect(":memory:")
        connection.execute("PRAGMA foreign_keys=ON")
        connection.executescript("""
            CREATE TABLE parent(id INTEGER PRIMARY KEY);
            CREATE TABLE child(id INTEGER PRIMARY KEY, parent_id INTEGER REFERENCES parent(id));
            INSERT INTO parent VALUES (1); INSERT INTO child VALUES (1, 1);
        """)
        with self.assertRaises(sqlite3.IntegrityError) as caught:
            connection.execute("DELETE FROM parent WHERE id = 1")
        text = describe_constraint_error(connection, "parent", caught.exception)
        self.assertIn("child.parent_id", text)
        self.assertEqual(describe_constraint_error(connection, "parent", ValueError("other")), "other")
        connection.close()

    def test_file_is_writable_respects_permissions(self):
        handle, path = tempfile.mkstemp(suffix=".sqlite")
        os.close(handle)
        try:
            self.assertTrue(file_is_writable(Path(path)))
            os.chmod(path, 0o444)
            if os.geteuid() != 0:
                self.assertFalse(file_is_writable(Path(path)))
        finally:
            os.chmod(path, 0o644)
            os.remove(path)

    def test_format_sql_breaks_clauses_and_preserves_strings_and_parameters(self):
        text = format_sql("select a, 'from x' as s from t where a = :key and b = 1 order by a")
        self.assertEqual(text.splitlines(), ["SELECT a, 'from x' AS s", "FROM t", "WHERE a = :key", "  AND b = 1", "ORDER BY a"])
        self.assertIn(":key", text, "named parameter case must not change")

    def test_markdown_row_matches_table_header(self):
        self.assertEqual(markdown_row(["a|b", "c"]), "| a\\|b | c |\n")
        self.assertTrue(markdown_table(["x"], []).startswith("| x |\n| --- |\n"))

    def test_search_database_finds_cells_and_escapes_wildcards(self):
        connection = sqlite3.connect(":memory:")
        connection.executescript("""
            CREATE TABLE people(id INTEGER PRIMARY KEY, name TEXT, note TEXT);
            CREATE TABLE cities(id INTEGER PRIMARY KEY, city TEXT);
            INSERT INTO people VALUES (1, 'Alice', '100% sure'), (2, 'Bob', NULL);
            INSERT INTO cities VALUES (1, 'Alicante');
        """)
        hits = search_database(connection, "alic")
        self.assertEqual(sorted((h[0], h[1]) for h in hits), [("cities", "city"), ("people", "name")])
        self.assertEqual(like_pattern("100%"), "%100\\%%")
        self.assertEqual([h[2] for h in search_database(connection, "100%")], ["100% sure"])
        self.assertEqual(search_database(connection, "zzz"), [])
        connection.close()

    def test_profile_column_reports_counts_and_numeric_stats(self):
        connection = sqlite3.connect(":memory:")
        connection.execute("CREATE TABLE t(v INTEGER, s TEXT)")
        connection.executemany("INSERT INTO t VALUES (?, ?)", [(1, "a"), (2, "a"), (3, None), (None, "b")])
        stats, top = profile_column(connection, "t", "v")
        self.assertEqual((stats["rows"], stats["null"], stats["distinct"], stats["min"], stats["max"]), (4, 1, 3, 1, 3))
        self.assertEqual((stats["average"], stats["sum"]), (2.0, 6))
        stats, top = profile_column(connection, "t", "s", "v IS NOT NULL", ())
        self.assertNotIn("average", stats)
        self.assertEqual(top[0], ("a", 2))
        connection.close()


if __name__ == "__main__":
    unittest.main(verbosity=2)
