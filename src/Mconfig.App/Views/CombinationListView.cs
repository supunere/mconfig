using Mconfig.App.Resources;
using Mconfig.Presentation.Combinations;

namespace Mconfig.App.Views;

/// <summary>
/// 組み合わせの表（参照のみ）。指定した条件はタグで示し、未指定の条件と割り当て結果を2段の見出しで区別する。
/// セルを選ぶと、その組み合わせとカテゴリを Presenter へ知らせる（値の表がそのカテゴリに切り替わる）。選んだ行と列の見出しを強調する。
/// 無効・未割当は色に加えて文言でも示す。絞り込みと選択は表示の状態だけで、データは変えない。
/// 表は仮想モードで、見えている行だけを求めて表示する（全行を保持しない）。組み合わせが 5 万件を超えるときは、
/// 表示と検証に時間がかかることを表の上に示す。
/// </summary>
internal sealed class CombinationListView : UserControl, ICombinationListView
{
    private static Color DisabledBack => UiAppearance.Unused;
    private static Color UnassignedBack => UiAppearance.Warning;
    private static Color SelectedRowBack => UiAppearance.Selection;
    private static Color SelectedHeaderBack => UiAppearance.Selection;

    private readonly FlowLayoutPanel _filters = new() { AutoSize = true, WrapContents = true, Margin = new Padding(0) };
    private readonly FlowLayoutPanel _filterArea = new() { Dock = DockStyle.Top, AutoSize = true, WrapContents = false, FlowDirection = FlowDirection.TopDown, Padding = new Padding(4) };
    private readonly FlowLayoutPanel _filterBar = new() { AutoSize = true, WrapContents = true, Margin = new Padding(0) };
    private readonly FlowLayoutPanel _filterTags = new() { AutoSize = true, WrapContents = true, Margin = new Padding(0) };
    private readonly FlowLayoutPanel _filterFooter = new() { AutoSize = true, Margin = new Padding(0), Visible = false };
    private readonly FlowLayoutPanel _countBar = new() { Dock = DockStyle.Top, AutoSize = true, WrapContents = true, Padding = new Padding(4, 2, 4, 4) };
    private readonly Button _filterToggle = new() { AutoSize = true };
    private readonly Button _filterClose = new() { AutoSize = true };
    private readonly Label _filterStatus = new() { AutoSize = true, Margin = new Padding(8, 10, 12, 3) };
    private readonly Panel _columnGroups = new() { Dock = DockStyle.Top, Height = 28 };
    private readonly Label _conditionsGroup = new() { TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(8, 0, 0, 0) };
    private readonly Label _resultsGroup = new() { TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(8, 0, 0, 0) };
    private readonly Dictionary<string, ComboBox> _filterEditors = new(StringComparer.Ordinal);
    private readonly Button _clear = new() { AutoSize = true, Margin = new Padding(12, 3, 3, 3) };
    private readonly Button _goToAssignment = new() { AutoSize = true, Enabled = false };
    private readonly Label _count = new() { AutoSize = true, Margin = new Padding(12, 8, 3, 3), ForeColor = SystemColors.GrayText };
    private readonly Button _showValues = new() { AutoSize = true, Margin = new Padding(12, 3, 3, 3), Visible = false };
    private readonly Label _warning = new()
    {
        Dock = DockStyle.Top, AutoSize = true, Visible = false, Padding = new Padding(6, 4, 6, 4),
        BackColor = Color.FromArgb(255, 244, 206), ForeColor = Color.FromArgb(107, 78, 0),
    };
    private readonly DataGridView _grid = new()
    {
        Dock = DockStyle.Fill,
        ReadOnly = true,
        AllowUserToAddRows = false,
        AllowUserToDeleteRows = false,
        AllowUserToResizeRows = false,
        SelectionMode = DataGridViewSelectionMode.CellSelect,
        MultiSelect = false,
        RowHeadersVisible = false,
        // 全行の幅を測らないよう、列幅は見えている行から一度だけ決める
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None,
        BackgroundColor = SystemColors.Window,
        EnableHeadersVisualStyles = false,
        VirtualMode = true,
    };

    private IReadOnlyList<FilterDisplay> _shownFilters = [];
    private CombinationTableDisplay? _shown;
    private (int Index, CombinationDisplayRow Row)? _rowCache;
    private Font? _bold;
    private bool _updating;
    private bool _filtersExpanded;
    private bool _spaceRequestPending;

    public CombinationListView()
    {
        Dock = DockStyle.Fill;
        Font = UiAppearance.BodyFont;
        BackColor = UiAppearance.Surface;
        UiAppearance.ApplyGrid(_grid);
        _grid.ColumnHeadersDefaultCellStyle.WrapMode = DataGridViewTriState.False;
        _grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
        _grid.ColumnHeadersHeight = LogicalToDeviceUnits(36);
        UiAppearance.ApplyButton(_clear);
        UiAppearance.ApplyButton(_showValues);
        UiAppearance.ApplyButton(_goToAssignment);
        UiAppearance.ApplyButton(_filterToggle);
        UiAppearance.ApplyButton(_filterClose);
        _count.ForeColor = UiAppearance.Muted;
        _filterStatus.ForeColor = UiAppearance.Muted;
        _filters.Visible = false;
        _filterBar.Controls.AddRange([_filterToggle, _filterStatus, _clear]);
        _filterFooter.Controls.Add(_filterClose);
        _filterArea.Controls.AddRange([_filterBar, _filterTags, _filters, _filterFooter]);
        _countBar.Controls.AddRange([_count, _goToAssignment, _showValues]);
        _columnGroups.Height = LogicalToDeviceUnits(28);
        _columnGroups.Controls.AddRange([_conditionsGroup, _resultsGroup]);
        _columnGroups.Paint += (_, e) => UiAppearance.DrawTableBorder(_columnGroups, e.Graphics);
        _conditionsGroup.Paint += DrawConditionBoundary;
        Panel table = new() { Dock = DockStyle.Fill };
        table.Controls.Add(_grid);
        table.Controls.Add(_columnGroups);
        Resize += (_, _) =>
        {
            int width = Math.Max(LogicalToDeviceUnits(180), ClientSize.Width - _filterArea.Padding.Horizontal - 8);
            foreach (Control area in new Control[] { _filterBar, _filterTags, _filters, _filterFooter })
                area.MaximumSize = new Size(width, 0);
            _count.MaximumSize = new Size(Math.Max(200, ClientSize.Width - 40), 0);
            RequestTableSpace();
        };
        _filterArea.SizeChanged += (_, _) => RequestTableSpace();
        _warning.SizeChanged += (_, _) => RequestTableSpace();
        Controls.Add(table);
        Controls.Add(_warning);
        Controls.Add(_countBar);
        Controls.Add(_filterArea);
        _grid.Scroll += (_, _) => UpdateColumnGroups();
        _grid.ColumnWidthChanged += (_, _) => UpdateColumnGroups();
        _grid.SizeChanged += (_, _) =>
        {
            UpdateColumnGroups();
        };
        _grid.Paint += DrawConditionBoundary;
        // Scroll通知の時点では列の表示位置が更新前のことがあるため、描画時にも同期する。
        _grid.Paint += (_, _) => UpdateColumnGroups();
        _grid.CellValueNeeded += OnCellValueNeeded;
        _grid.CellFormatting += OnCellFormatting;
        _grid.CurrentCellChanged += (_, _) =>
        {
            if (_updating || _shown is null || _grid.CurrentCell is not { } cell) return;
            int categoryIndex = cell.ColumnIndex - _shown.SpecColumns.Count;
            string? category = categoryIndex >= 0 && categoryIndex < _shown.CategoryColumns.Count ? _shown.CategoryColumns[categoryIndex].Key : null;
            CellSelected?.Invoke(this, new CellSelectedEventArgs(cell.RowIndex, category));
        };
        _clear.Click += (_, _) => FiltersCleared?.Invoke(this, EventArgs.Empty);
        _showValues.Click += (_, _) => ShowValuesRequested?.Invoke(this, EventArgs.Empty);
        _goToAssignment.Click += (_, _) => AssignmentRequested?.Invoke(this, EventArgs.Empty);
        _filterToggle.Click += (_, _) => SetFiltersExpanded(!_filtersExpanded);
        _filterClose.Click += (_, _) => SetFiltersExpanded(false);
        ApplyTexts();
    }

    public event EventHandler<FilterChangedEventArgs>? FilterChanged;
    public event EventHandler<CellSelectedEventArgs>? CellSelected;
    public event EventHandler? FiltersCleared;
    public event EventHandler? AssignmentRequested;

    /// <summary>値の表を隠しているときの「値を表示」。</summary>
    public event EventHandler? ShowValuesRequested;
    public event EventHandler? TableSpaceRequested;

    public int PreferredTableHeight => _filterArea.Height + _countBar.Height
        + (_warning.Visible ? _warning.Height : 0) + _columnGroups.Height + _grid.ColumnHeadersHeight
        + LogicalToDeviceUnits(96) + SystemInformation.HorizontalScrollBarHeight;

    private void RequestTableSpace()
    {
        if (_spaceRequestPending || !IsHandleCreated || IsDisposed) return;
        _spaceRequestPending = true;
        BeginInvoke(() =>
        {
            _spaceRequestPending = false;
            if (!IsDisposed) TableSpaceRequested?.Invoke(this, EventArgs.Empty);
        });
    }

    private void SetFiltersExpanded(bool expanded)
    {
        _filtersExpanded = expanded;
        _filters.Visible = _filterFooter.Visible = expanded;
        _filterToggle.Text = UiText.Get(expanded ? "Filter_Collapse" : "Filter_Expand");
        RequestTableSpace();
    }

    public void ApplyTexts()
    {
        _clear.Text = UiText.Get("Filter_ClearAll");
        _filterClose.Text = UiText.Get("Filter_Close");
        _filterToggle.Text = UiText.Get(_filtersExpanded ? "Filter_Collapse" : "Filter_Expand");
        _showValues.Text = UiText.Get("Values_Show");
        _goToAssignment.Text = UiText.Get("Combination_GoToAssignment");
        _conditionsGroup.Text = UiText.Get("Combination_ConditionsGroup");
        _resultsGroup.Text = UiText.Get("Combination_ResultsGroup");
        foreach (ComboBox editor in _filterEditors.Values) editor.Items[0] = UiText.Get("Filter_All");
        ShowFilterTags();
        if (_shown is null) return;
        ShowCount(_shown);
        ShowWarning(_shown);
        _grid.Invalidate();
    }

    /// <summary>値の表を隠しているか。隠している間は「値を表示」を出す。</summary>
    public void SetValuesHidden(bool hidden) => _showValues.Visible = hidden;

    public void FocusTable() => _grid.Focus();

    public void ShowFilters(IReadOnlyList<FilterDisplay> filters)
    {
        bool definitionsChanged = _shownFilters.Count != filters.Count || _shownFilters.Where((old, i) =>
            old.Key != filters[i].Key || old.Label != filters[i].Label || !old.Values.SequenceEqual(filters[i].Values)).Any();
        if (!definitionsChanged && _shownFilters.Select(f => f.Selected).SequenceEqual(filters.Select(f => f.Selected))) return;
        _updating = true;
        try
        {
            _filters.SuspendLayout();
            if (definitionsChanged)
            {
                foreach (Control old in _filters.Controls.Cast<Control>().ToArray()) old.Dispose();
                _filterEditors.Clear();
                foreach (FilterDisplay filter in filters)
                {
                    int width = LogicalToDeviceUnits(180);
                    FlowLayoutPanel field = new() { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = new Padding(4, 6, 12, 6) };
                    Label label = new() { Text = filter.Label, AutoSize = true, MaximumSize = new Size(width, 0), Margin = new Padding(3, 0, 3, 4) };
                    ComboBox combo = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = width, Tag = filter.Key, AccessibleName = filter.Label };
                    combo.Items.Add(UiText.Get("Filter_All"));
                    foreach (string value in filter.Values) combo.Items.Add(value);
                    combo.SelectedIndexChanged += OnFilterSelected;
                    field.Controls.AddRange([label, combo]);
                    _filters.Controls.Add(field);
                    _filterEditors.Add(filter.Key, combo);
                }
            }
            foreach (FilterDisplay filter in filters)
            {
                ComboBox combo = _filterEditors[filter.Key];
                combo.SelectedIndex = filter.Selected is null ? 0 : 1 + filter.Values.ToList().IndexOf(filter.Selected);
            }
            _shownFilters = filters;
            ShowFilterTags();
        }
        finally
        {
            _filters.ResumeLayout(true);
            _updating = false;
        }
    }

    private void ShowFilterTags()
    {
        _filterTags.SuspendLayout();
        foreach (Control old in _filterTags.Controls.Cast<Control>().ToArray()) old.Dispose();
        foreach (FilterDisplay filter in _shownFilters.Where(f => f.Selected is not null))
        {
            Button tag = new() { AutoSize = true, Text = $"{filter.Label}: {filter.Selected}  ×", AccessibleName = UiText.Format("Filter_Remove", filter.Label, filter.Selected!), Margin = new Padding(3) };
            UiAppearance.ApplyButton(tag);
            tag.BackColor = UiAppearance.Selection;
            tag.ForeColor = SystemInformation.HighContrast ? SystemColors.HighlightText : UiAppearance.Text;
            tag.Click += (_, _) => FilterChanged?.Invoke(this, new FilterChangedEventArgs(filter.Key, null));
            _filterTags.Controls.Add(tag);
        }
        int count = _shownFilters.Count(f => f.Selected is not null);
        _filterTags.Visible = count > 0;
        _clear.Enabled = count > 0;
        _filterStatus.Text = count == 0 ? UiText.Get("Filter_None") : UiText.Format("Filter_SelectedCount", count);
        _filterTags.ResumeLayout(true);
        RequestTableSpace();
    }

    public void ShowCombinations(CombinationTableDisplay table)
    {
        _updating = true;
        try
        {
            // 選択だけが変わったときは作り直さない（横のスクロール位置を保ち、ちらつかせない）
            bool rebuild = _shown is null || !SameContent(_shown, table);
            _shown = table;
            if (rebuild) Rebuild(table);
            ShowSelection(table, rebuild);
            ShowCount(table);
            _clear.Enabled = table.Filtered;
            int category = table.CategoryColumns.ToList().FindIndex(c => c.Key == table.SelectedCategory);
            _goToAssignment.Enabled = table.SelectedIndex is int selected && category >= 0
                && table.Rows[selected].Outcomes[category].Kind is OutcomeDisplayKind.Assigned or OutcomeDisplayKind.Disabled;
        }
        finally
        {
            _updating = false;
        }
    }

    private void Rebuild(CombinationTableDisplay table)
    {
        int horizontal = _grid.HorizontalScrollingOffset;
        int firstRow = _grid.FirstDisplayedScrollingRowIndex;
        _rowCache = null;
        _grid.SuspendLayout();
        try
        {
            _grid.RowCount = 0;
            _grid.Columns.Clear();
            foreach (ColumnDisplay column in table.SpecColumns)
                _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = column.Key, HeaderText = column.Label, SortMode = DataGridViewColumnSortMode.NotSortable });
            foreach (ColumnDisplay column in table.CategoryColumns)
                _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = column.Key, HeaderText = column.Label, SortMode = DataGridViewColumnSortMode.NotSortable });
            // 仮想モードなので、行は見えている分だけ CellValueNeeded で求める
            _grid.RowCount = table.Rows.Count;
            _grid.AutoResizeColumns(DataGridViewAutoSizeColumnsMode.DisplayedCells);
            if (firstRow >= 0 && firstRow < _grid.RowCount) _grid.FirstDisplayedScrollingRowIndex = firstRow;
            _grid.HorizontalScrollingOffset = horizontal;
        }
        finally
        {
            _grid.ResumeLayout();
        }
        ShowWarning(table);
        UpdateColumnGroups();
        RequestTableSpace();
    }

    private void UpdateColumnGroups()
    {
        if (_shown is null) return;
        _columnGroups.BackColor = UiAppearance.Background;
        ShowColumnGroup(_conditionsGroup, 0, _shown.SpecColumns.Count, UiAppearance.Background);
        ShowColumnGroup(_resultsGroup, _shown.SpecColumns.Count, _grid.Columns.Count,
            SystemInformation.HighContrast ? SystemColors.Control : UiAppearance.Selection);
        _columnGroups.Invalidate();
    }

    private void ShowColumnGroup(Label label, int start, int end, Color background)
    {
        Rectangle bounds = Rectangle.Empty;
        for (int c = start; c < Math.Min(end, _grid.Columns.Count); c++)
        {
            Rectangle cell = _grid.GetColumnDisplayRectangle(c, true);
            if (cell.Width > 0) bounds = bounds.IsEmpty ? cell : Rectangle.Union(bounds, cell);
        }
        label.Visible = !bounds.IsEmpty;
        label.Bounds = new Rectangle(bounds.X, 0, bounds.Width, _columnGroups.Height);
        label.BackColor = background;
        label.ForeColor = UiAppearance.Text;
    }

    private void DrawConditionBoundary(object? sender, PaintEventArgs e)
    {
        if (sender is not Control surface || _shown is null || _shown.SpecColumns.Count == 0 || _shown.SpecColumns.Count >= _grid.Columns.Count) return;
        Rectangle last = _grid.GetColumnDisplayRectangle(_shown.SpecColumns.Count - 1, true);
        if (last.Width <= 0) return;
        int x = last.Right - 1 - (ReferenceEquals(surface, _conditionsGroup) ? _conditionsGroup.Left : 0);
        using Pen pen = new(UiAppearance.GroupBorder, Math.Max(1, _grid.DeviceDpi / 96f));
        e.Graphics.DrawLine(pen, x, 0, x, surface.ClientSize.Height);
    }

    private void ShowWarning(CombinationTableDisplay table)
    {
        _warning.Visible = table.Large;
        _warning.Text = table.Truncated
            ? UiText.Format("Combination_LargeTruncated", table.TotalCount.ToString("N0", UiText.Culture), table.Rows.Count.ToString("N0", UiText.Culture))
            : UiText.Format("Combination_Large", table.TotalCount.ToString("N0", UiText.Culture));
    }

    private CombinationDisplayRow? RowAt(int index)
    {
        if (_shown is null || index < 0 || index >= _shown.Rows.Count) return null;
        if (_rowCache is { } cached && cached.Index == index) return cached.Row;
        CombinationDisplayRow row = _shown.Rows[index];
        _rowCache = (index, row);
        return row;
    }

    private void OnCellValueNeeded(object? sender, DataGridViewCellValueEventArgs e)
    {
        if (_shown is null || RowAt(e.RowIndex) is not { } row) return;
        int spec = _shown.SpecColumns.Count;
        e.Value = e.ColumnIndex < spec
            ? row.Values.GetValueOrDefault(_shown.SpecColumns[e.ColumnIndex].Key, "")
            : e.ColumnIndex - spec < row.Outcomes.Count ? OutcomeText(row.Outcomes[e.ColumnIndex - spec]) : "";
    }

    // 色は行を保持せず、描くときに決める（無効・未割当と、選んだ行の仕様の列）
    private void OnCellFormatting(object? sender, DataGridViewCellFormattingEventArgs e)
    {
        if (_shown is null || e.RowIndex < 0 || e.CellStyle is null) return;
        int spec = _shown.SpecColumns.Count;
        if (e.ColumnIndex < spec)
        {
            if (e.RowIndex == _shown.SelectedIndex)
            {
                e.CellStyle.BackColor = SelectedRowBack;
                if (SystemInformation.HighContrast) e.CellStyle.ForeColor = SystemColors.HighlightText;
            }
            return;
        }
        if (RowAt(e.RowIndex) is not { } row || e.ColumnIndex - spec >= row.Outcomes.Count) return;
        OutcomeDisplayKind kind = row.Outcomes[e.ColumnIndex - spec].Kind;
        if (kind == OutcomeDisplayKind.Assigned) return;
        e.CellStyle.BackColor = kind == OutcomeDisplayKind.Unassigned ? UnassignedBack : DisabledBack;
        e.CellStyle.ForeColor = SystemColors.ControlDarkDark;
    }

    private void ShowSelection(CombinationTableDisplay table, bool rebuilt)
    {
        int categoryColumn = table.SelectedCategory is { } category
            ? table.SpecColumns.Count + table.CategoryColumns.ToList().FindIndex(c => c.Key == category)
            : -1;
        if (categoryColumn < table.SpecColumns.Count) categoryColumn = -1;

        for (int c = 0; c < _grid.Columns.Count; c++)
        {
            DataGridViewColumnHeaderCell header = _grid.Columns[c].HeaderCell;
            bool selected = c == categoryColumn;
            header.Style.BackColor = selected ? SelectedHeaderBack : c < table.SpecColumns.Count ? UiAppearance.Background
                : SystemInformation.HighContrast ? SystemColors.Control : UiAppearance.Selection;
            header.Style.ForeColor = selected && SystemInformation.HighContrast ? SystemColors.HighlightText : Color.Empty;
            header.Style.Font = selected ? _bold ??= new Font(_grid.Font, FontStyle.Bold) : null;
        }

        if (table.SelectedIndex is int row && row < _grid.RowCount)
        {
            int column = !rebuilt && categoryColumn >= 0 && _grid.CurrentCell?.RowIndex == row && _grid.CurrentCell.ColumnIndex < table.SpecColumns.Count
                ? _grid.CurrentCell.ColumnIndex
                : categoryColumn >= 0 ? categoryColumn : 0;
            if (_grid.CurrentCell?.RowIndex != row || _grid.CurrentCell.ColumnIndex != column)
                _grid.CurrentCell = _grid[column, row];
        }
        else
        {
            _grid.CurrentCell = null;
            _grid.ClearSelection();
        }
        // 選んだ行の色は描くときに決めるので、描き直させる
        _grid.Invalidate();
        UpdateColumnGroups();
    }

    private void ShowCount(CombinationTableDisplay table) =>
        _count.Text = UiText.Format("Combination_Count", table.Rows.Count.ToString("N0", UiText.Culture), table.TotalCount.ToString("N0", UiText.Culture));

    private static bool SameContent(CombinationTableDisplay a, CombinationTableDisplay b) =>
        a.Version == b.Version &&
        ReferenceEquals(a.Rows, b.Rows) &&
        a.SpecColumns.SequenceEqual(b.SpecColumns) &&
        a.CategoryColumns.SequenceEqual(b.CategoryColumns);

    private static string OutcomeText(OutcomeDisplay outcome) => outcome.Kind switch
    {
        OutcomeDisplayKind.Assigned => outcome.Preset ?? "",
        OutcomeDisplayKind.Disabled => $"🚫 {UiText.Get("Outcome_Disabled")}",
        OutcomeDisplayKind.Unassigned => $"⚠ {UiText.Get("Outcome_Unassigned")}",
        _ => UiText.Get("Outcome_Unavailable"),
    };

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing) _bold?.Dispose();
    }

    private void OnFilterSelected(object? sender, EventArgs e)
    {
        if (_updating || sender is not ComboBox { Tag: string key } combo) return;
        FilterDisplay? filter = _shownFilters.FirstOrDefault(f => f.Key == key);
        if (filter is null) return;
        string? value = combo.SelectedIndex <= 0 ? null : filter.Values[combo.SelectedIndex - 1];
        FilterChanged?.Invoke(this, new FilterChangedEventArgs(key, value));
    }
}

