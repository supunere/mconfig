using Mconfig.App.Resources;
using Mconfig.Domain.Configuration;
using Mconfig.Presentation.Assignments;
using Mconfig.Presentation.Combinations;
using Mconfig.Presentation.Common;

namespace Mconfig.App.Views;

/// <summary>
/// 割り当て画面。左にカテゴリの一覧（未割当・使われない行の件数と未保存の変更 *）、
/// 右に割り当ての行（順位・条件・割り当て先・状態）を置く。
/// 条件（仕様値または任意）と割り当て先（プリセットまたは無効）を選び、行の追加・削除・優先順位の変更を行う。
/// 上位の行に覆われて使われない行は、色と行頭の記号と文言で示す。変更は Presenter へ知らせるだけで、表示は結果に応じて描き直される。
/// </summary>
internal sealed class AssignmentView : UserControl, IAssignmentView
{
    private static Color UnreachableBack => UiAppearance.Missing;
    private static Color ProblemFore => SystemInformation.HighContrast ? SystemColors.WindowText : Color.FromArgb(160, 60, 0);

    private readonly ListView _categoryList = new()
    {
        Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, MultiSelect = false, HideSelection = false,
        HeaderStyle = ColumnHeaderStyle.Nonclickable,
    };
    private readonly Label _categoryTitle = new() { AutoSize = true };
    private readonly Label _categoryLegend = new() { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(4), ForeColor = SystemColors.GrayText };
    private readonly Label _caption = new() { AutoSize = true, Margin = new Padding(4, 6, 16, 2) };
    private readonly Label _priorityHint = new() { AutoSize = true, ForeColor = UiAppearance.Muted, Anchor = AnchorStyles.Right };
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

    private IReadOnlyList<CategoryItemDisplay> _categories = [];
    private AssignmentTableDisplay? _shown;
    private bool _updating;
    private readonly SplitContainer _sections = new() { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, SplitterWidth = 6, BackColor = SystemColors.Control };
    public PresetComparisonView PresetManagement { get; } = new(managementMode: true);
    public string? CurrentCategory => _shown?.Category;
    public string? CurrentCategoryLabel => _shown?.CategoryLabel;
    public event EventHandler? CategoryContextChanged;
    public void ShowRules() => _grid.Focus();


    public AssignmentView()
    {
        Dock = DockStyle.Fill;
        Font = UiAppearance.BodyFont;
        BackColor = SystemColors.Window;
        UiAppearance.ApplyGrid(_grid);
        _grid.RowHeadersDefaultCellStyle.BackColor = UiAppearance.Background;
        _grid.RowHeadersDefaultCellStyle.ForeColor = UiAppearance.Text;
        UiAppearance.ApplyNavigationList(_categoryList);
        foreach (Button button in new[] { _add, _remove, _up, _down }) UiAppearance.ApplyButton(button);
        foreach (Label label in new[] { _categoryLegend, _info }) label.ForeColor = UiAppearance.Muted;
        // 左: カテゴリの一覧
        _categoryList.Columns.Add("", 150);
        _categoryList.Columns.Add("", 150);
        Panel left = new() { Dock = DockStyle.Fill, BackColor = SystemColors.Window, Padding = new Padding(8, 0, 8, 8) };
        TableLayoutPanel categoryHeader = new() { Dock = DockStyle.Top, AutoSize = true };
        UiAppearance.ArrangeSectionHeader(categoryHeader, [UiAppearance.SectionTitle(_categoryTitle)], []);
        categoryHeader.BackColor = SystemColors.Window;
        categoryHeader.Padding = new Padding(0, 4, 0, 4);
        left.Controls.Add(_categoryList);
        left.Controls.Add(_categoryLegend);
        left.Controls.Add(categoryHeader);

        // 右上: 割り当ての行
        TableLayoutPanel header = new() { Dock = DockStyle.Top, AutoSize = true };
        UiAppearance.ArrangeSectionHeader(header, [UiAppearance.SectionTitle(_caption)], []);
        header.BackColor = SystemColors.Window;
        FlowLayoutPanel bar = new() { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true, Margin = Padding.Empty };
        TableLayoutPanel toolbar = new() { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 2, RowCount = 1, Padding = new Padding(8, 4, 8, 4) };
        toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        toolbar.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _priorityHint.Margin = new Padding(8, 0, 0, 0);
        toolbar.Controls.Add(bar, 0, 0);
        toolbar.Controls.Add(_priorityHint, 1, 0);
        foreach (Button button in new[] { _add, _up, _down, _remove }) button.Margin = new Padding(0, 0, 6, 0);
        bar.Controls.AddRange([_add, _up, _down, _remove]);
        Panel rules = new() { Dock = DockStyle.Fill, BackColor = SystemColors.Window };
        rules.Controls.Add(_grid);
        rules.Controls.Add(_info);
        rules.Controls.Add(toolbar);
        rules.Controls.Add(header);
        rules.Resize += (_, _) =>
        {
            int width = Math.Max(120, rules.ClientSize.Width - rules.Padding.Horizontal - 16);
            _caption.MaximumSize = _info.MaximumSize = new Size(width, 0);
        };

        SplitContainer outer = new() { Dock = DockStyle.Fill, FixedPanel = FixedPanel.Panel1, SplitterWidth = 6, BackColor = SystemColors.Control };
        outer.Panel1.Controls.Add(left);
        _sections.Panel1.Controls.Add(rules);
        _sections.Panel2.Controls.Add(PresetManagement);
        outer.Panel2.Controls.Add(_sections);
        Controls.Add(outer);
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
            _sections.Panel1MinSize = _sections.Panel2MinSize = LogicalToDeviceUnits(120);
            _sections.SplitterDistance = Math.Max(_sections.Panel1MinSize, _sections.Height / 2);
            outer.SplitterDistance = Math.Min(LogicalToDeviceUnits(260), Math.Max(outer.Panel1MinSize, outer.Width / 3));
        });

        _categoryList.SelectedIndexChanged += (_, _) =>
        {
            if (_updating || _categoryList.SelectedIndices.Count == 0) return;
            CommitPendingEdit();
            CategorySelected?.Invoke(this, new CategorySelectedEventArgs(_categories[_categoryList.SelectedIndices[0]].Key));
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


    public void ApplyTexts()
    {
        PresetManagement.ApplyTexts();
        _priorityHint.Text = UiText.Get("Assignment_PriorityHint");
        _categoryTitle.Text = UiText.Get("Assignment_Categories");
        _categoryLegend.Text = UiText.Get("Assignment_CategoryLegend");
        _categoryList.Columns[0].Text = UiText.Get("Assignment_ColCategory");
        _categoryList.Columns[1].Text = UiText.Get("Assignment_ColProblems");
        _add.Text = UiText.Get("Assignment_Add");
        _remove.Text = UiText.Get("Assignment_Remove");
        _up.Text = UiText.Get("Assignment_Up");
        _down.Text = UiText.Get("Assignment_Down");
        if (_categories.Count > 0) ShowCategories(_categories, SelectedCategoryKey());
        if (_shown is not null) ShowAssignments(_shown);
    }

    public void CommitPendingEdit()
    {
        if (_grid.IsCurrentCellInEditMode) _grid.EndEdit();
        PresetManagement.CommitPendingEdit();
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
        bool categoryChanged = _shown?.Category != table.Category || _shown?.CategoryLabel != table.CategoryLabel;
        _shown = table;
        _info.Visible = !table.Available || table.RuleNotFound;
        if (categoryChanged) CategoryContextChanged?.Invoke(this, EventArgs.Empty);
        _updating = true;
        _grid.SuspendLayout();
        try
        {
            if (_grid.IsCurrentCellInEditMode) _grid.CancelEdit();
            _grid.Rows.Clear();
            _grid.Columns.Clear();
            _grid.ReadOnly = !table.Editable;
            _caption.Text = UiText.Format("Assignment_RulesCaption", table.CategoryLabel);
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
                    FlatStyle = FlatStyle.Standard,
                    SortMode = DataGridViewColumnSortMode.NotSortable,
                });
            _grid.Columns.Add(new DataGridViewComboBoxColumn
            {
                Name = "preset",
                HeaderText = UiText.Get("Assignment_Preset"),
                DisplayStyle = DataGridViewComboBoxDisplayStyle.DropDownButton,
                FlatStyle = FlatStyle.Standard,
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
            {
                _grid.CurrentCell = _grid.Rows[selected].Cells[Math.Min(currentColumn, _grid.Columns.Count - 1)];
                if (table.RuleNavigation)
                {
                    _grid.CurrentCell = _grid.Rows[selected].Cells[table.Columns.Count];
                    _grid.Rows[selected].DefaultCellStyle.BackColor = UiAppearance.Selection;
                    _grid.FirstDisplayedScrollingRowIndex = selected;
                    _grid.EndEdit();
                }
            }
            if (table.RuleNotFound)
            {
                _grid.ClearSelection();
                _grid.CurrentCell = null;
            }
            _info.Text = table.RuleNotFound ? UiText.Get("Assignment_RuleNotFound") : "";
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

