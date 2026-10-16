using Mconfig.App.Resources;
using Mconfig.Application.History;
using Mconfig.Application.Ports;
using Mconfig.Application.Releases.Queries;
using Mconfig.Domain.Configuration;
using Mconfig.Presentation.DataManagement;

using static Mconfig.App.Views.DataViewParts;

namespace Mconfig.App.Views;

/// <summary>
/// 保存と履歴の画面。左に現在の状態と履歴に保存、変更内容、検証結果を縦に並べ、
/// 右に履歴（選んだ版の変更・現在との違い・この版に戻す）を置く。
/// 出力（テスト版・リリース版・中断した出力の再開）は <see cref="ExportView"/> で、ツールバーの「出力」で開く。
/// 通常の画面には保存・履歴・版などの業務用語だけを使う。
/// </summary>
internal sealed class SaveHistoryView : UserControl, ISaveHistoryView
{
    private readonly Label _changeCount = new() { AutoSize = true, Font = UiAppearance.HeadingFont, Margin = new Padding(3, 4, 12, 4) };
    private readonly Label _validationState = new() { AutoSize = true, Margin = new Padding(3, 4, 3, 4) };
    private readonly GroupBox _validationGroup = new() { Dock = DockStyle.Fill, Padding = new Padding(8) };
    private readonly Label _validationSummary = new() { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(0, 0, 0, 4) };
    private readonly ListView _issues = CreateList();
    private readonly Button _validate = new() { AutoSize = true };
    private readonly Button _goToIssue = new() { AutoSize = true };
    private readonly Button _copyIssues = new() { AutoSize = true };
    private readonly Button _exportUncovered = new() { AutoSize = true };

    // 全件検証の進捗と中止。出力の画面にも同じものを出す
    private readonly ProgressStrip _historyProgress = new();
    private ProgressDisplay? _progressShown;

    private readonly GroupBox _uncommittedGroup = new() { Dock = DockStyle.Fill, Padding = new Padding(8) };
    private readonly ListView _uncommitted = CreateList();

    private readonly GroupBox _saveGroup = new() { Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(8) };
    private readonly Label _state = new() { AutoSize = true, Margin = new Padding(12, 8, 3, 3), ForeColor = SystemColors.GrayText };
    private readonly Button _saveDraft = new() { AutoSize = true };
    private readonly Button _discard = new() { AutoSize = true };
    private readonly Label _reasonLabel = new() { AutoSize = true, Margin = new Padding(3, 8, 3, 3) };
    private readonly TextBox _reason = new() { Dock = DockStyle.Top };
    private readonly Label _authorLabel = new() { AutoSize = true, Margin = new Padding(3, 8, 3, 3) };
    private readonly Label _blocked = new() { AutoSize = true, Margin = new Padding(3, 8, 3, 3), ForeColor = Color.FromArgb(196, 43, 28) };
    private readonly Button _commit = new() { AutoSize = true };

    private readonly GroupBox _historyGroup = new() { Dock = DockStyle.Fill, Padding = new Padding(8) };
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
        foreach (GroupBox group in new[] { _saveGroup, _uncommittedGroup, _validationGroup, _historyGroup }) UiAppearance.ApplyGroup(group);
        foreach (Button button in new[] { _validate, _goToIssue, _copyIssues, _exportUncovered, _saveDraft, _discard, _reloadHistory, _moreHistory, _compare, _restore }) UiAppearance.ApplyButton(button);
        UiAppearance.ApplyPrimaryButton(_commit);
        _state.ForeColor = _authorLabel.ForeColor = UiAppearance.Muted;
        _reason.BorderStyle = BorderStyle.Fixed3D;

        // 左下: 検証結果
        _issues.Columns.Add("", 70);
        _issues.Columns.Add("", 300);
        _issues.Columns.Add("", 420);
        FlowLayoutPanel issueButtons = Row(_validate, _goToIssue, _copyIssues, _exportUncovered);
        issueButtons.Dock = DockStyle.Bottom;
        _validationGroup.Controls.Add(_issues);
        _validationGroup.Controls.Add(issueButtons);
        _validationGroup.Controls.Add(_validationSummary);

        // 左中: 前回の履歴保存からの変更
        AddChangeColumns(_uncommitted);
        _uncommittedGroup.Controls.Add(_uncommitted);

        // 左上: 現在の状態と履歴に保存。入力幅はペインに合わせ、狭い画面でも操作を隠さない。
        TableLayoutPanel save = new() { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1, RowCount = 7 };
        save.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        save.Controls.Add(Row(_changeCount, _state), 0, 0);
        save.Controls.Add(Row(_validationState), 0, 1);
        save.Controls.Add(_reasonLabel, 0, 2);
        save.Controls.Add(_reason, 0, 3);
        save.Controls.Add(Row(_authorLabel), 0, 4);
        save.Controls.Add(Row(_commit, _blocked), 0, 5);
        save.Controls.Add(Row(_saveDraft, _discard), 0, 6);
        _saveGroup.Controls.Add(save);

        TableLayoutPanel left = new() { Dock = DockStyle.Top, ColumnCount = 1, RowCount = 3, Margin = new Padding(0) };
        left.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        left.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        left.RowStyles.Add(new RowStyle(SizeType.Percent, 55));
        left.RowStyles.Add(new RowStyle(SizeType.Percent, 45));
        left.Controls.Add(_saveGroup, 0, 0);
        left.Controls.Add(_uncommittedGroup, 0, 1);
        left.Controls.Add(_validationGroup, 0, 2);
        Panel current = new() { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(0, 0, 8, 0) };
        current.Controls.Add(left);
        current.Resize += (_, _) =>
        {
            left.Height = Math.Max(LogicalToDeviceUnits(600), current.ClientSize.Height);
            int width = Math.Max(120, current.ClientSize.Width - 48);
            foreach (Label label in new[] { _changeCount, _state, _validationState, _authorLabel, _blocked, _validationSummary })
                label.MaximumSize = new Size(width, 0);
        };

        // 右: 履歴と、選んだ版の変更
        _revisions.Columns.Add("", 110);
        _revisions.Columns.Add("", 145);
        _revisions.Columns.Add("", 90);
        _revisions.Columns.Add("", 260);
        AddChangeColumns(_changes);
        TableLayoutPanel history = new() { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5 };
        history.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        history.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        history.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        history.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        history.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        history.Controls.Add(_revisions, 0, 0);
        history.Controls.Add(Row(_reloadHistory, _moreHistory), 0, 1);
        history.Controls.Add(_changesLabel, 0, 2);
        history.Controls.Add(_changes, 0, 3);
        history.Controls.Add(Row(_compare, _restore), 0, 4);
        _historyGroup.Controls.Add(history);

        SplitContainer split = new() { Dock = DockStyle.Fill, SplitterWidth = 6 };
        split.Panel1.Controls.Add(current);
        split.Panel2.Controls.Add(_historyGroup);
        Load += (_, _) => split.SplitterDistance = Math.Max(split.Panel1MinSize, split.Width * 11 / 20);
        Controls.Add(split);
        Controls.Add(_historyProgress);

        _historyProgress.CancelClicked += (_, _) => CancelRequested?.Invoke(this, EventArgs.Empty);
        _exportUncovered.Click += (_, _) => ExportUncoveredRequested?.Invoke(this, EventArgs.Empty);

        _validate.Click += (_, _) => ValidateRequested?.Invoke(this, EventArgs.Empty);
        _goToIssue.Click += (_, _) => GoToIssue();
        _issues.DoubleClick += (_, _) => GoToIssue();
        _issues.SelectedIndexChanged += (_, _) => UpdateButtons();
        _saveDraft.Click += (_, _) => SaveDraftRequested?.Invoke(this, EventArgs.Empty);
        _discard.Click += (_, _) => RequestDiscard();
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

    /// <summary>「履歴に保存...」（ツールバー）: 変更理由の入力へ移る。</summary>
    public void FocusReason()
    {
        if (IsHandleCreated) BeginInvoke(() => _reason.Focus());
    }

    public event EventHandler? ValidateRequested;
    public event EventHandler? SaveDraftRequested;
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
        _saveGroup.Text = UiText.Get("History_CurrentChanges");
        _validate.Text = UiText.Get("Data_Validate");
        _goToIssue.Text = UiText.Get("Data_GoToIssue");
        _saveDraft.Text = UiText.Get("Data_SaveDraft");
        _discard.Text = UiText.Get("Data_Discard");
        _reasonLabel.Text = UiText.Get("Data_Reason");
        _commit.Text = UiText.Get("Data_Commit");
        _validationGroup.Text = UiText.Get("Data_ValidationGroup");
        _copyIssues.Text = UiText.Get("Data_CopyIssues");
        _exportUncovered.Text = UiText.Get("Data_ExportUncovered");
        if (_progressShown is not null) ShowProgress(_progressShown);
        _issues.Columns[0].Text = UiText.Get("Data_ColSeverity");
        _issues.Columns[1].Text = UiText.Get("Data_ColLocation");
        _issues.Columns[2].Text = UiText.Get("Data_ColIssue");
        _historyGroup.Text = UiText.Get("Data_HistoryGroup");
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
        else _validationState.Text = UiText.Get("History_ValidationPending");
        ShowUncommittedChanges(_uncommittedShown);
        ShowHistory(_history, _hasMore);
        ShowRevisionChanges(_changesShown.Revision, _changesShown.Changes);
    }

    public void ShowState(DataStateDisplay state)
    {
        _stateShown = state;
        _authorLabel.Text = UiText.Format("Data_Author", state.Author);
        _blocked.Text = state.CommitErrors is > 0 && state.Editable
            ? UiText.Format("Data_CommitBlocked", state.CommitErrors.Value)
            : !state.Unsaved && !state.Uncommitted ? UiText.Get("Data_NothingToCommit") : "";
        _blocked.ForeColor = state.CommitErrors is > 0 && !SystemInformation.HighContrast ? Color.FromArgb(196, 43, 28) : UiAppearance.Muted;
        _state.Text = state.RecoveryPending ? UiText.Get("Status_RecoveryPending")
            : state.ReadOnly ? UiText.Get("Status_ReadOnly")
            : state.Busy ? UiText.Get("Status_Saving")
            : string.Join(" / ", UiText.Get(state.Unsaved ? "Status_Unsaved" : "Status_Saved"),
                UiText.Get(state.Uncommitted ? "Status_Uncommitted" : "Status_Committed"));
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
        _validationGroup.Text = validation.Incomplete
            ? UiText.Get("Data_ValidationGroup")
            : UiText.Format("Data_ValidationGroupCount", validation.Errors, validation.Warnings);
        _validationSummary.ForeColor = validation.Incomplete ? Color.FromArgb(196, 43, 28) : SystemColors.ControlText;
        _validationSummary.Text = validation.Incomplete
            ? UiText.Get("Data_ValidationIncomplete")
            : (validation.Items.Count == 0 ? UiText.Get("Data_NoIssues") : UiText.Get("Data_GoToIssueHint"))
                + (validation.Stale ? " " + UiText.Get("Data_ValidationStale") : "");
        _validationState.Text = validation.Incomplete ? UiText.Get("History_ValidationPending")
            : validation.Stale ? UiText.Get("History_ValidationStale")
            : UiText.Format("History_ValidationSummary", validation.Errors, validation.Warnings);
        _validationState.ForeColor = validation.Incomplete || validation.Stale || validation.Errors > 0
            ? (SystemInformation.HighContrast ? SystemColors.WindowText : Color.FromArgb(163, 37, 43)) : UiAppearance.Muted;
        UpdateButtons();
    }

    public void ShowProgress(ProgressDisplay? progress)
    {
        _progressShown = progress;
        _historyProgress.Show(progress);
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
        _changeCount.Text = changes is null ? UiText.Get("Data_UncommittedUnavailable")
            : UiText.Format("History_CurrentChangeCount", changes.Count);
        _uncommittedGroup.Text = changes is null
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
        if (result.Operation == DataOperation.Commit && result.Status == Mconfig.Application.Workspaces.OperationStatus.Succeeded) _reason.Clear();
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
        _saveDraft.Enabled = editable && _stateShown!.Unsaved;
        _discard.Enabled = editable && _stateShown!.Unsaved;
        // エラーがある間は押せない（理由は横に示す）
        _commit.Enabled = editable && (_stateShown!.Unsaved || _stateShown.Uncommitted) && _stateShown.CommitErrors is 0;
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

