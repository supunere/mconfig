using Mconfig.App.Resources;
using Mconfig.Domain.Configuration;
using Mconfig.Presentation.Assignments;
using Mconfig.Presentation.Combinations;
using Mconfig.Presentation.Common;

namespace Mconfig.App.Views;

/// <summary>
/// 割り当て画面。左にカテゴリの一覧（未割当・使われない行の件数と未保存の変更 *）、
/// 右上に割り当ての行（順位・条件・割り当て先・状態）、右下に一時保存の時点と比べて割り当て先が変わる組み合わせと集計・手順の案内を置く。
/// 条件（仕様値または任意）と割り当て先（プリセットまたは無効）を選び、行の追加・削除・優先順位の変更を行う。
/// 上位の行に覆われて使われない行は、色と行頭の記号と文言で示す。変更は Presenter へ知らせるだけで、表示は結果に応じて描き直される。
/// </summary>
internal sealed class AssignmentView : UserControl, IAssignmentView
{
    private static Color UnreachableBack => UiAppearance.Missing;
    private static Color ChangedBack => UiAppearance.Warning;
    private static Color ProblemFore => SystemInformation.HighContrast ? SystemColors.WindowText : Color.FromArgb(160, 60, 0);
    private readonly Label _heading = new() { Dock = DockStyle.Top, AutoSize = true, Font = UiAppearance.HeadingFont, Padding = new Padding(8, 8, 8, 12) };

    private readonly ListView _categoryList = new()
    {
        Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, MultiSelect = false, HideSelection = false,
        HeaderStyle = ColumnHeaderStyle.Nonclickable,
    };
    private readonly Label _categoryTitle = new() { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(4, 6, 4, 4) };
    private readonly Label _categoryLegend = new() { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(4), ForeColor = SystemColors.GrayText };
    private readonly Label _caption = new() { AutoSize = true, Margin = new Padding(4, 6, 16, 2) };
    private readonly Label _rule = new() { AutoSize = true, Margin = new Padding(4, 6, 3, 2), ForeColor = SystemColors.GrayText };
    private readonly Button _add = new() { AutoSize = true };
    private readonly Button _remove = new() { AutoSize = true };
    private readonly Button _up = new() { AutoSize = true };
    private readonly Button _down = new() { AutoSize = true };
    private readonly Label _info = new() { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(4) };
    private readonly DataGridView _grid = new()
    {
        Dock = DockStyle.Fill,
        AllowUserToAddRows = false,
        AllowUserToDeleteRows = false,
        AllowUserToResizeRows = false,
        // EditOnEnter は行全体の選択と組み合わせられないので、セル単位で選ぶ
        SelectionMode = DataGridViewSelectionMode.CellSelect,
        MultiSelect = false,
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.AllCells,
        BackgroundColor = SystemColors.Window,
        EditMode = DataGridViewEditMode.EditOnEnter,
        RowHeadersWidth = 60,
    };

    private readonly GroupBox _changesGroup = new() { Dock = DockStyle.Fill, Padding = new Padding(8) };
    private readonly RadioButton _changedOnly = new() { AutoSize = true, Checked = true, Margin = new Padding(3, 3, 12, 3) };
    private readonly RadioButton _all = new() { AutoSize = true };
    private readonly Label _compared = new() { AutoSize = true, Margin = new Padding(16, 6, 3, 3), ForeColor = SystemColors.GrayText };
    private readonly ListView _changes = new()
    {
        Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, HeaderStyle = ColumnHeaderStyle.Nonclickable, VirtualMode = true,
    };
    private readonly GroupBox _totalsGroup = new() { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(8) };
    private readonly Label _totals = new() { Dock = DockStyle.Top, AutoSize = true };
    private readonly GroupBox _guideGroup = new() { Dock = DockStyle.Fill, Padding = new Padding(8) };
    private readonly Label _guide = new() { Dock = DockStyle.Fill, AutoSize = false };

    private IReadOnlyList<CategoryItemDisplay> _categories = [];
    private AssignmentTableDisplay? _shown;
    private bool _updating;
    private IReadOnlyList<AssignmentChangeDisplay> _impactRows = [];
    private string? _impactEmpty;

    public AssignmentView()
    {
        Dock = DockStyle.Fill;
        Font = UiAppearance.BodyFont;
        BackColor = UiAppearance.Background;
        UiAppearance.ApplyGrid(_grid);
        _grid.RowHeadersDefaultCellStyle.BackColor = UiAppearance.Background;
        _grid.RowHeadersDefaultCellStyle.ForeColor = UiAppearance.Text;
        foreach (ListView list in new[] { _categoryList, _changes }) UiAppearance.ApplyList(list);
        foreach (Button button in new[] { _add, _remove, _up, _down }) UiAppearance.ApplyButton(button);
        foreach (GroupBox group in new[] { _changesGroup, _totalsGroup, _guideGroup }) UiAppearance.ApplyGroup(group);
        foreach (Label label in new[] { _categoryLegend, _rule, _compared, _info }) label.ForeColor = UiAppearance.Muted;
        _changes.RetrieveVirtualItem += (_, e) =>
        {
            if (_impactEmpty is { } empty)
            {
                e.Item = new ListViewItem(empty) { ForeColor = SystemColors.GrayText };
                // VirtualModeでは空状態の説明行にも全列分のサブ項目が必要。
                while (e.Item.SubItems.Count < _changes.Columns.Count) e.Item.SubItems.Add("");
                return;
            }
            AssignmentChangeDisplay change = _impactRows[e.ItemIndex];
            e.Item = new ListViewItem(change.Values.Append(OutcomeText(change.Before)).Append(OutcomeText(change.After))
                .Append(change.DecidingRow is int row ? UiText.Format("Assignment_DecidingRow", row) : "").ToArray());
            if (change.Changed) e.Item.BackColor = ChangedBack;
        };

        // 左: カテゴリの一覧
        _categoryList.Columns.Add("", 150);
        _categoryList.Columns.Add("", 150);
        Panel left = new() { Dock = DockStyle.Fill, BackColor = UiAppearance.Surface, Padding = new Padding(8) };
        left.Controls.Add(_categoryList);
        left.Controls.Add(_categoryLegend);
        left.Controls.Add(_categoryTitle);

        // 右上: 割り当ての行
        FlowLayoutPanel header = new() { Dock = DockStyle.Top, AutoSize = true, WrapContents = true, Padding = new Padding(4, 2, 4, 0) };
        header.Controls.AddRange([_caption, _rule]);
        _caption.Font = UiAppearance.HeadingFont;
        FlowLayoutPanel bar = new() { Dock = DockStyle.Top, AutoSize = true, WrapContents = true, Padding = new Padding(4, 0, 4, 2) };
        bar.Controls.AddRange([_add, _up, _down, _remove]);
        Panel rules = new() { Dock = DockStyle.Fill, BackColor = UiAppearance.Surface, Padding = new Padding(8) };
        rules.Controls.Add(_grid);
        rules.Controls.Add(_info);
        rules.Controls.Add(bar);
        rules.Controls.Add(header);
        rules.Resize += (_, _) =>
        {
            int width = Math.Max(120, rules.ClientSize.Width - rules.Padding.Horizontal - 16);
            _caption.MaximumSize = _rule.MaximumSize = _info.MaximumSize = new Size(width, 0);
        };

        // 右下: 変わる組み合わせと、集計・手順の案内
        FlowLayoutPanel scope = new() { Dock = DockStyle.Top, AutoSize = true, WrapContents = true };
        scope.Controls.AddRange([_changedOnly, _all, _compared]);
        _changesGroup.Controls.Add(_changes);
        _changesGroup.Controls.Add(scope);
        _totalsGroup.Controls.Add(_totals);
        _guideGroup.Controls.Add(_guide);
        _guideGroup.Dock = DockStyle.Top;
        _guideGroup.AutoSize = true;
        _guideGroup.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        _guide.Dock = DockStyle.Top;
        _guide.AutoSize = true;
        Panel side = new() { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(8, 0, 0, 0) };
        side.Resize += (_, _) => _guide.MaximumSize = _totals.MaximumSize = new Size(Math.Max(120, side.ClientSize.Width - 40), 0);
        side.Controls.Add(_guideGroup);
        side.Controls.Add(_totalsGroup);
        TableLayoutPanel impact = new() { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
        impact.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 62));
        impact.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 38));
        impact.Controls.Add(_changesGroup, 0, 0);
        impact.Controls.Add(side, 1, 0);

        SplitContainer right = new() { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, SplitterWidth = 6 };
        right.Panel1.Controls.Add(rules);
        right.Panel2.Controls.Add(impact);
        SplitContainer outer = new() { Dock = DockStyle.Fill, FixedPanel = FixedPanel.Panel1, SplitterWidth = 6 };
        outer.Panel1.Controls.Add(left);
        outer.Panel2.Controls.Add(right);
        Controls.Add(outer);
        Controls.Add(_heading);
        _categoryList.Resize += (_, _) =>
        {
            int width = Math.Max(180, _categoryList.ClientSize.Width - SystemInformation.VerticalScrollBarWidth);
            _categoryList.Columns[0].Width = width * 2 / 5;
            _categoryList.Columns[1].Width = width * 3 / 5;
        };
        // 親画面のDockレイアウト後に初期幅を適用する。
        Load += (_, _) => BeginInvoke(() =>
        {
            if (IsDisposed) return;
            outer.SplitterDistance = Math.Min(LogicalToDeviceUnits(260), Math.Max(outer.Panel1MinSize, outer.Width / 3));
            right.SplitterDistance = Math.Max(right.Panel1MinSize, right.Height * 11 / 20);
        });

        _categoryList.SelectedIndexChanged += (_, _) =>
        {
            if (_updating || _categoryList.SelectedIndices.Count == 0) return;
            CategorySelected?.Invoke(this, new CategorySelectedEventArgs(_categories[_categoryList.SelectedIndices[0]].Key));
        };
        _changedOnly.CheckedChanged += (_, _) =>
        {
            if (!_updating && _changedOnly.Checked) ChangeScopeChanged?.Invoke(this, new ChangeScopeEventArgs(false));
        };
        _all.CheckedChanged += (_, _) =>
        {
            if (!_updating && _all.Checked) ChangeScopeChanged?.Invoke(this, new ChangeScopeEventArgs(true));
        };
        _add.Click += (_, _) => RowAdded?.Invoke(this, EventArgs.Empty);
        _remove.Click += (_, _) =>
        {
            if (CurrentRow() is not int row) return;
            if (MessageBox.Show(this, UiText.Format("Assignment_RemoveConfirm", row + 1), UiText.Get("Assignment_Remove"),
                    MessageBoxButtons.OKCancel, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) == DialogResult.OK)
                RowRemoved?.Invoke(this, new AssignmentRowEventArgs(row));
        };
        _up.Click += (_, _) => MoveRow(-1);
        _down.Click += (_, _) => MoveRow(1);
        // コンボボックスは選んだ時点で確定させる
        _grid.CurrentCellDirtyStateChanged += (_, _) =>
        {
            if (_grid.IsCurrentCellDirty) _grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
        };
        _grid.CellValueChanged += OnCellValueChanged;
        _grid.DataError += (_, e) => e.ThrowException = false;
        _grid.CurrentCellChanged += (_, _) => UpdateButtons();
        ApplyTexts();
    }

    public event EventHandler<CategorySelectedEventArgs>? CategorySelected;
    public event EventHandler<ConditionChangedEventArgs>? ConditionChanged;
    public event EventHandler<AssignmentPresetChangedEventArgs>? PresetChanged;
    public event EventHandler? RowAdded;
    public event EventHandler<AssignmentRowEventArgs>? RowRemoved;
    public event EventHandler<AssignmentMovedEventArgs>? RowMoved;
    public event EventHandler<ChangeScopeEventArgs>? ChangeScopeChanged;

    public void ApplyTexts()
    {
        _heading.Text = UiText.Get("Screen_Assignments");
        _categoryTitle.Text = UiText.Get("Assignment_Categories");
        _categoryLegend.Text = UiText.Get("Assignment_CategoryLegend");
        _categoryList.Columns[0].Text = UiText.Get("Assignment_ColCategory");
        _categoryList.Columns[1].Text = UiText.Get("Assignment_ColProblems");
        _rule.Text = UiText.Get("Assignment_Rule");
        _add.Text = UiText.Get("Assignment_Add");
        _remove.Text = UiText.Get("Assignment_Remove");
        _up.Text = UiText.Get("Assignment_Up");
        _down.Text = UiText.Get("Assignment_Down");
        _changesGroup.Text = UiText.Get("Assignment_ChangesTitle");
        _compared.Text = UiText.Get("Assignment_ComparedWithDraft");
        _guideGroup.Text = UiText.Get("Assignment_GuideTitle");
        _guide.Text = UiText.Get("Assignment_Guide");
        if (_categories.Count > 0) ShowCategories(_categories, SelectedCategoryKey());
        if (_shown is not null) ShowAssignments(_shown);
    }

    public void CommitPendingEdit()
    {
        if (_grid.IsCurrentCellInEditMode) _grid.EndEdit();
    }

    public void ShowCategories(IReadOnlyList<CategoryItemDisplay> categories, string? selected)
    {
        _updating = true;
        try
        {
            _categories = categories;
            _categoryList.BeginUpdate();
            _categoryList.Items.Clear();
            foreach (CategoryItemDisplay category in categories)
            {
                ListViewItem item = new([category.Unsaved ? $"{category.Label} *" : category.Label, ProblemText(category)])
                {
                    UseItemStyleForSubItems = false,
                };
                if (!category.Available || category.Unassigned > 0 || category.UnreachableRows > 0) item.SubItems[1].ForeColor = ProblemFore;
                _categoryList.Items.Add(item);
            }
            int index = categories.ToList().FindIndex(c => c.Key == selected);
            if (index >= 0)
            {
                _categoryList.Items[index].Selected = true;
                _categoryList.Items[index].Focused = true;
            }
            _categoryList.EndUpdate();
        }
        finally
        {
            _updating = false;
        }
    }

    private static string ProblemText(CategoryItemDisplay category)
    {
        if (!category.Available) return UiText.Get("Assignment_CategoryUnavailable");
        List<string> problems = [];
        if (category.Unassigned > 0) problems.Add(UiText.Format("Assignment_CategoryUnassigned", category.Unassigned));
        if (category.UnreachableRows > 0) problems.Add(UiText.Format("Assignment_CategoryUnreachable", category.UnreachableRows));
        return string.Join(UiText.Get("SharedEdit_Separator"), problems);
    }

    private string? SelectedCategoryKey() =>
        _categoryList.SelectedIndices.Count > 0 && _categoryList.SelectedIndices[0] < _categories.Count
            ? _categories[_categoryList.SelectedIndices[0]].Key
            : null;

    public void ShowAssignments(AssignmentTableDisplay table)
    {
        int currentColumn = _grid.CurrentCell?.ColumnIndex ?? 0;
        _shown = table;
        _updating = true;
        _grid.SuspendLayout();
        try
        {
            if (_grid.IsCurrentCellInEditMode) _grid.CancelEdit();
            _grid.Rows.Clear();
            _grid.Columns.Clear();
            _grid.ReadOnly = !table.Editable;
            _caption.Text = UiText.Format("Assignment_Caption", table.CategoryLabel);
            ShowImpact(table);
            if (!table.Available)
            {
                _info.Text = UiText.Get("Assignment_Unavailable");
                return;
            }

            foreach (ConditionColumnDisplay column in table.Columns)
                _grid.Columns.Add(new DataGridViewComboBoxColumn
                {
                    Name = "spec_" + column.Key,
                    HeaderText = column.Label,
                    DisplayStyle = DataGridViewComboBoxDisplayStyle.DropDownButton,
                    FlatStyle = FlatStyle.Flat,
                    SortMode = DataGridViewColumnSortMode.NotSortable,
                });
            _grid.Columns.Add(new DataGridViewComboBoxColumn
            {
                Name = "preset",
                HeaderText = UiText.Get("Assignment_Preset"),
                DisplayStyle = DataGridViewComboBoxDisplayStyle.DropDownButton,
                FlatStyle = FlatStyle.Flat,
                SortMode = DataGridViewColumnSortMode.NotSortable,
            });
            _grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = "state",
                HeaderText = UiText.Get("Assignment_ColState"),
                ReadOnly = true,
                SortMode = DataGridViewColumnSortMode.NotSortable,
            });

            for (int r = 0; r < table.Rows.Count; r++)
            {
                AssignmentRowDisplay row = table.Rows[r];
                int index = _grid.Rows.Add();
                DataGridViewRow gridRow = _grid.Rows[index];
                for (int c = 0; c < table.Columns.Count; c++)
                {
                    // 仕様にない値も、その行の選択肢に加えて失わずに見せる
                    string value = row.Conditions[c];
                    DataGridViewComboBoxCell cell = (DataGridViewComboBoxCell)gridRow.Cells[c];
                    cell.Items.Add(new Choice(AssignmentEvaluation.AnyValue, UiText.Get("Assignment_Any")));
                    foreach (string v in table.Columns[c].Values) cell.Items.Add(new Choice(v, v));
                    if (value != AssignmentEvaluation.AnyValue && !table.Columns[c].Values.Contains(value))
                        cell.Items.Add(new Choice(value, UiText.Format("Assignment_UnknownValue", value)));
                    cell.DisplayMember = nameof(Choice.Label);
                    cell.ValueMember = nameof(Choice.Value);
                    cell.Value = value;
                }
                DataGridViewComboBoxCell preset = (DataGridViewComboBoxCell)gridRow.Cells[table.Columns.Count];
                preset.Items.Add(new Choice("", UiText.Get("Assignment_Disabled")));
                foreach (string name in table.Presets) preset.Items.Add(new Choice(name, name));
                if (row.Preset is not null && !table.Presets.Contains(row.Preset))
                    preset.Items.Add(new Choice(row.Preset, UiText.Format("Assignment_MissingPreset", row.Preset)));
                preset.DisplayMember = nameof(Choice.Label);
                preset.ValueMember = nameof(Choice.Value);
                preset.Value = row.Preset ?? "";

                gridRow.Cells[table.Columns.Count + 1].Value = StateText(row);
                gridRow.Cells[table.Columns.Count + 1].Style.ForeColor = row.Unreachable ? ProblemFore : SystemColors.GrayText;
                string number = (r + 1).ToString(UiText.Culture);
                gridRow.HeaderCell.Value = row.Unreachable ? $"⚠ {number}" : row.Unsaved ? $"* {number}" : number;
                if (row.Unreachable)
                {
                    gridRow.DefaultCellStyle.BackColor = UnreachableBack;
                    gridRow.HeaderCell.ToolTipText = UiText.Get("Assignment_UnreachableTip");
                    foreach (DataGridViewCell cell in gridRow.Cells) cell.ToolTipText = UiText.Get("Assignment_UnreachableTip");
                }
            }

            if (table.SelectedRow is int selected && selected < _grid.Rows.Count)
                _grid.CurrentCell = _grid.Rows[selected].Cells[Math.Min(currentColumn, _grid.Columns.Count - 1)];
            int unreachable = table.Rows.Count(r => r.Unreachable);
            _info.Text = unreachable > 0
                ? UiText.Format("Assignment_CountWithUnreachable", table.Rows.Count, unreachable)
                : UiText.Format("Assignment_Count", table.Rows.Count);
        }
        finally
        {
            _grid.ResumeLayout();
            _updating = false;
            UpdateButtons();
        }
    }

    private static string StateText(AssignmentRowDisplay row)
    {
        string text = row.AppliedCount is not int count ? UiText.Get("Assignment_StateNotCounted")
            : count == 0 ? UiText.Get("Assignment_StateUnreachable")
            : UiText.Format("Assignment_StateApplied", count);
        return row.Unsaved ? text + UiText.Get("Assignment_StateUnsaved") : text;
    }

    /// <summary>変わる組み合わせ（一時保存の時点 → いま）と、集計を表示する。</summary>
    private void ShowImpact(AssignmentTableDisplay table)
    {
        _changedOnly.Enabled = _all.Enabled = !table.ImpactSkipped;
        _changedOnly.Text = UiText.Format("Assignment_ChangedOnly", table.ChangedCount);
        _all.Text = UiText.Format("Assignment_AllCombinations", table.Totals.Total);
        _changedOnly.Checked = !table.ShowAllChanges;
        _all.Checked = table.ShowAllChanges;

        _changes.BeginUpdate();
        _changes.VirtualListSize = 0;
        _changes.Columns.Clear();
        foreach (ConditionColumnDisplay column in table.Columns) _changes.Columns.Add(column.Label, 90);
        _changes.Columns.Add(UiText.Get("Assignment_ColBefore"), 130);
        _changes.Columns.Add(UiText.Get("Assignment_ColAfter"), 130);
        _changes.Columns.Add(UiText.Get("Assignment_ColDeciding"), 90);
        _impactRows = table.Changes;
        // 組み合わせが多すぎるときは求めていないので、検証で確かめるよう示す
        string? empty = table.ImpactSkipped ? UiText.Format("Assignment_ImpactSkipped", table.CombinationCount)
            : table.Changes.Count > 0 ? null
            : UiText.Get(table.ShowAllChanges ? "Assignment_NoCombinations" : "Assignment_NoChanges");
        _impactEmpty = empty;
        _changes.VirtualListSize = empty is null ? _impactRows.Count : 1;
        _changes.EndUpdate();

        _totalsGroup.Text = table.ImpactSkipped
            ? UiText.Format("Assignment_TotalsTitleLarge", table.CategoryLabel, table.CombinationCount)
            : UiText.Format("Assignment_TotalsTitle", table.CategoryLabel, table.Totals.Total);
        _totals.Text = table.ImpactSkipped
            ? UiText.Get("Assignment_TotalsSkipped")
            : UiText.Format("Assignment_Totals",
                table.Totals.Assigned, table.Totals.Disabled, table.Totals.Unassigned, table.Totals.UnreachableRows);
    }

    private static string OutcomeText(OutcomeDisplay outcome) => outcome.Kind switch
    {
        OutcomeDisplayKind.Assigned => outcome.Preset ?? "",
        OutcomeDisplayKind.Disabled => $"🚫 {UiText.Get("Outcome_Disabled")}",
        _ => $"⚠ {UiText.Get("Outcome_Unassigned")}",
    };

    public void ShowEditFeedback(EditFeedback feedback) =>
        MessageBox.Show(this, Feedback.Edit(feedback), UiText.Get("App_Title"), MessageBoxButtons.OK, MessageBoxIcon.Warning);

    private void OnCellValueChanged(object? sender, DataGridViewCellEventArgs e)
    {
        if (_updating || _shown is null || e.RowIndex < 0 || e.ColumnIndex < 0 || e.ColumnIndex > _shown.Columns.Count) return;
        string value = _grid.Rows[e.RowIndex].Cells[e.ColumnIndex].Value as string ?? "";
        int row = e.RowIndex;
        int column = e.ColumnIndex;
        AssignmentTableDisplay shown = _shown;
        // 表示の描き直しはグリッドのイベントの外で行う
        BeginInvoke(() =>
        {
            if (column < shown.Columns.Count)
                ConditionChanged?.Invoke(this, new ConditionChangedEventArgs(row, shown.Columns[column].Key, value));
            else
                PresetChanged?.Invoke(this, new AssignmentPresetChangedEventArgs(row, value.Length == 0 ? null : value));
        });
    }

    private int? CurrentRow() => _grid.CurrentCell?.RowIndex is int row && row >= 0 ? row : null;

    private void MoveRow(int offset)
    {
        if (CurrentRow() is not int row) return;
        CommitPendingEdit();
        RowMoved?.Invoke(this, new AssignmentMovedEventArgs(row, row + offset));
    }

    private void UpdateButtons()
    {
        bool editable = _shown is { Available: true, Editable: true };
        int? row = CurrentRow();
        _add.Enabled = editable;
        _remove.Enabled = editable && row is not null;
        _up.Enabled = editable && row > 0;
        _down.Enabled = editable && row is int r && r < _grid.Rows.Count - 1;
    }

    /// <summary>コンボボックスの選択肢。値（保存する文字列）と表示を分ける。</summary>
    private sealed record Choice(string Value, string Label);
}

