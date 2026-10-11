using Mconfig.App.Resources;
using Mconfig.Presentation.Combinations;

namespace Mconfig.App.Views;

/// <summary>
/// 組み合わせの表（参照のみ）。絞り込みを表のすぐ上の 1 行に並べ、仕様の列を左に固定してカテゴリの列だけを横にスクロールする。
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
    private readonly Label _heading = new() { Dock = DockStyle.Top, AutoSize = true, Font = UiAppearance.HeadingFont, Padding = new Padding(8, 8, 8, 4) };
    private readonly Button _clear = new() { AutoSize = true, Margin = new Padding(12, 3, 3, 3) };
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

    public CombinationListView()
    {
        Dock = DockStyle.Fill;
        Font = UiAppearance.BodyFont;
        BackColor = UiAppearance.Surface;
        UiAppearance.ApplyGrid(_grid);
        UiAppearance.ApplyButton(_clear);
        UiAppearance.ApplyButton(_showValues);
        _count.ForeColor = UiAppearance.Muted;
        FlowLayoutPanel bar = new() { Dock = DockStyle.Top, AutoSize = true, WrapContents = true, Padding = new Padding(4, 4, 4, 8) };
        Resize += (_, _) =>
        {
            _filters.MaximumSize = new Size(Math.Max(200, ClientSize.Width - 24), 0);
            _count.MaximumSize = new Size(Math.Max(200, ClientSize.Width - 40), 0);
        };
        bar.Controls.AddRange([_filters, _clear, _count, _showValues]);
        Controls.Add(_grid);
        Controls.Add(_warning);
        Controls.Add(bar);
        Controls.Add(_heading);
        _grid.CellValueNeeded += OnCellValueNeeded;
        _grid.CellFormatting += OnCellFormatting;
        _grid.SelectionChanged += (_, _) =>
        {
            if (_updating || _shown is null || _grid.CurrentCell is not { } cell) return;
            int categoryIndex = cell.ColumnIndex - _shown.SpecColumns.Count;
            string? category = categoryIndex >= 0 && categoryIndex < _shown.CategoryColumns.Count ? _shown.CategoryColumns[categoryIndex].Key : null;
            CellSelected?.Invoke(this, new CellSelectedEventArgs(cell.RowIndex, category));
        };
        _clear.Click += (_, _) => FiltersCleared?.Invoke(this, EventArgs.Empty);
        _showValues.Click += (_, _) => ShowValuesRequested?.Invoke(this, EventArgs.Empty);
        ApplyTexts();
    }

    public event EventHandler<FilterChangedEventArgs>? FilterChanged;
    public event EventHandler<CellSelectedEventArgs>? CellSelected;
    public event EventHandler? FiltersCleared;

    /// <summary>値の表を隠しているときの「値を表示」。</summary>
    public event EventHandler? ShowValuesRequested;

    public void ApplyTexts()
    {
        _heading.Text = UiText.Get("Combination_Title");
        _clear.Text = UiText.Get("Filter_Clear");
        _showValues.Text = UiText.Get("Values_Show");
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
        _updating = true;
        try
        {
            _filters.SuspendLayout();
            foreach (Control old in _filters.Controls.Cast<Control>().ToArray()) old.Dispose();
            _filters.Controls.Clear();
            foreach (FilterDisplay filter in filters)
            {
                Label label = new() { Text = filter.Label, AutoSize = true, Margin = new Padding(8, 7, 2, 0) };
                ComboBox combo = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 110, Tag = filter.Key };
                combo.Items.Add(UiText.Get("Filter_All"));
                foreach (string value in filter.Values) combo.Items.Add(value);
                combo.SelectedIndex = filter.Selected is null ? 0 : 1 + filter.Values.ToList().IndexOf(filter.Selected);
                combo.SelectedIndexChanged += OnFilterSelected;
                _filters.Controls.Add(label);
                _filters.Controls.Add(combo);
            }
            _filters.ResumeLayout();
            _shownFilters = filters;
        }
        finally
        {
            _updating = false;
        }
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
            ShowSelection(table);
            ShowCount(table);
            _clear.Enabled = table.Filtered;
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
                _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = column.Key, HeaderText = column.Label, SortMode = DataGridViewColumnSortMode.NotSortable, Frozen = true });
            foreach (ColumnDisplay column in table.CategoryColumns)
                _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = column.Key, HeaderText = column.Label, SortMode = DataGridViewColumnSortMode.NotSortable });
            // 固定した仕様の列の右端に区切りを付け、スクロールする範囲との境を分かるようにする
            if (table.SpecColumns.Count > 0) _grid.Columns[table.SpecColumns.Count - 1].DividerWidth = 2;

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

    private void ShowSelection(CombinationTableDisplay table)
    {
        int categoryColumn = table.SelectedCategory is { } category
            ? table.SpecColumns.Count + table.CategoryColumns.ToList().FindIndex(c => c.Key == category)
            : -1;
        if (categoryColumn < table.SpecColumns.Count) categoryColumn = -1;

        for (int c = 0; c < _grid.Columns.Count; c++)
        {
            DataGridViewColumnHeaderCell header = _grid.Columns[c].HeaderCell;
            bool selected = c == categoryColumn;
            header.Style.BackColor = selected ? SelectedHeaderBack : Color.Empty;
            header.Style.ForeColor = selected && SystemInformation.HighContrast ? SystemColors.HighlightText : Color.Empty;
            header.Style.Font = selected ? _bold ??= new Font(_grid.Font, FontStyle.Bold) : null;
        }

        if (table.SelectedIndex is int row && row < _grid.RowCount)
        {
            int column = categoryColumn >= 0 && _grid.CurrentCell?.RowIndex == row && _grid.CurrentCell.ColumnIndex < table.SpecColumns.Count
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
    }

    private void ShowCount(CombinationTableDisplay table) =>
        _count.Text = UiText.Format("Combination_Count", table.Rows.Count.ToString("N0", UiText.Culture), table.TotalCount.ToString("N0", UiText.Culture))
            + "　・　" + UiText.Get("Combination_Hint");

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

