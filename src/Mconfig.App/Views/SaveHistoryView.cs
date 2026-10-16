using Mconfig.App.Resources;
using Mconfig.Application.History;
using Mconfig.Application.Ports;
using Mconfig.Application.Releases.Queries;
using Mconfig.Domain.Configuration;
using Mconfig.Presentation.DataManagement;

using static Mconfig.App.Views.DataViewParts;

namespace Mconfig.App.Views;

/// <summary>
/// 発行と履歴の2ページ。発行理由・変更内容・検証結果・発行操作はこのControlに置く。
/// 過去の版と選択した版の変更はHistoryPageに置き、同じPresenterで状態と操作を管理する。
/// 発行処理はZIP作成に成功した設定を自動で履歴へ記録する。
/// 通常の画面には保存・履歴・版などの業務用語だけを使う。
/// </summary>
internal sealed class SaveHistoryView : UserControl, ISaveHistoryView
{
    private readonly Label _validationTitle = new() { AutoSize = true };
    private readonly TableLayoutPanel _validationGroup = new() { Dock = DockStyle.Fill };
    private readonly Label _validationSummary = new() { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(0, 0, 0, 4) };
    private readonly ListView _issues = CreateList();
    private readonly Button _validate = new() { AutoSize = true };
    private readonly Button _goToIssue = new() { AutoSize = true };
    private readonly Button _copyIssues = new() { AutoSize = true };
    private readonly Button _exportUncovered = new() { AutoSize = true };

    // 全件検証の進捗と中止。出力の画面にも同じものを出す
    private readonly ProgressStrip _historyProgress = new();
    private readonly ProgressStrip _revisionProgress = new();
    public Panel HistoryPage { get; } = new() { Dock = DockStyle.Fill, Visible = false };
    private ProgressDisplay? _progressShown;

    private readonly Label _uncommittedTitle = new() { AutoSize = true };
    private readonly TableLayoutPanel _uncommittedGroup = new() { Dock = DockStyle.Fill };
    private readonly ListView _uncommitted = CreateList();

    private readonly Label _saveTitle = new() { AutoSize = true };
    private readonly TableLayoutPanel _saveGroup = new() { Dock = DockStyle.Fill };
    private readonly Label _publicationDescription = new() { AutoSize = true, Margin = new Padding(3, 4, 3, 0) };
    private readonly Label _destination = new() { AutoSize = true, Margin = new Padding(3, 4, 3, 0), ForeColor = UiAppearance.Muted };
    private string _publicationDirectory = "";
    private readonly Label _reasonLabel = new() { AutoSize = true, Margin = new Padding(3, 8, 3, 3) };
    private readonly TextBox _reason = new() { Dock = DockStyle.Top };
    private readonly Label _authorLabel = new() { AutoSize = true, Margin = new Padding(3, 8, 3, 3) };
    private readonly Label _blocked = new() { AutoSize = true, Margin = new Padding(3, 8, 3, 3), ForeColor = Color.FromArgb(196, 43, 28) };
    private readonly Button _commit = new() { AutoSize = true };

    private readonly Label _historyTitle = new() { AutoSize = true };
    private readonly TableLayoutPanel _historyGroup = new() { Dock = DockStyle.Fill };
    private readonly ListView _revisions = CreateList();
    private readonly Label _changesLabel = new() { AutoSize = true, Margin = new Padding(3, 8, 3, 3) };
    private readonly ListView _changes = CreateList();
    private readonly Button _reloadHistory = new() { AutoSize = true };
    private readonly Button _moreHistory = new() { AutoSize = true };
    private readonly Button _compare = new() { AutoSize = true };
    private readonly Button _restore = new() { AutoSize = true };


    private DataStateDisplay? _stateShown;
    private ValidationDisplay? _validationShown;
    private IReadOnlyList<ChangeDisplay>? _uncommittedShown = [];
    private IReadOnlyList<RevisionDisplay> _history = [];
    private bool _hasMore;
    private (RevisionDisplay? Revision, IReadOnlyList<ChangeDisplay> Changes) _changesShown = (null, []);

    public SaveHistoryView()
    {
        Dock = DockStyle.Fill;
        Font = UiAppearance.BodyFont;
        BackColor = UiAppearance.Background;
        HistoryPage.BackColor = UiAppearance.Background;
        foreach (Button button in new[] { _validate, _goToIssue, _copyIssues, _exportUncovered, _reloadHistory, _moreHistory, _compare, _restore }) UiAppearance.ApplyButton(button);
        UiAppearance.ApplyPrimaryButton(_commit);
        _authorLabel.ForeColor = UiAppearance.Muted;
        _reason.BorderStyle = BorderStyle.Fixed3D;

        // 左下: 検証結果
        _issues.Columns.Add("", 70);
        _issues.Columns.Add("", 300);
        _issues.Columns.Add("", 420);
        FlowLayoutPanel issueButtons = Row(_validate, _goToIssue, _copyIssues, _exportUncovered);
        issueButtons.Dock = DockStyle.Top;
        Panel validationBody = new() { Dock = DockStyle.Fill, Padding = new Padding(8, 0, 8, 8) };
        validationBody.Controls.Add(_issues);
        validationBody.Controls.Add(_validationSummary);
        validationBody.Controls.Add(issueButtons);
        ArrangeSection(_validationGroup, _validationTitle, validationBody);

        // 左中: 前回の履歴保存からの変更
        AddChangeColumns(_uncommitted);
        Panel changesBody = new() { Dock = DockStyle.Fill, Padding = new Padding(8, 0, 8, 8) };
        changesBody.Controls.Add(_uncommitted);
        ArrangeSection(_uncommittedGroup, _uncommittedTitle, changesBody);

        // 左上: 現在の状態と履歴に保存。入力幅はペインに合わせ、狭い画面でも操作を隠さない。
        TableLayoutPanel save = new() { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1, RowCount = 6, Padding = new Padding(8, 0, 8, 8), Margin = Padding.Empty };
        save.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        save.Controls.Add(_publicationDescription, 0, 0);
        save.Controls.Add(_destination, 0, 1);
        save.Controls.Add(_reasonLabel, 0, 2);
        save.Controls.Add(_reason, 0, 3);
        save.Controls.Add(Row(_authorLabel), 0, 4);
        save.Controls.Add(Row(_commit, _blocked), 0, 5);
        ArrangeSection(_saveGroup, _saveTitle, save, autoSize: true);

        TableLayoutPanel left = new() { Dock = DockStyle.Top, ColumnCount = 1, RowCount = 3, Margin = Padding.Empty };
        left.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        left.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        left.RowStyles.Add(new RowStyle(SizeType.Percent, 55));
        left.RowStyles.Add(new RowStyle(SizeType.Percent, 45));
        left.Controls.Add(_saveGroup, 0, 0);
        left.Controls.Add(_uncommittedGroup, 0, 1);
        left.Controls.Add(_validationGroup, 0, 2);
        Panel current = new() { Dock = DockStyle.Fill, AutoScroll = true, Padding = Padding.Empty };
        current.Controls.Add(left);
        current.Resize += (_, _) =>
        {
            left.Height = Math.Max(LogicalToDeviceUnits(600), current.ClientSize.Height);
            int width = Math.Max(120, current.ClientSize.Width - 48);
            foreach (Label label in new[] { _publicationDescription, _destination, _authorLabel, _blocked, _validationSummary })
                label.MaximumSize = new Size(width, 0);
        };

        // 右: 履歴と、選んだ版の変更
        _revisions.Columns.Add("", 110);
        _revisions.Columns.Add("", 145);
        _revisions.Columns.Add("", 90);
        _revisions.Columns.Add("", 260);
        _revisions.Columns[0].DisplayIndex = 3;
        AddChangeColumns(_changes);
        Panel revisionBody = new() { Dock = DockStyle.Fill, Padding = new Padding(8, 0, 8, 8) };
        FlowLayoutPanel historyButtons = Row(_reloadHistory, _moreHistory);
        historyButtons.Dock = DockStyle.Top;
        revisionBody.Controls.Add(_revisions);
        revisionBody.Controls.Add(historyButtons);
        ArrangeSection(_historyGroup, _historyTitle, revisionBody);

        Panel selectedBody = new() { Dock = DockStyle.Fill, Padding = new Padding(8, 0, 8, 8) };
        FlowLayoutPanel revisionButtons = Row(_compare, _restore);
        revisionButtons.Dock = DockStyle.Top;
        selectedBody.Controls.Add(_changes);
        selectedBody.Controls.Add(revisionButtons);
        TableLayoutPanel selectedSection = new() { Dock = DockStyle.Fill };
        ArrangeSection(selectedSection, _changesLabel, selectedBody);
        TableLayoutPanel history = new() { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = Padding.Empty };
        history.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        history.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        history.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        history.Controls.Add(_historyGroup, 0, 0);
        history.Controls.Add(selectedSection, 0, 1);

        Controls.Add(current);
        Controls.Add(_historyProgress);
        HistoryPage.Controls.Add(history);
        HistoryPage.Controls.Add(_revisionProgress);
        _historyProgress.CancelClicked += (_, _) => CancelRequested?.Invoke(this, EventArgs.Empty);
        _revisionProgress.CancelClicked += (_, _) => CancelRequested?.Invoke(this, EventArgs.Empty);

        _exportUncovered.Click += (_, _) => ExportUncoveredRequested?.Invoke(this, EventArgs.Empty);

        _validate.Click += (_, _) => ValidateRequested?.Invoke(this, EventArgs.Empty);
        _goToIssue.Click += (_, _) => GoToIssue();
        _issues.DoubleClick += (_, _) => GoToIssue();
        _issues.SelectedIndexChanged += (_, _) => UpdateButtons();
        _commit.Click += (_, _) => CommitRequested?.Invoke(this, new CommitRequestedEventArgs(_reason.Text));
        _copyIssues.Click += (_, _) => CopyIssues();
        _reloadHistory.Click += (_, _) => HistoryRequested?.Invoke(this, EventArgs.Empty);
        _moreHistory.Click += (_, _) => MoreHistoryRequested?.Invoke(this, EventArgs.Empty);
        _revisions.SelectedIndexChanged += (_, _) =>
        {
            RevisionSelected?.Invoke(this, new RevisionEventArgs(SelectedRevision()?.Id));
            UpdateButtons();
        };
        _compare.Click += (_, _) =>
        {
            if (SelectedRevision() is { } revision) CompareWithCurrentRequested?.Invoke(this, new RevisionEventArgs(revision.Id));
        };
        _restore.Click += (_, _) =>
        {
            if (SelectedRevision() is { } revision) RestoreRequested?.Invoke(this, new RevisionEventArgs(revision.Id));
        };
        ApplyTexts();
    }

    // 他の業務ページと同じ標準Panel・見出し部品を使い、領域間は背景色の隙間で区切る。
    private static void ArrangeSection(TableLayoutPanel section, Label title, Control body, bool autoSize = false)
    {
        section.BackColor = UiAppearance.Surface;
        section.Margin = new Padding(0, 0, 0, 6);
        section.ColumnCount = 1;
        section.RowCount = 2;
        section.AutoSize = autoSize;
        section.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        section.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        section.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        section.RowStyles.Add(new RowStyle(autoSize ? SizeType.AutoSize : SizeType.Percent, 100));
        TableLayoutPanel header = new() { Dock = DockStyle.Top, AutoSize = true, Margin = Padding.Empty };
        UiAppearance.ArrangeSectionHeader(header, [UiAppearance.SectionTitle(title)], []);
        header.BackColor = UiAppearance.Surface;
        body.Margin = Padding.Empty;
        section.Controls.Add(header, 0, 0);
        section.Controls.Add(body, 0, 1);
    }

    /// <summary>「発行…」（ツールバー）: 発行理由の入力へ移る。</summary>
    public void FocusReason()
    {
        if (IsHandleCreated) BeginInvoke(() => _reason.Focus());
    }

    public void SetPublicationDirectory(string path)
    {
        _publicationDirectory = path;
        _destination.Text = UiText.Format("Publish_Destination", path);
    }

    public void ReloadHistory() => HistoryRequested?.Invoke(this, EventArgs.Empty);

    public event EventHandler? ValidateRequested;
    public event EventHandler? DiscardRequested;
    public event EventHandler<CommitRequestedEventArgs>? CommitRequested;
    public event EventHandler? HistoryRequested;
    public event EventHandler? MoreHistoryRequested;
    public event EventHandler<RevisionEventArgs>? RevisionSelected;
    public event EventHandler<RevisionEventArgs>? RestoreRequested;
    public event EventHandler<RevisionEventArgs>? CompareWithCurrentRequested;
    public event EventHandler<IssueEventArgs>? NavigateRequested;
    public event EventHandler? CancelRequested;
    public event EventHandler? ExportUncoveredRequested;

    public void ApplyTexts()
    {
        _saveTitle.Text = UiText.Get("Publish_Title");
        _publicationDescription.Text = UiText.Get("Publish_Description");
        SetPublicationDirectory(_publicationDirectory);
        _validate.Text = UiText.Get("Data_Validate");
        _goToIssue.Text = UiText.Get("Data_GoToIssue");
        _reasonLabel.Text = UiText.Get("Data_Reason");
        _commit.Text = UiText.Get("Data_Commit");
        _validationTitle.Text = UiText.Get("Data_ValidationGroup");
        _copyIssues.Text = UiText.Get("Data_CopyIssues");
        _exportUncovered.Text = UiText.Get("Data_ExportUncovered");
        if (_progressShown is not null) ShowProgress(_progressShown);
        _issues.Columns[0].Text = UiText.Get("Data_ColSeverity");
        _issues.Columns[1].Text = UiText.Get("Data_ColLocation");
        _issues.Columns[2].Text = UiText.Get("Data_ColIssue");
        _historyTitle.Text = UiText.Get("Data_HistoryGroup");
        _revisions.Columns[0].Text = UiText.Get("Data_ColRevision");
        _revisions.Columns[1].Text = UiText.Get("Data_ColWhen");
        _revisions.Columns[2].Text = UiText.Get("Data_ColAuthor");
        _revisions.Columns[3].Text = UiText.Get("Data_ColReason");
        _changesLabel.Text = UiText.Get("Data_RevisionChanges");
        SetChangeHeaders(_changes);
        SetChangeHeaders(_uncommitted);
        _reloadHistory.Text = UiText.Get("Data_ReloadHistory");
        _moreHistory.Text = UiText.Get("Data_MoreHistory");
        _compare.Text = UiText.Get("Data_CompareWithCurrent");
        _restore.Text = UiText.Get("Data_Restore");
        if (_stateShown is not null) ShowState(_stateShown);
        if (_validationShown is not null) ShowValidation(_validationShown);
        ShowUncommittedChanges(_uncommittedShown);
        ShowHistory(_history, _hasMore);
        ShowRevisionChanges(_changesShown.Revision, _changesShown.Changes);
    }

    public void ShowState(DataStateDisplay state)
    {
        _stateShown = state;
        _authorLabel.Text = UiText.Format("Data_Author", state.Author);
        UpdateButtons();
    }

    public void ShowValidation(ValidationDisplay validation)
    {
        _validationShown = validation;
        _issues.BeginUpdate();
        _issues.Items.Clear();
        foreach (IssueDisplay issue in validation.Items)
        {
            ListViewItem item = new([Feedback.Severity(issue.Severity), issue.Location, Feedback.Issue(issue)])
            {
                ForeColor = issue.Severity == ValidationSeverity.Error && !SystemInformation.HighContrast ? Color.DarkRed : UiAppearance.Text,
            };
            _issues.Items.Add(item);
        }
        _issues.EndUpdate();
        // 途中で中止した検証は「検証未完了」とし、件数も結果も示さない
        _validationTitle.Text = UiText.Get("Data_ValidationGroup");
        _validationSummary.ForeColor = validation.Incomplete ? Color.FromArgb(196, 43, 28) : SystemColors.ControlText;
        _validationSummary.Text = validation.Incomplete
            ? UiText.Get("Data_ValidationIncomplete")
            : (validation.Items.Count == 0 ? UiText.Get("Data_NoIssues") : UiText.Get("Data_GoToIssueHint"))
                + (validation.Stale ? " " + UiText.Get("Data_ValidationStale") : "");
        UpdateButtons();
    }

    public void ShowProgress(ProgressDisplay? progress)
    {
        _progressShown = progress;
        _historyProgress.Show(progress);
        _revisionProgress.Show(progress);
        UpdateButtons();
    }

    public string? ChooseCsvPath()
    {
        using SaveFileDialog dialog = new()
        {
            Title = UiText.Get("Csv_Title"),
            Filter = UiText.Get("Csv_Filter"),
            FileName = "uncovered.csv",
            OverwritePrompt = true,
        };
        return dialog.ShowDialog(FindForm()) == DialogResult.OK ? dialog.FileName : null;
    }

    public void ShowUncoveredExported(long? written, string path, string? error)
    {
        if (written is long rows)
        {
            MessageBox.Show(FindForm(), UiText.Format("Csv_Done", rows.ToString("N0", UiText.Culture), path), UiText.Get("Csv_Title"),
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        MessageBox.Show(FindForm(), error is null ? UiText.Get("Csv_Canceled") : UiText.Format("Csv_Failed", error), UiText.Get("Csv_Title"),
            MessageBoxButtons.OK, error is null ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
    }

    public void ShowUncommittedChanges(IReadOnlyList<ChangeDisplay>? changes)
    {
        _uncommittedShown = changes;
        _uncommittedTitle.Text = changes is null
            ? UiText.Get("Data_UncommittedGroup")
            : UiText.Format("Data_UncommittedGroupCount", changes.Count);
        FillChanges(_uncommitted, changes ?? []);
        if (changes is null)
            _uncommitted.Items.Add(new ListViewItem(UiText.Get("Data_UncommittedUnavailable")) { ForeColor = SystemColors.GrayText });
    }

    public void ShowDifferenceFromCurrent(RevisionDisplay revision, IReadOnlyList<ChangeDisplay> changes)
    {
        using Form dialog = Dialogs.Create(UiText.Get("Data_CompareWithCurrentTitle"));
        TableLayoutPanel layout = Dialogs.Layout(3);
        Label text = new()
        {
            Text = UiText.Format("Data_CompareWithCurrentMessage", revision.When.ToLocalTime().ToString("G", UiText.Culture),
                revision.Author, FirstLine(revision.Reason), changes.Count) + Environment.NewLine +
                UiText.Format("Data_RevisionId", revision.Id.Value),
            AutoSize = true, MaximumSize = new Size(640, 0), Margin = new Padding(0, 0, 0, 8),
        };
        ListView list = CreateList();
        list.Dock = DockStyle.None;
        list.Size = new Size(640, 260);
        AddChangeColumns(list);
        SetChangeHeaders(list);
        FillChanges(list, changes);
        Button close = new() { Text = UiText.Get("Button_OK"), AutoSize = true, DialogResult = DialogResult.OK };
        layout.Controls.Add(text, 0, 0);
        layout.Controls.Add(list, 0, 1);
        layout.Controls.Add(Dialogs.Buttons(close), 0, 2);
        dialog.Controls.Add(layout);
        dialog.AcceptButton = close;
        dialog.CancelButton = close;
        dialog.ShowDialog(FindForm());
    }

    private void GoToIssue()
    {
        if (_validationShown is null || _issues.SelectedIndices.Count != 1) return;
        IssueDisplay issue = _validationShown.Items[_issues.SelectedIndices[0]];
        NavigateRequested?.Invoke(this, new IssueEventArgs(issue));
    }

    public void ShowHistory(IReadOnlyList<RevisionDisplay> revisions, bool hasMore)
    {
        RevisionId? selected = SelectedRevision()?.Id;
        _history = revisions;
        _hasMore = hasMore;
        _revisions.BeginUpdate();
        _revisions.Items.Clear();
        foreach (RevisionDisplay revision in revisions)
        {
            ListViewItem item = _revisions.Items.Add(new ListViewItem([
                revision.Id.Value, revision.When.ToLocalTime().ToString("G", UiText.Culture), revision.Author, FirstLine(revision.Reason)]));
            item.ToolTipText = revision.Reason;
            if (revision.Id == selected) item.Selected = true;
        }
        _revisions.EndUpdate();
        UpdateButtons();
    }

    public void ShowRevisionChanges(RevisionDisplay? revision, IReadOnlyList<ChangeDisplay> changes)
    {
        _changesShown = (revision, changes);
        _changesLabel.Text = UiText.Get("Data_RevisionChanges")
            + (revision is null ? "" : " — " + UiText.Format("Data_RevisionId", revision.Id.Value));
        FillChanges(_changes, changes);
    }

    public void ShowOperationResult(OperationDisplay result)
    {
        if (result.Operation is DataOperation.Commit or DataOperation.Publish && result.Status == Mconfig.Application.Workspaces.OperationStatus.Succeeded) _reason.Clear();
        Feedback.ShowOperation(FindForm(), result);
    }

    /// <summary>共通の保存メニューからも、Presenter の確認と Command 経路へ渡す。</summary>
    internal void RequestDiscard() => DiscardRequested?.Invoke(this, EventArgs.Empty);

    public bool ConfirmDiscard() =>
        MessageBox.Show(FindForm(), UiText.Get("Data_DiscardConfirm"), UiText.Get("Data_Discard"),
            MessageBoxButtons.OKCancel, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) == DialogResult.OK;

    public bool ConfirmWarnings(ConfirmationRequest request)
    {
        List<string> lines = [];
        if (request.Warnings.Count > 0)
        {
            lines.Add(UiText.Get("Confirm_Warnings"));
            lines.AddRange(request.Warnings.Select(w => "・" + Feedback.Issue(w)));
        }
        if (request.ShortReasonLength is int length)
        {
            if (lines.Count > 0) lines.Add("");
            lines.Add(UiText.Format("Confirm_ShortReason", length, CommitReason.MinimumLength));
        }
        lines.Add("");
        lines.Add(UiText.Get("Confirm_Proceed"));
        return MessageBox.Show(FindForm(), string.Join(Environment.NewLine, lines), UiText.Get("Confirm_Title"),
            MessageBoxButtons.OKCancel, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) == DialogResult.OK;
    }

    public bool ConfirmRestore(RestorePreviewDisplay preview)
    {
        using Form dialog = Dialogs.Create(UiText.Get("Restore_Title"));
        TableLayoutPanel layout = Dialogs.Layout(4);
        string message = UiText.Format("Restore_Message", preview.Revision.When.ToLocalTime().ToString("G", UiText.Culture),
            preview.Revision.Author, FirstLine(preview.Revision.Reason), preview.Changes.Count)
            + Environment.NewLine + UiText.Format("Data_RevisionId", preview.Revision.Id.Value)
            + Environment.NewLine + Environment.NewLine + UiText.Get("Restore_Choice_" + preview.Choice);
        Label text = new() { Text = message, AutoSize = true, MaximumSize = new Size(640, 0), Margin = new Padding(0, 0, 0, 8) };
        ListView changes = CreateList();
        changes.Dock = DockStyle.None;
        changes.Size = new Size(640, 220);
        changes.Columns.Add(UiText.Get("Data_ColChange"), 160);
        changes.Columns.Add(UiText.Get("Data_ColLocation"), 220);
        changes.Columns.Add(UiText.Get("Data_ColBefore"), 120);
        changes.Columns.Add(UiText.Get("Data_ColAfter"), 120);
        FillChanges(changes, preview.Changes);
        Label issues = new()
        {
            Text = string.Join(Environment.NewLine, preview.Issues.Where(i => i.Severity == ValidationSeverity.Error).Take(10).Select(i => "・" + Feedback.Issue(i))),
            AutoSize = true, MaximumSize = new Size(640, 0), ForeColor = Color.DarkRed,
        };
        bool canRestore = preview.Choice is RestoreChoice.Restore or RestoreChoice.RestoreByDiscarding;
        Button cancel = new() { Text = UiText.Get(canRestore ? "Button_Cancel" : "Button_OK"), AutoSize = true, DialogResult = DialogResult.Cancel };
        Button ok = new() { Text = UiText.Get("Data_Restore"), AutoSize = true, DialogResult = DialogResult.OK, Visible = canRestore };
        layout.Controls.Add(text, 0, 0);
        layout.Controls.Add(changes, 0, 1);
        layout.Controls.Add(issues, 0, 2);
        layout.Controls.Add(Dialogs.Buttons(cancel, ok), 0, 3);
        dialog.Controls.Add(layout);
        dialog.CancelButton = cancel;
        return dialog.ShowDialog(FindForm()) == DialogResult.OK && canRestore;
    }

    private RevisionDisplay? SelectedRevision() =>
        _revisions.SelectedIndices.Count == 1 && _revisions.SelectedIndices[0] < _history.Count ? _history[_revisions.SelectedIndices[0]] : null;

    private void UpdateButtons()
    {
        bool busy = _stateShown?.Busy ?? true;
        bool editable = (_stateShown?.Editable ?? false) && !busy;
        bool running = _progressShown is not null;
        _validate.Enabled = !busy && !running;
        _exportUncovered.Enabled = !running && _validationShown is { Incomplete: false, HasUncovered: true };
        bool valid = _validationShown is { Incomplete: false, Stale: false, Errors: 0, Warnings: 0 };
        _commit.Enabled = editable && !running && _stateShown is { Unsaved: false, Uncommitted: true, CommitErrors: 0 } && valid;
        _blocked.Text = _stateShown is { Unsaved: false, Uncommitted: false } ? UiText.Get("Data_NothingToCommit")
            : _validationShown is { Incomplete: false, Stale: false } validation && (validation.Errors > 0 || validation.Warnings > 0)
                ? UiText.Get("Data_CommitIssuesBlocked")
                : !valid ? UiText.Get("History_ValidationPending") : "";
        _moreHistory.Enabled = _hasMore;
        _restore.Enabled = editable && SelectedRevision() is not null;
        _compare.Enabled = SelectedRevision() is not null && !busy;
        _goToIssue.Enabled = _validationShown is not null && _issues.SelectedIndices.Count == 1 &&
            NavigationTarget.For(_validationShown.Items[_issues.SelectedIndices[0]]) is not null;
    }

    private void CopyIssues()
    {
        if (_validationShown is null) return;
        string text = string.Join(Environment.NewLine,
            _validationShown.Items.Select(i => $"{Feedback.Severity(i.Severity)}\t{i.Code}\t{Feedback.Issue(i)}"));
        if (text.Length > 0) Clipboard.SetText(text);
    }
}

