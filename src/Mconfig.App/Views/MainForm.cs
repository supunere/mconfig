using System.ComponentModel;
using System.Globalization;
using Mconfig.App.Resources;
using Mconfig.Presentation.Common;
using Mconfig.Presentation.DataManagement;
using Mconfig.Presentation.Workspaces;

namespace Mconfig.App.Views;

/// <summary>Presenter の結果を UI スレッドへ戻す。フォームを閉じた後に届いた結果は捨てる。</summary>
internal sealed class ControlDispatcher(Control owner) : IUiDispatcher
{
    public void Post(Action action)
    {
        if (owner.IsDisposed || owner.Disposing || !owner.IsHandleCreated) return;
        try
        {
            owner.BeginInvoke(() =>
            {
                if (!owner.IsDisposed) action();
            });
        }
        catch (InvalidOperationException)
        {
            // ハンドルが破棄された直後は捨てる
        }
    }
}

/// <summary>
/// 状態表示（ステータスバー）。選んでいるセルの情報、未保存の件数、履歴に未保存であること、エラー件数、未割当件数を区切って示す。
/// エラー件数と未割当件数はリンクで、押すと該当する画面へ移る。
/// </summary>
internal sealed class StatusStripView : StatusStrip, IWorkspaceStatusView
{
    private readonly ToolStripStatusLabel _cell = new() { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
    private readonly ToolStripStatusLabel _saved = new() { BorderSides = ToolStripStatusLabelBorderSides.Left };
    private readonly ToolStripStatusLabel _committed = new() { BorderSides = ToolStripStatusLabelBorderSides.Left };
    private readonly ToolStripStatusLabel _errors = new() { BorderSides = ToolStripStatusLabelBorderSides.Left };
    private readonly ToolStripStatusLabel _unassigned = new() { BorderSides = ToolStripStatusLabelBorderSides.Left };
    private readonly ToolStripStatusLabel _mode = new() { ForeColor = Color.DarkRed, BorderSides = ToolStripStatusLabelBorderSides.Left };
    private readonly ToolStripStatusLabel _workspace = new() { BorderSides = ToolStripStatusLabelBorderSides.Left, ForeColor = SystemColors.GrayText };
    private readonly string _workspacePath;
    private StatusDisplay? _status;

    public StatusStripView(string workspacePath)
    {
        _workspacePath = workspacePath;
        Font = UiAppearance.BodyFont;
        BackColor = SystemColors.Control;
        Renderer = UiAppearance.Renderer();
        SizingGrip = false;
        Padding = new Padding(1, 0, 8, 0);
        ShowItemToolTips = true;
        Items.AddRange([_cell, _saved, _committed, _errors, _unassigned, _mode, _workspace]);
        foreach (ToolStripStatusLabel item in Items.OfType<ToolStripStatusLabel>())
        {
            item.BorderSides = ToolStripStatusLabelBorderSides.None;
            item.Margin = new Padding(2, 0, 2, 0);
            item.Padding = new Padding(3, 1, 3, 1);
        }
        // 固定幅でワークスペース名を切らず、通常の自動幅と末尾余白で表示する。
        _workspace.AutoSize = true;
        _workspace.AutoToolTip = false;
        _errors.Click += (_, _) => { if (_errors.IsLink) ErrorsClicked?.Invoke(this, EventArgs.Empty); };
        _unassigned.Click += (_, _) => { if (_unassigned.IsLink) UnassignedClicked?.Invoke(this, EventArgs.Empty); };
        ApplyTexts();
    }

    public event EventHandler? ErrorsClicked;
    public event EventHandler? UnassignedClicked;

    /// <summary>状態が変わったとき（タイトルバーの表示に使う）。</summary>
    public event EventHandler? StatusChanged;

    public StatusDisplay? Current => _status;

    public void ShowStatus(StatusDisplay status)
    {
        _status = status;
        _saved.Text = status.Saving ? UiText.Get("Status_Saving")
            : status.Unsaved ? UiText.Format("Status_UnsavedCount", status.UnsavedChanges)
            : UiText.Get("Status_Saved");
        _saved.BackColor = status.Unsaved || status.Saving ? UiAppearance.Warning : SystemColors.Control;
        _saved.ForeColor = UiAppearance.Text;
        _committed.Text = UiText.Get(status.Uncommitted ? "Status_Uncommitted" : "Status_Committed");
        _committed.BackColor = status.Uncommitted ? UiAppearance.Warning : SystemColors.Control;
        _committed.ForeColor = status.Uncommitted ? UiAppearance.Text : UiAppearance.Muted;
        _errors.Text = status.Errors > 0 ? UiText.Format("Status_Errors", status.Errors) : UiText.Get("Status_NoErrors");
        _errors.IsLink = status.Errors > 0;
        _errors.BackColor = status.Errors > 0 ? UiAppearance.Missing : SystemColors.Control;
        _errors.ForeColor = _errors.LinkColor = status.Errors > 0 && !SystemInformation.HighContrast ? Color.FromArgb(163, 37, 43) : UiAppearance.Muted;
        // 組み合わせが多すぎて数えていないときは、検証で確かめるよう示す
        _unassigned.Text = status.Unassigned is int unassigned
            ? UiText.Format("Status_Unassigned", unassigned)
            : UiText.Get("Status_UnassignedNotCounted");
        _unassigned.IsLink = status.Unassigned > 0;
        _unassigned.BackColor = status.Unassigned > 0 || status.Unassigned is null ? UiAppearance.Warning : SystemColors.Control;
        _unassigned.ForeColor = _unassigned.LinkColor = UiAppearance.Muted;
        _mode.Text = status.RecoveryPending ? UiText.Get("Status_RecoveryPending") : status.ReadOnly ? UiText.Get("Status_ReadOnly") : "";
        _mode.BackColor = UiAppearance.Missing;
        _mode.ForeColor = UiAppearance.Text;
        _mode.Visible = _mode.Text.Length > 0;
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>選んでいるセルの情報。null なら消す。</summary>
    public void ShowCellInfo(string? text) => _cell.Text = text ?? "";

    public void ApplyTexts()
    {
        _workspace.Text = UiText.Format("Status_Workspace", Path.GetFileName(Path.TrimEndingDirectorySeparator(_workspacePath)));
        _workspace.ToolTipText = _workspacePath;
        _errors.ToolTipText = UiText.Get("Status_ErrorsTip");
        _unassigned.ToolTipText = UiText.Get("Status_UnassignedTip");
        if (_status is not null) ShowStatus(_status);
    }
}

/// <summary>画面（ツールバーの切り替えボタンとメニュー「表示」の Ctrl+1〜4）。</summary>
internal enum AppScreen { Combinations, Assignments, History, Export }

/// <summary>
/// メインフォーム。画面はツールバー左端の切り替えボタンとメニュー「表示」で切り替え、タブや左の画面メニューは使わない。
/// 「組み合わせと値」は上下分割で、上が組み合わせの表、下が値の表。境界の位置と、値の表を隠しているか（F6）は利用者が変えられる。
/// 画面の切り替えと終了では、未保存（E≠W）なら一時保存・保存しない・キャンセルを尋ね、終了時は未確定（W≠C）を知らせる。
/// 「保存と履歴」を開くたびに検証し、「出力」は独立した画面にする。
/// 表示言語はメニューで切り替え、表示名は選択言語 → ja → キーの順で解決する。
/// </summary>
internal sealed class MainForm : Form, ILeaveConfirmationView
{
    private const double DefaultSplitRatio = 0.45;

    private readonly DisplayLanguage _language;
    private readonly Panel _host = new() { Dock = DockStyle.Fill };
    private readonly SplitContainer _split = new() { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, SplitterWidth = 6 };
    private readonly Dictionary<AppScreen, (ToolStripButton Button, ToolStripMenuItem Menu)> _screenItems = [];
    private readonly ToolStripSplitButton _saveButton = new() { DisplayStyle = ToolStripItemDisplayStyle.Text, Alignment = ToolStripItemAlignment.Right, Tag = "primary", Padding = new Padding(12, 5, 12, 5) };
    private readonly ToolStripMenuItem _draftItem = new();
    private readonly ToolStripMenuItem _commitButton = new();
    private readonly ToolStripMenuItem _discardItem = new();
    private readonly ToolStripMenuItem _validateButton = new();
    private readonly ToolStripLabel _changes = new() { Alignment = ToolStripItemAlignment.Right, Padding = new Padding(8, 5, 8, 5), ForeColor = UiAppearance.Muted };
    private readonly ToolStripMenuItem _fileMenu = new();
    private readonly ToolStripMenuItem _saveMenu = new() { ShortcutKeys = Keys.Control | Keys.S };
    private readonly ToolStripMenuItem _exitMenu = new();
    private readonly ToolStripMenuItem _viewMenu = new();
    private readonly ToolStripMenuItem _hideValuesMenu = new() { ShortcutKeys = Keys.F6, CheckOnClick = false };
    private readonly ToolStripMenuItem _languageMenu = new();
    private readonly ToolStripMenuItem _japanese = new() { Tag = "ja" };
    private readonly ToolStripMenuItem _english = new() { Tag = "en" };
    private readonly bool _readOnly;
    private AppScreen _screen = AppScreen.Combinations;
    private double _splitRatio = DefaultSplitRatio;
    private LeaveGuard? _guard;
    private bool _switching;
    private bool _closeConfirmed;
    private bool _adjustingSplit;
    private bool _valuesHidden;

    public MainForm(DisplayLanguage language, string workspacePath, bool readOnly)
    {
        _language = language;
        _readOnly = readOnly;
        AutoScaleMode = AutoScaleMode.Dpi;
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(1280, 860);
        MinimumSize = new Size(760, 520);
        BackColor = UiAppearance.Background;
        _host.Padding = new Padding(4);

        CombinationList = new CombinationListView();
        Comparison = new PresetComparisonView();
        Assignments = new AssignmentView { Visible = false };
        History = new SaveHistoryView { Visible = false };
        Export = new ExportView { Visible = false };
        Status = new StatusStripView(workspacePath);

        _split.Panel1.Controls.Add(CombinationList);
        _split.Panel2.Controls.Add(Comparison);
        _split.Panel1MinSize = 120;
        _split.Panel2MinSize = 120;
        _split.SplitterMoved += (_, _) =>
        {
            if (!_adjustingSplit && _split.Height > 0 && !_valuesHidden) _splitRatio = (double)_split.SplitterDistance / _split.Height;
        };
        CombinationList.TableSpaceRequested += (_, _) => ApplySplitRatio();
        Load += (_, _) => ApplySplitRatio();
        _split.SizeChanged += (_, _) => ApplySplitRatio();
        _host.Controls.AddRange([_split, Assignments, History, Export]);
        Assignments.Dock = DockStyle.Fill;

        ToolStrip tools = new() { GripStyle = ToolStripGripStyle.Hidden, Padding = new Padding(2), Font = UiAppearance.BodyFont, BackColor = SystemColors.Control, Renderer = UiAppearance.Renderer() };
        MenuStrip menu = new();
        int index = 1;
        foreach (AppScreen screen in Enum.GetValues<AppScreen>())
        {
            AppScreen target = screen;
            ToolStripButton button = new() { DisplayStyle = ToolStripItemDisplayStyle.Text, Margin = new Padding(0, 0, 2, 0), Padding = new Padding(4, 2, 4, 2), Tag = "navigation" };
            ToolStripMenuItem item = new() { ShortcutKeys = Keys.Control | (Keys.D0 + index++) };
            button.Click += (_, _) => RequestScreen(target);
            item.Click += (_, _) => RequestScreen(target);
            tools.Items.Add(button);
            _viewMenu.DropDownItems.Add(item);
            _screenItems[screen] = (button, item);
        }
        _saveButton.ButtonClick += (_, _) => RequestSave();
        _draftItem.Click += (_, _) => RequestSave();
        _commitButton.Click += (_, _) => RequestScreen(AppScreen.History, History.FocusReason);
        _discardItem.Click += (_, _) =>
        {
            CommitPendingEdits();
            BeginInvoke(() => History.RequestDiscard());
        };
        _validateButton.Click += (_, _) => ShowErrors();
        _saveButton.DropDownItems.AddRange([_draftItem, _commitButton, new ToolStripSeparator(), _discardItem, new ToolStripSeparator(), _validateButton]);
        tools.Items.AddRange([_saveButton, _changes]);

        _saveMenu.Click += (_, _) => RequestSave();
        _exitMenu.Click += (_, _) => Close();
        _fileMenu.DropDownItems.AddRange([_saveMenu, new ToolStripSeparator(), _exitMenu]);
        _hideValuesMenu.Click += (_, _) => ToggleValues();
        _japanese.Click += OnLanguageSelected;
        _english.Click += OnLanguageSelected;
        _languageMenu.DropDownItems.AddRange([_japanese, _english]);
        _viewMenu.DropDownItems.AddRange([new ToolStripSeparator(), _hideValuesMenu, new ToolStripSeparator(), _languageMenu]);
        menu.Items.AddRange([_fileMenu, _viewMenu]);

        Comparison.HideRequested += (_, _) => ToggleValues();
        CombinationList.TableCellClicked += (_, _) =>
        {
            if (_screen != AppScreen.Combinations || !ValuesHidden) return;
            ValuesHidden = false;
            ShowCellInfo();
        };
        Comparison.CellInfoChanged += (_, _) => ShowCellInfo();
        Assignments.PresetManagement.CellInfoChanged += (_, _) => ShowCellInfo();
        Status.StatusChanged += (_, _) => { UpdateTitle(); UpdateSaveStatus(); };
        Status.ErrorsClicked += (_, _) => ShowErrors();
        Status.UnassignedClicked += (_, _) => RequestScreen(AppScreen.Assignments);

        Controls.Add(_host);
        Controls.Add(Status);
        Controls.Add(tools);
        Controls.Add(menu);
        MainMenuStrip = menu;
        ApplyTexts();
        ShowScreen(AppScreen.Combinations);
    }

    public CombinationListView CombinationList { get; }
    public PresetComparisonView Comparison { get; }
    public AssignmentView Assignments { get; }
    public SaveHistoryView History { get; }
    public ExportView Export { get; }
    public StatusStripView Status { get; }

    /// <summary>一時保存（Ctrl+S とツールバー）。</summary>
    public event EventHandler? SaveRequested;

    /// <summary>検証（保存と履歴の画面を開いたとき、ツールバー、ステータスバーのエラー件数）。</summary>
    public event EventHandler? ValidateRequested;

    /// <summary>別画面から出力画面を開いた。復旧状態を読み直す。</summary>
    public event EventHandler? ExportOpened;

    /// <summary>画面を開き（未保存の確認を経て）、開けたら then を実行する（検証結果の該当箇所へ移るときに使う）。</summary>
    internal void OpenScreen(AppScreen screen, Action? then = null) => RequestScreen(screen, then);

    /// <summary>上下の境界の位置（上の高さの割合）と、値の表を隠しているか。起動時に復元し、終了時に記憶する。</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public double ValuesSplitRatio
    {
        get => _splitRatio;
        set
        {
            _splitRatio = double.IsFinite(value) ? Math.Clamp(value, 0.15, 0.85) : DefaultSplitRatio;
            if (IsHandleCreated) ApplySplitRatio();
        }
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool ValuesHidden
    {
        get => _valuesHidden;
        set
        {
            _valuesHidden = value;
            _hideValuesMenu.Checked = value;
            Comparison.SetContentHidden(value);
            if (IsHandleCreated) ApplySplitRatio();
        }
    }

    /// <summary>画面の切り替え・終了の確認に使う。</summary>
    public void UseLeaveGuard(LeaveGuard guard) => _guard = guard;

    private void ApplySplitRatio()
    {
        if (_adjustingSplit || _split.Height <= _split.Panel1MinSize + _split.SplitterWidth) return;
        _adjustingSplit = true;
        try
        {
            int minimum = _valuesHidden ? Comparison.CollapsedHeight : LogicalToDeviceUnits(120);
            _split.Panel2MinSize = Math.Min(minimum, _split.Height - _split.Panel1MinSize - _split.SplitterWidth);
            _split.IsSplitterFixed = _valuesHidden;
            int maximum = _split.Height - _split.Panel2MinSize - _split.SplitterWidth;
            int preferred = _valuesHidden ? maximum : Math.Max((int)(_split.Height * _splitRatio), CombinationList.PreferredTableHeight);
            _split.SplitterDistance = Math.Clamp(preferred, _split.Panel1MinSize, maximum);
        }
        finally { _adjustingSplit = false; }
    }

    private void ToggleValues()
    {
        if (_screen != AppScreen.Combinations) return;
        Comparison.CommitPendingEdit();
        ValuesHidden = !ValuesHidden;
        if (ValuesHidden) CombinationList.FocusTable();
        ShowCellInfo();
    }

    private void CommitPendingEdits()
    {
        Comparison.CommitPendingEdit();
        Assignments.CommitPendingEdit();
    }

    private void RequestSave()
    {
        CommitPendingEdits();
        // 編集中のセルの確定（BeginInvoke で届く）より後に保存する
        BeginInvoke(() => SaveRequested?.Invoke(this, EventArgs.Empty));
    }

    // 保存と履歴の画面を開けば検証する。すでに開いていれば検証し直す
    private void ShowErrors()
    {
        if (_screen == AppScreen.History) ValidateRequested?.Invoke(this, EventArgs.Empty);
        else RequestScreen(AppScreen.History);
    }

    private Control ViewOf(AppScreen screen) => screen switch
    {
        AppScreen.Combinations => _split,
        AppScreen.Assignments => Assignments,
        AppScreen.History => History,
        _ => Export,
    };

    // 未保存の確認は非同期（一時保存を待つ）なので、確認が済んでから切り替える。
    // 編集中のセルの確定（BeginInvoke で届く）より後に確認するため、確認も BeginInvoke で始める。
    // 同じ画面の中の移動（保存と履歴 ↔ 出力）は確認しない
    private void RequestScreen(AppScreen target, Action? then = null)
    {
        if (_switching) return;
        if (ViewOf(target) == ViewOf(_screen) || _guard is null)
        {
            ShowScreen(target);
            then?.Invoke();
            return;
        }
        _switching = true;
        CommitPendingEdits();
        BeginInvoke(async () =>
        {
            try
            {
                if (!await _guard.CanLeaveAsync(LeaveKind.SwitchTab)) return;
                ShowScreen(target);
                then?.Invoke();
            }
            finally
            {
                _switching = false;
            }
        });
    }

    private void ShowScreen(AppScreen screen)
    {
        bool opened = screen != _screen;
        _screen = screen;
        Control shown = ViewOf(screen);
        _host.SuspendLayout();
        foreach (Control view in _host.Controls) view.Visible = view == shown;
        _host.ResumeLayout();
        foreach ((AppScreen key, (ToolStripButton button, ToolStripMenuItem item)) in _screenItems)
            button.Checked = item.Checked = key == screen;
        _hideValuesMenu.Enabled = screen == AppScreen.Combinations;
        if (screen == AppScreen.Combinations) ApplySplitRatio();
        if (screen == AppScreen.History && opened) ValidateRequested?.Invoke(this, EventArgs.Empty);
        if (screen == AppScreen.Export && opened) ExportOpened?.Invoke(this, EventArgs.Empty);
        ShowCellInfo();
        UpdateTitle();
    }

    private void ShowCellInfo() =>
        Status.ShowCellInfo(_screen == AppScreen.Combinations && !ValuesHidden ? Comparison.CurrentCellInfo()
            : _screen == AppScreen.Assignments ? Assignments.PresetManagement.CurrentCellInfo() : null);

    private void UpdateTitle()
    {
        string title = UiText.Get("App_Title") + " - " + _screenItems[_screen].Button.Text;
        if (_readOnly) title += $" - {UiText.Get("Status_ReadOnly")}";
        if (Status.Current is { Unsaved: true }) title += UiText.Get("Title_Unsaved");
        Text = title;
    }

    private void UpdateSaveStatus()
    {
        StatusDisplay? status = Status.Current;
        _changes.Text = status is { Saving: true } ? UiText.Get("Status_Saving")
            : status is { Unsaved: true } ? UiText.Format("Tool_Changes", status.UnsavedChanges)
            : UiText.Get("Status_Saved");
        bool writable = !_readOnly && status is { ReadOnly: false, RecoveryPending: false, Saving: false };
        _saveButton.Enabled = _draftItem.Enabled = _saveMenu.Enabled = writable;
        _commitButton.Enabled = writable;
        _discardItem.Enabled = writable && status is { Unsaved: true };
        _validateButton.Enabled = status is { Saving: false };
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // Windows の終了など、利用者に尋ねられない終了は止めない
        if (_closeConfirmed || _guard is null || e.CloseReason is CloseReason.WindowsShutDown or CloseReason.TaskManagerClosing)
        {
            base.OnFormClosing(e);
            return;
        }
        e.Cancel = true;
        CommitPendingEdits();
        BeginInvoke(async () =>
        {
            if (!await _guard.CanLeaveAsync(LeaveKind.Exit)) return;
            _closeConfirmed = true;
            Close();
        });
    }

    public UnsavedChoice AskUnsaved(LeaveKind kind)
    {
        using Form dialog = Dialogs.Create(UiText.Get("App_Title"));
        TableLayoutPanel layout = Dialogs.Layout(2);
        Label message = new()
        {
            Text = UiText.Format(kind == LeaveKind.Exit ? "Leave_UnsavedExit" : "Leave_UnsavedTab", Status.Current?.UnsavedChanges ?? 1),
            AutoSize = true, MaximumSize = new Size(480, 0), Margin = new Padding(0, 0, 0, 12),
        };
        Button cancel = new() { Text = UiText.Get("Button_Cancel"), AutoSize = true, DialogResult = DialogResult.Cancel };
        Button discard = new() { Text = UiText.Get("Leave_Discard"), AutoSize = true, DialogResult = DialogResult.No };
        Button save = new() { Text = UiText.Get("Leave_Save"), AutoSize = true, DialogResult = DialogResult.Yes };
        layout.Controls.Add(message, 0, 0);
        layout.Controls.Add(Dialogs.Buttons(cancel, discard, save), 0, 1);
        dialog.Controls.Add(layout);
        dialog.AcceptButton = save;
        dialog.CancelButton = cancel;
        return dialog.ShowDialog(this) switch
        {
            DialogResult.Yes => UnsavedChoice.Save,
            DialogResult.No => UnsavedChoice.Discard,
            _ => UnsavedChoice.Cancel,
        };
    }

    public bool ConfirmExitWithUncommitted() =>
        MessageBox.Show(this, UiText.Get("Leave_UncommittedExit"), UiText.Get("App_Title"),
            MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) == DialogResult.Yes;

    public void ShowBusy() =>
        MessageBox.Show(this, UiText.Get("Leave_Busy"), UiText.Get("App_Title"), MessageBoxButtons.OK, MessageBoxIcon.Information);

    public void ShowOperationResult(OperationDisplay result) => Feedback.ShowOperation(this, result);

    private void OnLanguageSelected(object? sender, EventArgs e)
    {
        if (sender is not ToolStripMenuItem { Tag: string code }) return;
        // 固定の文言を先に切り替えてから、Presenter に表示名の解決をやり直させる
        UiText.Culture = CultureInfo.GetCultureInfo(code);
        ApplyTexts();
        _language.Change(code);
    }

    private void ApplyTexts()
    {
        _fileMenu.Text = UiText.Get("Menu_File");
        _saveMenu.Text = UiText.Get("Menu_SaveDraft");
        _exitMenu.Text = UiText.Get("Menu_Exit");
        _viewMenu.Text = UiText.Get("Menu_View");
        _hideValuesMenu.Text = UiText.Get("Menu_HideValues");
        _languageMenu.Text = UiText.Get("Menu_Language");
        _japanese.Text = UiText.Get("Menu_Japanese");
        _english.Text = UiText.Get("Menu_English");
        _japanese.Checked = UiText.Culture.TwoLetterISOLanguageName == "ja";
        _english.Checked = UiText.Culture.TwoLetterISOLanguageName == "en";
        foreach ((AppScreen screen, (ToolStripButton button, ToolStripMenuItem item)) in _screenItems)
        {
            button.Text = UiText.Get("Screen_" + screen);
            item.Text = UiText.Get("ScreenMenu_" + screen);
        }
        _saveButton.Text = UiText.Get("Tool_Save");
        _saveButton.ToolTipText = UiText.Get("Tool_SaveTip");
        _draftItem.Text = UiText.Get("Tool_SaveDraft");
        _draftItem.ShortcutKeyDisplayString = "Ctrl+S";
        _commitButton.Text = UiText.Get("Tool_Commit");
        _discardItem.Text = UiText.Get("Tool_Discard");
        _validateButton.Text = UiText.Get("Tool_Validate");
        CombinationList.ApplyTexts();
        Comparison.ApplyTexts();
        Assignments.ApplyTexts();
        History.ApplyTexts();
        Export.ApplyTexts();
        Status.ApplyTexts();
        UpdateSaveStatus();
        UpdateTitle();
    }
}

