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

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new StudioForm(args.Length > 0 ? args[0] : null));
    }
}

sealed class StudioForm : Form
{
    const int PageSize = 500;
    const int ResultLimit = 5000;

    static readonly Color Accent = Color.FromArgb(16, 139, 128);
    static readonly Color AccentHover = Color.FromArgb(32, 91, 178);
    static readonly Color LightBackground = Color.FromArgb(245, 247, 250);
    static readonly Color DarkBackground = Color.FromArgb(15, 23, 36);
    static readonly Color DarkSurface = Color.FromArgb(23, 34, 49);
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
    bool _dark = true;
    readonly StudioPreferences _preferences = StudioPreferences.Load();
    int _page;
    Task? _activeBrowseTask;
    bool _closeAfterOperation;
    bool _rebuildingTree;
    readonly List<DbObject> _schemaObjects = [];
    int _dataVersion;
    readonly List<string> _displayColumns = [];
    readonly List<string> _keyColumns = [];
    readonly List<object?[]> _rowKeys = [];
    readonly List<string> _history = [];

    readonly MenuStrip _menu = new();
    readonly ToolStrip _toolbar = new();
    readonly ToolStripLabel _pathLabel = new();
    readonly ToolStripLabel _pendingLabel = new();
    readonly ToolStripButton _stopButton = new("Stop") { Enabled = false };
    readonly SplitContainer _mainSplit = new();
    readonly TextBox _objectFilter = new();
    readonly TreeView _objectTree = new();
    readonly TabControl _workspace = new StudioTabs();
    readonly TabPage _homeTab = new("Overview");
    readonly Label _overviewTitle = new();
    readonly Label _overviewStats = new();
    readonly ListBox _recentFiles = new();
    readonly ToolStripProgressBar _activity = new() { Style = ProgressBarStyle.Marquee, Visible = false, Width = 100 };
    readonly TabControl _queryTabs = new StudioTabs();
    readonly ToolStripTextBox _findSql = new() { ToolTipText = "Find text in this query. Enter finds the next match." };
    int _queryNumber = 1;
    readonly TabPage _browseTab = new("Browse data");
    readonly TabPage _sqlTab = new("SQL workspace");
    readonly TabPage _schemaTab = new("Schema");
    readonly DataGridView _dataGrid = CreateGrid("Choose a table in Explorer to start browsing");
    readonly TextBox _whereBox = new();
    readonly Label _tableTitle = new();
    readonly Label _pageLabel = new();
    readonly Label _rowCountLabel = new();
    readonly NumericUpDown _goToPage = new();
    readonly Button _previousButton = MakeButton("Previous");
    readonly Button _nextButton = MakeButton("Next");
    readonly RichTextBox _emptySqlEditor = new();
    RichTextBox _sqlEditor => (_queryTabs.SelectedTab?.Tag as QueryDocument)?.Editor ?? _emptySqlEditor;
    readonly DataGridView _resultGrid = CreateGrid("Run a query to explore its results");
    readonly ListBox _historyList = new();
    readonly RichTextBox _sqlLog = new();
    readonly ToolStripButton _runButton = new("Run SQL  F9") { BackColor = Accent, ForeColor = Color.White };
    readonly ToolStripButton _cancelButton = new("Cancel");
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
        SuspendLayout();
        AutoScaleDimensions = new SizeF(96, 96);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = "SQLiteStudio C#";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(1040, 700);
        Size = new Size(1440, 900);
        Font = new Font("Segoe UI", 9.5f);
        _dark = _preferences.Dark;
        KeyPreview = true;

        BuildMenu();
        BuildToolbar();
        BuildWorkspace();
        BuildStatusBar();
        // Explicit rows keep the menu, toolbar and status visible at every DPI.
        var shell = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Margin = Padding.Empty, Padding = Padding.Empty };
        shell.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        shell.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        shell.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        shell.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        shell.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        foreach (Control control in new Control[] { _menu, _toolbar, _mainSplit, _status }) control.Margin = Padding.Empty;
        shell.Controls.Add(_menu, 0, 0);
        shell.Controls.Add(_toolbar, 0, 1);
        shell.Controls.Add(_mainSplit, 0, 2);
        shell.Controls.Add(_status, 0, 3);
        _menu.Dock = _toolbar.Dock = _status.Dock = DockStyle.Fill;
        Controls.Add(shell);
        _schemaFilterTimer.Tick += (_, _) =>
        {
            _schemaFilterTimer.Stop();
            FilterObjectTree();
        };
        ApplyTheme();
        UpdateConnectedState();
        ResumeLayout(true);

        FormClosing += OnClosing;
        KeyDown += HandleShortcut;
        Shown += async (_, _) =>
        {
            if (!string.IsNullOrWhiteSpace(initialPath))
                await SafeUiAsync(() => OpenDatabaseAsync(initialPath));
        };
        AllowDrop = true;
        DragEnter += (_, e) => { if (e.Data?.GetDataPresent(DataFormats.FileDrop) == true) e.Effect = DragDropEffects.Copy; };
        DragDrop += async (_, e) =>
        {
            if (_queryCancellation is not null || e.Data?.GetData(DataFormats.FileDrop) is not string[] paths || paths.Length == 0) return;
            await SafeUiAsync(async () =>
            {
                if (Path.GetExtension(paths[0]).Equals(".sql", StringComparison.OrdinalIgnoreCase)) await OpenSqlPathAsync(paths[0]);
                else await OpenDatabaseAsync(paths[0]);
            });
        };
    }

    static DataGridView CreateGrid(string emptyMessage = "No rows to display") => new BufferedGrid()
    {
        EmptyMessage = emptyMessage,
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
        RowTemplate = { Height = 26 },
        SelectionMode = DataGridViewSelectionMode.FullRowSelect
    };

    static Button MakeButton(string text) => new StudioButton()
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
        edit.DropDownItems.Add(MenuItem("Copy selected rows", Keys.None, CopyRows));
        edit.DropDownItems.Add(MenuItem("Delete selected rows", Keys.None, async () => await DeleteRowsAsync()));

        var data = new ToolStripMenuItem("&Data");
        data.DropDownItems.Add(MenuItem("Add row…", Keys.Control | Keys.Insert, async () => await AddRowAsync()));
        data.DropDownItems.Add(MenuItem("Edit row…", Keys.Control | Keys.E, async () => await EditRowAsync()));
        data.DropDownItems.Add(MenuItem("Duplicate row", Keys.Control | Keys.D, async () => await DuplicateRowAsync()));
        data.DropDownItems.Add(new ToolStripSeparator());
        data.DropDownItems.Add(MenuItem("Import CSV or JSON…", Keys.None, ImportDataAsync));
        data.DropDownItems.Add(MenuItem("Export all matching rows…", Keys.None, ExportAllAsync));
        data.DropDownItems.Add(MenuItem("Export visible rows as CSV…", Keys.None, ExportVisibleCsv));
        data.DropDownItems.Add(MenuItem("Export visible rows as JSON…", Keys.None, ExportVisibleJson));

        var database = new ToolStripMenuItem("&Database");
        database.DropDownItems.Add(MenuItem("&Refresh", Keys.F5, async () => await RefreshWorkspaceAsync()));
        database.DropDownItems.Add(MenuItem("Create backup…", Keys.None, BackupDatabaseAsync));
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
        item.Click += (_, _) => { try { action(); } catch (Exception ex) { ShowError("Operation failed", ex); } };
        return item;
    }

    ToolStripMenuItem MenuItem(string text, Keys keys, Func<Task> action)
    {
        var item = new ToolStripMenuItem(text) { ShortcutKeys = keys };
        item.Click += async (_, _) => await SafeUiAsync(action);
        return item;
    }

    void BuildToolbar()
    {
        _toolbar.GripStyle = ToolStripGripStyle.Hidden;
        _toolbar.Padding = new Padding(8, 5, 8, 5);
        _toolbar.AutoSize = true;
        _toolbar.Items.Add(new ToolStripLabel("SQLITE  /  STUDIO") { Font = new Font(Font, FontStyle.Bold), ForeColor = Accent, Padding = new Padding(10, 0, 18, 0) });
        AddToolButton("Open", async () => await ChooseDatabaseAsync(false));
        AddToolButton("New", async () => await ChooseDatabaseAsync(true));
        _toolbar.Items.Add(new ToolStripSeparator());
        AddToolButton("Refresh", async () => await RefreshWorkspaceAsync());
        AddToolButton("Commit", async () => await CommitAsync());
        AddToolButton("Rollback", async () => await RollbackAsync());
        _stopButton.Click += (_, _) => CancelQuery();
        _toolbar.Items.Add(_stopButton);
        _toolbar.Items.Add(new ToolStripSeparator());
        _pendingLabel.ForeColor = Color.FromArgb(205, 105, 30);
        _pendingLabel.Font = new Font(Font, FontStyle.Bold);
        _toolbar.Items.Add(_pendingLabel);
        _pathLabel.Alignment = ToolStripItemAlignment.Right;
        _pathLabel.AutoToolTip = true;
        _pathLabel.Text = "No database open";
        _pathLabel.AutoSize = false;
        _pathLabel.Width = 300;
        _pathLabel.TextAlign = ContentAlignment.MiddleRight;
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
        Controls.Add(_mainSplit);
        Load += (_, _) =>
        {
            var scale = DeviceDpi / 96f;
            _mainSplit.Panel1MinSize = (int)(200 * scale);
            _mainSplit.Panel2MinSize = (int)(500 * scale);
            _mainSplit.SplitterDistance = (int)(260 * scale);
            _objectTree.ItemHeight = (int)(28 * scale);
        };

        BuildObjectBrowser();
        _workspace.Dock = DockStyle.Fill;
        _workspace.Padding = new Point(16, 6);
        _workspace.TabPages.AddRange([_homeTab, _browseTab, _sqlTab, _schemaTab]);
        _mainSplit.Panel2.Controls.Add(_workspace);
        BuildBrowseTab();
        BuildSqlTab();
        BuildSchemaTab();
        BuildOverview();
    }

    void BuildObjectBrowser()
    {
        var header = new Panel { Dock = DockStyle.Top, Height = 78, Padding = new Padding(14, 12, 14, 8) };
        var title = new Label { Text = "EXPLORER", Dock = DockStyle.Top, Height = 25, Font = new Font(Font, FontStyle.Bold) };
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
        _objectTree.ShowLines = false;
        _objectTree.FullRowSelect = true;
        _objectTree.DrawMode = TreeViewDrawMode.OwnerDrawAll;
        _objectTree.DrawNode += (_, e) =>
        {
            if (e.Node is null) return;
            var selected = e.Node == _objectTree.SelectedNode;
            using var brush = new SolidBrush(selected ? Accent : _objectTree.BackColor);
            var row = new Rectangle(0, e.Node.Bounds.Y, _objectTree.ClientSize.Width, e.Node.Bounds.Height);
            e.Graphics.FillRectangle(brush, row);
            var label = new Rectangle(e.Node.Bounds.X, row.Y, Math.Max(0, row.Width - e.Node.Bounds.X), row.Height);
            TextRenderer.DrawText(e.Graphics, e.Node.Text, e.Node.NodeFont ?? _objectTree.Font, label,
                selected ? Color.White : _objectTree.ForeColor, TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            if (e.Node.Nodes.Count > 0)
                TextRenderer.DrawText(e.Graphics, e.Node.IsExpanded ? "−" : "+", _objectTree.Font, new Rectangle(0, row.Y, e.Node.Bounds.X, row.Height), _objectTree.ForeColor, TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter);
        };
        _objectTree.ItemHeight = 28;
        _objectTree.AfterSelect += async (_, _) => { if (!_rebuildingTree) await SafeUiAsync(ObjectSelectedAsync); };
        var open = MakePrimaryButton("Query this table");
        open.Dock = DockStyle.Bottom;
        open.Height = 42;
        open.AutoSize = false;
        open.Margin = new Padding(12);
        open.Click += (_, _) =>
        {
            if (_objectTree.SelectedNode?.Tag is DbObject obj && obj.Type is "table" or "view")
                NewQuery($"SELECT *\nFROM {Quote(obj.Name)}\nLIMIT 1000;", obj.Name);
        };
        var footer = new Panel { Dock = DockStyle.Bottom, Height = 62, Padding = new Padding(12, 8, 12, 12) };
        footer.Controls.Add(open);
        _mainSplit.Panel1.Controls.Add(_objectTree);
        _mainSplit.Panel1.Controls.Add(footer);
        _mainSplit.Panel1.Controls.Add(header);
    }

    void BuildBrowseTab()
    {
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, Padding = new Padding(10) };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
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

        var actions = new ToolStrip { Dock = DockStyle.Fill, GripStyle = ToolStripGripStyle.Hidden, Padding = new Padding(0, 4, 0, 4) };
        void AddAction(string title, Func<Task> action)
        {
            var button = new ToolStripButton(title) { Padding = new Padding(8, 3, 8, 3) };
            button.Click += async (_, _) => await SafeUiAsync(action);
            actions.Items.Add(button);
        }
        AddAction("Add row", AddRowAsync);
        AddAction("Edit row", () => EditRowAsync());
        AddAction("Duplicate", DuplicateRowAsync);
        AddAction("Delete", DeleteRowsAsync);
        actions.Items.Add(new ToolStripSeparator());
        AddAction("Copy", () => { CopyRows(); return Task.CompletedTask; });
        var export = new ToolStripDropDownButton("Export");
        var all = new ToolStripMenuItem("All matching rows…");
        all.Click += async (_, _) => await SafeUiAsync(ExportAllAsync);
        export.DropDownItems.Add(all);
        export.DropDownItems.Add(new ToolStripSeparator());
        export.DropDownItems.Add("This page as CSV", null, (_, _) => ExportVisibleCsv());
        export.DropDownItems.Add("This page as JSON", null, (_, _) => ExportVisibleJson());
        actions.Items.Add(export);
        actions.Items.Add(new ToolStripSeparator());
        AddAction("Count rows", CountRowsAsync);
        root.Controls.Add(actions, 0, 1);

        var filters = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 1 };
        filters.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
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
        foreach (Control control in filters.Controls)
            if (control is Button button) { button.AutoSize = false; button.Dock = DockStyle.Fill; button.Padding = Padding.Empty; }
        root.Controls.Add(filters, 0, 2);

        _dataGrid.ColumnHeaderMouseClick += async (_, e) =>
        {
            if (e.ColumnIndex < 0 || e.ColumnIndex >= _displayColumns.Count) return;
            var column = _displayColumns[e.ColumnIndex];
            if (_sortColumn == column) _sortDescending = !_sortDescending;
            else { _sortColumn = column; _sortDescending = false; }
            await LoadCurrentObjectAsync(0);
        };
        _dataGrid.CellDoubleClick += async (_, e) => { if (e.RowIndex >= 0) await SafeUiAsync(() => EditRowAsync(e.RowIndex)); };
        _dataGrid.CellFormatting += GridCellFormatting;
        root.Controls.Add(_dataGrid, 0, 3);

        var pager = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 7, RowCount = 1 };
        pager.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        pager.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100));
        pager.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100));
        pager.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 95));
        pager.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 85));
        pager.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
        pager.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        pager.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _previousButton.Dock = DockStyle.Fill;
        _previousButton.AutoSize = false;
        _previousButton.Padding = Padding.Empty;
        _previousButton.Click += async (_, _) => await LoadCurrentObjectAsync(Math.Max(0, _page - 1));
        _nextButton.Dock = DockStyle.Fill;
        _nextButton.AutoSize = false;
        _nextButton.Padding = Padding.Empty;
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
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var actions = new ToolStrip { Dock = DockStyle.Fill, GripStyle = ToolStripGripStyle.Hidden };
        _runButton.Click += async (_, _) => await SafeUiAsync(() => ExecuteSqlAsync(false));
        _cancelButton.Enabled = false;
        _cancelButton.Click += (_, _) => CancelQuery();
        actions.Items.Add(_runButton);
        actions.Items.Add(_cancelButton);
        void AddSqlAction(string text, Func<Task> action)
        {
            var item = new ToolStripButton(text);
            item.Click += async (_, _) => { if (_queryCancellation is null) await SafeUiAsync(action); };
            actions.Items.Add(item);
        }
        AddSqlAction("Query plan", () => ExecuteSqlAsync(true));
        AddSqlAction("Format", () => { FormatSql(); return Task.CompletedTask; });
        AddSqlAction("Clear results", () => { ClearGrid(_resultGrid); return Task.CompletedTask; });
        actions.Items.Add(new ToolStripSeparator());
        AddSqlAction("+ Query", () => { if (_queryCancellation is null) NewQuery(); return Task.CompletedTask; });
        AddSqlAction("Open SQL", OpenSqlFileAsync);
        AddSqlAction("Save SQL", SaveSqlFileAsync);
        AddSqlAction("Close query", CloseQueryAsync);
        _findSql.AutoSize = false;
        _findSql.Width = 140;
        _findSql.TextBox.PlaceholderText = "Find in query";
        _findSql.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; FindInQuery(); } };
        actions.Items.Add(_findSql);
        AddSqlAction("Find next", () => { FindInQuery(); return Task.CompletedTask; });
        foreach (ToolStripItem item in actions.Items) item.Padding = new Padding(8, 3, 8, 3);
        root.Controls.Add(actions, 0, 0);

        var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, SplitterDistance = 330, Panel1MinSize = 150, Panel2MinSize = 150 };
        _queryTabs.Dock = DockStyle.Fill;
        _queryTabs.SelectedIndexChanged += (_, _) =>
        {
            if (_queryTabs.SelectedTab?.Tag is QueryDocument doc)
            {
                if (doc.Result is { } result) FillGrid(_resultGrid, result.Columns, result.Rows);
                else ClearGrid(_resultGrid);
            }
        };
        split.Panel1.Controls.Add(_queryTabs);
        NewQuery("-- Your next insight starts here. F9 runs the selection or the whole query.\nSELECT sqlite_version() AS sqlite_version;", "Query 1", activate: false);

        var output = new StudioTabs { Dock = DockStyle.Fill };
        var results = new TabPage("Results");
        _resultGrid.CellFormatting += GridCellFormatting;
        results.Controls.Add(_resultGrid);
        var history = new TabPage("History");
        _historyList.Dock = DockStyle.Fill;
        _historyList.BorderStyle = BorderStyle.None;
        _historyList.Font = new Font("Cascadia Mono", 9.5f);
        _historyList.DoubleClick += (_, _) =>
        {
            if (_historyList.SelectedItem is string sql) NewQuery(sql, "From history");
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
        var root = new StudioTabs { Dock = DockStyle.Fill, Padding = new Point(14, 6) };
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
        _status.Items.AddRange([_statusText, _activity, _statusRows, new ToolStripStatusLabel { Text = "  " }, _statusMode]);
        Controls.Add(_status);
    }

    void BuildOverview()
    {
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(28), ColumnCount = 1, RowCount = 6 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        foreach (var height in new[] { 30, 76, 56, 66, 44 }) layout.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.Controls.Add(new Label { Text = "YOUR DATA. IN FOCUS.", ForeColor = Accent, Font = new Font(Font, FontStyle.Bold), Dock = DockStyle.Fill }, 0, 0);
        _overviewTitle.Text = "A clearer view of your data.";
        _overviewTitle.Font = new Font("Segoe UI Semibold", 26);
        _overviewTitle.Dock = DockStyle.Fill;
        _overviewTitle.AutoEllipsis = true;
        layout.Controls.Add(_overviewTitle, 0, 1);
        _overviewStats.Text = "Open a database to explore, ask questions, and make changes with confidence.";
        _overviewStats.Dock = DockStyle.Fill;
        _overviewStats.AutoEllipsis = true;
        layout.Controls.Add(_overviewStats, 0, 2);
        var actions = new ToolStrip { Dock = DockStyle.Fill, GripStyle = ToolStripGripStyle.Hidden };
        void Add(string title, Func<Task> action)
        {
            var button = new ToolStripButton(title) { Padding = new Padding(16, 8, 16, 8) };
            button.Click += async (_, _) => await SafeUiAsync(action);
            actions.Items.Add(button);
        }
        Add("Open database", () => ChooseDatabaseAsync(false));
        Add("Create database", () => ChooseDatabaseAsync(true));
        Add("Import data", ImportDataAsync);
        Add("New query", () => { NewQuery(); return Task.CompletedTask; });
        Add("Backup", BackupDatabaseAsync);
        layout.Controls.Add(actions, 0, 3);
        layout.Controls.Add(new Label { Text = "RECENT DATABASES  ·  Double-click to open", Dock = DockStyle.Fill, TextAlign = ContentAlignment.BottomLeft, Font = new Font(Font, FontStyle.Bold) }, 0, 4);
        _recentFiles.Dock = DockStyle.Fill;
        _recentFiles.BorderStyle = BorderStyle.None;
        _recentFiles.IntegralHeight = false;
        _recentFiles.HorizontalScrollbar = true;
        _recentFiles.DoubleClick += async (_, _) =>
        {
            if (_recentFiles.SelectedItem is string path) await SafeUiAsync(() => OpenDatabaseAsync(path));
        };
        layout.Controls.Add(_recentFiles, 0, 5);
        _homeTab.Controls.Add(layout);
    }

    void UpdateOverview()
    {
        _overviewTitle.Text = _connection is null ? "A clearer view of your data." : Path.GetFileName(_databasePath ?? "Connected database");
        _overviewStats.Text = _connection is null
            ? "Drop a SQLite database here, or open one to get started. SQL files open in their own query tabs."
            : $"{_schemaObjects.Count(o => o.Type == "table")} tables     /     {_schemaObjects.Count(o => o.Type == "view")} views     /     {_schemaObjects.Count(o => o.Type == "index")} indexes     /     {(_readOnly ? "Read-only connection" : "Changes stay pending until you commit")}";
        _recentFiles.BeginUpdate();
        _recentFiles.Items.Clear();
        foreach (var path in _preferences.Recent) _recentFiles.Items.Add(path);
        _recentFiles.EndUpdate();
    }

    void SavePreferences()
    {
        _preferences.Dark = _dark;
        _preferences.Save();
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
        if (create && File.Exists(path)) { MessageBox.Show(this, "A file already exists at that path. Choose a new filename, or use Open database.", "Create database"); return; }
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
                Cache = SqliteCacheMode.Private,
                Pooling = false,
                DefaultTimeout = 5
            };
            _connection = new SqliteConnection(builder.ToString());
            _readOnly = readOnly;
            _databasePath = path;
            _dataVersion = await BrowseWorkAsync(async token =>
            {
                token.ThrowIfCancellationRequested();
                await _connection.OpenAsync();
                await ExecuteNonQueryDirectAsync("PRAGMA foreign_keys = ON");
                await ExecuteNonQueryDirectAsync("PRAGMA busy_timeout = 5000");
                if (!readOnly)
                {
                    try { await ExecuteScalarDirectAsync("PRAGMA journal_mode = WAL"); }
                    catch (SqliteException) { /* Some valid media cannot use WAL. */ }
                }
                token.ThrowIfCancellationRequested();
                return Convert.ToInt32(await ExecuteScalarDirectAsync("PRAGMA data_version"), CultureInfo.InvariantCulture);
            });
            Text = $"SQLiteStudio C# — {Path.GetFileName(path)}";
            _pathLabel.Text = Path.GetFileName(path);
            _pathLabel.ToolTipText = path;
            SetStatus($"Opened {Path.GetFileName(path)}");
            UpdateConnectedState();
            await RefreshSchemaAsync();
            _preferences.Recent.RemoveAll(p => p.Equals(path, StringComparison.OrdinalIgnoreCase));
            _preferences.Recent.Insert(0, path);
            _preferences.Recent = _preferences.Recent.Take(8).ToList();
            SavePreferences();
            UpdateOverview();
            _workspace.SelectedTab = _homeTab;
        }
        catch (Exception ex)
        {
            await DisposeConnectionAsync();
            _databasePath = null;
            UpdateConnectedState();
            if (ex is OperationCanceledException) { SetStatus("Open cancelled"); return; }
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
        if (_queryCancellation is not null) return false;
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
        _schemaObjects.Clear();
        ClearGrid(_dataGrid);
        ClearGrid(_resultGrid);
        foreach (TabPage page in _queryTabs.TabPages) if (page.Tag is QueryDocument doc) doc.Result = null;
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

    async Task RefreshWorkspaceAsync()
    {
        await RefreshSchemaAsync(preserveSelection: true);
        if (_objectTree.SelectedNode?.Tag is DbObject selected) await LoadStructureAsync(selected);
        await LoadCurrentObjectAsync(_page);
    }

    async Task RefreshSchemaAsync(bool preserveSelection = false)
    {
        if (_connection is null) return;
        var objects = await BrowseWorkAsync(async token =>
        {
            var result = new List<DbObject>();
            using var command = CreateCommand("SELECT type, name, tbl_name, COALESCE(sql, '') FROM sqlite_schema WHERE type IN ('table','view','index','trigger') AND name NOT LIKE 'sqlite_%' ORDER BY type, name");
            using var reader = await command.ExecuteReaderAsync(token);
            while (await reader.ReadAsync(token)) result.Add(new DbObject(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)));
            return result;
        });
        _schemaObjects.Clear();
        _schemaObjects.AddRange(objects);
        FilterObjectTree();
        UpdateOverview();
    }

    void FilterObjectTree()
    {
        var selected = (_objectTree.SelectedNode?.Tag as DbObject)?.Name ?? _currentObject;
        var filter = _objectFilter.Text.Trim();
        var objects = _schemaObjects.Where(obj => filter.Length == 0 || obj.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) || obj.Table.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();
        _rebuildingTree = true;
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
        _rebuildingTree = false;
    }

    async Task ObjectSelectedAsync()
    {
        if (_objectTree.SelectedNode?.Tag is not DbObject obj) return;
        await LoadStructureAsync(obj);
        if (obj.Type is "table" or "view") await LoadObjectAsync(obj.Name, obj.Type);
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
        var node = _objectTree.Nodes.Cast<TreeNode>().SelectMany(group => group.Nodes.Cast<TreeNode>()).FirstOrDefault(n => n.Tag is DbObject obj && obj.Name == name);
        if (node is not null)
        {
            _rebuildingTree = true;
            try { _objectTree.SelectedNode = node; }
            finally { _rebuildingTree = false; }
        }
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
        if (_connection is null || _currentObject is null || _activeBrowseTask is not null) return;
        var requestedPage = Math.Max(0, page ?? _page);
        var name = _currentObject;
        var type = _currentObjectType;
        var where = _whereBox.Text.Trim();
        var sort = _sortColumn;
        var descending = _sortDescending;
        try
        {
            var watch = Stopwatch.StartNew();
            var result = await BrowseWorkAsync(token => ReadPageAsync(name, type, where, sort, descending, requestedPage, token));
            // Publish values and identities together only after a successful read.
            _displayColumns.Clear();
            _displayColumns.AddRange(result.Columns);
            _keyColumns.Clear();
            _keyColumns.AddRange(result.Keys);
            _rowKeys.Clear();
            _rowKeys.AddRange(result.RowKeys);
            _page = requestedPage;
            FillGrid(_dataGrid, result.Columns, result.Rows);
            foreach (DataGridViewColumn column in _dataGrid.Columns)
                column.HeaderCell.SortGlyphDirection = column.HeaderText == sort
                    ? descending ? SortOrder.Descending : SortOrder.Ascending : SortOrder.None;
            _tableTitle.Text = name + (type == "view" ? "   /   Read-only view" : "   /   Table");
            _pageLabel.Text = $"Page {_page + 1:N0}";
            _rowCountLabel.Text = result.Rows.Count == 0 ? "No rows" : $"Rows {(long)_page * PageSize + 1:N0} - {(long)_page * PageSize + result.Rows.Count:N0}";
            _previousButton.Enabled = _page > 0;
            _nextButton.Enabled = result.HasMore;
            _goToPage.Value = Math.Min(_goToPage.Maximum, _page + 1);
            _statusRows.Text = $"{result.Rows.Count:N0} displayed";
            SetStatus($"Loaded {name} in {watch.Elapsed.TotalMilliseconds:N0} ms");
        }
        catch (OperationCanceledException) { SetStatus("Browse cancelled"); ClearBrowseRows(); }
        catch (Exception ex) { ClearBrowseRows(); ShowError("Could not load data. Check the WHERE expression.", ex); }
    }

    void ClearBrowseRows()
    {
        ClearGrid(_dataGrid);
        _rowKeys.Clear();
        _keyColumns.Clear();
        _previousButton.Enabled = _nextButton.Enabled = false;
        _rowCountLabel.Text = "No data loaded";
    }

    async Task<BrowsePage> ReadPageAsync(string name, string? type, string where, string? sort, bool descending, int page, CancellationToken token)
    {
        var columns = await GetColumnsAsync(name);
        token.ThrowIfCancellationRequested();
        var display = columns.Where(c => c.HiddenKind != 1).Select(c => c.Name).ToList();
        var keys = columns.Where(c => c.PrimaryKeyOrder > 0).OrderBy(c => c.PrimaryKeyOrder).Select(c => c.Name).ToList();
        if (type == "table" && !await IsWithoutRowIdAsync(name))
        {
            var alias = new[] { "rowid", "_rowid_", "oid" }.FirstOrDefault(candidate => !columns.Any(c => c.Name.Equals(candidate, StringComparison.OrdinalIgnoreCase)));
            if (alias is not null) { keys.Clear(); keys.Add(alias); }
            else if (columns.Where(c => c.PrimaryKeyOrder > 0).Any(c => !c.NotNull)) keys.Clear();
        }
        var ordering = new List<string>();
        if (sort is not null) ordering.Add(Quote(sort) + (descending ? " DESC" : " ASC"));
        ordering.AddRange(keys.Where(k => !k.Equals(sort, StringComparison.OrdinalIgnoreCase)).Select(Quote));
        var order = ordering.Count == 0 ? "" : " ORDER BY " + string.Join(", ", ordering);
        var projection = string.Concat(keys.Select((key, i) => $", {Quote(key)} AS {Quote($"__studio_key_{i}")}"));
        var suffix = where.Length == 0 ? "" : " WHERE " + where;
        using var command = CreateCommand($"SELECT *{projection} FROM {Quote(name)}{suffix}{order} LIMIT @limit OFFSET @offset");
        command.Parameters.AddWithValue("@limit", PageSize + 1);
        command.Parameters.AddWithValue("@offset", (long)page * PageSize);
        using var reader = await command.ExecuteReaderAsync(token);
        var rows = new List<object?[]>();
        var identities = new List<object?[]>();
        var hasMore = false;
        while (await reader.ReadAsync(token))
        {
            if (rows.Count == PageSize) { hasMore = true; break; }
            rows.Add(Enumerable.Range(0, display.Count).Select(i => DbValue(reader, i)).ToArray());
            identities.Add(Enumerable.Range(display.Count, keys.Count).Select(i => DbValue(reader, i)).ToArray());
        }
        return new BrowsePage(display, keys, rows, identities, hasMore);
    }

    async Task<T> BrowseWorkAsync<T>(Func<CancellationToken, Task<T>> work)
    {
        if (_queryCancellation is not null) throw new InvalidOperationException("Wait for the current operation or cancel it first.");
        using var cancellation = new CancellationTokenSource();
        _queryCancellation = cancellation;
        var connection = _connection!;
        ToggleQueryRunning(true);
        SetStatus("Loading...  Use Stop to cancel");
        try
        {
            var task = Task.Run(async () =>
            {
                using var registration = cancellation.Token.Register(() => connection?.Interrupt());
                return await work(cancellation.Token);
            });
            _activeBrowseTask = task;
            return await task;
        }
        catch (SqliteException) when (cancellation.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellation.Token);
        }
        finally
        {
            _activeBrowseTask = null;
            _queryCancellation = null;
            _hasChanges = _transaction is not null && _connection?.InTransaction == true;
            UpdatePendingState();
            ToggleQueryRunning(false);
            SetStatus(_hasChanges ? "Ready · Changes pending commit" : "Ready");
            if (_closeAfterOperation) { _closeAfterOperation = false; BeginInvoke(new Action(Close)); }
        }
    }

    async Task CountRowsAsync()
    {
        if (_connection is null || _currentObject is null) return;
        var name = _currentObject;
        var where = _whereBox.Text.Trim();
        try
        {
            var count = await BrowseWorkAsync(async token =>
            {
                using var command = CreateCommand($"SELECT COUNT(*) FROM {Quote(name)}" + (where.Length == 0 ? "" : " WHERE " + where));
                using var reader = await command.ExecuteReaderAsync(token);
                await reader.ReadAsync(token);
                return reader.GetInt64(0);
            });
            _rowCountLabel.Text = $"{count:N0} matching rows";
            SetStatus($"Counted {name}");
        }
        catch (OperationCanceledException) { SetStatus("Row count cancelled"); }
    }

    sealed record BrowsePage(List<string> Columns, List<string> Keys, List<object?[]> Rows, List<object?[]> RowKeys, bool HasMore);

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

        var snapshot = await BrowseWorkAsync(async token =>
        {
            var columns = await GetColumnsAsync(obj.Name);
        var indexRows = new List<object?[]>();
        await using (var command = CreateCommand($"PRAGMA index_list({Quote(obj.Name)})"))
        await using (var reader = await command.ExecuteReaderAsync(token))
        {
            while (await reader.ReadAsync(token))
            {
                var indexName = reader.GetString(1);
                var indexColumns = new List<string>();
                await using var detail = CreateCommand($"PRAGMA index_info({Quote(indexName)})");
                await using var detailReader = await detail.ExecuteReaderAsync(token);
                while (await detailReader.ReadAsync(token)) indexColumns.Add(detailReader.IsDBNull(2) ? "<expression>" : detailReader.GetString(2));
                indexRows.Add([indexName, reader.GetInt64(2) != 0 ? "Yes" : "No", reader.GetString(3), reader.FieldCount > 4 && reader.GetInt64(4) != 0 ? "Yes" : "No", string.Join(", ", indexColumns)]);
            }
        }


        var foreignRows = new List<object?[]>();
        await using (var command = CreateCommand($"PRAGMA foreign_key_list({Quote(obj.Name)})"))
        await using (var reader = await command.ExecuteReaderAsync(token))
        {
            while (await reader.ReadAsync(token))
                foreignRows.Add([reader.GetInt64(0), reader.GetString(3), reader.GetString(2), reader.IsDBNull(4) ? "" : reader.GetString(4), reader.GetString(5), reader.GetString(6)]);
        }
            return (columns, indexRows, foreignRows);
        });
        FillGrid(_columnsGrid, ["Name", "Type", "Nullable", "Default", "Primary key", "Generated/hidden"],
            snapshot.columns.Select(c => new object?[] { c.Name, c.Type, c.NotNull ? "No" : "Yes", c.DefaultValue, c.PrimaryKeyOrder == 0 ? "" : c.PrimaryKeyOrder, c.HiddenKind == 0 ? "No" : c.HiddenKind is 2 or 3 ? "Generated" : "Hidden" }));
        FillGrid(_indexesGrid, ["Name", "Unique", "Origin", "Partial", "Columns"], snapshot.indexRows);
        FillGrid(_foreignKeysGrid, ["ID", "From", "Referenced table", "To", "On update", "On delete"], snapshot.foreignRows);
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
        return Regex.IsMatch(SqlText.Unquoted(sql), @"\bWITHOUT\s+ROWID\b", RegexOptions.IgnoreCase);
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
        var valuesToBind = included.Select((item, i) => ($"@v{i}", item.Second.Value)).ToList();
        await ApplyEditsAsync([(sql, valuesToBind, (int?)1)]);
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
        var assignments = string.Join(", ", columns.Select((c, i) => $"{Quote(c.Name)}=@v{i}"));
        var parameters = columns.Select((_, i) => ($"@v{i}", dialog.Values[i].Value)).ToList();
        var predicate = SnapshotPredicate(rowIndex.Value, parameters);
        await ApplyEditsAsync([($"UPDATE {Quote(_currentObject!)} SET {assignments} WHERE {predicate}", parameters, (int?)1)]);
        MarkChanged("Row updated");
        await LoadCurrentObjectAsync(_page);
    }

    async Task DuplicateRowAsync()
    {
        if (!CanEditCurrent()) return;
        var rowIndex = SelectedRowIndex();
        if (rowIndex is null) { InformSelectRow(); return; }
        var columns = (await GetColumnsAsync(_currentObject!)).Where(c => c.HiddenKind == 0).ToList();
        var rowIdTable = !await IsWithoutRowIdAsync(_currentObject!);
        var hasPkIndex = Convert.ToInt64(await ExecuteScalarAsync($"SELECT COUNT(*) FROM pragma_index_list(@table) WHERE origin='pk'", ("@table", _currentObject)), CultureInfo.InvariantCulture) > 0;
        var insertColumns = columns.Where(c => !(rowIdTable && !hasPkIndex && c.PrimaryKeyOrder > 0 && c.Type.Equals("INTEGER", StringComparison.OrdinalIgnoreCase))).ToList();
        var values = insertColumns.Select(c => _dataGrid.Rows[rowIndex.Value].Cells[_displayColumns.IndexOf(c.Name)].Value).ToArray();
        using var dialog = new RecordEditorDialog("Duplicate row", insertColumns, values, allowDefault: true, _dark);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        var included = insertColumns.Zip(dialog.Values).Where(p => p.Second.Kind != EditorValueKind.Default).ToList();
        var sql = included.Count == 0
            ? $"INSERT INTO {Quote(_currentObject!)} DEFAULT VALUES"
            : $"INSERT INTO {Quote(_currentObject!)} ({string.Join(", ", included.Select(p => Quote(p.First.Name)))}) VALUES ({string.Join(", ", included.Select((_, i) => $"@v{i}"))})";
        var parameters = included.Select((item, i) => ($"@v{i}", item.Second.Value)).ToList();
        await ApplyEditsAsync([(sql, parameters, (int?)1)]);
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
        var edits = new List<(string Sql, List<(string, object?)> Parameters, int? Expected)>();
        foreach (var index in indices)
        {
            var parameters = new List<(string, object?)>();
            edits.Add(($"DELETE FROM {Quote(_currentObject!)} WHERE {SnapshotPredicate(index, parameters)}", parameters, 1));
        }
        await ApplyEditsAsync(edits);
        MarkChanged($"Deleted {indices.Count:N0} row(s)");
        await LoadCurrentObjectAsync(_page);
    }

    string SnapshotPredicate(int row, List<(string, object?)> parameters)
    {
        var predicate = KeyPredicate();
        for (var i = 0; i < _keyColumns.Count; i++) parameters.Add(($"@k{i}", _rowKeys[row][i]));
        // Compare original values as well as identity: a concurrent edit must not be overwritten.
        for (var i = 0; i < _displayColumns.Count; i++)
        {
            predicate += $" AND {Quote(_displayColumns[i])} IS @old{i}";
            parameters.Add(($"@old{i}", _dataGrid.Rows[row].Cells[i].Value));
        }
        return predicate;
    }

    async Task ApplyEditsAsync(List<(string Sql, List<(string Name, object? Value)> Parameters, int? Expected)> edits)
    {
        await BrowseWorkAsync(token => AtomicWriteAsync(() =>
        {
            foreach (var edit in edits)
            {
                using var command = CreateCommand(edit.Sql);
                foreach (var parameter in edit.Parameters) command.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);
                var changed = command.ExecuteNonQueryCore(token);
                if (edit.Expected is int expected && changed != expected)
                    throw new InvalidOperationException("The row changed or no longer exists. This operation was rolled back. Refresh the table before trying again.");
            }
            return Task.FromResult(true);
        }, token));
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
        if (_queryCancellation is not null) return false;
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
            await BrowseWorkAsync(async _ => { await _transaction!.CommitAsync(); return true; });
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
        await BrowseWorkAsync(async _ => { await _transaction!.RollbackAsync(); return true; });
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
        if (_queryCancellation is not null) return;
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
            FillGrid(_resultGrid, result.Columns, result.Rows);
            if (_queryTabs.SelectedTab?.Tag is QueryDocument doc) doc.Result = result;
            mutating = result.Mutated;
            if (mutating) MarkChanged($"SQL completed; {result.Affected:N0} row(s) affected");
            var summary = $"{DateTime.Now:HH:mm:ss}  {result.Statements} statement(s), {stopwatch.Elapsed.TotalMilliseconds:N0} ms" + (result.Truncated ? $", results limited to {ResultLimit:N0}" : "");
            AppendLog(summary + "\n" + text.Trim() + "\n");
            AddHistory(text.Trim());
            SetStatus(summary);
            _statusRows.Text = _resultGrid.RowCount > 0 ? $"{_resultGrid.RowCount:N0} result rows" + (result.Truncated ? " (truncated)" : "") : "";

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
            ClearGrid(_resultGrid);
            if (_queryTabs.SelectedTab?.Tag is QueryDocument failed) failed.Result = null;
            AppendLog($"{DateTime.Now:HH:mm:ss}  ERROR: {ex.Message}\n");
            ShowError("SQL execution failed", ex);
        }
        finally
        {
            _runningCommand = null;
            _activeQueryTask = null;
            _queryCancellation?.Dispose();
            _queryCancellation = null;
            _hasChanges = _transaction is not null && _connection?.InTransaction == true;
            UpdatePendingState();
            ToggleQueryRunning(false);
            if (_closeAfterOperation) { _closeAfterOperation = false; BeginInvoke(new Action(Close)); }
        }
        if (mutating && !_closeAfterOperation && !IsDisposed) await RefreshSchemaAsync(preserveSelection: true);
        if (mutating && _currentObject is not null) await LoadCurrentObjectAsync(_page);
    }

    async Task<SqlBatchResult> ExecuteSqlBatchAsync(string sql, IReadOnlyDictionary<string, object?> parameters, bool mutating, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        SqlText.ValidateScript(sql);
        var priorTransaction = _transaction is not null;
        var savepoint = false;
        var wrote = false;
        using var registration = token.Register(_connection!.Interrupt);
        try
        {
            using var command = CreateCommand(sql);
            foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Key, parameter.Value ?? DBNull.Value);
            _runningCommand = command;
            var columns = new List<string>();
            var rows = new List<object?[]>();
            var truncated = false;
            var statements = 0;
            using var reader = await command.ExecuteReaderAsync(token);
            do
            {
                token.ThrowIfCancellationRequested();
                if (!reader.HasStatement) break;
                statements++;
                SqlText.ValidateStatement(reader.StatementSql);
                var writes = !reader.IsReadOnly && !SqlText.IsExplain(reader.StatementSql);
                if (writes && !savepoint)
                {
                    await EnsureTransactionAsync();
                    _connection.ExecuteImmediate("SAVEPOINT studio_batch");
                    savepoint = true;
                }
                wrote |= writes;
                if (reader.FieldCount == 0) continue;
                columns.Clear(); rows.Clear(); truncated = false;
                for (var i = 0; i < reader.FieldCount; i++) columns.Add(reader.GetName(i));
                while (await reader.ReadAsync(token))
                {
                    if (rows.Count < ResultLimit)
                        rows.Add(Enumerable.Range(0, reader.FieldCount).Select(i => DbValue(reader, i)).ToArray());
                    else
                    {
                        truncated = true;
                        if (reader.IsReadOnly) { reader.SkipRemainingRows(); break; }
                    }
                }
            } while (await reader.NextResultAsync(token));
            var affected = reader.RecordsAffected;
            if (savepoint) _connection.ExecuteImmediate("RELEASE studio_batch");
            return new SqlBatchResult(columns, rows, Math.Max(0, affected), truncated, wrote, statements);
        }
        catch
        {
            if (savepoint && _connection.InTransaction)
            {
                _connection.ExecuteImmediate("ROLLBACK TO studio_batch");
                _connection.ExecuteImmediate("RELEASE studio_batch");
                if (!priorTransaction) await _transaction!.RollbackAsync();
            }
            if (!_connection.InTransaction && _transaction is not null)
            {
                _transaction.MarkCompleted();
                await _transaction.DisposeAsync();
                _transaction = null;
            }
            throw;
        }
        finally { _runningCommand = null; }
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
        var prepared = SqlText.Parameters(sql);
        return new PreparedSql(prepared.Sql, prepared.Names);
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
        _queryTabs.Enabled = !running;
        _homeTab.Enabled = !running;
        _activity.Visible = running;
        _cancelButton.Enabled = running;
        _objectTree.Enabled = !running;
        _browseTab.Enabled = !running;
        _schemaTab.Enabled = !running;
        _menu.Enabled = !running;
        _objectFilter.Enabled = !running;
        foreach (ToolStripItem item in _toolbar.Items)
            if (item is ToolStripButton) item.Enabled = !running;
        _stopButton.Enabled = running;
        // Stop stays accessible while connection-dependent actions are disabled.
        UseWaitCursor = false;
    }

    sealed class QueryDocument
    {
        public required RichTextBox Editor { get; init; }
        public required string Title { get; set; }
        public string? Path { get; set; }
        public SqlBatchResult? Result { get; set; }
    }

    QueryDocument NewQuery(string text = "", string? title = null, bool activate = true)
    {
        if (activate) _workspace.SelectedTab = _sqlTab;
        var editor = new RichTextBox
        {
            Dock = DockStyle.Fill, BorderStyle = BorderStyle.None, AcceptsTab = true,
            WordWrap = false, DetectUrls = false, Font = new Font("Cascadia Mono", 10.5f),
            Text = text, BackColor = _dark ? DarkBackground : Color.White,
            ForeColor = _dark ? DarkText : Color.FromArgb(33, 37, 41), HideSelection = false
        };
        var doc = new QueryDocument { Editor = editor, Title = title ?? $"Query {++_queryNumber}" };
        var page = new TabPage(doc.Title) { Tag = doc, Padding = new Padding(10), BackColor = editor.BackColor, ForeColor = editor.ForeColor };
        page.Controls.Add(editor);
        editor.Modified = false;
        editor.TextChanged += (_, _) => { editor.Modified = true; page.Text = doc.Title + " *"; };
        _queryTabs.TabPages.Add(page);
        _ = _queryTabs.Handle;
        _queryTabs.SelectedTab = page;
        ClearGrid(_resultGrid);
        if (activate) { _workspace.SelectedTab = _sqlTab; editor.Focus(); }
        return doc;
    }

    async Task OpenSqlFileAsync()
    {
        using var dialog = new OpenFileDialog { Filter = "SQL scripts (*.sql)|*.sql|All files (*.*)|*.*" };
        if (dialog.ShowDialog(this) == DialogResult.OK) await OpenSqlPathAsync(dialog.FileName);
    }

    async Task OpenSqlPathAsync(string path)
    {
        if (new FileInfo(path).Length > 16 * 1024 * 1024) throw new IOException("The SQL editor accepts files up to 16 MB. Split larger scripts before opening them.");
        var text = await File.ReadAllTextAsync(path);
        var doc = NewQuery(text, Path.GetFileName(path));
        doc.Path = path;
    }

    async Task SaveSqlFileAsync()
    {
        if (_queryTabs.SelectedTab?.Tag is QueryDocument doc) await SaveDocumentAsync(doc);
    }

    async Task<bool> SaveDocumentAsync(QueryDocument doc)
    {
        using var dialog = new SaveFileDialog { Filter = "SQL scripts (*.sql)|*.sql", DefaultExt = "sql", FileName = doc.Path ?? "query.sql" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return false;
        ValidateOutputPath(dialog.FileName);
        var text = doc.Editor.Text;
        var temporary = dialog.FileName + "." + Guid.NewGuid().ToString("N") + ".partial";
        try
        {
            await File.WriteAllTextAsync(temporary, text, new UTF8Encoding(false));
            File.Move(temporary, dialog.FileName, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        doc.Path = dialog.FileName;
        doc.Title = Path.GetFileName(doc.Path);
        doc.Editor.Modified = doc.Editor.Text != text;
        var page = _queryTabs.TabPages.Cast<TabPage>().FirstOrDefault(p => ReferenceEquals(p.Tag, doc));
        if (page is not null) page.Text = doc.Title + (doc.Editor.Modified ? " *" : "");
        SetStatus("Saved " + doc.Path);
        return true;
    }

    async Task CloseQueryAsync()
    {
        if (_queryCancellation is not null || _queryTabs.SelectedTab is not { Tag: QueryDocument doc } page) return;
        if (doc.Editor.Modified)
        {
            var answer = MessageBox.Show(this, $"Save changes to {doc.Title}?", "Close query", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
            if (answer == DialogResult.Cancel || (answer == DialogResult.Yes && !await SaveDocumentAsync(doc))) return;
        }
        _queryTabs.TabPages.Remove(page);
        page.Dispose();
        if (_queryTabs.TabCount == 0) NewQuery();
    }

    void FindInQuery()
    {
        var needle = _findSql.Text;
        if (needle.Length == 0) return;
        var start = _sqlEditor.SelectionStart + _sqlEditor.SelectionLength;
        var found = _sqlEditor.Text.IndexOf(needle, start, StringComparison.OrdinalIgnoreCase);
        if (found < 0) found = _sqlEditor.Text.IndexOf(needle, StringComparison.OrdinalIgnoreCase);
        if (found < 0) { SetStatus("No matches in this query"); return; }
        _sqlEditor.Select(found, needle.Length);
        _sqlEditor.ScrollToCaret();
        _sqlEditor.Focus();
    }

    void FormatSql()
    {
        var selected = _sqlEditor.SelectionLength > 0;
        var source = selected ? _sqlEditor.SelectedText : _sqlEditor.Text;
        var formatted = SqlText.Format(source);
        if (selected) _sqlEditor.SelectedText = formatted;
        else { _sqlEditor.SelectAll(); _sqlEditor.SelectedText = formatted; }
    }

    async Task RunCheckAsync(string sql, string title)
    {
        if (_connection is null) return;
        var result = await BrowseWorkAsync(async token =>
        {
            var rows = new List<object?[]>();
            var names = new List<string>();
            using var command = CreateCommand(sql);
            using var reader = await command.ExecuteReaderAsync(token);
            for (var i = 0; i < reader.FieldCount; i++) names.Add(reader.GetName(i));
            while (await reader.ReadAsync(token))
            {
                if (rows.Count == ResultLimit) { names[0] += " (display limited to 5,000 rows)"; break; }
                rows.Add(Enumerable.Range(0, reader.FieldCount).Select(i => DbValue(reader, i)).ToArray());
            }
            return (names, rows);
        });
        using var dialog = new ResultDialog(title, result.names, result.rows, _dark);
        dialog.ShowDialog(this);
    }

    async Task ExecuteMaintenanceAsync(string sql, string success)
    {
        if (_connection is null || _readOnly) return;
        if (_transaction is not null) { MessageBox.Show(this, "Commit or roll back pending changes first."); return; }
        await BrowseWorkAsync(token => { using var command = CreateCommand(sql); return Task.FromResult(command.ExecuteNonQueryCore(token)); });
        SetStatus(success);
    }

    async Task VacuumAsync()
    {
        if (_connection is null || _readOnly) return;
        if (_transaction is not null) { MessageBox.Show(this, "Commit or roll back pending changes before vacuuming."); return; }
        if (MessageBox.Show(this, "VACUUM rewrites the database and can take time. Continue?", "Vacuum", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        await BrowseWorkAsync(token => { using var command = CreateCommand("VACUUM"); return Task.FromResult(command.ExecuteNonQueryCore(token)); });
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

    void ValidateOutputPath(string path)
    {
        if (_databasePath is null) return;
        var output = Path.GetFullPath(path);
        foreach (var suffix in new[] { "", "-wal", "-shm", "-journal" })
            if (output.Equals(Path.GetFullPath(_databasePath + suffix), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Choose a destination other than the open database or its journal files.");
    }

    async Task ExportAllAsync()
    {
        if (_connection is null || _currentObject is null) { SetStatus("Select a table or view to export"); return; }
        using var dialog = new SaveFileDialog { Filter = "CSV (*.csv)|*.csv|JSON (*.json)|*.json|SQL INSERT statements (*.sql)|*.sql", FileName = _currentObject + ".csv", AddExtension = true };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        ValidateOutputPath(dialog.FileName);
        var table = _currentObject;
        var where = _whereBox.Text.Trim();
        var query = $"SELECT * FROM {Quote(table)}" + (where.Length == 0 ? "" : " WHERE " + where);
        var format = dialog.FilterIndex;
        if (format == 3)
        {
            var columns = await GetColumnsAsync(table);
            query = $"SELECT {string.Join(',', columns.Where(c => c.HiddenKind == 0).Select(c => Quote(c.Name)))} FROM {Quote(table)}" + (where.Length == 0 ? "" : " WHERE " + where);
        }
        var progress = new Progress<string>(message => { if (_queryCancellation is not null) SetStatus(message); });
        var count = await BrowseWorkAsync(token => Task.FromResult(DataTransfer.Export(_connection!, query, table, dialog.FileName, format, token, progress)));
        SetStatus($"Exported all {count:N0} matching rows to {Path.GetFileName(dialog.FileName)}");
    }

    async Task BackupDatabaseAsync()
    {
        if (_connection is null) { SetStatus("Open a database to back up"); return; }
        if (_transaction is not null) { MessageBox.Show(this, "Commit or roll back pending changes before creating a backup.", "Backup"); return; }
        using var dialog = new SaveFileDialog { Filter = "SQLite database (*.sqlite)|*.sqlite", FileName = Path.GetFileNameWithoutExtension(_databasePath ?? "database") + "-backup-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".sqlite" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        ValidateOutputPath(dialog.FileName);
        if (File.Exists(dialog.FileName)) throw new IOException("Choose a new filename for the backup; existing databases are not overwritten.");
        var progress = new Progress<string>(message => { if (_queryCancellation is not null) SetStatus(message); });
        await BrowseWorkAsync(token => { DataTransfer.Backup(_connection!, dialog.FileName, token, progress); return Task.FromResult(true); });
        SetStatus("Backup verified and saved: " + dialog.FileName);
    }

    async Task ImportDataAsync()
    {
        if (_connection is null || _readOnly) { SetStatus("Open a writable database before importing"); return; }
        using var file = new OpenFileDialog { Filter = "CSV or JSON (*.csv;*.json)|*.csv;*.json" };
        if (file.ShowDialog(this) != DialogResult.OK) return;
        var source = await BrowseWorkAsync(token => Task.FromResult(ImportSource.Open(file.FileName, token)));
        using var dialog = new ImportPreviewDialog(source, _dark);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        var name = dialog.TableName;
        var progress = new Progress<string>(message => { if (_queryCancellation is not null) SetStatus(message); });
        var count = await BrowseWorkAsync(token => AtomicWriteAsync(() => Task.FromResult(DataTransfer.Import(_connection!, source, name, token, progress)), token));
        MarkChanged($"Imported {count:N0} rows into {name}");
        await RefreshSchemaAsync();
        await LoadObjectAsync(name, "table");
    }

    async Task<T> AtomicWriteAsync<T>(Func<Task<T>> operation, CancellationToken token)
    {
        var prior = _transaction is not null;
        await EnsureTransactionAsync();
        _connection!.ExecuteImmediate("SAVEPOINT studio_edit");
        try
        {
            token.ThrowIfCancellationRequested();
            var result = await operation();
            token.ThrowIfCancellationRequested();
            _connection.ExecuteImmediate("RELEASE studio_edit");
            return result;
        }
        catch
        {
            if (_connection.InTransaction)
            {
                _connection.ExecuteImmediate("ROLLBACK TO studio_edit");
                _connection.ExecuteImmediate("RELEASE studio_edit");
                if (!prior) await _transaction!.RollbackAsync();
            }
            if (!_connection.InTransaction && _transaction is not null)
            {
                _transaction.MarkCompleted();
                await _transaction.DisposeAsync();
                _transaction = null;
            }
            throw;
        }
    }

    void ExportGrid(DataGridView grid, string filter, string extension, Action<Stream, DataGridView> writer)
    {
        if (grid.ColumnCount == 0) return;
        using var dialog = new SaveFileDialog { Filter = filter, DefaultExt = extension, AddExtension = true };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            ValidateOutputPath(dialog.FileName);
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
        var headings = DataTransfer.UniqueNames(grid.Columns.Cast<DataGridViewColumn>().Select(column => column.HeaderText));
        using var json = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
        json.WriteStartArray();
        foreach (DataGridViewRow row in grid.Rows)
        {
            json.WriteStartObject();
            for (var i = 0; i < grid.ColumnCount; i++)
            {
                json.WritePropertyName(headings[i]);
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
        if (grid is BufferedGrid buffered) { buffered.SetData(columns.ToArray(), rows.ToList()); return; }
        ClearGrid(grid);
        foreach (var column in columns) AddGridColumn(grid, column, column);
        foreach (var row in rows) grid.Rows.Add(row.Select(v => v ?? DBNull.Value).ToArray());
    }

    static void ClearGrid(DataGridView grid)
    {
        if (grid is BufferedGrid buffered) { buffered.SetData([], []); return; }
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
        _browseTab.Enabled = connected;
        _schemaTab.Enabled = connected;
        UpdateOverview();
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
        if (_workspace is StudioTabs mainTabs) { mainTabs.Dark = _dark; mainTabs.Invalidate(); }
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
        _recentFiles.BackColor = surface;
        _recentFiles.ForeColor = text;
        ToolStripManager.Renderer = new ToolStripProfessionalRenderer(new StudioMenuColors(_dark));
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
        grid.AlternatingRowsDefaultCellStyle.BackColor = surface == Color.White ? Color.FromArgb(248, 249, 251) : Color.FromArgb(29, 41, 57);
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
                case StudioTabs tabs: tabs.Dark = _dark; tabs.BackColor = background; tabs.ForeColor = text; tabs.Invalidate(); break;
                case RichTextBox editor: editor.BackColor = _dark ? DarkBackground : Color.White; editor.ForeColor = text; break;
                case ToolStrip strip: strip.BackColor = surface; strip.ForeColor = text; break;
                case TextBox box: box.BackColor = _dark ? Color.FromArgb(35, 38, 42) : Color.White; box.ForeColor = text; break;
                case NumericUpDown number: number.BackColor = surface; number.ForeColor = text; break;
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
        if (e.KeyCode == Keys.Escape && _queryCancellation is not null) { e.SuppressKeyPress = true; CancelQuery(); return; }
        if (_queryCancellation is not null) return;
        if (e.Control && e.KeyCode == Keys.F) { e.SuppressKeyPress = true; _workspace.SelectedTab = _sqlTab; _findSql.Focus(); return; }
        if (e.Control && e.Shift && e.KeyCode == Keys.N) { e.SuppressKeyPress = true; NewQuery(); return; }
        if (e.KeyCode == Keys.Delete && _dataGrid.Focused) { e.SuppressKeyPress = true; await SafeUiAsync(DeleteRowsAsync); return; }
        if (e.KeyCode == Keys.F9) { e.SuppressKeyPress = true; _workspace.SelectedTab = _sqlTab; await SafeUiAsync(() => ExecuteSqlAsync(false)); }
        if (e.Control && e.KeyCode == Keys.C && (_dataGrid.Focused || _resultGrid.Focused)) { e.SuppressKeyPress = true; CopyRows(); }
    }

    void ShowShortcuts() => MessageBox.Show(this,
        "Ctrl+O   Open database\nCtrl+N   Create database\nCtrl+S   Commit changes\nCtrl+Shift+S   Roll back\nF5   Refresh schema\nF9   Run selected/all SQL\nCtrl+L   SQL workspace\nCtrl+E   Edit row\nCtrl+Insert   Add row\nDelete   Delete selected rows\nCtrl+T   Toggle theme",
        "Keyboard shortcuts", MessageBoxButtons.OK, MessageBoxIcon.Information);

    async void OnClosing(object? sender, FormClosingEventArgs e)
    {
        if (_queryCancellation is not null)
        {
            e.Cancel = true;
            _closeAfterOperation = true;
            CancelQuery();
            return;
        }
        if (_queryTabs.TabPages.Cast<TabPage>().Any(p => p.Tag is QueryDocument doc && doc.Editor.Modified) && MessageBox.Show(this, "Close without saving the modified query tabs?", "Unsaved SQL", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
        {
            e.Cancel = true;
            return;
        }
        SavePreferences();
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
        catch (OperationCanceledException) { SetStatus("Operation cancelled"); }
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
    sealed record SqlBatchResult(List<string> Columns, List<object?[]> Rows, int Affected, bool Truncated, bool Mutated = false, int Statements = 0);
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
        SuspendLayout();
        _columns = columns;
        AutoScaleDimensions = new SizeF(96, 96);
        AutoScaleMode = AutoScaleMode.Dpi;
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
            var editor = new TextBox { Dock = DockStyle.Fill, Text = Display(value), Multiline = true, ScrollBars = ScrollBars.Vertical };
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
        DialogStyle.Apply(this, dark);
        ResumeLayout(true);
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
        catch (Exception ex) when (ex is FormatException or OverflowException)
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
        SuspendLayout();
        _names = names.ToList();
        AutoScaleDimensions = new SizeF(96, 96);
        AutoScaleMode = AutoScaleMode.Dpi;
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
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
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
        DialogStyle.Apply(this, dark);
        ResumeLayout(true);
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
        catch (Exception ex) when (ex is FormatException or OverflowException)
        {
            MessageBox.Show(this, "One of the numeric parameter values is not valid.", "Parameters", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }
}

sealed class ResultDialog : Form
{
    public ResultDialog(string title, IReadOnlyList<string> columns, IReadOnlyList<object?[]> rows, bool dark)
    {
        SuspendLayout();
        AutoScaleDimensions = new SizeF(96, 96);
        AutoScaleMode = AutoScaleMode.Dpi;
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
        DialogStyle.Apply(this, dark);
        ResumeLayout(true);
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

// Store bounded result arrays once; WinForms requests values only for visible cells.
sealed class BufferedGrid : DataGridView
{
    public string EmptyMessage = "No rows to display";
    List<object?[]> _values = [];
    public BufferedGrid()
    {
        DoubleBuffered = true;
        VirtualMode = true;
        CellValueNeeded += (_, e) =>
        {
            if (e.RowIndex < _values.Count && e.ColumnIndex < _values[e.RowIndex].Length)
                e.Value = _values[e.RowIndex][e.ColumnIndex] ?? DBNull.Value;
        };
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (RowCount > 0) return;
        var area = ClientRectangle;
        area.Y += ColumnHeadersVisible && ColumnCount > 0 ? ColumnHeadersHeight : 0;
        area.Height -= area.Y;
        TextRenderer.DrawText(e.Graphics, ColumnCount > 0 ? "No matching rows" : EmptyMessage, Font, area,
            Color.FromArgb(119, 139, 156), TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter | TextFormatFlags.WordBreak);
    }

    public void SetData(string[] columns, List<object?[]> rows)
    {
        SuspendLayout();
        try
        {
            var scale = DeviceDpi / 96f;
            RowCount = 0;
            RowTemplate.Height = (int)(28 * scale);
            ColumnHeadersHeight = (int)(34 * scale);
            _values = rows;
            if (!Columns.Cast<DataGridViewColumn>().Select(c => c.HeaderText).SequenceEqual(columns))
            {
                Columns.Clear();
                for (var i = 0; i < columns.Length; i++)
                    Columns.Add(new DataGridViewTextBoxColumn
                    {
                        Name = $"c{i}", HeaderText = columns[i], SortMode = DataGridViewColumnSortMode.Programmatic,
                        MinimumWidth = (int)(80 * scale), Width = (int)(Math.Clamp(columns[i].Length * 9 + 45, 130, 280) * scale)
                    });
            }
            RowCount = rows.Count;
        }
        finally { ResumeLayout(); Invalidate(); }
    }
}

sealed record SqliteParameter(string ParameterName, object? Value);

sealed class SqliteParameterCollection : IEnumerable<SqliteParameter>
{
    readonly List<SqliteParameter> _items = [];
    public void Clear() => _items.Clear();

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
    IntPtr _sqlBuffer;
    readonly int _sqlLength;
    int _sqlOffset;
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
        _sqlBuffer = NativeSqlite.AllocUtf8(sql, out _sqlLength);
        _startingChanges = connection.TotalChanges;
        try { PrepareNextStatement(); }
        catch { Marshal.FreeHGlobal(_sqlBuffer); _sqlBuffer = IntPtr.Zero; throw; }
    }

    public int FieldCount => _statement == IntPtr.Zero ? 0 : NativeSqlite.sqlite3_column_count(_statement);
    public bool HasStatement => _statement != IntPtr.Zero;
    public int RecordsAffected => Math.Max(0, _connection.TotalChanges - _startingChanges);
    public string StatementSql => _statement == IntPtr.Zero ? "" : Marshal.PtrToStringUTF8(NativeSqlite.sqlite3_sql(_statement)) ?? "";
    public bool IsReadOnly => _statement != IntPtr.Zero && NativeSqlite.sqlite3_stmt_readonly(_statement) != 0;

    internal void SkipRemainingRows()
    {
        if (!IsReadOnly) throw new InvalidOperationException("Write statements must run to completion.");
        FinalizeStatement();
    }

    internal void Restart()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var code = NativeSqlite.sqlite3_reset(_statement);
        if (code != NativeSqlite.Ok) throw _connection.CreateException(code);
        NativeSqlite.sqlite3_clear_bindings(_statement);
        BindParameters();
        _statementDone = false;
    }

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
        // Encode a batch once. Advancing a byte offset avoids repeatedly copying
        // the entire remaining script for every statement in a large import.
        while (_sqlOffset < _sqlLength)
        {
            _commandCancellation.ThrowIfCancellationRequested();
            var pointer = IntPtr.Add(_sqlBuffer, _sqlOffset);
            var remaining = _sqlLength - _sqlOffset;
            var result = NativeSqlite.sqlite3_prepare_v2(_connection.Handle, pointer, remaining, out _statement, out var tail);
            var consumed = checked((int)(tail.ToInt64() - pointer.ToInt64()));
            if (consumed < 0 || consumed > remaining) consumed = remaining;
            _sqlOffset += consumed;
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
            catch { FinalizeStatement(); throw; }
            _statementDone = false;
            return true;
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
        if (_sqlBuffer != IntPtr.Zero) { Marshal.FreeHGlobal(_sqlBuffer); _sqlBuffer = IntPtr.Zero; }
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
    internal static extern int sqlite3_stmt_readonly(IntPtr statement);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr sqlite3_sql(IntPtr statement);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int sqlite3_reset(IntPtr statement);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int sqlite3_clear_bindings(IntPtr statement);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr sqlite3_backup_init(IntPtr destination, IntPtr destinationName, IntPtr source, IntPtr sourceName);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int sqlite3_backup_step(IntPtr backup, int pages);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int sqlite3_backup_finish(IntPtr backup);
    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int sqlite3_backup_remaining(IntPtr backup);

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


sealed class StudioPreferences
{
    public bool Dark { get; set; } = true;
    public List<string> Recent { get; set; } = [];
    static string SettingsPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SQLiteStudio", "settings.json");
    public static StudioPreferences Load()
    {
        try
        {
            var preferences = JsonSerializer.Deserialize<StudioPreferences>(File.ReadAllText(SettingsPath)) ?? new();
            preferences.Recent = (preferences.Recent ?? []).Where(p => !string.IsNullOrWhiteSpace(p)).Take(8).ToList();
            return preferences;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return new(); }
    }
    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            var temporary = SettingsPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try { File.WriteAllText(temporary, JsonSerializer.Serialize(this)); File.Move(temporary, SettingsPath, true); }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* Preferences must never prevent database work. */ }
    }
}

sealed class StudioTabs : TabControl
{
    public bool Dark;
    public StudioTabs()
    {
        DrawMode = TabDrawMode.OwnerDrawFixed;
        SizeMode = TabSizeMode.Fixed;
        Padding = new Point(18, 8);
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
    }
    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ItemSize = new Size(148 * DeviceDpi / 96, 36 * DeviceDpi / 96);
    }
    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        base.OnDpiChangedAfterParent(e);
        ItemSize = new Size(148 * DeviceDpi / 96, 36 * DeviceDpi / 96);
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(Dark ? Color.FromArgb(15, 23, 36) : Color.FromArgb(240, 244, 248));
        for (var i = 0; i < TabCount; i++)
            OnDrawItem(new DrawItemEventArgs(e.Graphics, Font, GetTabRect(i), i, i == SelectedIndex ? DrawItemState.Selected : DrawItemState.None));
    }
    protected override void OnDrawItem(DrawItemEventArgs e)
    {
        if (e.Index < 0 || e.Index >= TabCount) return;
        var selected = e.Index == SelectedIndex;
        var background = Dark ? Color.FromArgb(selected ? 23 : 15, selected ? 34 : 23, selected ? 49 : 36) : selected ? Color.White : Color.FromArgb(240, 244, 248);
        using var brush = new SolidBrush(background);
        e.Graphics.FillRectangle(brush, e.Bounds);
        TextRenderer.DrawText(e.Graphics, TabPages[e.Index].Text, Font, e.Bounds, Dark ? Color.FromArgb(230, 237, 246) : Color.FromArgb(33, 48, 66), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        if (selected)
        {
            using var accent = new SolidBrush(Color.FromArgb(16, 139, 128));
            e.Graphics.FillRectangle(accent, e.Bounds.Left + 8, e.Bounds.Bottom - Math.Max(3, DeviceDpi / 48), e.Bounds.Width - 16, Math.Max(3, DeviceDpi / 48));
        }
    }
}

sealed class StudioMenuColors(bool dark) : ProfessionalColorTable
{
    Color Surface => dark ? Color.FromArgb(23, 34, 49) : Color.White;
    Color Hover => dark ? Color.FromArgb(38, 57, 73) : Color.FromArgb(223, 241, 239);
    public override Color ToolStripDropDownBackground => Surface;
    public override Color ImageMarginGradientBegin => Surface;
    public override Color ImageMarginGradientMiddle => Surface;
    public override Color ImageMarginGradientEnd => Surface;
    public override Color MenuItemSelected => Hover;
    public override Color MenuItemSelectedGradientBegin => Hover;
    public override Color MenuItemSelectedGradientEnd => Hover;
    public override Color MenuItemPressedGradientBegin => Hover;
    public override Color MenuItemPressedGradientMiddle => Hover;
    public override Color MenuItemPressedGradientEnd => Hover;
    public override Color ButtonSelectedHighlight => Hover;
    public override Color ButtonSelectedGradientBegin => Hover;
    public override Color ButtonSelectedGradientEnd => Hover;
    public override Color ButtonSelectedGradientMiddle => Hover;
    public override Color MenuBorder => Hover;
    public override Color MenuItemBorder => Hover;
    public override Color ToolStripBorder => Surface;
    public override Color OverflowButtonGradientBegin => Surface;
    public override Color OverflowButtonGradientMiddle => Surface;
    public override Color OverflowButtonGradientEnd => Surface;
}

sealed class StudioButton : Button
{
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (Enabled) return;
        using var brush = new SolidBrush(BackColor);
        e.Graphics.FillRectangle(brush, ClientRectangle);
        using var border = new Pen(FlatAppearance.BorderColor);
        e.Graphics.DrawRectangle(border, 0, 0, Width - 1, Height - 1);
        TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle, Color.FromArgb(120, 137, 151), TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter);
    }
}

static class SqlText
{
    public const string ProtectedPattern = """'(?:''|[^'])*'|"(?:""|[^"])*"|`(?:``|[^`])*`|\[[^\]]*\]|--[^\r\n]*(?:\r?\n|$)|/\*[\s\S]*?\*/""";
    public static string Unquoted(string sql) => Regex.Replace(sql, ProtectedPattern, " ");
    public static bool IsExplain(string sql) => Regex.IsMatch(Unquoted(sql), @"^\s*EXPLAIN\b", RegexOptions.IgnoreCase);
    public static void ValidateScript(string sql)
    {
        if (sql.Contains('\0')) throw new FormatException("SQL source cannot contain NUL characters. Bind these values as parameters instead.");
        // Some PRAGMAs take effect during prepare, so reject setters before preparing any statement.
        foreach (Match pragma in Regex.Matches(Unquoted(sql), @"(?:^|;)\s*PRAGMA\b[^;]*", RegexOptions.IgnoreCase))
            ValidateStatement(pragma.Value.TrimStart(';'));
    }
    public static void ValidateStatement(string sql)
    {
        var code = Unquoted(sql).Trim();
        if (Regex.IsMatch(code, @"^(BEGIN|COMMIT|END|ROLLBACK|SAVEPOINT|RELEASE|ATTACH|DETACH|VACUUM)\b", RegexOptions.IgnoreCase))
            throw new InvalidOperationException("Use the Commit, Rollback, Backup or Database actions for transaction and connection operations. Query batches manage their own transaction safely.");
        if (Regex.IsMatch(code, @"^PRAGMA\b", RegexOptions.IgnoreCase) && (code.Contains('=') || code.Contains('(')) &&
            !Regex.IsMatch(code, @"^PRAGMA\s+(?:\w+\.)?(table_info|table_xinfo|index_info|index_xinfo|index_list|foreign_key_list|foreign_key_check|integrity_check|quick_check)\s*\(", RegexOptions.IgnoreCase))
            throw new InvalidOperationException("Connection-setting PRAGMAs are not accepted in query batches. Read-only PRAGMA inspection is supported.");
    }
    public static string Format(string sql)
    {
        var tokens = Regex.Matches(sql, ProtectedPattern + @"|\s+|[A-Za-z_][A-Za-z_0-9]*|.", RegexOptions.Singleline);
        var result = new StringBuilder();
        var clauses = new HashSet<string>(new[] { "SELECT", "FROM", "WHERE", "GROUP", "ORDER", "HAVING", "LIMIT", "OFFSET", "UNION", "VALUES", "SET", "RETURNING" }, StringComparer.OrdinalIgnoreCase);
        foreach (Match match in tokens)
        {
            var token = match.Value;
            if (string.IsNullOrWhiteSpace(token))
            {
                if (result.Length > 0 && !char.IsWhiteSpace(result[^1])) result.Append(' ');
                continue;
            }
            if (clauses.Contains(token))
            {
                while (result.Length > 0 && result[^1] == ' ') result.Length--;
                if (result.Length > 0 && result[^1] != '\n') result.AppendLine();
                result.Append(token.ToUpperInvariant());
            }
            else result.Append(token);
        }
        return result.ToString().Trim();
    }
    public static (string Sql, List<string> Names) Parameters(string sql)
    {
        var names = new List<string>();
        var slots = new Dictionary<int, string>();
        var namedSlots = new Dictionary<string, int>(StringComparer.Ordinal);
        var maximum = 0;
        var statement = 0;
        var pattern = ProtectedPattern + @"|(?<named>[:@$][A-Za-z_][A-Za-z_0-9]*)|(?<pos>\?[0-9]*)|(?<end>;)";
        var rewritten = Regex.Replace(sql, pattern, match =>
        {
            if (match.Groups["end"].Success) { maximum = 0; statement++; slots.Clear(); namedSlots.Clear(); return match.Value; }
            if (!match.Groups["named"].Success && !match.Groups["pos"].Success) return match.Value;
            int index;
            if (match.Groups["named"].Success)
            {
                if (!namedSlots.TryGetValue(match.Value, out index)) { index = ++maximum; namedSlots.Add(match.Value, index); }
            }
            else if (match.Value.Length == 1) index = ++maximum;
            else
            {
                if (!int.TryParse(match.Value.AsSpan(1), out index) || index is < 1 or > 32766) throw new FormatException("Numbered SQL parameters must be between ?1 and ?32766.");
                maximum = Math.Max(maximum, index);
            }
            if (!slots.TryGetValue(index, out var name))
            {
                name = match.Groups["named"].Success ? match.Value : $"@__pos{statement}_{index}";
                while (match.Groups["pos"].Success && sql.Contains(name, StringComparison.Ordinal)) name += "_";
                slots[index] = name;
            }
            if (!names.Contains(name, StringComparer.Ordinal)) names.Add(name);
            return name;
        });
        return (rewritten, names);
    }
}


sealed record ImportSource(string Path, string[] Columns, List<object?[]> Preview, bool Json, long Length, DateTime Modified)
{
    public static ImportSource Open(string path, CancellationToken token)
    {
        var file = new FileInfo(path);
        var json = System.IO.Path.GetExtension(path).Equals(".json", StringComparison.OrdinalIgnoreCase);
        if (json && file.Length > 32 * 1024 * 1024) throw new IOException("JSON import is limited to 32 MB. Use CSV for streaming larger files.");
        string[] columns;
        var preview = new List<object?[]>();
        if (json)
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            if (document.RootElement.ValueKind != JsonValueKind.Array) throw new FormatException("JSON must be an array of objects.");
            columns = document.RootElement.EnumerateArray().SelectMany(row =>
            {
                if (row.ValueKind != JsonValueKind.Object) throw new FormatException("Each JSON row must be an object.");
                return row.EnumerateObject().Select(p => p.Name);
            }).Distinct(StringComparer.Ordinal).ToArray();
            foreach (var row in JsonRows(document, columns).Take(100)) { token.ThrowIfCancellationRequested(); preview.Add(row); }
        }
        else
        {
            using var reader = new StreamReader(path, new UTF8Encoding(false, true), true);
            using var rows = CsvRows(reader).GetEnumerator();
            if (!rows.MoveNext()) throw new FormatException("The CSV file is empty.");
            columns = rows.Current;
            while (preview.Count < 100 && rows.MoveNext())
            {
                token.ThrowIfCancellationRequested();
                if (rows.Current.Length != columns.Length) throw new FormatException($"CSV record {preview.Count + 2} has {rows.Current.Length} values; expected {columns.Length}.");
                preview.Add(rows.Current.Cast<object?>().ToArray());
            }
        }
        if (columns.Length == 0 || columns.Any(c => string.IsNullOrWhiteSpace(c) || c.Contains('\0')) || columns.Distinct(StringComparer.OrdinalIgnoreCase).Count() != columns.Length)
            throw new FormatException("Column names must be nonempty and unique (ignoring case).");
        return new ImportSource(path, columns, preview, json, file.Length, file.LastWriteTimeUtc);
    }
    internal static IEnumerable<object?[]> JsonRows(JsonDocument document, string[] columns)
    {
        foreach (var row in document.RootElement.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Object) throw new FormatException("Each JSON row must be an object.");
            if (row.EnumerateObject().Select(p => p.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != row.EnumerateObject().Count())
                throw new FormatException("A JSON object contains duplicate column names.");
            yield return columns.Select(column => !row.TryGetProperty(column, out var value) ? null : value.ValueKind switch
            {
                JsonValueKind.Null => null,
                JsonValueKind.True => (object)1L,
                JsonValueKind.False => 0L,
                JsonValueKind.Number when value.TryGetInt64(out var integer) => integer,
                JsonValueKind.Number => value.GetRawText(), // Preserve out-of-range and high-precision numbers losslessly.
                JsonValueKind.String => value.GetString(),
                _ => value.GetRawText()
            }).ToArray();
        }
    }
    internal static IEnumerable<string[]> CsvRows(TextReader reader)
    {
        var field = new StringBuilder();
        var row = new List<string>();
        var quoted = false;
        var closed = false;
        var started = false;
        while (reader.Read() is var code && code >= 0)
        {
            var c = (char)code;
            started = true;
            if (quoted)
            {
                if (c == '"')
                {
                    if (reader.Peek() == '"') { reader.Read(); field.Append('"'); }
                    else { quoted = false; closed = true; }
                }
                else field.Append(c);
                continue;
            }
            if (c == '"')
            {
                if (closed || field.Length > 0) throw new FormatException("A CSV quote must begin a field. Embedded quotes must be doubled.");
                quoted = true;
            }
            else if (c == ',' || c is '\r' or '\n')
            {
                row.Add(field.ToString()); field.Clear(); closed = false;
                if (c != ',')
                {
                    if (c == '\r' && reader.Peek() == '\n') reader.Read();
                    yield return row.ToArray(); row.Clear(); started = false;
                }
            }
            else
            {
                if (closed) throw new FormatException("Unexpected text after a quoted CSV field.");
                field.Append(c);
            }
        }
        if (quoted) throw new FormatException("The CSV ends inside a quoted field.");
        if (started || field.Length > 0 || row.Count > 0) { row.Add(field.ToString()); yield return row.ToArray(); }
    }
}

static class DataTransfer
{
    public static string[] UniqueNames(IEnumerable<string> columns)
    {
        var used = new HashSet<string>(StringComparer.Ordinal);
        return columns.Select(column =>
        {
            var name = column;
            for (var suffix = 2; !used.Add(name); suffix++) name = column + "_" + suffix;
            return name;
        }).ToArray();
    }
    static string Quote(string name) => '"' + name.Replace("\"", "\"\"") + '"';
    static string Text(object? value) => value switch { null or DBNull => "", byte[] bytes => Convert.ToHexString(bytes), _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "" };
    static string Csv(object? value) { var text = Text(value); return text.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? '"' + text.Replace("\"", "\"\"") + '"' : text; }
    static string Literal(object? value) => value switch
    {
        null or DBNull => "NULL", byte[] bytes => "X'" + Convert.ToHexString(bytes) + "'",
        long or int => Text(value), double v when double.IsFinite(v) => v.ToString("R", CultureInfo.InvariantCulture),
        string text when text.Contains('\0') => "CAST(X'" + Convert.ToHexString(Encoding.UTF8.GetBytes(text)) + "' AS TEXT)",
        _ => "'" + Text(value).Replace("'", "''") + "'"
    };
    public static long Export(SqliteConnection connection, string sql, string table, string destination, int format, CancellationToken token, IProgress<string>? progress = null)
    {
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".partial";
        long count = 0;
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var command = connection.CreateCommand())
            {
                command.CommandText = sql;
                using var reader = command.ExecuteReaderAsync(token).GetAwaiter().GetResult();
                var columns = Enumerable.Range(0, reader.FieldCount).Select(reader.GetName).ToArray();
                using var writer = new StreamWriter(stream, new UTF8Encoding(format == 1), 65536, leaveOpen: true);
                using var json = format == 2 ? new Utf8JsonWriter(stream) : null;
                if (format == 1) writer.WriteLine(string.Join(',', columns.Select(Csv)));
                if (json is not null) json.WriteStartArray();
                while (reader.ReadCore(token))
                {
                    var values = Enumerable.Range(0, reader.FieldCount).Select(reader.GetValue).ToArray();
                    if (format == 1) writer.WriteLine(string.Join(',', values.Select(Csv)));
                    else if (json is not null)
                    {
                        json.WriteStartObject();
                        for (var i = 0; i < columns.Length; i++)
                        {
                            json.WritePropertyName(columns[i]);
                            var value = values[i];
                            if (value is null or DBNull) json.WriteNullValue();
                            else if (value is long integer) json.WriteNumberValue(integer);
                            else if (value is double number && double.IsFinite(number)) json.WriteNumberValue(number);
                            else if (value is byte[] blob) json.WriteBase64StringValue(blob);
                            else json.WriteStringValue(Text(value));
                        }
                        json.WriteEndObject();
                        if (count % 1000 == 0) json.Flush();
                    }
                    else writer.WriteLine($"INSERT INTO {Quote(table)} ({string.Join(',', columns.Select(Quote))}) VALUES ({string.Join(',', values.Select(Literal))});");
                    if (++count % 1000 == 0) progress?.Report($"Exporting {count:N0} rows...");
                }
                if (json is not null) { json.WriteEndArray(); json.Flush(); }
                writer.Flush();
                stream.Flush(true);
            }
            token.ThrowIfCancellationRequested();
            File.Move(temporary, destination, true);
            return count;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public static long Import(SqliteConnection connection, ImportSource source, string table, CancellationToken token, IProgress<string>? progress = null)
    {
        if (string.IsNullOrWhiteSpace(table) || table.Contains('\0') || table.StartsWith("sqlite_", StringComparison.OrdinalIgnoreCase)) throw new FormatException("Enter a nonempty table name that does not start with sqlite_.");
        var file = new FileInfo(source.Path);
        if (file.Length != source.Length || file.LastWriteTimeUtc != source.Modified) throw new IOException("The import file changed after preview. Open it again to review the current contents.");
        using var stream = new FileStream(source.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var text = new StreamReader(stream, new UTF8Encoding(false, true), true);
        using var document = source.Json ? JsonDocument.Parse(text.ReadToEnd()) : null;
        IEnumerable<object?[]> rows;
        if (document is not null) rows = ImportSource.JsonRows(document, source.Columns);
        else rows = ImportSource.CsvRows(text).Skip(1).Select(row => row.Cast<object?>().ToArray());
        connection.ExecuteImmediate($"CREATE TABLE {Quote(table)} ({string.Join(',', source.Columns.Select(c => Quote(c) + (source.Json ? "" : " TEXT")))})");
        using var command = connection.CreateCommand();
        command.CommandText = $"INSERT INTO {Quote(table)} VALUES ({string.Join(',', source.Columns.Select((_, i) => "@v" + i))})";
        for (var i = 0; i < source.Columns.Length; i++) command.Parameters.AddWithValue("@v" + i, DBNull.Value);
        using var statement = command.ExecuteReaderAsync(token).GetAwaiter().GetResult();
        long count = 0;
        foreach (var row in rows)
        {
            token.ThrowIfCancellationRequested();
            if (row.Length != source.Columns.Length) throw new FormatException($"Record {count + 2} has {row.Length} values; expected {source.Columns.Length}. Nothing from this import was kept.");
            command.Parameters.Clear();
            for (var i = 0; i < row.Length; i++) command.Parameters.AddWithValue("@v" + i, row[i] ?? DBNull.Value);
            statement.Restart();
            statement.ReadCore(token);
            if (++count % 1000 == 0) progress?.Report($"Importing {count:N0} rows...");
        }
        return count;
    }
    public static void Backup(SqliteConnection source, string destination, CancellationToken token, IProgress<string>? progress = null)
    {
        if (source.InTransaction) throw new InvalidOperationException("Commit or roll back before backup.");
        if (File.Exists(destination)) throw new IOException("The backup destination already exists.");
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".partial";
        try
        {
            var target = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = temporary }.ToString());
            try
            {
                target.OpenAsync().GetAwaiter().GetResult();
                using var cancellation = token.Register(target.Interrupt);
                var name = NativeSqlite.AllocUtf8("main", out _);
                try
                {
                    var backup = NativeSqlite.sqlite3_backup_init(target.Handle, name, source.Handle, name);
                    if (backup == IntPtr.Zero) throw new IOException(NativeSqlite.ErrorMessage(target.Handle));
                    var watch = Stopwatch.StartNew();
                    try
                    {
                        while (true)
                        {
                            token.ThrowIfCancellationRequested();
                            var code = NativeSqlite.sqlite3_backup_step(backup, 256);
                            if (code == NativeSqlite.Done) break;
                            if (code is 5 or 6)
                            {
                                if (watch.Elapsed.TotalSeconds > 10) throw new IOException("The database stayed busy for too long. Try the backup again.");
                                if (token.WaitHandle.WaitOne(50)) token.ThrowIfCancellationRequested();
                            }
                            else if (code != NativeSqlite.Ok) throw source.CreateException(code);
                            else watch.Restart();
                            progress?.Report($"Backing up: {NativeSqlite.sqlite3_backup_remaining(backup):N0} pages remaining");
                        }
                    }
                    finally
                    {
                        var code = NativeSqlite.sqlite3_backup_finish(backup);
                        if (code != NativeSqlite.Ok && !token.IsCancellationRequested) throw target.CreateException(code);
                    }
                }
                finally { Marshal.FreeHGlobal(name); }
                using var check = target.CreateCommand();
                check.CommandText = "PRAGMA quick_check";
                using var reader = check.ExecuteReaderAsync(token).GetAwaiter().GetResult();
                if (!reader.ReadCore(token) || reader.GetString(0) != "ok") throw new IOException("The backup did not pass SQLite's quick check.");
            }
            finally { target.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
            token.ThrowIfCancellationRequested();
            File.Move(temporary, destination);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}


sealed class ImportPreviewDialog : Form
{
    readonly TextBox _name = new() { Dock = DockStyle.Fill };
    public string TableName => _name.Text.Trim();
    public ImportPreviewDialog(ImportSource source, bool dark)
    {
        SuspendLayout();
        AutoScaleDimensions = new SizeF(96, 96);
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Segoe UI", 9.5f);
        Text = "Review import";
        StartPosition = FormStartPosition.CenterParent;
        Size = new Size(860, 560);
        MinimumSize = new Size(650, 420);
        ShowInTaskbar = false;
        BackColor = dark ? Color.FromArgb(15, 23, 36) : Color.FromArgb(245, 247, 250);
        ForeColor = dark ? Color.FromArgb(230, 237, 246) : Color.FromArgb(33, 48, 66);
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(20), ColumnCount = 1, RowCount = 4 };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 68));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 50));
        var nameRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
        nameRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 125));
        nameRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        nameRow.Controls.Add(new Label { Text = "New table name", Dock = DockStyle.Fill }, 0, 0);
        _name.Text = Regex.Replace(Path.GetFileNameWithoutExtension(source.Path), @"[^\w]", "_");
        nameRow.Controls.Add(_name, 1, 0);
        root.Controls.Add(nameRow, 0, 0);
        root.Controls.Add(new Label { Dock = DockStyle.Fill, Text = $"Preview of up to 100 rows / {source.Columns.Length} columns\n" + (source.Json ? "JSON: integers and nulls retain their types. Other numbers and nested values use lossless text." : "CSV: all fields import as text, including leading zeroes. Empty fields stay empty strings.") + "\nA new table is created. Changes remain pending until you commit." }, 0, 1);
        var grid = new BufferedGrid { Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, RowHeadersVisible = false, BackgroundColor = BackColor, BorderStyle = BorderStyle.None };
        grid.DefaultCellStyle.BackColor = BackColor;
        grid.DefaultCellStyle.ForeColor = ForeColor;
        Load += (_, _) => grid.SetData(source.Columns, source.Preview);
        root.Controls.Add(grid, 0, 2);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
        var import = new Button { Text = "Import into new table", AutoSize = true, Padding = new Padding(12, 5, 12, 5), BackColor = Color.FromArgb(16, 139, 128), ForeColor = Color.White, FlatStyle = FlatStyle.Flat };
        import.Click += (_, _) =>
        {
            if (TableName.Length == 0 || TableName.StartsWith("sqlite_", StringComparison.OrdinalIgnoreCase)) { MessageBox.Show(this, "Enter a nonempty table name that does not start with sqlite_."); return; }
            DialogResult = DialogResult.OK;
        };
        var cancel = new Button { Text = "Cancel", AutoSize = true, Padding = new Padding(12, 5, 12, 5), DialogResult = DialogResult.Cancel };
        buttons.Controls.Add(import); buttons.Controls.Add(cancel);
        root.Controls.Add(buttons, 0, 3);
        Controls.Add(root);
        AcceptButton = import; CancelButton = cancel;
        DialogStyle.Apply(this, dark);
        ResumeLayout(true);
    }
}


static class DialogStyle
{
    public static void Apply(Control control, bool dark)
    {
        var background = dark ? Color.FromArgb(15,23,36) : Color.FromArgb(245,247,250);
        var surface = dark ? Color.FromArgb(23,34,49) : Color.White;
        var text = dark ? Color.FromArgb(230,237,246) : Color.FromArgb(33,48,66);
        control.ForeColor = text;
        control.BackColor = background;
        if (control is TextBoxBase or ComboBox) control.BackColor = surface;
        if (control is ComboBox combo) combo.FlatStyle = FlatStyle.Flat;
        if (control is Button button)
        {
            button.AutoSize = false;
            button.Height = 32;
            button.Width = Math.Max(110, (int)(TextRenderer.MeasureText(button.Text, button.Font).Width / (button.DeviceDpi / 96f)) + 36);
            button.Padding = Padding.Empty;
            button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderColor = dark ? Color.FromArgb(60,76,91) : Color.FromArgb(190,203,213);
            button.BackColor = button.Text is "Save" or "Run" or "Import into new table" ? Color.FromArgb(16,139,128) : surface;
            if (button.BackColor == Color.FromArgb(16,139,128)) button.ForeColor = Color.White;
        }
        if (control is DataGridView grid)
        {
            grid.BackgroundColor = surface;
            grid.EnableHeadersVisualStyles = false;
            grid.DefaultCellStyle.BackColor = surface; grid.DefaultCellStyle.ForeColor = text;
            grid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(16,139,128); grid.DefaultCellStyle.SelectionForeColor = Color.White;
            grid.ColumnHeadersDefaultCellStyle.BackColor = background; grid.ColumnHeadersDefaultCellStyle.ForeColor = text;
        }
        foreach (Control child in control.Controls) Apply(child,dark);
    }
}
