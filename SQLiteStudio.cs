#:property TargetFramework=net10.0-windows
#:property OutputType=WinExe
#:property UseWindowsForms=true
#:property EnableWindowsTargeting=true
#:property PublishAot=false
#:property TreatWarningsAsErrors=true
#:property RestoreIgnoreFailedSources=true
#:property NuGetAudit=false

// SQLiteStudio C# — the additional .NET 10 file-based Windows edition.
// The cross-platform Python edition remains available in studio.py.
// SQLite access uses Windows' built-in winsqlite3.dll: no NuGet restore or internet is required.
// Open this file in Visual Studio with .NET 10 support, or run: dotnet run SQLiteStudio.cs
// Pass a database path after -- to open it immediately:
// dotnet run SQLiteStudio.cs -- "C:\\data\\sample.sqlite"

using System.Data;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Drawing;
using System.Windows.Forms;

Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
Application.EnableVisualStyles();
Application.SetCompatibleTextRenderingDefault(false);
Application.Run(new StudioForm(args.Length > 0 ? args[0] : null));

sealed class StudioForm : Form
{
    const int PageSize = 500;
    const int ResultLimit = 5000;

    static readonly Color Accent = Color.FromArgb(45, 112, 214);
    static readonly Color AccentHover = Color.FromArgb(32, 91, 178);
    static readonly Color LightBackground = Color.FromArgb(245, 247, 250);
    static readonly Color DarkBackground = Color.FromArgb(30, 32, 36);
    static readonly Color DarkSurface = Color.FromArgb(42, 45, 50);
    static readonly Color DarkText = Color.FromArgb(235, 238, 242);

    SqliteConnection? _connection;
    SqliteTransaction? _transaction;
    SqliteCommand? _runningCommand;
    CancellationTokenSource? _queryCancellation;
    Task<SqlBatchResult>? _activeQueryTask;
    string? _databasePath;
    string? _currentObject;
    string? _currentObjectType;
    string? _sortColumn;
    bool _sortDescending;
    bool _hasChanges;
    bool _readOnly;
    bool _dark;
    int _page;
    long _rowCount;
    int _dataVersion;
    readonly List<string> _displayColumns = [];
    readonly List<string> _keyColumns = [];
    readonly List<object?[]> _rowKeys = [];
    readonly List<string> _history = [];

    readonly MenuStrip _menu = new();
    readonly ToolStrip _toolbar = new();
    readonly ToolStripLabel _pathLabel = new();
    readonly ToolStripLabel _pendingLabel = new();
    readonly SplitContainer _mainSplit = new();
    readonly TextBox _objectFilter = new();
    readonly TreeView _objectTree = new();
    readonly TabControl _workspace = new();
    readonly TabPage _browseTab = new("Browse data");
    readonly TabPage _sqlTab = new("SQL workspace");
    readonly TabPage _schemaTab = new("Schema");
    readonly DataGridView _dataGrid = CreateGrid();
    readonly TextBox _whereBox = new();
    readonly Label _tableTitle = new();
    readonly Label _pageLabel = new();
    readonly Label _rowCountLabel = new();
    readonly NumericUpDown _goToPage = new();
    readonly Button _previousButton = MakeButton("Previous");
    readonly Button _nextButton = MakeButton("Next");
    readonly RichTextBox _sqlEditor = new();
    readonly DataGridView _resultGrid = CreateGrid();
    readonly ListBox _historyList = new();
    readonly RichTextBox _sqlLog = new();
    readonly Button _runButton = MakePrimaryButton("Run selected / all   F9");
    readonly Button _cancelButton = MakeButton("Cancel");
    readonly DataGridView _columnsGrid = CreateGrid();
    readonly DataGridView _indexesGrid = CreateGrid();
    readonly DataGridView _foreignKeysGrid = CreateGrid();
    readonly RichTextBox _definitionBox = new();
    readonly StatusStrip _status = new();
    readonly ToolStripStatusLabel _statusText = new("Ready") { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
    readonly ToolStripStatusLabel _statusRows = new();
    readonly ToolStripStatusLabel _statusMode = new("No database");
    readonly System.Windows.Forms.Timer _schemaFilterTimer = new() { Interval = 250 };
    readonly Font _nullFont = new("Segoe UI", 9.5f, FontStyle.Italic);
    readonly Font _treeHeaderFont = new("Segoe UI", 9.5f, FontStyle.Bold);

    public StudioForm(string? initialPath)
    {
        Text = "SQLiteStudio C#";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(1040, 700);
        Size = new Size(1440, 900);
        Font = new Font("Segoe UI", 9.5f);
        KeyPreview = true;

        BuildMenu();
        BuildToolbar();
        BuildWorkspace();
        BuildStatusBar();
        // Docked edge controls must be ahead of the Fill control in WinForms'
        // z-order or a maximized window can hide the menu/toolbar.
        _mainSplit.SendToBack();
        _toolbar.BringToFront();
        _menu.BringToFront();
        _status.BringToFront();
        _schemaFilterTimer.Tick += async (_, _) =>
        {
            _schemaFilterTimer.Stop();
            await SafeUiAsync(() => RefreshSchemaAsync(preserveSelection: true));
        };
        ApplyTheme();
        UpdateConnectedState();

        FormClosing += OnClosing;
        KeyDown += HandleShortcut;
        Shown += async (_, _) =>
        {
            if (!string.IsNullOrWhiteSpace(initialPath))
                await OpenDatabaseAsync(initialPath);
        };
    }

    static DataGridView CreateGrid() => new()
    {
        Dock = DockStyle.Fill,
        AllowUserToAddRows = false,
        AllowUserToDeleteRows = false,
        AllowUserToOrderColumns = true,
        AllowUserToResizeRows = false,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None,
        BackgroundColor = Color.White,
        BorderStyle = BorderStyle.None,
        CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal,
        ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None,
        ColumnHeadersHeight = 36,
        ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing,
        EditMode = DataGridViewEditMode.EditProgrammatically,
        EnableHeadersVisualStyles = false,
        MultiSelect = true,
        ReadOnly = true,
        RowHeadersVisible = false,
        RowTemplate = { Height = 30 },
        SelectionMode = DataGridViewSelectionMode.FullRowSelect
    };

    static Button MakeButton(string text) => new()
    {
        Text = text,
        AutoSize = true,
        Height = 32,
        FlatStyle = FlatStyle.Flat,
        Padding = new Padding(10, 2, 10, 2),
        Margin = new Padding(3)
    };

    static Button MakePrimaryButton(string text)
    {
        var button = MakeButton(text);
        button.BackColor = Accent;
        button.ForeColor = Color.White;
        button.FlatAppearance.BorderColor = Accent;
        button.FlatAppearance.MouseOverBackColor = AccentHover;
        return button;
    }

    void BuildMenu()
    {
        var file = new ToolStripMenuItem("&File");
        file.DropDownItems.Add(MenuItem("&Open database…", Keys.Control | Keys.O, async () => await ChooseDatabaseAsync(false)));
        file.DropDownItems.Add(MenuItem("&Create database…", Keys.Control | Keys.N, async () => await ChooseDatabaseAsync(true)));
        file.DropDownItems.Add(new ToolStripSeparator());
        file.DropDownItems.Add(MenuItem("Open &read-only…", Keys.None, async () => await ChooseDatabaseAsync(false, true)));
        file.DropDownItems.Add(MenuItem("&Close database", Keys.Control | Keys.W, async () => await CloseDatabaseAsync()));
        file.DropDownItems.Add(new ToolStripSeparator());
        file.DropDownItems.Add(MenuItem("E&xit", Keys.Alt | Keys.F4, Close));

        var edit = new ToolStripMenuItem("&Edit");
        edit.DropDownItems.Add(MenuItem("&Commit changes", Keys.Control | Keys.S, async () => await CommitAsync()));
        edit.DropDownItems.Add(MenuItem("&Rollback changes", Keys.Control | Keys.Shift | Keys.S, async () => await RollbackAsync()));
        edit.DropDownItems.Add(new ToolStripSeparator());
        edit.DropDownItems.Add(MenuItem("Copy selected rows", Keys.Control | Keys.C, CopyRows));
        edit.DropDownItems.Add(MenuItem("Delete selected rows", Keys.Delete, async () => await DeleteRowsAsync()));

        var data = new ToolStripMenuItem("&Data");
        data.DropDownItems.Add(MenuItem("Add row…", Keys.Control | Keys.Insert, async () => await AddRowAsync()));
        data.DropDownItems.Add(MenuItem("Edit row…", Keys.Control | Keys.E, async () => await EditRowAsync()));
        data.DropDownItems.Add(MenuItem("Duplicate row", Keys.Control | Keys.D, async () => await DuplicateRowAsync()));
        data.DropDownItems.Add(new ToolStripSeparator());
        data.DropDownItems.Add(MenuItem("Export visible rows as CSV…", Keys.None, ExportVisibleCsv));
        data.DropDownItems.Add(MenuItem("Export visible rows as JSON…", Keys.None, ExportVisibleJson));

        var database = new ToolStripMenuItem("&Database");
        database.DropDownItems.Add(MenuItem("&Refresh", Keys.F5, async () => await RefreshSchemaAsync()));
        database.DropDownItems.Add(MenuItem("Integrity check", Keys.None, async () => await RunCheckAsync("PRAGMA integrity_check", "Integrity check")));
        database.DropDownItems.Add(MenuItem("Foreign-key check", Keys.None, async () => await RunCheckAsync("PRAGMA foreign_key_check", "Foreign-key check")));
        database.DropDownItems.Add(MenuItem("Optimize", Keys.None, async () => await ExecuteMaintenanceAsync("PRAGMA optimize", "Database optimized.")));
        database.DropDownItems.Add(MenuItem("Vacuum", Keys.None, async () => await VacuumAsync()));

        var view = new ToolStripMenuItem("&View");
        view.DropDownItems.Add(MenuItem("Toggle dark theme", Keys.Control | Keys.T, () => { _dark = !_dark; ApplyTheme(); }));
        view.DropDownItems.Add(MenuItem("SQL workspace", Keys.Control | Keys.L, () => { _workspace.SelectedTab = _sqlTab; _sqlEditor.Focus(); }));

        var help = new ToolStripMenuItem("&Help");
        help.DropDownItems.Add(MenuItem("Keyboard shortcuts", Keys.None, ShowShortcuts));
        help.DropDownItems.Add(MenuItem("About", Keys.None, () => MessageBox.Show(this,
            "SQLiteStudio C#\n\nA source-only SQLite workbench built as a .NET 10 file-based app.\nUses Windows' built-in SQLite library; no NuGet package, project file or packaged executable is required.",
            "About SQLiteStudio", MessageBoxButtons.OK, MessageBoxIcon.Information)));

        _menu.Items.AddRange([file, edit, data, database, view, help]);
        MainMenuStrip = _menu;
        Controls.Add(_menu);
    }

    ToolStripMenuItem MenuItem(string text, Keys keys, Action action)
    {
        var item = new ToolStripMenuItem(text) { ShortcutKeys = keys };
        item.Click += (_, _) => action();
        return item;
    }

    void BuildToolbar()
    {
        _toolbar.GripStyle = ToolStripGripStyle.Hidden;
        _toolbar.Padding = new Padding(8, 5, 8, 5);
        _toolbar.AutoSize = true;
        AddToolButton("Open", async () => await ChooseDatabaseAsync(false));
        AddToolButton("New", async () => await ChooseDatabaseAsync(true));
        _toolbar.Items.Add(new ToolStripSeparator());
        AddToolButton("Refresh", async () => await RefreshSchemaAsync());
        AddToolButton("Commit", async () => await CommitAsync());
        AddToolButton("Rollback", async () => await RollbackAsync());
        _toolbar.Items.Add(new ToolStripSeparator());
        _pendingLabel.ForeColor = Color.FromArgb(205, 105, 30);
        _pendingLabel.Font = new Font(Font, FontStyle.Bold);
        _toolbar.Items.Add(_pendingLabel);
        _pathLabel.Alignment = ToolStripItemAlignment.Right;
        _pathLabel.AutoToolTip = true;
        _pathLabel.Text = "No database open";
        _toolbar.Items.Add(_pathLabel);
        Controls.Add(_toolbar);
    }

    void AddToolButton(string text, Func<Task> action)
    {
        var button = new ToolStripButton(text) { DisplayStyle = ToolStripItemDisplayStyle.Text, Padding = new Padding(8, 3, 8, 3) };
        button.Click += async (_, _) => await SafeUiAsync(action);
        _toolbar.Items.Add(button);
    }

    void BuildWorkspace()
    {
        _mainSplit.Dock = DockStyle.Fill;
        _mainSplit.FixedPanel = FixedPanel.Panel1;
        _mainSplit.SplitterDistance = 285;
        _mainSplit.Panel1MinSize = 220;
        _mainSplit.Panel2MinSize = 600;
        Controls.Add(_mainSplit);

        BuildObjectBrowser();
        _workspace.Dock = DockStyle.Fill;
        _workspace.Padding = new Point(16, 6);
        _workspace.TabPages.AddRange([_browseTab, _sqlTab, _schemaTab]);
        _mainSplit.Panel2.Controls.Add(_workspace);
        BuildBrowseTab();
        BuildSqlTab();
        BuildSchemaTab();
    }

    void BuildObjectBrowser()
    {
        var header = new Panel { Dock = DockStyle.Top, Height = 92, Padding = new Padding(14, 12, 14, 8) };
        var title = new Label { Text = "DATABASE OBJECTS", Dock = DockStyle.Top, Height = 25, Font = new Font(Font, FontStyle.Bold) };
        _objectFilter.Dock = DockStyle.Bottom;
        _objectFilter.PlaceholderText = "Filter tables, views, indexes…";
        _objectFilter.BorderStyle = BorderStyle.FixedSingle;
        _objectFilter.TextChanged += (_, _) =>
        {
            _schemaFilterTimer.Stop();
            _schemaFilterTimer.Start();
        };
        header.Controls.Add(_objectFilter);
        header.Controls.Add(title);
        _objectTree.Dock = DockStyle.Fill;
        _objectTree.BorderStyle = BorderStyle.None;
        _objectTree.HideSelection = false;
        _objectTree.ItemHeight = 28;
        _objectTree.AfterSelect += async (_, _) => await ObjectSelectedAsync();
        _objectTree.NodeMouseDoubleClick += async (_, e) =>
        {
            if (e.Node?.Tag is DbObject obj && (obj.Type is "table" or "view"))
                await LoadObjectAsync(obj.Name, obj.Type);
        };
        var open = MakePrimaryButton("Browse selected object");
        open.Dock = DockStyle.Bottom;
        open.Height = 42;
        open.AutoSize = false;
        open.Margin = new Padding(12);
        open.Click += async (_, _) => await OpenSelectedObjectAsync();
        var footer = new Panel { Dock = DockStyle.Bottom, Height = 62, Padding = new Padding(12, 8, 12, 12) };
        footer.Controls.Add(open);
        _mainSplit.Panel1.Controls.Add(_objectTree);
        _mainSplit.Panel1.Controls.Add(footer);
        _mainSplit.Panel1.Controls.Add(header);
    }

    void BuildBrowseTab()
    {
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, Padding = new Padding(10) };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 45));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 43));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));

        _tableTitle.Text = "Select a table or view from the object browser";
        _tableTitle.Dock = DockStyle.Fill;
        _tableTitle.Font = new Font(Font.FontFamily, 13, FontStyle.Bold);
        _tableTitle.TextAlign = ContentAlignment.MiddleLeft;
        root.Controls.Add(_tableTitle, 0, 0);

        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, AutoScroll = true };
        actions.Controls.Add(ActionButton("Add row", async () => await AddRowAsync(), primary: true));
        actions.Controls.Add(ActionButton("Edit row", async () => await EditRowAsync()));
        actions.Controls.Add(ActionButton("Duplicate", async () => await DuplicateRowAsync()));
        actions.Controls.Add(ActionButton("Delete", async () => await DeleteRowsAsync()));
        actions.Controls.Add(ActionButton("Copy", () => { CopyRows(); return Task.CompletedTask; }));
        actions.Controls.Add(ActionButton("Export CSV", () => { ExportVisibleCsv(); return Task.CompletedTask; }));
        actions.Controls.Add(ActionButton("Export JSON", () => { ExportVisibleJson(); return Task.CompletedTask; }));
        root.Controls.Add(actions, 0, 1);

        var filters = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4 };
        filters.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 85));
        filters.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        filters.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
        filters.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
        filters.Controls.Add(new Label { Text = "SQL WHERE", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 0);
        _whereBox.Dock = DockStyle.Fill;
        _whereBox.PlaceholderText = "Example: active = 1 AND name LIKE 'A%'";
        _whereBox.KeyDown += async (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; await LoadCurrentObjectAsync(0); } };
        filters.Controls.Add(_whereBox, 1, 0);
        filters.Controls.Add(ActionButton("Apply", async () => await LoadCurrentObjectAsync(0), primary: true), 2, 0);
        filters.Controls.Add(ActionButton("Clear", async () => { _whereBox.Clear(); await LoadCurrentObjectAsync(0); }), 3, 0);
        root.Controls.Add(filters, 0, 2);

        _dataGrid.ColumnHeaderMouseClick += async (_, e) =>
        {
            if (e.ColumnIndex < 0 || e.ColumnIndex >= _displayColumns.Count) return;
            var column = _displayColumns[e.ColumnIndex];
            if (_sortColumn == column) _sortDescending = !_sortDescending;
            else { _sortColumn = column; _sortDescending = false; }
            await LoadCurrentObjectAsync(0);
        };
        _dataGrid.CellDoubleClick += async (_, e) => { if (e.RowIndex >= 0) await EditRowAsync(e.RowIndex); };
        _dataGrid.CellFormatting += GridCellFormatting;
        root.Controls.Add(_dataGrid, 0, 3);

        var pager = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 7 };
        pager.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100));
        pager.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100));
        pager.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 95));
        pager.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 85));
        pager.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
        pager.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        pager.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _previousButton.Dock = DockStyle.Fill;
        _previousButton.Click += async (_, _) => await LoadCurrentObjectAsync(Math.Max(0, _page - 1));
        _nextButton.Dock = DockStyle.Fill;
        _nextButton.Click += async (_, _) => await LoadCurrentObjectAsync(_page + 1);
        _goToPage.Minimum = 1;
        _goToPage.Maximum = 1_000_000;
        _goToPage.Dock = DockStyle.Fill;
        _goToPage.KeyDown += async (_, e) => { if (e.KeyCode == Keys.Enter) await LoadCurrentObjectAsync((int)_goToPage.Value - 1); };
        pager.Controls.Add(_previousButton, 0, 0);
        pager.Controls.Add(_nextButton, 1, 0);
        pager.Controls.Add(new Label { Text = "Go to page", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleRight }, 2, 0);
        pager.Controls.Add(_goToPage, 3, 0);
        _pageLabel.Dock = DockStyle.Fill;
        _pageLabel.TextAlign = ContentAlignment.MiddleLeft;
        pager.Controls.Add(_pageLabel, 4, 0);
        _rowCountLabel.Dock = DockStyle.Fill;
        _rowCountLabel.TextAlign = ContentAlignment.MiddleRight;
        pager.Controls.Add(_rowCountLabel, 6, 0);
        root.Controls.Add(pager, 0, 4);
        _browseTab.Controls.Add(root);
    }

    Button ActionButton(string text, Func<Task> action, bool primary = false)
    {
        var button = primary ? MakePrimaryButton(text) : MakeButton(text);
        button.Click += async (_, _) => await SafeUiAsync(action);
        return button;
    }

    void BuildSqlTab()
    {
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Padding = new Padding(10) };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
        _runButton.Click += async (_, _) => await ExecuteSqlAsync(false);
        _cancelButton.Enabled = false;
        _cancelButton.Click += (_, _) => CancelQuery();
        actions.Controls.Add(_runButton);
        actions.Controls.Add(_cancelButton);
        actions.Controls.Add(ActionButton("Query plan", async () => await ExecuteSqlAsync(true)));
        actions.Controls.Add(ActionButton("Format", () => { FormatSql(); return Task.CompletedTask; }));
        actions.Controls.Add(ActionButton("Clear results", () => { ClearGrid(_resultGrid); return Task.CompletedTask; }));
        root.Controls.Add(actions, 0, 0);

        var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, SplitterDistance = 330, Panel1MinSize = 150, Panel2MinSize = 150 };
        _sqlEditor.Dock = DockStyle.Fill;
        _sqlEditor.BorderStyle = BorderStyle.None;
        _sqlEditor.AcceptsTab = true;
        _sqlEditor.Font = new Font("Cascadia Mono", 11f);
        _sqlEditor.Text = "-- Write SQL here. Select a statement or press F9 to run everything.\nSELECT sqlite_version() AS sqlite_version;";
        _sqlEditor.KeyDown += async (_, e) =>
        {
            if (e.KeyCode == Keys.F9)
            {
                e.SuppressKeyPress = true;
                await ExecuteSqlAsync(false);
            }
        };
        split.Panel1.Padding = new Padding(1);
        split.Panel1.Controls.Add(_sqlEditor);

        var output = new TabControl { Dock = DockStyle.Fill };
        var results = new TabPage("Results");
        _resultGrid.CellFormatting += GridCellFormatting;
        results.Controls.Add(_resultGrid);
        var history = new TabPage("History");
        _historyList.Dock = DockStyle.Fill;
        _historyList.BorderStyle = BorderStyle.None;
        _historyList.Font = new Font("Cascadia Mono", 9.5f);
        _historyList.DoubleClick += (_, _) =>
        {
            if (_historyList.SelectedItem is string sql) _sqlEditor.Text = sql;
        };
        history.Controls.Add(_historyList);
        var log = new TabPage("SQL log");
        _sqlLog.Dock = DockStyle.Fill;
        _sqlLog.ReadOnly = true;
        _sqlLog.BorderStyle = BorderStyle.None;
        _sqlLog.Font = new Font("Cascadia Mono", 9.5f);
        log.Controls.Add(_sqlLog);
        output.TabPages.AddRange([results, history, log]);
        split.Panel2.Controls.Add(output);
        root.Controls.Add(split, 0, 1);
        _sqlTab.Controls.Add(root);
    }

    void BuildSchemaTab()
    {
        var root = new TabControl { Dock = DockStyle.Fill, Padding = new Point(14, 6) };
        var columns = new TabPage("Columns");
        var indexes = new TabPage("Indexes");
        var foreign = new TabPage("Foreign keys");
        var definition = new TabPage("SQL definition");
        columns.Controls.Add(_columnsGrid);
        indexes.Controls.Add(_indexesGrid);
        foreign.Controls.Add(_foreignKeysGrid);
        _definitionBox.Dock = DockStyle.Fill;
        _definitionBox.ReadOnly = true;
        _definitionBox.BorderStyle = BorderStyle.None;
        _definitionBox.Font = new Font("Cascadia Mono", 10.5f);
        definition.Controls.Add(_definitionBox);
        root.TabPages.AddRange([columns, indexes, foreign, definition]);
        _schemaTab.Controls.Add(root);
    }

    void BuildStatusBar()
    {
        _status.Items.AddRange([_statusText, _statusRows, new ToolStripStatusLabel { Text = "  " }, _statusMode]);
        Controls.Add(_status);
    }

    async Task ChooseDatabaseAsync(bool create, bool readOnly = false)
    {
        using FileDialog dialog = create
            ? new SaveFileDialog { Title = "Create SQLite database", Filter = "SQLite database (*.sqlite;*.db)|*.sqlite;*.db|All files (*.*)|*.*", DefaultExt = "sqlite", AddExtension = true }
            : new OpenFileDialog { Title = readOnly ? "Open SQLite database read-only" : "Open SQLite database", Filter = "SQLite database (*.sqlite;*.sqlite3;*.db)|*.sqlite;*.sqlite3;*.db|All files (*.*)|*.*", CheckFileExists = true };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        await OpenDatabaseAsync(dialog.FileName, readOnly, create);
    }

    async Task OpenDatabaseAsync(string path, bool readOnly = false, bool create = false)
    {
        path = Path.GetFullPath(path);
        if (!create && !File.Exists(path))
        {
            MessageBox.Show(this, "The selected database does not exist.", "Open database", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        var fileMarkedReadOnly = false;
        if (!create && !readOnly)
        {
            try { fileMarkedReadOnly = (File.GetAttributes(path) & FileAttributes.ReadOnly) != 0; }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        if (fileMarkedReadOnly)
        {
            readOnly = true;
            MessageBox.Show(this, "The database file is marked read-only, so it will be opened safely in read-only mode.",
                "Read-only database", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        if (!await CloseDatabaseAsync()) return;

        try
        {
            var builder = new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = readOnly ? SqliteOpenMode.ReadOnly : (create ? SqliteOpenMode.ReadWriteCreate : SqliteOpenMode.ReadWrite),
                Cache = SqliteCacheMode.Shared,
                Pooling = false,
                DefaultTimeout = 5
            };
            _connection = new SqliteConnection(builder.ToString());
            await _connection.OpenAsync();
            _readOnly = readOnly;
            _databasePath = path;

            await ExecuteNonQueryDirectAsync("PRAGMA foreign_keys = ON");
            await ExecuteNonQueryDirectAsync("PRAGMA busy_timeout = 5000");
            if (!readOnly)
            {
                try { await ExecuteScalarDirectAsync("PRAGMA journal_mode = WAL"); }
                catch (SqliteException) { /* Some valid databases/media cannot use WAL. */ }
            }
            _dataVersion = Convert.ToInt32(await ExecuteScalarDirectAsync("PRAGMA data_version"), CultureInfo.InvariantCulture);
            Text = $"SQLiteStudio C# — {Path.GetFileName(path)}";
            _pathLabel.Text = path;
            _pathLabel.ToolTipText = path;
            SetStatus($"Opened {Path.GetFileName(path)}");
            UpdateConnectedState();
            await RefreshSchemaAsync();
        }
        catch (Exception ex)
        {
            await DisposeConnectionAsync();
            if (!readOnly && !create && ex is SqliteException sqlite && sqlite.SqliteErrorCode is 8 or 14)
            {
                MessageBox.Show(this, "The database could not be opened for writing. SQLiteStudio will try read-only mode.",
                    "Read-only fallback", MessageBoxButtons.OK, MessageBoxIcon.Information);
                await OpenDatabaseAsync(path, readOnly: true);
                return;
            }
            ShowError("Could not open database", ex);
        }
    }

    async Task<bool> CloseDatabaseAsync(bool prompt = true)
    {
        if (_connection is null) return true;
        if (prompt && _hasChanges)
        {
            var answer = MessageBox.Show(this, "There are uncommitted changes. Commit them before closing?",
                "Pending changes", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Warning);
            if (answer == DialogResult.Cancel) return false;
            if (answer == DialogResult.Yes && !await CommitAsync()) return false;
            if (answer == DialogResult.No) await RollbackAsync(refresh: false);
        }
        await DisposeConnectionAsync();
        _databasePath = null;
        _currentObject = null;
        _currentObjectType = null;
        _hasChanges = false;
        _readOnly = false;
        _objectTree.Nodes.Clear();
        ClearGrid(_dataGrid);
        ClearGrid(_resultGrid);
        ClearSchemaPanels();
        _tableTitle.Text = "Select a table or view from the object browser";
        _pathLabel.Text = "No database open";
        Text = "SQLiteStudio C#";
        UpdatePendingState();
        UpdateConnectedState();
        SetStatus("Ready");
        return true;
    }

    async Task DisposeConnectionAsync()
    {
        CancelQuery();
        if (_activeQueryTask is not null)
        {
            try { await _activeQueryTask; }
            catch (OperationCanceledException) { }
            catch (SqliteException) { }
        }
        if (_transaction is not null)
        {
            try { await _transaction.RollbackAsync(); } catch { }
            await _transaction.DisposeAsync();
            _transaction = null;
        }
        if (_connection is not null)
        {
            await _connection.CloseAsync();
            await _connection.DisposeAsync();
            _connection = null;
        }
    }

    async Task RefreshSchemaAsync(bool preserveSelection = false)
    {
        if (_connection is null) return;
        var selected = preserveSelection && _objectTree.SelectedNode?.Tag is DbObject chosen ? chosen.Name : _currentObject;
        var filter = _objectFilter.Text.Trim();
        var objects = new List<DbObject>();
        await using (var command = CreateCommand("SELECT type, name, tbl_name, COALESCE(sql, '') FROM sqlite_schema WHERE type IN ('table','view','index','trigger') AND name NOT LIKE 'sqlite_%' ORDER BY type, name"))
        await using (var reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                var obj = new DbObject(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3));
                if (filter.Length == 0 || obj.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) || obj.Table.Contains(filter, StringComparison.OrdinalIgnoreCase))
                    objects.Add(obj);
            }
        }

        _objectTree.BeginUpdate();
        _objectTree.Nodes.Clear();
        TreeNode? selectNode = null;
        foreach (var group in new[] { ("table", "Tables"), ("view", "Views"), ("index", "Indexes"), ("trigger", "Triggers") })
        {
            var matches = objects.Where(o => o.Type == group.Item1).ToList();
            if (matches.Count == 0) continue;
            var parent = new TreeNode($"{group.Item2}  ({matches.Count})") { NodeFont = _treeHeaderFont };
            foreach (var obj in matches)
            {
                var node = new TreeNode(obj.Name) { Tag = obj };
                parent.Nodes.Add(node);
                if (obj.Name == selected) selectNode = node;
            }
            _objectTree.Nodes.Add(parent);
            parent.Expand();
        }
        _objectTree.EndUpdate();
        if (selectNode is not null) _objectTree.SelectedNode = selectNode;
        SetStatus($"Loaded {objects.Count} database objects");
    }

    async Task ObjectSelectedAsync()
    {
        if (_objectTree.SelectedNode?.Tag is not DbObject obj) return;
        await LoadStructureAsync(obj);
    }

    async Task OpenSelectedObjectAsync()
    {
        if (_objectTree.SelectedNode?.Tag is not DbObject obj || obj.Type is not ("table" or "view"))
        {
            MessageBox.Show(this, "Select a table or view first.", "Browse data", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        await LoadObjectAsync(obj.Name, obj.Type);
    }

    async Task LoadObjectAsync(string name, string type)
    {
        _currentObject = name;
        _currentObjectType = type;
        _sortColumn = null;
        _sortDescending = false;
        _whereBox.Clear();
        _workspace.SelectedTab = _browseTab;
        await LoadCurrentObjectAsync(0);
    }

    async Task LoadCurrentObjectAsync(int? page = null)
    {
        if (_connection is null || _currentObject is null) return;
        if (page.HasValue) _page = Math.Max(0, page.Value);
        var table = Quote(_currentObject);
        var where = _whereBox.Text.Trim();
        var suffix = where.Length > 0 ? $" WHERE {where}" : "";
        try
        {
            var count = await ExecuteScalarAsync($"SELECT COUNT(*) FROM {table}{suffix}");
            _rowCount = Convert.ToInt64(count, CultureInfo.InvariantCulture);
            var pages = Math.Max(1, (long)Math.Ceiling(_rowCount / (double)PageSize));
            if (_page >= pages) _page = (int)pages - 1;

            var columns = await GetColumnsAsync(_currentObject);
            _displayColumns.Clear();
            // table_xinfo uses 1 for virtual-table-only hidden columns and 2/3 for
            // generated columns. SELECT * includes generated columns, but not kind 1.
            _displayColumns.AddRange(columns.Where(c => c.HiddenKind != 1).Select(c => c.Name));
            _keyColumns.Clear();
            _keyColumns.AddRange(columns.Where(c => c.PrimaryKeyOrder > 0).OrderBy(c => c.PrimaryKeyOrder).Select(c => c.Name));
            var useRowId = _keyColumns.Count == 0 && _currentObjectType == "table" && !await IsWithoutRowIdAsync(_currentObject);

            var order = _sortColumn is not null
                ? $" ORDER BY {Quote(_sortColumn)} {(_sortDescending ? "DESC" : "ASC")}" + StableOrderSuffix(_sortColumn, useRowId)
                : DefaultOrder(useRowId);
            var keyProjection = useRowId
                ? ", rowid AS " + Quote("__studio_key_0")
                : string.Concat(_keyColumns.Select((column, i) => $", {Quote(column)} AS {Quote($"__studio_key_{i}")}"));
            await using var command = CreateCommand($"SELECT *{keyProjection} FROM {table}{suffix}{order} LIMIT @limit OFFSET @offset");
            command.Parameters.AddWithValue("@limit", PageSize);
            command.Parameters.AddWithValue("@offset", (long)_page * PageSize);
            await using var reader = await command.ExecuteReaderAsync();

            ClearGrid(_dataGrid);
            _rowKeys.Clear();
            for (var i = 0; i < _displayColumns.Count; i++)
                AddGridColumn(_dataGrid, _displayColumns[i], _displayColumns[i]);
            while (await reader.ReadAsync())
            {
                var display = new object?[_displayColumns.Count];
                for (var i = 0; i < display.Length; i++) display[i] = DbValue(reader, i);
                var keyCount = useRowId ? 1 : _keyColumns.Count;
                var keys = new object?[keyCount];
                for (var i = 0; i < keyCount; i++) keys[i] = DbValue(reader, _displayColumns.Count + i);
                _rowKeys.Add(keys);
                _dataGrid.Rows.Add(display.Select(v => v ?? DBNull.Value).ToArray());
            }

            if (useRowId) _keyColumns.Add("rowid");
            _tableTitle.Text = $"{_currentObject}   ·   {(_currentObjectType == "view" ? "VIEW · READ ONLY" : $"{_rowCount:N0} ROWS")}";
            _pageLabel.Text = $"Page {_page + 1} of {pages:N0}";
            _rowCountLabel.Text = $"Showing {_dataGrid.RowCount:N0} of {_rowCount:N0}";
            _previousButton.Enabled = _page > 0;
            _nextButton.Enabled = _page + 1 < pages;
            _goToPage.Value = Math.Min(_goToPage.Maximum, _page + 1);
            _statusRows.Text = $"{_dataGrid.RowCount:N0} displayed";
            SetStatus($"Loaded {_currentObject}");
        }
        catch (Exception ex)
        {
            ShowError("Could not load data. Check the WHERE expression.", ex);
        }
    }

    string StableOrderSuffix(string sortedColumn, bool useRowId)
    {
        var stable = _keyColumns.Where(k => !k.Equals(sortedColumn, StringComparison.OrdinalIgnoreCase)).Select(Quote).ToList();
        if (useRowId) stable.Add("rowid");
        return stable.Count > 0 ? ", " + string.Join(", ", stable) : "";
    }

    string DefaultOrder(bool useRowId)
    {
        if (_keyColumns.Count > 0) return " ORDER BY " + string.Join(", ", _keyColumns.Select(Quote));
        return useRowId ? " ORDER BY rowid" : "";
    }

    async Task LoadStructureAsync(DbObject obj)
    {
        if (_connection is null) return;
        ClearSchemaPanels();
        _definitionBox.Text = obj.Sql.Length > 0 ? obj.Sql : "-- No SQL definition is stored for this object.";
        if (obj.Type is not ("table" or "view"))
        {
            _workspace.SelectedTab = _schemaTab;
            return;
        }

        var columns = await GetColumnsAsync(obj.Name);
        FillGrid(_columnsGrid,
            ["Name", "Type", "Nullable", "Default", "Primary key", "Generated/hidden"],
            columns.Select(c => new object?[] { c.Name, c.Type, c.NotNull ? "No" : "Yes", c.DefaultValue, c.PrimaryKeyOrder == 0 ? "" : c.PrimaryKeyOrder, c.HiddenKind == 0 ? "No" : c.HiddenKind is 2 or 3 ? "Generated" : "Hidden" }));

        var indexRows = new List<object?[]>();
        await using (var command = CreateCommand($"PRAGMA index_list({Quote(obj.Name)})"))
        await using (var reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                var indexName = reader.GetString(1);
                var indexColumns = new List<string>();
                await using var detail = CreateCommand($"PRAGMA index_info({Quote(indexName)})");
                await using var detailReader = await detail.ExecuteReaderAsync();
                while (await detailReader.ReadAsync()) indexColumns.Add(detailReader.IsDBNull(2) ? "<expression>" : detailReader.GetString(2));
                indexRows.Add([indexName, reader.GetInt64(2) != 0 ? "Yes" : "No", reader.GetString(3), reader.FieldCount > 4 && reader.GetInt64(4) != 0 ? "Yes" : "No", string.Join(", ", indexColumns)]);
            }
        }
        FillGrid(_indexesGrid, ["Name", "Unique", "Origin", "Partial", "Columns"], indexRows);

        var foreignRows = new List<object?[]>();
        await using (var command = CreateCommand($"PRAGMA foreign_key_list({Quote(obj.Name)})"))
        await using (var reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
                foreignRows.Add([reader.GetInt64(0), reader.GetString(3), reader.GetString(2), reader.IsDBNull(4) ? "" : reader.GetString(4), reader.GetString(5), reader.GetString(6)]);
        }
        FillGrid(_foreignKeysGrid, ["ID", "From", "Referenced table", "To", "On update", "On delete"], foreignRows);
    }

    async Task<List<ColumnInfo>> GetColumnsAsync(string table)
    {
        var columns = new List<ColumnInfo>();
        await using var command = CreateCommand($"PRAGMA table_xinfo({Quote(table)})");
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            columns.Add(new ColumnInfo(
                reader.GetString(1),
                reader.IsDBNull(2) ? "" : reader.GetString(2),
                reader.GetInt64(3) != 0,
                reader.IsDBNull(4) ? null : reader.GetValue(4),
                Convert.ToInt32(reader.GetInt64(5)),
                reader.FieldCount > 6 ? Convert.ToInt32(reader.GetInt64(6)) : 0));
        return columns;
    }

    async Task<bool> IsWithoutRowIdAsync(string table)
    {
        var sql = Convert.ToString(await ExecuteScalarAsync("SELECT sql FROM sqlite_schema WHERE type='table' AND name=@name", ("@name", table)), CultureInfo.InvariantCulture) ?? "";
        return Regex.IsMatch(sql, @"\bWITHOUT\s+ROWID\b", RegexOptions.IgnoreCase);
    }

    async Task AddRowAsync()
    {
        if (!CanEditCurrent()) return;
        var columns = (await GetColumnsAsync(_currentObject!)).Where(c => c.HiddenKind == 0).ToList();
        using var dialog = new RecordEditorDialog("Add row", columns, null, allowDefault: true, _dark);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        var included = columns.Zip(dialog.Values).Where(pair => pair.Second.Kind != EditorValueKind.Default).ToList();
        var names = string.Join(", ", included.Select(x => Quote(x.First.Name)));
        var parameters = string.Join(", ", included.Select((_, i) => $"@v{i}"));
        var sql = included.Count == 0
            ? $"INSERT INTO {Quote(_currentObject!)} DEFAULT VALUES"
            : $"INSERT INTO {Quote(_currentObject!)} ({names}) VALUES ({parameters})";
        await EnsureTransactionAsync();
        await using var command = CreateCommand(sql);
        for (var i = 0; i < included.Count; i++) command.Parameters.AddWithValue($"@v{i}", included[i].Second.Value ?? DBNull.Value);
        await command.ExecuteNonQueryAsync();
        MarkChanged("Row added");
        await LoadCurrentObjectAsync(_page);
    }

    async Task EditRowAsync(int? explicitRow = null)
    {
        if (!CanEditCurrent()) return;
        var rowIndex = explicitRow ?? SelectedRowIndex();
        if (rowIndex is null) { InformSelectRow(); return; }
        var columns = (await GetColumnsAsync(_currentObject!)).Where(c => c.HiddenKind == 0).ToList();
        var values = columns.Select(c => _dataGrid.Rows[rowIndex.Value].Cells[_displayColumns.IndexOf(c.Name)].Value).ToArray();
        using var dialog = new RecordEditorDialog("Edit row", columns, values, allowDefault: false, _dark);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        await EnsureTransactionAsync();
        var assignments = string.Join(", ", columns.Select((c, i) => $"{Quote(c.Name)}=@v{i}"));
        await using var command = CreateCommand($"UPDATE {Quote(_currentObject!)} SET {assignments} WHERE {KeyPredicate()}");
        for (var i = 0; i < columns.Count; i++) command.Parameters.AddWithValue($"@v{i}", dialog.Values[i].Value ?? DBNull.Value);
        AddKeyParameters(command, _rowKeys[rowIndex.Value]);
        var changed = await command.ExecuteNonQueryAsync();
        if (changed != 1) throw new InvalidOperationException($"Expected to update one row, but SQLite reported {changed}. The data was refreshed to avoid editing the wrong row.");
        MarkChanged("Row updated");
        await LoadCurrentObjectAsync(_page);
    }

    async Task DuplicateRowAsync()
    {
        if (!CanEditCurrent()) return;
        var rowIndex = SelectedRowIndex();
        if (rowIndex is null) { InformSelectRow(); return; }
        var columns = (await GetColumnsAsync(_currentObject!)).Where(c => c.HiddenKind == 0).ToList();
        var insertColumns = columns.Where(c => !(c.PrimaryKeyOrder > 0 && c.Type.Contains("INT", StringComparison.OrdinalIgnoreCase))).ToList();
        var values = insertColumns.Select(c => _dataGrid.Rows[rowIndex.Value].Cells[_displayColumns.IndexOf(c.Name)].Value).ToArray();
        using var dialog = new RecordEditorDialog("Duplicate row", insertColumns, values, allowDefault: true, _dark);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        var included = insertColumns.Zip(dialog.Values).Where(p => p.Second.Kind != EditorValueKind.Default).ToList();
        await EnsureTransactionAsync();
        var sql = included.Count == 0
            ? $"INSERT INTO {Quote(_currentObject!)} DEFAULT VALUES"
            : $"INSERT INTO {Quote(_currentObject!)} ({string.Join(", ", included.Select(p => Quote(p.First.Name)))}) VALUES ({string.Join(", ", included.Select((_, i) => $"@v{i}"))})";
        await using var command = CreateCommand(sql);
        for (var i = 0; i < included.Count; i++) command.Parameters.AddWithValue($"@v{i}", included[i].Second.Value ?? DBNull.Value);
        await command.ExecuteNonQueryAsync();
        MarkChanged("Row duplicated");
        await LoadCurrentObjectAsync(_page);
    }

    async Task DeleteRowsAsync()
    {
        if (!CanEditCurrent()) return;
        var indices = _dataGrid.SelectedRows.Cast<DataGridViewRow>().Select(r => r.Index).Where(i => i >= 0 && i < _rowKeys.Count).Distinct().OrderByDescending(i => i).ToList();
        if (indices.Count == 0) { InformSelectRow(); return; }
        if (MessageBox.Show(this, $"Delete {indices.Count:N0} selected row(s)?\n\nThe change remains reversible until you commit.",
            "Delete rows", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        await EnsureTransactionAsync();
        var deleted = 0;
        foreach (var index in indices)
        {
            await using var command = CreateCommand($"DELETE FROM {Quote(_currentObject!)} WHERE {KeyPredicate()}");
            AddKeyParameters(command, _rowKeys[index]);
            deleted += await command.ExecuteNonQueryAsync();
        }
        if (deleted != indices.Count) throw new InvalidOperationException($"Requested {indices.Count} deletes, but SQLite reported {deleted}. The table will be refreshed.");
        MarkChanged($"Deleted {deleted:N0} row(s)");
        await LoadCurrentObjectAsync(_page);
    }

    string KeyPredicate()
    {
        if (_keyColumns.Count == 0) throw new InvalidOperationException("This object has no usable primary key or rowid and cannot be edited safely.");
        return string.Join(" AND ", _keyColumns.Select((column, i) => $"{Quote(column)} IS @k{i}"));
    }

    void AddKeyParameters(SqliteCommand command, object?[] values)
    {
        if (values.Length != _keyColumns.Count) throw new InvalidOperationException("The stored row identity no longer matches the table schema.");
        for (var i = 0; i < values.Length; i++) command.Parameters.AddWithValue($"@k{i}", values[i] ?? DBNull.Value);
    }

    bool CanEditCurrent()
    {
        if (_connection is null || _currentObject is null) { MessageBox.Show(this, "Open a table first."); return false; }
        if (_readOnly) { MessageBox.Show(this, "This database was opened read-only."); return false; }
        if (_currentObjectType != "table") { MessageBox.Show(this, "Views are read-only in the data editor. Use SQL if the view has INSTEAD OF triggers."); return false; }
        if (_keyColumns.Count == 0) { MessageBox.Show(this, "This table has neither a primary key nor an accessible rowid, so editing is disabled to prevent wrong-row updates."); return false; }
        return true;
    }

    int? SelectedRowIndex() => _dataGrid.SelectedRows.Count > 0 ? _dataGrid.SelectedRows[0].Index : _dataGrid.CurrentCell?.RowIndex;

    void InformSelectRow() => MessageBox.Show(this, "Select a row first.", "Rows", MessageBoxButtons.OK, MessageBoxIcon.Information);

    async Task EnsureTransactionAsync()
    {
        if (_connection is null) throw new InvalidOperationException("No database is open.");
        if (_readOnly) throw new InvalidOperationException("The database is read-only.");
        _transaction ??= (SqliteTransaction)await _connection.BeginTransactionAsync();
    }

    async Task<bool> CommitAsync()
    {
        if (_transaction is null) { SetStatus("Nothing to commit"); return true; }
        try
        {
            var currentVersion = Convert.ToInt32(await ExecuteScalarDirectAsync("PRAGMA data_version"), CultureInfo.InvariantCulture);
            if (currentVersion != _dataVersion)
            {
                var answer = MessageBox.Show(this, "Another connection changed this database after it was opened. Committing may overwrite assumptions made by your edits. Continue?",
                    "External database change", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                if (answer != DialogResult.Yes) return false;
            }
            await _transaction.CommitAsync();
            await _transaction.DisposeAsync();
            _transaction = null;
            _hasChanges = false;
            _dataVersion = Convert.ToInt32(await ExecuteScalarDirectAsync("PRAGMA data_version"), CultureInfo.InvariantCulture);
            UpdatePendingState();
            SetStatus("Changes committed");
            await RefreshSchemaAsync(preserveSelection: true);
            return true;
        }
        catch (Exception ex) { ShowError("Commit failed", ex); return false; }
    }

    async Task RollbackAsync(bool refresh = true)
    {
        if (_transaction is null) { SetStatus("Nothing to roll back"); return; }
        await _transaction.RollbackAsync();
        await _transaction.DisposeAsync();
        _transaction = null;
        _hasChanges = false;
        UpdatePendingState();
        SetStatus("Changes rolled back");
        if (refresh)
        {
            await RefreshSchemaAsync(preserveSelection: true);
            await LoadCurrentObjectAsync(_page);
        }
    }

    void MarkChanged(string message)
    {
        _hasChanges = true;
        UpdatePendingState();
        SetStatus(message + " — pending commit");
    }

    async Task ExecuteSqlAsync(bool explain)
    {
        if (_connection is null) { MessageBox.Show(this, "Open a database first."); return; }
        var text = _sqlEditor.SelectedText.Length > 0 ? _sqlEditor.SelectedText : _sqlEditor.Text;
        if (string.IsNullOrWhiteSpace(text)) return;
        var statementCount = SplitStatements(text).Count();
        if (statementCount == 0) return;
        if (explain)
        {
            if (statementCount != 1) { MessageBox.Show(this, "Select one statement for a query plan."); return; }
            text = "EXPLAIN QUERY PLAN " + text.Trim().TrimEnd(';');
        }
        var mutating = ContainsMutatingSql(text);
        if (_readOnly && mutating)
        {
            MessageBox.Show(this, "The database is read-only. Mutating statements were not run.");
            return;
        }

        var prepared = PrepareParameters(text);
        Dictionary<string, object?> parameters = [];
        if (prepared.Names.Count > 0)
        {
            using var dialog = new ParameterDialog(prepared.Names, _dark);
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            parameters = dialog.Values;
        }

        ToggleQueryRunning(true);
        _queryCancellation = new CancellationTokenSource();
        var stopwatch = Stopwatch.StartNew();
        try
        {
            _activeQueryTask = Task.Run(() => ExecuteSqlBatchAsync(prepared.Sql, parameters, mutating, _queryCancellation.Token));
            var result = await _activeQueryTask;
            stopwatch.Stop();
            ClearGrid(_resultGrid);
            for (var i = 0; i < result.Columns.Count; i++) AddGridColumn(_resultGrid, $"c{i}", result.Columns[i]);
            foreach (var row in result.Rows) _resultGrid.Rows.Add(row.Select(v => v ?? DBNull.Value).ToArray());
            if (mutating) MarkChanged($"SQL completed; {result.Affected:N0} row(s) affected");
            var summary = $"{DateTime.Now:HH:mm:ss}  {statementCount} statement(s), {stopwatch.Elapsed.TotalMilliseconds:N0} ms" + (result.Truncated ? $", results limited to {ResultLimit:N0}" : "");
            AppendLog(summary + "\n" + text.Trim() + "\n");
            AddHistory(text.Trim());
            SetStatus(summary);
            _statusRows.Text = _resultGrid.RowCount > 0 ? $"{_resultGrid.RowCount:N0} result rows" + (result.Truncated ? " (truncated)" : "") : "";
            await RefreshSchemaAsync(preserveSelection: true);
            if (_currentObject is not null) await LoadCurrentObjectAsync(_page);
        }
        catch (OperationCanceledException)
        {
            AppendLog($"{DateTime.Now:HH:mm:ss}  Query cancelled after {stopwatch.Elapsed.TotalMilliseconds:N0} ms\n");
            if (_transaction is not null && _connection is not null && !_connection.InTransaction)
            {
                // SQLite rolls back an explicit transaction when a mutating
                // statement is interrupted. Keep the UI state in sync.
                _transaction.MarkCompleted();
                await _transaction.DisposeAsync();
                _transaction = null;
                _hasChanges = false;
                UpdatePendingState();
                SetStatus("Query cancelled; the interrupted transaction was rolled back");
            }
            else SetStatus("Query cancelled");
        }
        catch (Exception ex)
        {
            AppendLog($"{DateTime.Now:HH:mm:ss}  ERROR: {ex.Message}\n");
            ShowError("SQL execution failed", ex);
        }
        finally
        {
            _runningCommand = null;
            _activeQueryTask = null;
            _queryCancellation?.Dispose();
            _queryCancellation = null;
            ToggleQueryRunning(false);
        }
    }

    async Task<SqlBatchResult> ExecuteSqlBatchAsync(string sql, IReadOnlyDictionary<string, object?> parameters, bool mutating, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (mutating) await EnsureTransactionAsync();
        await using var command = CreateCommand(sql);
        foreach (var parameter in parameters)
            command.Parameters.AddWithValue(parameter.Key, parameter.Value ?? DBNull.Value);
        _runningCommand = command;
        var columns = new List<string>();
        var rows = new List<object?[]>();
        var truncated = false;
        await using var reader = await command.ExecuteReaderAsync(token);
        do
        {
            token.ThrowIfCancellationRequested();
            if (reader.FieldCount == 0) continue;
            columns.Clear();
            rows.Clear();
            for (var i = 0; i < reader.FieldCount; i++) columns.Add(reader.GetName(i));
            var seen = 0;
            while (await reader.ReadAsync(token))
            {
                if (seen < ResultLimit)
                {
                    var row = new object?[reader.FieldCount];
                    for (var i = 0; i < row.Length; i++) row[i] = DbValue(reader, i);
                    rows.Add(row);
                }
                else truncated = true;
                seen++;
            }
        } while (await reader.NextResultAsync(token));
        return new SqlBatchResult(columns, rows, Math.Max(0, reader.RecordsAffected), truncated);
    }

    static bool ContainsMutatingSql(string sql)
    {
        var scrubbed = Regex.Replace(sql,
            """'(?:''|[^'])*'|"(?:[^"]|"")*"|--[^\r\n]*(?:\r?\n|$)|/\*.*?\*/""",
            " ", RegexOptions.Singleline);
        return Regex.IsMatch(scrubbed, @"\b(INSERT|UPDATE|DELETE|REPLACE|CREATE|DROP|ALTER|VACUUM|ANALYZE|REINDEX|ATTACH|DETACH)\b", RegexOptions.IgnoreCase)
            || Regex.IsMatch(scrubbed, @"\bPRAGMA\s+[A-Za-z_][A-Za-z0-9_]*\s*=", RegexOptions.IgnoreCase);
    }

    static PreparedSql PrepareParameters(string sql)
    {
        var names = new List<string>();
        var positional = 0;
        var pattern = new Regex("""'(?:''|[^'])*'|"(?:[^"]|"")*"|--[^\r\n]*(?:\r?\n|$)|/\*.*?\*/|(?<named>[:@$][A-Za-z_][A-Za-z0-9_]*)|(?<pos>\?)""", RegexOptions.Singleline);
        var rewritten = pattern.Replace(sql, match =>
        {
            if (match.Groups["named"].Success)
            {
                var name = match.Value;
                if (!names.Contains(name, StringComparer.Ordinal)) names.Add(name);
                return name;
            }
            if (match.Groups["pos"].Success)
            {
                var name = $"@__pos{positional++}";
                names.Add(name);
                return name;
            }
            return match.Value;
        });
        return new PreparedSql(rewritten, names);
    }

    static IEnumerable<string> SplitStatements(string script)
    {
        var buffer = new StringBuilder();
        var single = false; var dbl = false; var bracket = false; var lineComment = false; var blockComment = false;
        for (var i = 0; i < script.Length; i++)
        {
            var c = script[i];
            var next = i + 1 < script.Length ? script[i + 1] : '\0';
            if (lineComment)
            {
                buffer.Append(c);
                if (c is '\n' or '\r') lineComment = false;
                continue;
            }
            if (blockComment)
            {
                buffer.Append(c);
                if (c == '*' && next == '/') { buffer.Append(next); i++; blockComment = false; }
                continue;
            }
            if (!single && !dbl && !bracket && c == '-' && next == '-') { buffer.Append(c).Append(next); i++; lineComment = true; continue; }
            if (!single && !dbl && !bracket && c == '/' && next == '*') { buffer.Append(c).Append(next); i++; blockComment = true; continue; }
            if (!dbl && !bracket && c == '\'')
            {
                buffer.Append(c);
                if (single && next == '\'') { buffer.Append(next); i++; }
                else single = !single;
                continue;
            }
            if (!single && !bracket && c == '"')
            {
                buffer.Append(c);
                if (dbl && next == '"') { buffer.Append(next); i++; }
                else dbl = !dbl;
                continue;
            }
            if (!single && !dbl && c == '[') bracket = true;
            if (bracket && c == ']') bracket = false;
            if (!single && !dbl && !bracket && c == ';')
            {
                var statement = buffer.ToString().Trim();
                if (statement.Length > 0) yield return statement;
                buffer.Clear();
            }
            else buffer.Append(c);
        }
        var remaining = buffer.ToString().Trim();
        if (remaining.Length > 0) yield return remaining;
    }

    void CancelQuery()
    {
        try { _queryCancellation?.Cancel(); _runningCommand?.Cancel(); } catch { }
    }

    void ToggleQueryRunning(bool running)
    {
        _runButton.Enabled = !running;
        _cancelButton.Enabled = running;
        _objectTree.Enabled = !running;
        _browseTab.Enabled = !running;
        _schemaTab.Enabled = !running;
        foreach (ToolStripItem item in _toolbar.Items)
            if (item is ToolStripButton) item.Enabled = !running;
        UseWaitCursor = running;
    }

    void FormatSql()
    {
        var source = _sqlEditor.SelectedText.Length > 0 ? _sqlEditor.SelectedText : _sqlEditor.Text;
        var result = Regex.Replace(source.Trim(), @"\s+", " ");
        var clauses = new[] { "SELECT", "FROM", "WHERE", "GROUP BY", "HAVING", "ORDER BY", "LIMIT", "OFFSET", "UNION", "INSERT INTO", "VALUES", "UPDATE", "SET", "DELETE FROM", "RETURNING" };
        foreach (var clause in clauses)
            result = Regex.Replace(result, $@"\s*\b{Regex.Escape(clause)}\b\s*", "\n" + clause + " ", RegexOptions.IgnoreCase);
        result = Regex.Replace(result, @"\s+\b(AND|OR)\b\s+", "\n  $1 ", RegexOptions.IgnoreCase).Trim();
        if (_sqlEditor.SelectedText.Length > 0) _sqlEditor.SelectedText = result;
        else _sqlEditor.Text = result;
    }

    async Task RunCheckAsync(string sql, string title)
    {
        if (_connection is null) return;
        var rows = new List<object?[]>();
        var names = new List<string>();
        await using var command = CreateCommand(sql);
        await using var reader = await command.ExecuteReaderAsync();
        for (var i = 0; i < reader.FieldCount; i++) names.Add(reader.GetName(i));
        while (await reader.ReadAsync())
        {
            var row = new object?[reader.FieldCount];
            for (var i = 0; i < row.Length; i++) row[i] = DbValue(reader, i);
            rows.Add(row);
        }
        using var dialog = new ResultDialog(title, names, rows, _dark);
        dialog.ShowDialog(this);
    }

    async Task ExecuteMaintenanceAsync(string sql, string success)
    {
        if (_connection is null || _readOnly) return;
        if (_transaction is not null) { MessageBox.Show(this, "Commit or roll back pending changes first."); return; }
        await ExecuteNonQueryDirectAsync(sql);
        SetStatus(success);
    }

    async Task VacuumAsync()
    {
        if (_connection is null || _readOnly) return;
        if (_transaction is not null) { MessageBox.Show(this, "Commit or roll back pending changes before vacuuming."); return; }
        if (MessageBox.Show(this, "VACUUM rewrites the database and can take time. Continue?", "Vacuum", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        await ExecuteNonQueryDirectAsync("VACUUM");
        SetStatus("Vacuum complete");
    }

    void CopyRows()
    {
        var grid = _workspace.SelectedTab == _sqlTab ? _resultGrid : _dataGrid;
        var rows = grid.SelectedRows.Cast<DataGridViewRow>().OrderBy(r => r.Index).ToList();
        if (rows.Count == 0) return;
        var text = new StringBuilder();
        text.AppendLine(string.Join('\t', grid.Columns.Cast<DataGridViewColumn>().Select(c => c.HeaderText)));
        foreach (var row in rows)
            text.AppendLine(string.Join('\t', row.Cells.Cast<DataGridViewCell>().Select(c => ExportText(c.Value))));
        Clipboard.SetText(text.ToString());
        SetStatus($"Copied {rows.Count:N0} row(s)");
    }

    void ExportVisibleCsv() => ExportGrid(_workspace.SelectedTab == _sqlTab ? _resultGrid : _dataGrid, "CSV files (*.csv)|*.csv", "csv", WriteCsv);
    void ExportVisibleJson() => ExportGrid(_workspace.SelectedTab == _sqlTab ? _resultGrid : _dataGrid, "JSON files (*.json)|*.json", "json", WriteJson);

    void ExportGrid(DataGridView grid, string filter, string extension, Action<Stream, DataGridView> writer)
    {
        if (grid.ColumnCount == 0) return;
        using var dialog = new SaveFileDialog { Filter = filter, DefaultExt = extension, AddExtension = true };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            using var stream = File.Create(dialog.FileName);
            writer(stream, grid);
            SetStatus($"Exported {grid.RowCount:N0} row(s) to {Path.GetFileName(dialog.FileName)}");
        }
        catch (Exception ex) { ShowError("Export failed", ex); }
    }

    static void WriteCsv(Stream stream, DataGridView grid)
    {
        using var writer = new StreamWriter(stream, new UTF8Encoding(true));
        writer.WriteLine(string.Join(',', grid.Columns.Cast<DataGridViewColumn>().Select(c => Csv(c.HeaderText))));
        foreach (DataGridViewRow row in grid.Rows)
            writer.WriteLine(string.Join(',', row.Cells.Cast<DataGridViewCell>().Select(c => Csv(c.Value is null ? "" : ExportText(c.Value)))));
    }

    static void WriteJson(Stream stream, DataGridView grid)
    {
        using var json = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
        json.WriteStartArray();
        foreach (DataGridViewRow row in grid.Rows)
        {
            json.WriteStartObject();
            for (var i = 0; i < grid.ColumnCount; i++)
            {
                json.WritePropertyName(grid.Columns[i].HeaderText);
                WriteJsonValue(json, row.Cells[i].Value);
            }
            json.WriteEndObject();
        }
        json.WriteEndArray();
    }

    static void WriteJsonValue(Utf8JsonWriter writer, object? value)
    {
        switch (value)
        {
            case null or DBNull: writer.WriteNullValue(); break;
            case long v: writer.WriteNumberValue(v); break;
            case int v: writer.WriteNumberValue(v); break;
            case double v when double.IsFinite(v): writer.WriteNumberValue(v); break;
            case float v when float.IsFinite(v): writer.WriteNumberValue(v); break;
            case decimal v: writer.WriteNumberValue(v); break;
            case bool v: writer.WriteBooleanValue(v); break;
            case byte[] v: writer.WriteStringValue(Convert.ToBase64String(v)); break;
            default: writer.WriteStringValue(Convert.ToString(value, CultureInfo.InvariantCulture)); break;
        }
    }

    static string Csv(string value) => value.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? '"' + value.Replace("\"", "\"\"") + '"' : value;
    static string ExportText(object? value) => value switch { null or DBNull => "", byte[] bytes => Convert.ToHexString(bytes), _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "" };

    SqliteCommand CreateCommand(string sql)
    {
        if (_connection is null) throw new InvalidOperationException("No database is open.");
        var command = _connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = 30;
        if (_transaction is not null) command.Transaction = _transaction;
        return command;
    }

    async Task<object?> ExecuteScalarAsync(string sql, params (string Name, object? Value)[] parameters)
    {
        await using var command = CreateCommand(sql);
        foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);
        return await command.ExecuteScalarAsync();
    }

    async Task<object?> ExecuteScalarDirectAsync(string sql)
    {
        if (_connection is null) throw new InvalidOperationException("No database is open.");
        await using var command = _connection.CreateCommand();
        command.CommandText = sql;
        if (_transaction is not null) command.Transaction = _transaction;
        return await command.ExecuteScalarAsync();
    }

    async Task ExecuteNonQueryDirectAsync(string sql)
    {
        if (_connection is null) throw new InvalidOperationException("No database is open.");
        await using var command = _connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    static string Quote(string name) => '"' + name.Replace("\"", "\"\"") + '"';

    static object? DbValue(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetValue(ordinal);

    static void AddGridColumn(DataGridView grid, string name, string header)
    {
        var column = new DataGridViewTextBoxColumn
        {
            Name = name,
            HeaderText = header,
            SortMode = DataGridViewColumnSortMode.Programmatic,
            MinimumWidth = 80,
            Width = Math.Clamp(header.Length * 10 + 45, 100, 260)
        };
        grid.Columns.Add(column);
    }

    static void FillGrid(DataGridView grid, IEnumerable<string> columns, IEnumerable<object?[]> rows)
    {
        ClearGrid(grid);
        foreach (var column in columns) AddGridColumn(grid, column, column);
        foreach (var row in rows) grid.Rows.Add(row.Select(v => v ?? DBNull.Value).ToArray());
    }

    static void ClearGrid(DataGridView grid)
    {
        grid.Rows.Clear();
        grid.Columns.Clear();
    }

    void ClearSchemaPanels()
    {
        ClearGrid(_columnsGrid);
        ClearGrid(_indexesGrid);
        ClearGrid(_foreignKeysGrid);
        _definitionBox.Clear();
    }

    void GridCellFormatting(object? sender, DataGridViewCellFormattingEventArgs e)
    {
        if (e.Value is null or DBNull)
        {
            e.Value = "NULL";
            e.CellStyle.ForeColor = _dark ? Color.FromArgb(150, 160, 172) : Color.FromArgb(110, 118, 128);
            e.CellStyle.Font = _nullFont;
            e.FormattingApplied = true;
        }
        else if (e.Value is byte[] bytes)
        {
            e.Value = $"BLOB · {bytes.Length:N0} bytes · {Convert.ToHexString(bytes.AsSpan(0, Math.Min(bytes.Length, 12)))}{(bytes.Length > 12 ? "…" : "")}";
            e.FormattingApplied = true;
        }
    }

    void AddHistory(string sql)
    {
        if (_history.Count > 0 && _history[0] == sql) return;
        _history.Insert(0, sql);
        if (_history.Count > 100) _history.RemoveAt(_history.Count - 1);
        _historyList.Items.Clear();
        foreach (var item in _history) _historyList.Items.Add(item);
    }

    void AppendLog(string message)
    {
        _sqlLog.AppendText(message + Environment.NewLine);
        _sqlLog.SelectionStart = _sqlLog.TextLength;
        _sqlLog.ScrollToCaret();
    }

    void SetStatus(string message) => _statusText.Text = message;

    void UpdatePendingState()
    {
        _pendingLabel.Text = _hasChanges ? "● UNCOMMITTED CHANGES" : "";
        _statusMode.Text = _connection is null ? "No database" : _readOnly ? "Read only" : _hasChanges ? "Transaction open" : "Writable";
    }

    void UpdateConnectedState()
    {
        var connected = _connection is not null;
        foreach (ToolStripItem item in _toolbar.Items)
        {
            if (item.Text is "Refresh" or "Commit" or "Rollback") item.Enabled = connected;
        }
        _workspace.Enabled = connected;
        UpdatePendingState();
    }

    void ApplyTheme()
    {
        var background = _dark ? DarkBackground : LightBackground;
        var surface = _dark ? DarkSurface : Color.White;
        var text = _dark ? DarkText : Color.FromArgb(33, 37, 41);
        var muted = _dark ? Color.FromArgb(52, 56, 62) : Color.FromArgb(232, 235, 240);
        BackColor = background;
        ForeColor = text;
        _menu.BackColor = surface;
        _menu.ForeColor = text;
        _toolbar.BackColor = surface;
        _toolbar.ForeColor = text;
        _status.BackColor = surface;
        _status.ForeColor = text;
        _mainSplit.BackColor = muted;
        _mainSplit.Panel1.BackColor = surface;
        _mainSplit.Panel2.BackColor = background;
        StyleTree(_objectTree, surface, text);
        _objectFilter.BackColor = _dark ? Color.FromArgb(35, 38, 42) : Color.White;
        _objectFilter.ForeColor = text;
        _sqlEditor.BackColor = _dark ? Color.FromArgb(29, 31, 35) : Color.White;
        _sqlEditor.ForeColor = text;
        _definitionBox.BackColor = _sqlEditor.BackColor;
        _definitionBox.ForeColor = text;
        _sqlLog.BackColor = _sqlEditor.BackColor;
        _sqlLog.ForeColor = text;
        _historyList.BackColor = _sqlEditor.BackColor;
        _historyList.ForeColor = text;
        foreach (var grid in new[] { _dataGrid, _resultGrid, _columnsGrid, _indexesGrid, _foreignKeysGrid }) StyleGrid(grid, surface, text, muted);
        ApplyThemeRecursive(_workspace, background, surface, text);
        _runButton.BackColor = Accent;
        _runButton.ForeColor = Color.White;
        Invalidate(true);
    }

    static void StyleTree(TreeView tree, Color surface, Color text)
    {
        tree.BackColor = surface;
        tree.ForeColor = text;
        tree.LineColor = text;
    }

    static void StyleGrid(DataGridView grid, Color surface, Color text, Color muted)
    {
        grid.BackgroundColor = surface;
        grid.GridColor = muted;
        grid.DefaultCellStyle.BackColor = surface;
        grid.DefaultCellStyle.ForeColor = text;
        grid.DefaultCellStyle.SelectionBackColor = Accent;
        grid.DefaultCellStyle.SelectionForeColor = Color.White;
        grid.AlternatingRowsDefaultCellStyle.BackColor = surface == Color.White ? Color.FromArgb(248, 249, 251) : Color.FromArgb(46, 49, 54);
        grid.ColumnHeadersDefaultCellStyle.BackColor = muted;
        grid.ColumnHeadersDefaultCellStyle.ForeColor = text;
        grid.ColumnHeadersDefaultCellStyle.SelectionBackColor = muted;
    }

    void ApplyThemeRecursive(Control parent, Color background, Color surface, Color text)
    {
        foreach (Control control in parent.Controls)
        {
            switch (control)
            {
                case TabPage: control.BackColor = background; control.ForeColor = text; break;
                case TextBox box: box.BackColor = _dark ? Color.FromArgb(35, 38, 42) : Color.White; box.ForeColor = text; break;
                case Button button when button.BackColor != Accent:
                    button.BackColor = surface; button.ForeColor = text; button.FlatAppearance.BorderColor = _dark ? Color.FromArgb(82, 87, 94) : Color.FromArgb(196, 201, 208); break;
                case Label: control.ForeColor = text; control.BackColor = Color.Transparent; break;
                case Panel or TableLayoutPanel or FlowLayoutPanel: control.BackColor = Color.Transparent; control.ForeColor = text; break;
            }
            ApplyThemeRecursive(control, background, surface, text);
        }
    }

    async void HandleShortcut(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.F9) { e.SuppressKeyPress = true; _workspace.SelectedTab = _sqlTab; await ExecuteSqlAsync(false); }
        if (e.Control && e.KeyCode == Keys.C && (_dataGrid.Focused || _resultGrid.Focused)) { e.SuppressKeyPress = true; CopyRows(); }
    }

    void ShowShortcuts() => MessageBox.Show(this,
        "Ctrl+O   Open database\nCtrl+N   Create database\nCtrl+S   Commit changes\nCtrl+Shift+S   Roll back\nF5   Refresh schema\nF9   Run selected/all SQL\nCtrl+L   SQL workspace\nCtrl+E   Edit row\nCtrl+Insert   Add row\nDelete   Delete selected rows\nCtrl+T   Toggle theme",
        "Keyboard shortcuts", MessageBoxButtons.OK, MessageBoxIcon.Information);

    async void OnClosing(object? sender, FormClosingEventArgs e)
    {
        if (_connection is null)
        {
            _schemaFilterTimer.Dispose();
            _nullFont.Dispose();
            _treeHeaderFont.Dispose();
            return;
        }
        e.Cancel = true;
        FormClosing -= OnClosing;
        if (await CloseDatabaseAsync())
        {
            _schemaFilterTimer.Dispose();
            _nullFont.Dispose();
            _treeHeaderFont.Dispose();
            Close();
        }
        else FormClosing += OnClosing;
    }

    async Task SafeUiAsync(Func<Task> action)
    {
        try { await action(); }
        catch (Exception ex) { ShowError("Operation failed", ex); }
    }

    void ShowError(string title, Exception ex)
    {
        UseWaitCursor = false;
        MessageBox.Show(this, ex.Message, title, MessageBoxButtons.OK, MessageBoxIcon.Error);
        SetStatus(title + ": " + ex.Message);
    }

    sealed record DbObject(string Type, string Name, string Table, string Sql);
    sealed record ColumnInfo(string Name, string Type, bool NotNull, object? DefaultValue, int PrimaryKeyOrder, int HiddenKind);
    sealed record PreparedSql(string Sql, List<string> Names);
    sealed record SqlBatchResult(List<string> Columns, List<object?[]> Rows, int Affected, bool Truncated);
}

enum EditorValueKind { Value, Null, Default }
sealed record EditorValue(EditorValueKind Kind, object? Value);

sealed class RecordEditorDialog : Form
{
    readonly List<ColumnInfoView> _columns;
    readonly List<TextBox> _editors = [];
    readonly List<ComboBox> _modes = [];
    public List<EditorValue> Values { get; } = [];

    public RecordEditorDialog(string title, IEnumerable<object> columns, object?[]? values, bool allowDefault, bool dark)
        : this(title, columns.Select(c => ColumnInfoView.From(c)).ToList(), values, allowDefault, dark) { }

    RecordEditorDialog(string title, List<ColumnInfoView> columns, object?[]? values, bool allowDefault, bool dark)
    {
        _columns = columns;
        Text = title;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        Size = new Size(720, Math.Min(760, 155 + columns.Count * 43));
        MinimumSize = new Size(620, 330);
        Font = new Font("Segoe UI", 9.5f);
        BackColor = dark ? Color.FromArgb(30, 32, 36) : Color.FromArgb(245, 247, 250);
        ForeColor = dark ? Color.FromArgb(235, 238, 242) : Color.FromArgb(33, 37, 41);

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(16) };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 45));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 54));
        root.Controls.Add(new Label { Text = "Choose VALUE, NULL, or DEFAULT for each column.", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 0);
        var panel = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
        var fields = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 3, Padding = new Padding(0, 0, 12, 0) };
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 190));
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100));
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (var i = 0; i < columns.Count; i++)
        {
            var column = columns[i];
            fields.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
            fields.Controls.Add(new Label { Text = column.Name + (column.Type.Length > 0 ? $"  ·  {column.Type}" : ""), Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true }, 0, i);
            var mode = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
            mode.Items.Add("VALUE");
            mode.Items.Add("NULL");
            if (allowDefault) mode.Items.Add("DEFAULT");
            var value = values is not null && i < values.Length ? values[i] : null;
            mode.SelectedItem = values is null && allowDefault ? "DEFAULT" : value is null or DBNull ? "NULL" : "VALUE";
            var editor = new TextBox { Dock = DockStyle.Fill, Text = Display(value) };
            mode.SelectedIndexChanged += (_, _) => editor.Enabled = Equals(mode.SelectedItem, "VALUE");
            editor.Enabled = Equals(mode.SelectedItem, "VALUE");
            _modes.Add(mode);
            _editors.Add(editor);
            fields.Controls.Add(mode, 1, i);
            fields.Controls.Add(editor, 2, i);
        }
        panel.Controls.Add(fields);
        root.Controls.Add(panel, 0, 1);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(0, 10, 0, 0) };
        var save = new Button { Text = "Save", DialogResult = DialogResult.None, AutoSize = true, Height = 34, Padding = new Padding(14, 2, 14, 2), BackColor = Color.FromArgb(45, 112, 214), ForeColor = Color.White, FlatStyle = FlatStyle.Flat };
        save.Click += (_, _) => Accept();
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true, Height = 34, Padding = new Padding(14, 2, 14, 2), FlatStyle = FlatStyle.Flat };
        buttons.Controls.Add(save);
        buttons.Controls.Add(cancel);
        root.Controls.Add(buttons, 0, 2);
        Controls.Add(root);
        AcceptButton = save;
        CancelButton = cancel;
    }

    void Accept()
    {
        Values.Clear();
        try
        {
            for (var i = 0; i < _columns.Count; i++)
            {
                var mode = Convert.ToString(_modes[i].SelectedItem, CultureInfo.InvariantCulture);
                if (mode == "NULL") Values.Add(new EditorValue(EditorValueKind.Null, null));
                else if (mode == "DEFAULT") Values.Add(new EditorValue(EditorValueKind.Default, null));
                else Values.Add(new EditorValue(EditorValueKind.Value, Coerce(_editors[i].Text, _columns[i].Type)));
            }
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (FormatException)
        {
            MessageBox.Show(this, "A number or BLOB value is not valid. Enter BLOBs as hexadecimal text.",
                "Invalid value", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    static object Coerce(string text, string declaredType)
    {
        var type = declaredType.ToUpperInvariant();
        if (type.Contains("BLOB"))
        {
            var hex = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? text[2..] : text;
            return Convert.FromHexString(Regex.Replace(hex, @"\s+", ""));
        }
        if (type.Contains("INT") && long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer)) return integer;
        if ((type.Contains("REAL") || type.Contains("FLOA") || type.Contains("DOUB") || type.Contains("NUM")) && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var real)) return real;
        return text;
    }

    static string Display(object? value) => value switch { null or DBNull => "", byte[] bytes => Convert.ToHexString(bytes), _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "" };

    sealed record ColumnInfoView(string Name, string Type)
    {
        public static ColumnInfoView From(object value)
        {
            var type = value.GetType();
            return new ColumnInfoView(
                Convert.ToString(type.GetProperty("Name")?.GetValue(value), CultureInfo.InvariantCulture) ?? "",
                Convert.ToString(type.GetProperty("Type")?.GetValue(value), CultureInfo.InvariantCulture) ?? "");
        }
    }
}

sealed class ParameterDialog : Form
{
    readonly List<string> _names;
    readonly List<ComboBox> _types = [];
    readonly List<TextBox> _editors = [];
    public Dictionary<string, object?> Values { get; } = [];

    public ParameterDialog(IReadOnlyList<string> names, bool dark)
    {
        _names = names.ToList();
        Text = "SQL parameters";
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        Size = new Size(650, Math.Min(680, 175 + names.Count * 43));
        MinimumSize = new Size(580, 300);
        Font = new Font("Segoe UI", 9.5f);
        BackColor = dark ? Color.FromArgb(30, 32, 36) : Color.FromArgb(245, 247, 250);
        ForeColor = dark ? Color.FromArgb(235, 238, 242) : Color.FromArgb(33, 37, 41);

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(16) };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 54));
        root.Controls.Add(new Label
        {
            Text = "Bind each parameter. Values are passed to SQLite safely and are never concatenated into the SQL.",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft
        }, 0, 0);

        var scrolling = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
        var fields = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 3, Padding = new Padding(0, 0, 12, 0) };
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 160));
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
        fields.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (var i = 0; i < names.Count; i++)
        {
            fields.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
            var displayName = names[i].StartsWith("@__pos", StringComparison.Ordinal) ? $"?  #{i + 1}" : names[i];
            fields.Controls.Add(new Label { Text = displayName, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, i);
            var type = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
            type.Items.AddRange(["Text", "Integer", "Decimal", "NULL"]);
            type.SelectedIndex = 0;
            var editor = new TextBox { Dock = DockStyle.Fill };
            type.SelectedIndexChanged += (_, _) => editor.Enabled = Convert.ToString(type.SelectedItem, CultureInfo.InvariantCulture) != "NULL";
            _types.Add(type);
            _editors.Add(editor);
            fields.Controls.Add(type, 1, i);
            fields.Controls.Add(editor, 2, i);
        }
        scrolling.Controls.Add(fields);
        root.Controls.Add(scrolling, 0, 1);

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(0, 10, 0, 0) };
        var run = new Button { Text = "Run", AutoSize = true, Height = 34, Padding = new Padding(14, 2, 14, 2), BackColor = Color.FromArgb(45, 112, 214), ForeColor = Color.White, FlatStyle = FlatStyle.Flat };
        run.Click += (_, _) => AcceptValues();
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true, Height = 34, Padding = new Padding(14, 2, 14, 2), FlatStyle = FlatStyle.Flat };
        buttons.Controls.Add(run);
        buttons.Controls.Add(cancel);
        root.Controls.Add(buttons, 0, 2);
        Controls.Add(root);
        AcceptButton = run;
        CancelButton = cancel;
    }

    void AcceptValues()
    {
        Values.Clear();
        try
        {
            for (var i = 0; i < _names.Count; i++)
            {
                var type = Convert.ToString(_types[i].SelectedItem, CultureInfo.InvariantCulture);
                var text = _editors[i].Text;
                Values[_names[i]] = type switch
                {
                    "NULL" => null,
                    "Integer" => long.Parse(text, NumberStyles.Integer, CultureInfo.InvariantCulture),
                    "Decimal" => double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture),
                    _ => text
                };
            }
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (FormatException)
        {
            MessageBox.Show(this, "One of the numeric parameter values is not valid.", "Parameters", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }
}

sealed class ResultDialog : Form
{
    public ResultDialog(string title, IReadOnlyList<string> columns, IReadOnlyList<object?[]> rows, bool dark)
    {
        Text = title;
        StartPosition = FormStartPosition.CenterParent;
        Size = new Size(820, 520);
        MinimumSize = new Size(600, 360);
        Font = new Font("Segoe UI", 9.5f);
        var grid = new DataGridView
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            RowHeadersVisible = false,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            BackgroundColor = dark ? Color.FromArgb(42, 45, 50) : Color.White,
            ForeColor = dark ? Color.White : Color.Black
        };
        foreach (var column in columns) grid.Columns.Add(column, column);
        foreach (var row in rows) grid.Rows.Add(row.Select(v => v ?? DBNull.Value).ToArray());
        Controls.Add(grid);
    }
}

// Minimal ADO-style adapter over Windows' inbox SQLite library. Keeping this
// adapter in the source file preserves offline execution on managed PCs where
// NuGet is unavailable. Only the API surface used by SQLiteStudio is exposed.
enum SqliteOpenMode { ReadOnly, ReadWrite, ReadWriteCreate }
enum SqliteCacheMode { Default, Private, Shared }

sealed class SqliteConnectionStringBuilder
{
    public string DataSource { get; set; } = "";
    public SqliteOpenMode Mode { get; set; } = SqliteOpenMode.ReadWriteCreate;
    public SqliteCacheMode Cache { get; set; }
    public bool Pooling { get; set; }
    public int DefaultTimeout { get; set; } = 30;

    public override string ToString()
    {
        var path = Convert.ToBase64String(Encoding.UTF8.GetBytes(DataSource));
        return $"winsqlite3|{(int)Mode}|{(int)Cache}|{DefaultTimeout}|{path}";
    }

    internal static SqliteConnectionOptions Parse(string value)
    {
        var parts = value.Split('|', 5);
        if (parts.Length != 5 || parts[0] != "winsqlite3")
            throw new ArgumentException("The SQLite connection string is not valid.", nameof(value));
        return new SqliteConnectionOptions(
            Encoding.UTF8.GetString(Convert.FromBase64String(parts[4])),
            (SqliteOpenMode)int.Parse(parts[1], CultureInfo.InvariantCulture),
            (SqliteCacheMode)int.Parse(parts[2], CultureInfo.InvariantCulture),
            int.Parse(parts[3], CultureInfo.InvariantCulture));
    }
}

sealed record SqliteConnectionOptions(string DataSource, SqliteOpenMode Mode, SqliteCacheMode Cache, int TimeoutSeconds);

sealed class SqliteException : Exception
{
    public int SqliteErrorCode { get; }
    public int SqliteExtendedErrorCode { get; }

    internal SqliteException(string message, int resultCode) : base(message)
    {
        SqliteExtendedErrorCode = resultCode;
        SqliteErrorCode = resultCode & 0xff;
    }
}

sealed class SqliteConnection : IAsyncDisposable
{
    readonly SqliteConnectionOptions _options;
    IntPtr _handle;
    bool _disposed;

    internal IntPtr Handle => _handle != IntPtr.Zero
        ? _handle
        : throw new InvalidOperationException("The SQLite connection is not open.");

    internal int TotalChanges => _handle == IntPtr.Zero ? 0 : NativeSqlite.sqlite3_total_changes(_handle);
    internal bool InTransaction => _handle != IntPtr.Zero && NativeSqlite.sqlite3_get_autocommit(_handle) == 0;

    public SqliteConnection(string connectionString) => _options = SqliteConnectionStringBuilder.Parse(connectionString);

    public Task OpenAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_handle != IntPtr.Zero) return Task.CompletedTask;

        var flags = _options.Mode switch
        {
            SqliteOpenMode.ReadOnly => NativeSqlite.OpenReadOnly,
            SqliteOpenMode.ReadWrite => NativeSqlite.OpenReadWrite,
            _ => NativeSqlite.OpenReadWrite | NativeSqlite.OpenCreate
        };
        flags |= _options.Cache switch
        {
            SqliteCacheMode.Shared => NativeSqlite.OpenSharedCache,
            SqliteCacheMode.Private => NativeSqlite.OpenPrivateCache,
            _ => 0
        };

        var path = NativeSqlite.AllocUtf8(_options.DataSource, out _);
        try
        {
            int result;
            try
            {
                result = NativeSqlite.sqlite3_open_v2(path, out _handle, flags, IntPtr.Zero);
            }
            catch (DllNotFoundException ex)
            {
                throw new PlatformNotSupportedException(
                    "Windows' built-in winsqlite3.dll was not found. SQLiteStudio C# requires a supported, fully updated Windows installation.", ex);
            }
            if (result != NativeSqlite.Ok)
            {
                var message = _handle == IntPtr.Zero ? "SQLite could not open the database." : NativeSqlite.ErrorMessage(_handle);
                if (_handle != IntPtr.Zero) NativeSqlite.sqlite3_close_v2(_handle);
                _handle = IntPtr.Zero;
                throw new SqliteException(message, result);
            }
            NativeSqlite.sqlite3_extended_result_codes(_handle, 1);
            NativeSqlite.sqlite3_busy_timeout(_handle, Math.Max(0, _options.TimeoutSeconds) * 1000);
            return Task.CompletedTask;
        }
        finally
        {
            Marshal.FreeHGlobal(path);
        }
    }

    public SqliteCommand CreateCommand()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _ = Handle;
        return new SqliteCommand(this);
    }

    public Task<SqliteTransaction> BeginTransactionAsync()
    {
        ExecuteImmediate("BEGIN");
        return Task.FromResult(new SqliteTransaction(this));
    }

    internal void ExecuteImmediate(string sql)
    {
        using var command = CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQueryCore(CancellationToken.None);
    }

    internal void Interrupt()
    {
        if (_handle != IntPtr.Zero) NativeSqlite.sqlite3_interrupt(_handle);
    }

    internal SqliteException CreateException(int resultCode) =>
        new($"{NativeSqlite.ErrorMessage(Handle)} (SQLite error {resultCode})", resultCode);

    public Task CloseAsync()
    {
        if (_handle == IntPtr.Zero) return Task.CompletedTask;
        var handle = _handle;
        _handle = IntPtr.Zero;
        var result = NativeSqlite.sqlite3_close_v2(handle);
        if (result != NativeSqlite.Ok) throw new SqliteException("SQLite could not close the database cleanly.", result);
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        await CloseAsync();
        _disposed = true;
    }
}

sealed class SqliteTransaction : IAsyncDisposable
{
    readonly SqliteConnection _connection;
    bool _active = true;

    internal SqliteTransaction(SqliteConnection connection) => _connection = connection;
    internal bool Active => _active;
    internal void MarkCompleted() => _active = false;

    public Task CommitAsync()
    {
        if (!_active) return Task.CompletedTask;
        _connection.ExecuteImmediate("COMMIT");
        _active = false;
        return Task.CompletedTask;
    }

    public Task RollbackAsync()
    {
        if (!_active) return Task.CompletedTask;
        _connection.ExecuteImmediate("ROLLBACK");
        _active = false;
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (_active)
        {
            try { await RollbackAsync(); }
            catch { _active = false; }
        }
    }
}

sealed record SqliteParameter(string ParameterName, object? Value);

sealed class SqliteParameterCollection : IEnumerable<SqliteParameter>
{
    readonly List<SqliteParameter> _items = [];

    public SqliteParameter AddWithValue(string parameterName, object? value)
    {
        var parameter = new SqliteParameter(parameterName, value);
        _items.Add(parameter);
        return parameter;
    }

    internal bool TryGet(string nativeName, out object? value)
    {
        var exact = _items.LastOrDefault(item => item.ParameterName == nativeName);
        if (exact is not null)
        {
            value = exact.Value;
            return true;
        }
        var bareName = nativeName.TrimStart(':', '@', '$');
        var compatible = _items.LastOrDefault(item => item.ParameterName.TrimStart(':', '@', '$') == bareName);
        if (compatible is not null)
        {
            value = compatible.Value;
            return true;
        }
        value = null;
        return false;
    }

    public IEnumerator<SqliteParameter> GetEnumerator() => _items.GetEnumerator();
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}

sealed class SqliteCommand : IAsyncDisposable, IDisposable
{
    readonly SqliteConnection _connection;
    bool _disposed;

    public string CommandText { get; set; } = "";
    public int CommandTimeout { get; set; } = 30;
    public SqliteTransaction? Transaction { get; set; }
    public SqliteParameterCollection Parameters { get; } = new();

    internal SqliteCommand(SqliteConnection connection) => _connection = connection;

    public Task<SqliteDataReader> ExecuteReaderAsync(CancellationToken cancellationToken = default)
    {
        Validate();
        return Task.FromResult(new SqliteDataReader(_connection, CommandText, Parameters, cancellationToken));
    }

    public Task<object?> ExecuteScalarAsync()
    {
        Validate();
        using var reader = new SqliteDataReader(_connection, CommandText, Parameters, CancellationToken.None);
        do
        {
            if (reader.FieldCount > 0 && reader.ReadCore(CancellationToken.None))
                return Task.FromResult<object?>(reader.GetValue(0));
        } while (reader.NextResultCore(CancellationToken.None));
        return Task.FromResult<object?>(null);
    }

    public Task<int> ExecuteNonQueryAsync()
    {
        Validate();
        return Task.FromResult(ExecuteNonQueryCore(CancellationToken.None));
    }

    internal int ExecuteNonQueryCore(CancellationToken cancellationToken)
    {
        Validate();
        using var reader = new SqliteDataReader(_connection, CommandText, Parameters, cancellationToken);
        do
        {
            while (reader.ReadCore(cancellationToken)) { }
        } while (reader.NextResultCore(cancellationToken));
        // sqlite3_changes excludes auxiliary trigger and foreign-key writes,
        // matching ADO ExecuteNonQuery semantics and the editor's one-row guard.
        return NativeSqlite.sqlite3_changes(_connection.Handle);
    }

    public void Cancel() => _connection.Interrupt();

    void Validate()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (string.IsNullOrWhiteSpace(CommandText)) throw new InvalidOperationException("CommandText is empty.");
        if (Transaction is not null && !Transaction.Active) throw new InvalidOperationException("The SQLite transaction is no longer active.");
    }

    public void Dispose() => _disposed = true;
    public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
}

sealed class SqliteDataReader : IAsyncDisposable, IDisposable
{
    readonly SqliteConnection _connection;
    readonly SqliteParameterCollection _parameters;
    readonly CancellationToken _commandCancellation;
    readonly int _startingChanges;
    string _remainingSql;
    IntPtr _statement;
    bool _statementDone;
    bool _disposed;

    internal SqliteDataReader(
        SqliteConnection connection,
        string sql,
        SqliteParameterCollection parameters,
        CancellationToken cancellationToken)
    {
        _connection = connection;
        _parameters = parameters;
        _commandCancellation = cancellationToken;
        _remainingSql = sql;
        _startingChanges = connection.TotalChanges;
        PrepareNextStatement();
    }

    public int FieldCount => _statement == IntPtr.Zero ? 0 : NativeSqlite.sqlite3_column_count(_statement);
    public int RecordsAffected => Math.Max(0, _connection.TotalChanges - _startingChanges);

    public Task<bool> ReadAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(ReadCore(CombineCancellation(cancellationToken)));

    internal bool ReadCore(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_statement == IntPtr.Zero || _statementDone) return false;
        cancellationToken.ThrowIfCancellationRequested();
        var result = NativeSqlite.sqlite3_step(_statement);
        if (result == NativeSqlite.Row) return true;
        if (result == NativeSqlite.Done)
        {
            _statementDone = true;
            return false;
        }
        if (result == NativeSqlite.Interrupt && cancellationToken.IsCancellationRequested)
            throw new OperationCanceledException(cancellationToken);
        throw _connection.CreateException(result);
    }

    public Task<bool> NextResultAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(NextResultCore(CombineCancellation(cancellationToken)));

    internal bool NextResultCore(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        while (_statement != IntPtr.Zero && !_statementDone)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = NativeSqlite.sqlite3_step(_statement);
            if (result == NativeSqlite.Row) continue;
            if (result == NativeSqlite.Done) { _statementDone = true; break; }
            if (result == NativeSqlite.Interrupt && cancellationToken.IsCancellationRequested)
                throw new OperationCanceledException(cancellationToken);
            throw _connection.CreateException(result);
        }
        FinalizeStatement();
        return PrepareNextStatement();
    }

    bool PrepareNextStatement()
    {
        while (!string.IsNullOrWhiteSpace(_remainingSql))
        {
            var bytes = Encoding.UTF8.GetBytes(_remainingSql);
            var sqlPointer = Marshal.AllocHGlobal(bytes.Length + 1);
            try
            {
                Marshal.Copy(bytes, 0, sqlPointer, bytes.Length);
                Marshal.WriteByte(sqlPointer, bytes.Length, 0);
                var result = NativeSqlite.sqlite3_prepare_v2(
                    _connection.Handle, sqlPointer, -1, out _statement, out var tail);
                var consumed = checked((int)(tail.ToInt64() - sqlPointer.ToInt64()));
                if (consumed < 0 || consumed > bytes.Length) consumed = bytes.Length;
                _remainingSql = consumed >= bytes.Length
                    ? ""
                    : Encoding.UTF8.GetString(bytes, consumed, bytes.Length - consumed);
                if (result != NativeSqlite.Ok)
                {
                    FinalizeStatement();
                    throw _connection.CreateException(result);
                }
                if (_statement == IntPtr.Zero)
                {
                    if (consumed == 0) return false;
                    continue;
                }
                try { BindParameters(); }
                catch
                {
                    FinalizeStatement();
                    throw;
                }
                _statementDone = false;
                return true;
            }
            finally
            {
                Marshal.FreeHGlobal(sqlPointer);
            }
        }
        _statement = IntPtr.Zero;
        return false;
    }

    void BindParameters()
    {
        var count = NativeSqlite.sqlite3_bind_parameter_count(_statement);
        for (var index = 1; index <= count; index++)
        {
            var namePointer = NativeSqlite.sqlite3_bind_parameter_name(_statement, index);
            var name = namePointer == IntPtr.Zero ? $"?{index}" : Marshal.PtrToStringUTF8(namePointer) ?? $"?{index}";
            if (!_parameters.TryGet(name, out var value))
                throw new InvalidOperationException($"No value was supplied for SQL parameter {name}.");
            var result = BindValue(index, value);
            if (result != NativeSqlite.Ok) throw _connection.CreateException(result);
        }
    }

    int BindValue(int index, object? value)
    {
        if (value is null or DBNull) return NativeSqlite.sqlite3_bind_null(_statement, index);
        if (value is byte[] blob)
        {
            if (blob.Length == 0) return NativeSqlite.sqlite3_bind_zeroblob(_statement, index, 0);
            var blobPointer = Marshal.AllocHGlobal(blob.Length);
            try
            {
                Marshal.Copy(blob, 0, blobPointer, blob.Length);
                return NativeSqlite.sqlite3_bind_blob(_statement, index, blobPointer, blob.Length, NativeSqlite.Transient);
            }
            finally { Marshal.FreeHGlobal(blobPointer); }
        }
        if (value is bool boolean) return NativeSqlite.sqlite3_bind_int64(_statement, index, boolean ? 1 : 0);
        if (value is sbyte or byte or short or ushort or int or uint or long)
            return NativeSqlite.sqlite3_bind_int64(_statement, index, Convert.ToInt64(value, CultureInfo.InvariantCulture));
        if (value is float or double or decimal)
            return NativeSqlite.sqlite3_bind_double(_statement, index, Convert.ToDouble(value, CultureInfo.InvariantCulture));
        var text = value switch
        {
            DateTime date => date.ToString("O", CultureInfo.InvariantCulture),
            DateTimeOffset date => date.ToString("O", CultureInfo.InvariantCulture),
            _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? ""
        };
        var pointer = NativeSqlite.AllocUtf8(text, out var length);
        try { return NativeSqlite.sqlite3_bind_text(_statement, index, pointer, length, NativeSqlite.Transient); }
        finally { Marshal.FreeHGlobal(pointer); }
    }

    public string GetName(int ordinal)
    {
        ValidateOrdinal(ordinal);
        var pointer = NativeSqlite.sqlite3_column_name(_statement, ordinal);
        return pointer == IntPtr.Zero ? $"column_{ordinal + 1}" : Marshal.PtrToStringUTF8(pointer) ?? $"column_{ordinal + 1}";
    }

    public bool IsDBNull(int ordinal)
    {
        ValidateOrdinal(ordinal);
        return NativeSqlite.sqlite3_column_type(_statement, ordinal) == NativeSqlite.Null;
    }

    public object GetValue(int ordinal)
    {
        ValidateOrdinal(ordinal);
        return NativeSqlite.sqlite3_column_type(_statement, ordinal) switch
        {
            NativeSqlite.Integer => NativeSqlite.sqlite3_column_int64(_statement, ordinal),
            NativeSqlite.Float => NativeSqlite.sqlite3_column_double(_statement, ordinal),
            NativeSqlite.Text => ReadText(ordinal),
            NativeSqlite.Blob => ReadBlob(ordinal),
            _ => DBNull.Value
        };
    }

    public string GetString(int ordinal) => Convert.ToString(GetValue(ordinal), CultureInfo.InvariantCulture) ?? "";
    public long GetInt64(int ordinal) => Convert.ToInt64(GetValue(ordinal), CultureInfo.InvariantCulture);

    string ReadText(int ordinal)
    {
        var pointer = NativeSqlite.sqlite3_column_text(_statement, ordinal);
        var length = NativeSqlite.sqlite3_column_bytes(_statement, ordinal);
        return NativeSqlite.ReadUtf8(pointer, length);
    }

    byte[] ReadBlob(int ordinal)
    {
        var pointer = NativeSqlite.sqlite3_column_blob(_statement, ordinal);
        var length = NativeSqlite.sqlite3_column_bytes(_statement, ordinal);
        if (length == 0) return [];
        if (pointer == IntPtr.Zero) return [];
        var bytes = new byte[length];
        Marshal.Copy(pointer, bytes, 0, length);
        return bytes;
    }

    void ValidateOrdinal(int ordinal)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_statement == IntPtr.Zero || ordinal < 0 || ordinal >= FieldCount)
            throw new IndexOutOfRangeException($"Column ordinal {ordinal} is outside the current result set.");
    }

    CancellationToken CombineCancellation(CancellationToken supplied) =>
        supplied.CanBeCanceled ? supplied : _commandCancellation;

    void FinalizeStatement()
    {
        if (_statement == IntPtr.Zero) return;
        NativeSqlite.sqlite3_finalize(_statement);
        _statement = IntPtr.Zero;
        _statementDone = true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        FinalizeStatement();
        _disposed = true;
    }

    public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
}

static class NativeSqlite
{
    internal const int Ok = 0;
    internal const int Interrupt = 9;
    internal const int Row = 100;
    internal const int Done = 101;
    internal const int Integer = 1;
    internal const int Float = 2;
    internal const int Text = 3;
    internal const int Blob = 4;
    internal const int Null = 5;
    internal const int OpenReadOnly = 0x00000001;
    internal const int OpenReadWrite = 0x00000002;
    internal const int OpenCreate = 0x00000004;
    internal const int OpenPrivateCache = 0x00040000;
    internal const int OpenSharedCache = 0x00020000;
    internal static readonly IntPtr Transient = new(-1);
    const string Library = "winsqlite3.dll";

    internal static IntPtr AllocUtf8(string value, out int length)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        length = bytes.Length;
        var pointer = Marshal.AllocHGlobal(length + 1);
        Marshal.Copy(bytes, 0, pointer, length);
        Marshal.WriteByte(pointer, length, 0);
        return pointer;
    }

    internal static string ReadUtf8(IntPtr pointer, int length)
    {
        if (pointer == IntPtr.Zero || length <= 0) return "";
        var bytes = new byte[length];
        Marshal.Copy(pointer, bytes, 0, length);
        return Encoding.UTF8.GetString(bytes);
    }

    internal static string ErrorMessage(IntPtr database)
    {
        var pointer = sqlite3_errmsg(database);
        return pointer == IntPtr.Zero ? "Unknown SQLite error." : Marshal.PtrToStringUTF8(pointer) ?? "Unknown SQLite error.";
    }

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int sqlite3_open_v2(IntPtr filename, out IntPtr database, int flags, IntPtr vfs);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int sqlite3_close_v2(IntPtr database);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr sqlite3_errmsg(IntPtr database);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int sqlite3_extended_result_codes(IntPtr database, int enabled);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int sqlite3_busy_timeout(IntPtr database, int milliseconds);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void sqlite3_interrupt(IntPtr database);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int sqlite3_total_changes(IntPtr database);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int sqlite3_changes(IntPtr database);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int sqlite3_get_autocommit(IntPtr database);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int sqlite3_prepare_v2(IntPtr database, IntPtr sql, int byteCount, out IntPtr statement, out IntPtr tail);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int sqlite3_step(IntPtr statement);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int sqlite3_finalize(IntPtr statement);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int sqlite3_column_count(IntPtr statement);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr sqlite3_column_name(IntPtr statement, int ordinal);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int sqlite3_column_type(IntPtr statement, int ordinal);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern long sqlite3_column_int64(IntPtr statement, int ordinal);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern double sqlite3_column_double(IntPtr statement, int ordinal);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr sqlite3_column_text(IntPtr statement, int ordinal);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr sqlite3_column_blob(IntPtr statement, int ordinal);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int sqlite3_column_bytes(IntPtr statement, int ordinal);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int sqlite3_bind_parameter_count(IntPtr statement);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr sqlite3_bind_parameter_name(IntPtr statement, int index);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int sqlite3_bind_null(IntPtr statement, int index);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int sqlite3_bind_int64(IntPtr statement, int index, long value);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int sqlite3_bind_double(IntPtr statement, int index, double value);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int sqlite3_bind_text(IntPtr statement, int index, IntPtr value, int byteCount, IntPtr destructor);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int sqlite3_bind_blob(IntPtr statement, int index, IntPtr value, int byteCount, IntPtr destructor);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int sqlite3_bind_zeroblob(IntPtr statement, int index, int byteCount);
}
