"""Windows/.NET 10 integration checks; all generated build files stay in TEMP.

Run: python selftest_csharp.py [screenshot-directory]
Uses the actual application source and SQLite provider, without NuGet or a project.
"""
from pathlib import Path
import os
import subprocess
import sys
import tempfile

HARNESS = r'''
var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA) throw new Exception("WinForms requires an STA entry point");
using var form = new StudioForm(null);
WindowsFormsSynchronizationContext.AutoInstall = false;
SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
Control.CheckForIllegalCrossThreadCalls = true;
void RenderDialog(Form dialog, string filename)
{
    if (args.Length == 0) return;
    Directory.CreateDirectory(args[0]);
    dialog.ShowInTaskbar = false; dialog.StartPosition = FormStartPosition.Manual; dialog.Location = new Point(-30000,-30000);
    dialog.Show(); Application.DoEvents();
    using var screenshot = new Bitmap(dialog.Width,dialog.Height);
    dialog.DrawToBitmap(screenshot,new Rectangle(Point.Empty,dialog.Size));
    screenshot.Save(Path.Combine(args[0],filename));
    dialog.Close();
}
object? Field(string name) => typeof(StudioForm).GetField(name, flags)?.GetValue(form) ?? typeof(StudioForm).GetProperty(name, flags)?.GetValue(form);
void Set(string name, object? value) => typeof(StudioForm).GetField(name, flags)!.SetValue(form, value);
object? Call(string name, params object?[] values) => typeof(StudioForm).GetMethod(name, flags)!.Invoke(form, values);
void Pump(Task task)
{
    var timeout = Stopwatch.StartNew();
    while (!task.IsCompleted)
    {
        Application.DoEvents();
        Thread.Sleep(1);
        if (timeout.Elapsed.TotalSeconds > 20) throw new Exception("Operation timed out");
    }
    task.GetAwaiter().GetResult();
}
var passed = 0;
void Check(bool pass, string message)
{
    if (!pass) throw new Exception(message);
    passed++;
    Console.WriteLine("PASS " + message);
}
var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = ":memory:" }.ToString());
connection.OpenAsync().GetAwaiter().GetResult();
void Exec(string sql) { using var c = connection.CreateCommand(); c.CommandText = sql; c.ExecuteNonQueryCore(CancellationToken.None); }
long Scalar(string sql) { using var c = connection.CreateCommand(); c.CommandText = sql; return Convert.ToInt64(c.ExecuteScalarAsync().Result); }
Exec("CREATE TABLE records(id INTEGER PRIMARY KEY, category TEXT, value TEXT); WITH RECURSIVE n(x) AS (VALUES(1) UNION ALL SELECT x+1 FROM n WHERE x<100000) INSERT INTO records SELECT x, 'group ' || (x%7), 'Record ' || x FROM n; CREATE TABLE composite(a TEXT,b INTEGER,value TEXT,PRIMARY KEY(a,b)) WITHOUT ROWID; INSERT INTO composite VALUES('a',1,'one'),('a',2,'two'); CREATE TABLE shadow(rowid TEXT,value TEXT); INSERT INTO shadow VALUES('same','one'),('same','two'); CREATE TABLE generated(id INTEGER PRIMARY KEY,a INTEGER,b INTEGER GENERATED ALWAYS AS (a*2)); INSERT INTO generated(a) VALUES(3);");
Set("_connection", connection);
Set("_dataVersion", (int)Scalar("PRAGMA data_version"));
Call("UpdateConnectedState");
form.ShowInTaskbar = false;
form.StartPosition = FormStartPosition.Manual;
form.Location = new Point(-30000, -30000);
form.Show();
Application.DoEvents();
form.Hide();
var grid = (DataGridView)Field("_dataGrid")!;
var watch = Stopwatch.StartNew();
Pump((Task)Call("LoadObjectAsync", "records", "table")!);
Console.WriteLine($"First page / 100,000 rows: {watch.ElapsedMilliseconds} ms");
Check(grid.VirtualMode && grid.Rows.Count == 500, "virtual browse page contains 500 rows");
Check(((Button)Field("_nextButton")!).Enabled, "lookahead enables next page");
Check(Convert.ToInt64(grid.Rows[0].Cells[0].Value) == 1, "virtual values remain accessible to editors/export");
grid.Columns[1].Width = 241;
Pump((Task)Call("LoadCurrentObjectAsync", (int?)1)!);
Check(Convert.ToInt64(grid.Rows[0].Cells[0].Value) == 501 && grid.Columns[1].Width == 241, "paging preserves column widths and correct values");
Pump((Task)Call("LoadCurrentObjectAsync", (int?)199)!);
Check(!((Button)Field("_nextButton")!).Enabled && grid.Rows.Count == 500, "exact page boundary has no phantom next page");
Pump((Task)Call("LoadObjectAsync", "composite", "table")!);
Check(((List<string>)Field("_keyColumns")!).SequenceEqual(new[] { "a", "b" }), "composite WITHOUT ROWID identities");
Pump((Task)Call("LoadObjectAsync", "shadow", "table")!);
Check(((List<string>)Field("_keyColumns")!)[0] == "_rowid_", "shadowed rowid uses an accessible alias");
Check(Convert.ToInt64(((List<object?[]>)Field("_rowKeys")!)[1][0]) == 2, "shadowed rowid values match displayed rows");
Pump((Task)Call("LoadObjectAsync", "generated", "table")!);
Check(Convert.ToInt64(grid.Rows[0].Cells[2].Value) == 6, "generated columns remain visible");
var batch = (Task)Call("ExecuteSqlBatchAsync", "WITH RECURSIVE n(x) AS (VALUES(1) UNION ALL SELECT x+1 FROM n WHERE x<100000000) SELECT x FROM n; SELECT 42 AS answer;", new Dictionary<string, object?>(), false, CancellationToken.None)!;
Pump(batch);
var result = batch.GetType().GetProperty("Result")!.GetValue(batch)!;
var resultRows = (List<object?[]>)result.GetType().GetProperty("Rows")!.GetValue(result)!;
Check(resultRows.Count == 1 && Convert.ToInt64(resultRows[0][0]) == 42, "truncated read skips discarded work and runs next statement");
Check(!(bool)result.GetType().GetProperty("Truncated")!.GetValue(result)!, "truncation belongs to the displayed final result");
batch = (Task)Call("ExecuteSqlBatchAsync", "UPDATE records SET value='changed' RETURNING id;", new Dictionary<string, object?>(), true, CancellationToken.None)!;
Pump(batch);
Check(Scalar("SELECT COUNT(*) FROM records WHERE value='changed'") == 100000, "RETURNING writes finish beyond the display limit");
Pump((Task)Call("RollbackAsync", false)!);
Check(Scalar("SELECT COUNT(*) FROM records WHERE value='changed'") == 0, "write batch remains rollbackable");
Exec("CREATE VIEW expensive AS WITH RECURSIVE n(x) AS (VALUES(1) UNION ALL SELECT x+1 FROM n WHERE x<100000000) SELECT x FROM n;");
watch.Restart();
Pump((Task)Call("LoadObjectAsync", "expensive", "view")!);
Check(grid.Rows.Count == 500 && watch.Elapsed.TotalSeconds < 5, "large view browse does not count all rows");
var count = (Task)Call("CountRowsAsync")!;
Application.DoEvents();
Check(!count.IsCompleted && !((MenuStrip)Field("_menu")!).Enabled, "long count yields to UI and prevents overlapping database actions");
Call("CancelQuery");
Pump(count);
Check(((MenuStrip)Field("_menu")!).Enabled && Field("_queryCancellation") is null, "cancellation restores controls and connection availability");

void Fails(Action action, string message)
{
    try { action(); }
    catch (Exception ex) when (ex is not OutOfMemoryException) { Check(true, message); return; }
    throw new Exception("Expected failure: " + message);
}
Task Batch(string sql) => (Task)Call("ExecuteSqlBatchAsync", sql, new Dictionary<string, object?>(), true, CancellationToken.None)!;
Pump(Batch("-- comment only\n/* still no SQL */"));
Check(!connection.InTransaction, "comment-only SQL does not open a transaction");
Check(DataTransfer.UniqueNames(new[]{"x","x","x_2"}).Distinct().Count()==3, "JSON export gives duplicate result headings unique names");
Exec("PRAGMA foreign_keys=ON; CREATE TABLE atomic_test(id INTEGER PRIMARY KEY, value TEXT);");
Fails(() => Pump(Batch("PRAGMA foreign_keys=OFF;")), "setting PRAGMAs are rejected before preparation");
Check(Scalar("PRAGMA foreign_keys") == 1, "rejected PRAGMA leaves foreign-key enforcement enabled");
Fails(() => Pump(Batch("INSERT INTO atomic_test VALUES(1,'partial'); INSERT INTO missing_table VALUES(1);")), "failed batch reports its error");
Check(Scalar("SELECT COUNT(*) FROM atomic_test") == 0 && !connection.InTransaction, "failed new batch leaves no partial writes or transaction");
Pump(Batch("INSERT INTO atomic_test VALUES(1,'earlier');"));
Fails(() => Pump(Batch("INSERT INTO atomic_test VALUES(2,'later'); INSERT INTO atomic_test VALUES(1,'conflict');")), "constraint failure surfaces");
Check(Scalar("SELECT COUNT(*) FROM atomic_test") == 1 && connection.InTransaction, "failed batch preserves earlier pending edits");
Fails(() => Pump(Batch("COMMIT;")), "manual transaction control cannot bypass commit workflow");
Check(connection.InTransaction, "rejected COMMIT leaves pending edits intact");
Pump((Task)Call("RollbackAsync", false)!);
Pump(Batch("CREATE TRIGGER audit_trigger AFTER INSERT ON atomic_test BEGIN UPDATE atomic_test SET value='trigger; works' WHERE id=new.id; END; INSERT INTO atomic_test VALUES(1,'start');"));
Check(Scalar("SELECT COUNT(*) FROM atomic_test WHERE value='trigger; works'") == 1, "trigger bodies execute as single native statements");
Pump((Task)Call("RollbackAsync", false)!);
Set("_readOnly", true);
Fails(() => Pump(Batch("WITH n AS (SELECT 1) INSERT INTO atomic_test SELECT 1,'blocked' FROM n;")), "native classification blocks WITH writes in read-only mode");
Set("_readOnly", false);
Check(Scalar("SELECT COUNT(*) FROM atomic_test") == 0, "read-only rejection leaves data unchanged");
var originalSql = "select '  FROM x; -- not a comment' AS [WHERE], `ORDER` from (select 1 AS `ORDER`) -- keep comment\nwhere 1=1;";
var formattedSql = SqlText.Format(originalSql);
Check(formattedSql.Contains("'  FROM x; -- not a comment'") && formattedSql.Contains("[WHERE]") && formattedSql.Contains("`ORDER`") && formattedSql.Contains("-- keep comment\n"), "formatter preserves quoted text and comment boundaries");
var parameterized = SqlText.Parameters("SELECT ?,?1,@name,?2,':ignored',[?not],`@not`; SELECT ?3,?3;");
var bound = SqlText.Parameters("SELECT ?,?1,@name,?2;");
using (var boundCommand = connection.CreateCommand())
{
    boundCommand.CommandText = bound.Sql;
    boundCommand.Parameters.AddWithValue(bound.Names[0], "first");
    boundCommand.Parameters.AddWithValue(bound.Names[1], "second");
    using var boundReader = boundCommand.ExecuteReaderAsync().Result;
    boundReader.ReadCore(CancellationToken.None);
    Check(boundReader.GetString(0) == boundReader.GetString(1) && boundReader.GetString(2) == boundReader.GetString(3), "numbered aliases bind the same actual values");
}
Check(parameterized.Names.Count == 3 && parameterized.Sql.Contains("':ignored'") && parameterized.Sql.Contains("[?not]"), "numbered parameters share slots and quoted markers are ignored");
Exec("CREATE TABLE nullable_key(k TEXT PRIMARY KEY, value TEXT); INSERT INTO nullable_key VALUES(NULL,'one'),(NULL,'two');");
Pump((Task)Call("LoadObjectAsync", "nullable_key", "table")!);
Check(((List<string>)Field("_keyColumns")!)[0] == "rowid", "nullable primary keys use unique row identities");
var originalParameters = new List<(string, object?)>();
var originalPredicate = (string)Call("SnapshotPredicate", 0, originalParameters)!;
Exec("UPDATE nullable_key SET value='external' WHERE rowid=1");
var guardedEdits = new List<(string, List<(string, object?)>, int?)> { ($"UPDATE nullable_key SET value='overwrite' WHERE {originalPredicate}", originalParameters, 1) };
Fails(() => Pump((Task)Call("ApplyEditsAsync", guardedEdits)!), "stale edit is rejected");
Check(Scalar("SELECT COUNT(*) FROM nullable_key WHERE value='external'") == 1, "stale edit rollback preserves concurrent values");
var docs = (TabControl)Field("_queryTabs")!;
var firstEditor = (RichTextBox)Field("_sqlEditor")!;
Check(ReferenceEquals(firstEditor, docs.SelectedTab!.Controls[0]), "active editor belongs to the selected query tab");
firstEditor.Text = "SELECT 'first document';";
Call("NewQuery", "SELECT 'second document';", "Second", true);
docs.SelectedIndex = 0;
Check(((RichTextBox)Field("_sqlEditor")!).Text.Contains("first document"), "query tabs retain independent text");
foreach (TabPage page in docs.TabPages) ((RichTextBox)page.Controls[0]).Modified = false;
using (var recordDialog = new RecordEditorDialog("Edit row", new object[] { new { Name="id",Type="INTEGER" }, new {Name="description",Type="TEXT"},new{Name="binary_value",Type="BLOB"} }, new object?[] {1L,"A sample row",new byte[]{0,1,255}}, false,true)) RenderDialog(recordDialog,"csharp-record.png");
using (var parameterDialog = new ParameterDialog(new[]{"@name","@minimum"},true)) RenderDialog(parameterDialog,"csharp-parameters.png");
var files = Path.Combine(Path.GetTempPath(), "studio-transfer-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(files);
try
{
    var csvPath = Path.Combine(files,"input.csv");
    File.WriteAllText(csvPath, "id,note\r\n001,\"line 1\r\nline 2\"\r\n002,\"a,\"\"quote\"\"\"\r\n003,\r\n", new UTF8Encoding(true));
    var import = ImportSource.Open(csvPath, CancellationToken.None);
    using (var previewDialog = new ImportPreviewDialog(import, true)) RenderDialog(previewDialog, "csharp-import.png");
    Check(import.Preview.Count == 3 && (string)import.Preview[0][0]! == "001" && (string)import.Preview[1][1]! == "a,\"quote\"", "CSV preview preserves leading zeroes, multiline and escaped quotes");
    Task<long> Import(ImportSource source, string table) => (Task<long>)typeof(StudioForm).GetMethod("AtomicWriteAsync", flags)!.MakeGenericMethod(typeof(long)).Invoke(form, new object[] { (Func<Task<long>>)(() => Task.FromResult(DataTransfer.Import(connection, source, table, CancellationToken.None))), CancellationToken.None })!;
    Pump(Import(import,"csv_import"));
    Check(Scalar("SELECT COUNT(*) FROM csv_import WHERE typeof(id)='text'") == 3, "CSV import preserves field types without coercion");
    var badPath = Path.Combine(files,"bad.csv");
    File.WriteAllText(badPath, "a,b\n" + string.Concat(Enumerable.Repeat("1,2\n", 101)) + "bad,row,extra\n");
    var bad = ImportSource.Open(badPath, CancellationToken.None);
    Fails(() => Pump(Import(bad,"broken_import")), "malformed later CSV record rejects the entire import");
    Check(Scalar("SELECT COUNT(*) FROM sqlite_schema WHERE name='broken_import'") == 0 && Scalar("SELECT COUNT(*) FROM csv_import") == 3, "import failure rolls back its table and preserves previous edits");
    Pump((Task)Call("CommitAsync")!);
    for (var format = 1; format <= 3; format++)
    {
        var output = Path.Combine(files,"export"+format);
        var exported = DataTransfer.Export(connection,"SELECT * FROM records","records",output,format,CancellationToken.None);
        Check(exported == 100000, $"format {format} exports beyond the visible page/result limit");
        if (format == 2)
        {
            using var json = JsonDocument.Parse(File.ReadAllBytes(output));
            Check(json.RootElement.GetArrayLength() == 100000 && json.RootElement[0].GetProperty("id").GetInt64() == 1, "streamed JSON is valid and preserves integer values");
        }
    }

    Exec("CREATE TABLE typed_roundtrip(i INTEGER,t TEXT,b BLOB,n); INSERT INTO typed_roundtrip VALUES(9223372036854775807,'quote '' and nul'||char(0)||'tail',X'0001FF',NULL);");
    var typedPath = Path.Combine(files,"typed.sql");
    DataTransfer.Export(connection,"SELECT * FROM typed_roundtrip","typed_roundtrip",typedPath,3,CancellationToken.None);
    var typedCopy = new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=":memory:"}.ToString());
    try
    {
        typedCopy.OpenAsync().GetAwaiter().GetResult();
        typedCopy.ExecuteImmediate("CREATE TABLE typed_roundtrip(i INTEGER,t TEXT,b BLOB,n);");
        typedCopy.ExecuteImmediate(File.ReadAllText(typedPath));
        using var typedCheck = typedCopy.CreateCommand(); typedCheck.CommandText="SELECT i,t,b,n FROM typed_roundtrip";
        using var typedReader = typedCheck.ExecuteReaderAsync().Result; typedReader.ReadCore(CancellationToken.None);
        Check(typedReader.GetInt64(0)==long.MaxValue && typedReader.GetString(1).Contains('\0') && ((byte[])typedReader.GetValue(2)!).SequenceEqual(new byte[]{0,1,255}) && typedReader.IsDBNull(3), "SQL export round-trips 64-bit integers, quotes, embedded NUL, BLOBs and NULL");
        var insertScript = "CREATE TABLE script_test(v);" + string.Concat(Enumerable.Range(1,5000).Select(i=>$"INSERT INTO script_test VALUES({i});"));
        typedCopy.ExecuteImmediate(insertScript);
        using var manyCheck = typedCopy.CreateCommand(); manyCheck.CommandText="SELECT COUNT(*) FROM script_test";
        Check(Convert.ToInt64(manyCheck.ExecuteScalarAsync().Result)==5000, "large multi-statement scripts advance through a single UTF-8 buffer");
    }
    finally { typedCopy.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
    var keep = Path.Combine(files,"keep.csv"); File.WriteAllText(keep,"keep existing destination");
    using (var cancelled = new CancellationTokenSource())
    {
        cancelled.Cancel();
        Fails(() => DataTransfer.Export(connection,"SELECT * FROM records","records",keep,1,cancelled.Token), "cancelled export reports cancellation");
    }
    Check(File.ReadAllText(keep) == "keep existing destination" && Directory.GetFiles(files,"*.partial").Length == 0, "cancelled export preserves destination and removes partial output");
    var jsonPath = Path.Combine(files,"input.json");
    File.WriteAllText(jsonPath,"[{\"id\":1,\"value\":null},{\"id\":2,\"value\":1.234567890123456789,\"extra\":{\"a\":1}}]");
    Pump(Import(ImportSource.Open(jsonPath,CancellationToken.None),"json_import"));
    Check(Scalar("SELECT COUNT(*) FROM json_import WHERE value IS NULL") == 1, "JSON import preserves null and missing fields");
    Check(Scalar("SELECT COUNT(*) FROM json_import WHERE value='1.234567890123456789'") == 1, "JSON import preserves high precision numeric text");
    Pump((Task)Call("CommitAsync")!);
    var backupPath = Path.Combine(files,"backup.sqlite");
    DataTransfer.Backup(connection,backupPath,CancellationToken.None);
    var restored = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource=backupPath, Mode=SqliteOpenMode.ReadOnly }.ToString());
    try
    {
        restored.OpenAsync().GetAwaiter().GetResult();
        using var checkBackup = restored.CreateCommand(); checkBackup.CommandText="SELECT COUNT(*) FROM records";
        Check(Convert.ToInt64(checkBackup.ExecuteScalarAsync().Result) == 100000, "backup reopens read-only with all committed records");
    }
    finally { restored.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
    Fails(() => DataTransfer.Backup(connection,backupPath,CancellationToken.None), "backup never overwrites an existing database");
}
finally { Directory.Delete(files,true); }

Pump((Task)Call("LoadObjectAsync", "records", "table")!);
Pump((Task)Call("RefreshSchemaAsync", true)!);
((ToolStripLabel)Field("_pathLabel")!).Text = "Test database";
void CheckLayout()
{
    var menu = (MenuStrip)Field("_menu")!;
    var toolbar = (ToolStrip)Field("_toolbar")!;
    var split = (SplitContainer)Field("_mainSplit")!;
    Check(menu.Bottom <= toolbar.Top && toolbar.Bottom <= split.Top, "menu and toolbar never overlap the workspace");
    var previous = (Button)Field("_previousButton")!;
    foreach (var tabs in new[] { (TabControl)Field("_workspace")!, (TabControl)Field("_queryTabs")! }) Check(tabs.GetTabRect(0).Height >= tabs.Font.Height + 8, "tab height accommodates its font at current DPI");
    Check(previous.Parent!.ClientRectangle.Contains(previous.Bounds) && previous.Height >= previous.Font.Height + 8, "pager fits its layout row at current DPI");
}
CheckLayout();
if (args.Length > 0)
{
    form.Show();
    Application.DoEvents();
    Directory.CreateDirectory(args[0]);
    foreach (var dark in new[] { false, true })
    {
        Set("_dark", dark); Call("ApplyTheme"); Application.DoEvents();
        using var bitmap = new Bitmap(form.Width, form.Height);
        form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size));
        bitmap.Save(Path.Combine(args[0], dark ? "csharp-browse-dark.png" : "csharp-browse-light.png"));
    }
    ((TabControl)Field("_workspace")!).SelectedTab = (TabPage)Field("_homeTab")!;
    Application.DoEvents();
    using (var overview = new Bitmap(form.Width, form.Height))
    {
        form.DrawToBitmap(overview, new Rectangle(Point.Empty, form.Size));
        overview.Save(Path.Combine(args[0], "csharp-overview.png"));
    }
    ((TabControl)Field("_workspace")!).SelectedTab = (TabPage)Field("_browseTab")!;
    form.Size = form.MinimumSize;
    Application.DoEvents();
    CheckLayout();
    using (var bitmap = new Bitmap(form.Width, form.Height))
    {
        form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size));
        bitmap.Save(Path.Combine(args[0], "csharp-browse-minimum.png"));
    }
    ((TabControl)Field("_workspace")!).SelectedTab = (TabPage)Field("_sqlTab")!;
    Application.DoEvents();
    using (var bitmap = new Bitmap(form.Width, form.Height))
    {
        form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size));
        bitmap.Save(Path.Combine(args[0], "csharp-sql-minimum.png"));
    }
}
((RichTextBox)Field("_sqlEditor")!).Modified = false;
Pump((Task)Call("CloseDatabaseAsync", false)!);
Check(grid.Rows.Count == 0, "closing clears virtual rows");
form.Close();
Console.WriteLine($"All {passed} C# integration checks passed.");
'''


def main():
    source = Path(__file__).with_name("SQLiteStudio.cs").read_text(encoding="utf-8")
    source = source.replace("#:property OutputType=WinExe", "#:property OutputType=Exe")
    if os.environ.get("STUDIO_TEST_DPI") == "96":
        source = source.replace("HighDpiMode.PerMonitorV2", "HighDpiMode.DpiUnaware")
    source = source.replace("StudioPreferences.Load();", "new StudioPreferences();")
    source = source.replace("_preferences.Save();", "/* Tests never write user preferences. */")
    source = source.replace("Application.Run(new StudioForm(args.Length > 0 ? args[0] : null));", HARNESS)
    # Make application errors fail the test instead of opening a modal dialog.
    start = source.index("    void ShowError(string title, Exception ex)")
    end = source.index("    sealed record DbObject", start)
    source = source[:start] + '    void ShowError(string title, Exception ex) => throw new Exception(title, ex);\n\n' + source[end:]
    with tempfile.TemporaryDirectory(prefix="sqlite-studio-tests-") as temp:
        harness = Path(temp) / "StudioTests.cs"
        harness.write_text(source, encoding="utf-8")
        command = ["dotnet", "run", str(harness)]
        if len(sys.argv) > 1:
            command += ["--", str(Path(sys.argv[1]).resolve())]
        subprocess.run(command, check=True, timeout=240)


if __name__ == "__main__":
    main()
