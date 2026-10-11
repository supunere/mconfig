using Mconfig.App.Resources;
using Mconfig.Application.Ports;
using Mconfig.Application.Releases.Queries;
using Mconfig.Domain.Configuration;
using Mconfig.Presentation.DataManagement;
using static Mconfig.App.Views.DataViewParts;

namespace Mconfig.App.Views;

/// <summary>
/// 出力の画面。テスト版・リリース版の出力（プレビューで比較元・差分・警告を確かめてから）と、
/// 中断した出力の再開。全件検証の進捗と中止を上に示す。
/// </summary>
internal sealed class ExportView : UserControl, IExportView
{
    private readonly Label _heading = new() { Dock = DockStyle.Top, AutoSize = true, Font = UiAppearance.HeadingFont, Padding = new Padding(8, 8, 8, 12) };
    private readonly ProgressStrip _progress = new();
    private readonly GroupBox _exportGroup = new() { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(8) };
    private readonly FlowLayoutPanel _cards = new() { Dock = DockStyle.Top, AutoSize = true, WrapContents = true };
    private readonly Label _testTitle = new() { AutoSize = true, Font = UiAppearance.HeadingFont };
    private readonly Label _releaseTitle = new() { AutoSize = true, Font = UiAppearance.HeadingFont };
    private readonly Label _testDescription = new() { AutoSize = true, ForeColor = UiAppearance.Muted };
    private readonly Label _releaseDescription = new() { AutoSize = true, ForeColor = UiAppearance.Muted };
    private readonly TableLayoutPanel _testCard;
    private readonly TableLayoutPanel _releaseCard;
    private readonly Button _exportTest = new() { AutoSize = true };
    private readonly Button _exportRelease = new() { AutoSize = true };
    private readonly ListView _pending = CreateList();
    private readonly GroupBox _pendingGroup = new() { Dock = DockStyle.Fill, Padding = new Padding(12) };
    private readonly Button _resume = new() { AutoSize = true };
    private readonly Label _pendingLabel = new() { AutoSize = true, Margin = new Padding(3, 12, 3, 3) };
    private readonly TextBox _pendingProblems = new()
    {
        Dock = DockStyle.Bottom, ReadOnly = true, Multiline = true, ScrollBars = ScrollBars.Vertical, Height = 100, Visible = false,
    };

    private DataStateDisplay? _stateShown;
    private ProgressDisplay? _progressShown;
    private PendingExportsDisplay _pendingShown = new([], []);
    private int _cardWidth;

    public ExportView()
    {
        Dock = DockStyle.Fill;
        AutoScroll = true;
        Font = UiAppearance.BodyFont;
        BackColor = UiAppearance.Background;
        UiAppearance.ApplyGroup(_exportGroup);
        UiAppearance.ApplyGroup(_pendingGroup);
        UiAppearance.ApplyPrimaryButton(_exportTest);
        UiAppearance.ApplyButton(_exportRelease);
        UiAppearance.ApplyButton(_resume);
        _pendingLabel.ForeColor = UiAppearance.Muted;
        _pendingLabel.Dock = DockStyle.Top;
        _pendingLabel.Padding = new Padding(8, 12, 8, 12);
        _pending.Columns.Add("", 220);
        _pending.Columns.Add("", 150);
        _pending.Columns.Add("", 120);
        _testCard = CreateExportCard(_testTitle, _testDescription, _exportTest);
        _releaseCard = CreateExportCard(_releaseTitle, _releaseDescription, _exportRelease);
        _cards.Controls.AddRange([_testCard, _releaseCard]);
        _cards.Resize += (_, _) => FitCards();
        _exportGroup.SizeChanged += (_, _) => FitScrollableContent();
        _exportGroup.Controls.Add(_cards);
        FlowLayoutPanel resume = Row(_resume);
        resume.Dock = DockStyle.Bottom;
        _pendingGroup.Controls.Add(_pending);
        _pendingGroup.Controls.Add(resume);
        _pendingGroup.Controls.Add(_pendingProblems);
        _pendingGroup.Controls.Add(_pendingLabel);
        Controls.Add(_pendingGroup);
        Controls.Add(_exportGroup);
        Controls.Add(_progress);
        Controls.Add(_heading);

        _progress.CancelClicked += (_, _) => CancelRequested?.Invoke(this, EventArgs.Empty);
        _exportTest.Click += (_, _) => ExportRequested?.Invoke(this, new ExportRequestedEventArgs(ReleaseKind.Test));
        _exportRelease.Click += (_, _) => ExportRequested?.Invoke(this, new ExportRequestedEventArgs(ReleaseKind.Release));
        _pending.SelectedIndexChanged += (_, _) => UpdateButtons();
        _resume.Click += (_, _) =>
        {
            if (_pending.SelectedIndices.Count == 1)
                ResumeExportRequested?.Invoke(this, new ResumeExportRequestedEventArgs(_pendingShown.Exports[_pending.SelectedIndices[0]].OperationId));
        };
        ApplyTexts();
    }

    public event EventHandler<ExportRequestedEventArgs>? ExportRequested;
    public event EventHandler<ResumeExportRequestedEventArgs>? ResumeExportRequested;
    public event EventHandler? CancelRequested;

    public void ApplyTexts()
    {
        _heading.Text = UiText.Get("Screen_Export");
        _exportGroup.Text = UiText.Get("Output_Choose");
        _testTitle.Text = UiText.Get("Output_TestTitle");
        _releaseTitle.Text = UiText.Get("Output_ReleaseTitle");
        _testDescription.Text = UiText.Get("Output_TestDescription");
        _releaseDescription.Text = UiText.Get("Output_ReleaseDescription");
        _exportTest.Text = UiText.Get("Data_ExportTest");
        _exportRelease.Text = UiText.Get("Data_ExportRelease");
        _pendingGroup.Text = UiText.Get("Output_PendingTitle");
        _resume.Text = UiText.Get("Data_Resume");
        _pending.Columns[0].Text = UiText.Get("Data_ColName");
        _pending.Columns[1].Text = UiText.Get("Data_ColWhen");
        _pending.Columns[2].Text = UiText.Get("Data_ColStage");
        if (_progressShown is not null) ShowProgress(_progressShown);
        ShowPendingExports(_pendingShown);
        FitCards();
    }

    private static TableLayoutPanel CreateExportCard(Label title, Label description, Button action)
    {
        TableLayoutPanel card = new()
        {
            ColumnCount = 1, RowCount = 3, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            BackColor = UiAppearance.Surface, Padding = new Padding(16), Margin = new Padding(6, 8, 6, 12),
            CellBorderStyle = TableLayoutPanelCellBorderStyle.None,
        };
        card.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        title.Margin = new Padding(0, 0, 0, 8);
        description.Margin = new Padding(0, 0, 0, 16);
        action.Margin = new Padding(0);
        card.Paint += (_, e) => ControlPaint.DrawBorder(e.Graphics, card.ClientRectangle, UiAppearance.Border, ButtonBorderStyle.Solid);
        card.Controls.Add(title, 0, 0);
        card.Controls.Add(description, 0, 1);
        card.Controls.Add(action, 0, 2);
        return card;
    }

    private void FitCards()
    {
        int available = _cards.ClientSize.Width - _cards.Padding.Horizontal;
        if (available < 160) return;
        int margin = _testCard.Margin.Horizontal;
        int width = available >= LogicalToDeviceUnits(640) + margin * 2
            ? (available - margin * 2) / 2 : available - margin;
        if (width == _cardWidth) return;
        _cardWidth = width;
        foreach (TableLayoutPanel card in new[] { _testCard, _releaseCard })
        {
            card.MinimumSize = new Size(0, 0);
            card.MaximumSize = new Size(width, 0);
            card.MinimumSize = new Size(width, 0);
            foreach (Label label in card.Controls.OfType<Label>()) label.MaximumSize = new Size(Math.Max(80, width - card.Padding.Horizontal - 4), 0);
        }
    }

    private void FitScrollableContent() => AutoScrollMinSize = new Size(0,
        _heading.Height + _exportGroup.Height + (_progress.Visible ? _progress.Height : 0) + LogicalToDeviceUnits(220));

    public void ShowState(DataStateDisplay state)
    {
        _stateShown = state;
        UpdateButtons();
    }

    public void ShowProgress(ProgressDisplay? progress)
    {
        _progressShown = progress;
        _progress.Show(progress);
        FitScrollableContent();
        UpdateButtons();
    }

    public void ShowOperationResult(OperationDisplay result) => Feedback.ShowOperation(FindForm(), result);

    public void ShowPendingExports(PendingExportsDisplay pending)
    {
        _pendingShown = pending;
        _pending.BeginUpdate();
        _pending.Items.Clear();
        foreach (PendingExportDisplay export in pending.Exports)
            _pending.Items.Add(new ListViewItem([export.Name, export.CreatedAt.ToLocalTime().ToString("g", UiText.Culture), export.Stage]));
        _pending.EndUpdate();
        _pendingProblems.Text = string.Join(Environment.NewLine + Environment.NewLine, pending.Problems.Select(p =>
            UiText.Get("Export_PendingProblem_" + p.Kind) + Environment.NewLine
            + (p.OperationId is { } id ? UiText.Format("Export_OperationId", id) + Environment.NewLine : "")
            + p.Location));
        _pendingProblems.Visible = pending.Problems.Count > 0;
        _pending.Visible = pending.Exports.Count > 0;
        _resume.Visible = pending.Exports.Count > 0;
        _pendingLabel.Visible = pending.Exports.Count == 0;
        _pendingLabel.Text = UiText.Get(pending.Problems.Count > 0 ? "Output_PendingNeedsAttention" : "Output_NoPending");
        UpdateButtons();
    }

    public ExportDecision ConfirmExport(ExportPreviewDisplay preview)
    {
        using Form dialog = Dialogs.Create(UiText.Get(preview.Kind == ReleaseKind.Release ? "Data_ExportRelease" : "Data_ExportTest"));
        TableLayoutPanel layout = Dialogs.Layout(5);
        List<string> lines = [];
        if (preview.Blocker != ReleaseBlocker.None) lines.Add(UiText.Get("Export_Blocker_" + preview.Blocker));
        if (preview.NeedsDraftSave) lines.Add(UiText.Get("Export_NeedsDraftSave"));
        lines.Add(preview.CompareTo is { } compare
            ? UiText.Format("Export_CompareTo", compare.Name, compare.PublishedAt.ToLocalTime().ToString("g", UiText.Culture))
            : UiText.Get("Export_NoCompareTo"));
        lines.Add(UiText.Format("Export_Summary", preview.Commits.Count, preview.Changes.Count));
        if (preview.UncommittedChanges.Count > 0) lines.Add(UiText.Format("Export_Uncommitted", preview.UncommittedChanges.Count));
        IssueDisplay[] shownIssues = preview.Issues
            .Where(i => i.Severity == ValidationSeverity.Error || i.RequiresConfirmation).ToArray();
        List<string> issueLines = [];
        if (shownIssues.Length > 0)
        {
            issueLines.AddRange(shownIssues.Select(i => $"・[{Feedback.Severity(i.Severity)}] {Feedback.Issue(i)}"));
            if (preview.NeedsApproval && preview.Blocker == ReleaseBlocker.None) issueLines.Add(UiText.Get("Export_ApproveWarnings"));
        }
        Label text = new() { Text = string.Join(Environment.NewLine, lines), AutoSize = true, MaximumSize = new Size(640, 0), Margin = new Padding(0, 0, 0, 8) };
        TextBox issues = new()
        {
            Text = string.Join(Environment.NewLine, issueLines), ReadOnly = true, Multiline = true,
            ScrollBars = ScrollBars.Vertical, Size = new Size(640, 140), Visible = shownIssues.Length > 0,
        };

        // 比較元の選び直し
        FlowLayoutPanel compareRow = new() { AutoSize = true, WrapContents = false, Margin = new Padding(0, 0, 0, 8) };
        ComboBox candidates = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 360 };
        candidates.Items.Add(UiText.Get("Export_NoCompareTo"));
        foreach (ReleaseCandidate candidate in preview.Candidates)
            candidates.Items.Add($"{candidate.Name} ({candidate.PublishedAt.ToLocalTime().ToString("g", UiText.Culture)})");
        int current = preview.CompareTo is null ? 0 : 1 + preview.Candidates.ToList().FindIndex(c => c.Name == preview.CompareTo.Name);
        candidates.SelectedIndex = Math.Max(0, current);
        Button change = new() { Text = UiText.Get("Export_ChangeCompareTo"), AutoSize = true, DialogResult = DialogResult.Retry, Enabled = false };
        candidates.SelectedIndexChanged += (_, _) => change.Enabled = candidates.SelectedIndex != current;
        compareRow.Controls.AddRange([new Label { Text = UiText.Get("Export_CompareLabel"), AutoSize = true, Margin = new Padding(0, 7, 4, 0) }, candidates, change]);

        ListView changes = CreateList();
        changes.Dock = DockStyle.None;
        changes.Size = new Size(640, 200);
        changes.Columns.Add(UiText.Get("Data_ColChange"), 160);
        changes.Columns.Add(UiText.Get("Data_ColLocation"), 220);
        changes.Columns.Add(UiText.Get("Data_ColBefore"), 120);
        changes.Columns.Add(UiText.Get("Data_ColAfter"), 120);
        FillChanges(changes, [.. preview.Changes, .. preview.UncommittedChanges]);

        bool canExport = preview.Blocker == ReleaseBlocker.None;
        Button cancel = new() { Text = UiText.Get("Button_Cancel"), AutoSize = true, DialogResult = DialogResult.Cancel };
        Button ok = new() { Text = UiText.Get("Export_Run"), AutoSize = true, DialogResult = DialogResult.OK, Enabled = canExport };
        layout.Controls.Add(text, 0, 0);
        layout.Controls.Add(compareRow, 0, 1);
        layout.Controls.Add(issues, 0, 2);
        layout.Controls.Add(changes, 0, 3);
        layout.Controls.Add(Dialogs.Buttons(cancel, ok), 0, 4);
        dialog.Controls.Add(layout);
        dialog.CancelButton = cancel;

        return dialog.ShowDialog(FindForm()) switch
        {
            DialogResult.OK when canExport => new ExportDecision(ExportAction.Export),
            DialogResult.Retry => new ExportDecision(ExportAction.ChangeCompareTo,
                ReleaseComparison.From(candidates.SelectedIndex <= 0 ? null : preview.Candidates[candidates.SelectedIndex - 1])),
            _ => new ExportDecision(ExportAction.Cancel),
        };
    }

    private void UpdateButtons()
    {
        bool editable = (_stateShown?.Editable ?? false) && !(_stateShown?.Busy ?? true);
        _exportTest.Enabled = _exportRelease.Enabled = editable;
        _resume.Enabled = editable && _pending.SelectedIndices.Count == 1;
    }
}

