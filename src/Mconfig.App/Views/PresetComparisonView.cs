using Mconfig.App.Resources;
using Mconfig.Application.Presets.Queries;
using Mconfig.Presentation.Combinations;
using Mconfig.Presentation.Common;

namespace Mconfig.App.Views;

/// <summary>
/// 値の表（プリセット比較と値の編集）。プリセットの列順を保ち、組み合わせの割り当て先を見出しで強調する。
/// 上端にカテゴリ名と表示対象の選択を置く。管理モードでは表示対象を固定し、管理操作を置く。セルの状態は色と文言の両方で区別する:
/// 未入力（淡い赤と「（未入力）」）、使用しない（灰色と「使用しない」）、未保存の変更（太字と「*」）、選択中のプリセットと違う値（淡い黄）。
/// 初期表示は全プリセット。「表示するプリセット」から表示対象を選べる。
/// セルの確定と Ctrl+V の矩形貼り付けは Presenter へ知らせるだけで、表示は Presenter が結果に応じて描き直す。
/// セルの入力中に届いた表示は、入力を捨てないよう入力が終わるまで保留する。
/// </summary>
internal sealed class PresetComparisonView : UserControl, IPresetComparisonView
{
    private const int FixedColumns = 3;
    private static Color DiffBack => UiAppearance.Warning;
    private static Color UnusedBack => UiAppearance.Unused;
    private static Color MissingBack => UiAppearance.Missing;

    private readonly Label _caption = new() { AutoSize = true, Font = UiAppearance.SectionHeadingFont, Margin = new Padding(0, 0, 8, 2) };
    private readonly Button _hide = new() { AutoSize = true, Anchor = AnchorStyles.Right, Margin = new Padding(6, 0, 0, 0) };
    private readonly Label _legendDiff = Legend(DiffBack);
    private readonly Button _choose = new() { AutoSize = true, Margin = new Padding(16, 3, 3, 3) };
    private readonly Button _add = new();
    private readonly Button _duplicate = new();
    private readonly Button _rename = new();
    private readonly Button _delete = new();
    private readonly Label _info = new() { Dock = DockStyle.Bottom, AutoSize = true, Padding = new Padding(4) };
    private readonly FlowLayoutPanel _selectionNotice = new() { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(8, 2, 8, 6), Visible = false };
    private readonly Label _selectionOutside = new() { AutoSize = true, Margin = new Padding(3, 7, 8, 3) };
    private readonly Button _includeSelected = new() { AutoSize = true };
    private readonly DataGridView _grid = new()
    {
        Dock = DockStyle.Fill,
        AllowUserToAddRows = false,
        AllowUserToDeleteRows = false,
        AllowUserToResizeRows = false,
        SelectionMode = DataGridViewSelectionMode.CellSelect,
        RowHeadersVisible = false,
        // セル値・Styleの更新ごとに全行を測り直さない。幅調整は表示更新の後に一度だけ行う。
        AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None,
        BackgroundColor = SystemColors.Window,
        EditMode = DataGridViewEditMode.EditOnKeystrokeOrF2,
        ClipboardCopyMode = DataGridViewClipboardCopyMode.Disable,
    };

    private readonly Panel _body = new() { Dock = DockStyle.Fill };
    private readonly TableLayoutPanel _captionBar = new() { Dock = DockStyle.Top, AutoSize = true };
    private readonly bool _managementMode;
    private bool _contentHidden;
    private ComparisonDisplay? _shown;
    // 入力中に届いた最新の表示。入力が終わったら表示する
    private ComparisonDisplay? _pending;
    private readonly Font _bold;
    private bool _updating;
    private bool _cellsEdited;
    private bool _widthsDirty = true;

    public PresetComparisonView(bool managementMode = false)
    {
        _managementMode = managementMode;
        Dock = DockStyle.Fill;
        Font = UiAppearance.BodyFont;
        BackColor = SystemColors.Window;
        UiAppearance.ApplyGrid(_grid);
        // プリセット名と選択中の表示を二段にし、行の選択で見出しの高さを変えない。
        _grid.ColumnHeadersDefaultCellStyle.WrapMode = DataGridViewTriState.True;
        _grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
        _grid.ColumnHeadersHeight = LogicalToDeviceUnits(_managementMode ? 26 : 44);
        foreach (Button button in new[] { _hide, _includeSelected, _choose, _add, _duplicate, _rename, _delete }) UiAppearance.ApplyButton(button);
        // 標準のラベルでカテゴリ名を示す。右端に「値を隠す」。
        TableLayoutPanel caption = _captionBar;
        UiAppearance.ArrangeSectionHeader(caption, [UiAppearance.SectionTitle(_caption)], [_hide]);
        caption.BackColor = SystemColors.Window;
        FlowLayoutPanel bar = new() { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true, Margin = Padding.Empty };
        _choose.Margin = new Padding(0, 0, 6, 0);
        FlowLayoutPanel management = new() { Dock = DockStyle.None, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = Padding.Empty, WrapContents = true };
        foreach (Button button in new[] { _add, _duplicate, _rename, _delete })
        {
            button.AutoSize = true;
            button.Margin = new Padding(0, 0, 6, 0);
            management.Controls.Add(button);
        }
        management.Visible = _managementMode;
        _hide.Visible = _choose.Visible = _legendDiff.Visible = !_managementMode;
        bar.Controls.AddRange([_choose, management]);
        TableLayoutPanel toolbar = new() { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 2, RowCount = 1, Padding = new Padding(8, 4, 8, 4) };
        toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        toolbar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        toolbar.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        _legendDiff.Anchor = AnchorStyles.Right;
        _legendDiff.Margin = new Padding(8, 2, 0, 2);
        toolbar.Controls.Add(bar, 0, 0);
        toolbar.Controls.Add(_legendDiff, 1, 0);
        Resize += (_, _) =>
        {
            _caption.MaximumSize = new Size(Math.Max(160, ClientSize.Width - _hide.Width - 40), 0);
        };
        // 選択中プリセットの見出しの色は列ごとに指定する。
        _grid.EnableHeadersVisualStyles = false;
        _bold = new Font(_grid.Font, FontStyle.Bold);
        _body.Controls.Add(_grid);
        _body.Controls.Add(_info);
        _selectionNotice.Controls.AddRange([_selectionOutside, _includeSelected]);
        _body.Controls.Add(_selectionNotice);
        _body.Controls.Add(toolbar);
        Controls.Add(_body);
        Controls.Add(caption);

        _hide.Click += (_, _) => HideRequested?.Invoke(this, EventArgs.Empty);
        _choose.Click += (_, _) => ChoosePresets();
        _includeSelected.Click += (_, _) =>
        {
            if (_shown is { SelectedPreset: { } selected } shown)
                PresetsChosen?.Invoke(this, new PresetsChosenEventArgs(shown.Columns.Select(c => c.Name).Append(selected).Distinct(StringComparer.Ordinal).ToArray()));
        };
        _add.Click += (_, _) => RequestOperation(PresetOperation.Add);
        _duplicate.Click += (_, _) => RequestOperation(PresetOperation.Duplicate);
        _rename.Click += (_, _) => RequestOperation(PresetOperation.Rename);
        _delete.Click += (_, _) => RequestOperation(PresetOperation.Delete);
        _grid.CellMouseEnter += (_, e) =>
        {
            if (e.RowIndex < 0 || e.ColumnIndex < FixedColumns) return;
            _grid[e.ColumnIndex, e.RowIndex].ToolTipText =
                !_grid.ReadOnly && CellAt(e.RowIndex, e.ColumnIndex) is { State: ComparisonCellState.Editable }
                    ? UiText.Get("Comparison_EditHint") : "";
        };
        _grid.CellBeginEdit += OnCellBeginEdit;
        _grid.CellEndEdit += OnCellEndEdit;
        _grid.CellPainting += (_, e) =>
        {
            if (e.Graphics is not { } graphics || e.RowIndex < 0 || e.ColumnIndex < FixedColumns || CellAt(e.RowIndex, e.ColumnIndex) is not { Unsaved: true }) return;
            if (!e.Handled) e.Paint(e.ClipBounds, e.PaintParts);
            using SolidBrush brush = new(UiAppearance.Accent);
            graphics.FillRectangle(brush, e.CellBounds.Left + 1, e.CellBounds.Top + 3,
                Math.Max(2, _grid.DeviceDpi * 2 / 96), Math.Max(0, e.CellBounds.Height - 6));
            e.Handled = true;
        };
        _grid.KeyDown += OnGridKeyDown;
        _grid.CurrentCellChanged += (_, _) =>
        {
            UpdateButtons();
            if (!_updating) CellInfoChanged?.Invoke(this, EventArgs.Empty);
        };
        ApplyTexts();
    }

    public int CollapsedHeight => _captionBar.GetPreferredSize(new Size(ClientSize.Width, 0)).Height;

    public void SetContentHidden(bool hidden)
    {
        _contentHidden = hidden;
        _body.Visible = !hidden;
        _hide.Text = UiText.Get(hidden ? "Values_Show" : "Values_Hide");
    }

    /// <summary>「値を隠す（F6）」。</summary>
    public event EventHandler? HideRequested;

    /// <summary>選んでいるセルが変わったとき（ステータスバーの表示に使う）。</summary>
    public event EventHandler? CellInfoChanged;

    public event EventHandler<PresetsChosenEventArgs>? PresetsChosen;
    public event EventHandler<CellEditStartingEventArgs>? CellEditStarting;
    public event EventHandler<CellEditedEventArgs>? CellEdited;
    public event EventHandler<PasteRequestedEventArgs>? PasteRequested;
    public event EventHandler<PresetOperationRequestedEventArgs>? PresetOperationRequested;

    /// <summary>表示言語が変わったとき、固定の文言を付け直す。</summary>
    public void ApplyTexts()
    {
        _widthsDirty = true;
        _hide.Text = UiText.Get(_contentHidden ? "Values_Show" : "Values_Hide");
        _legendDiff.Text = UiText.Get("Legend_Differs");
        _selectionOutside.Text = UiText.Get("Comparison_SelectionOutside");
        _includeSelected.Text = UiText.Get("Comparison_IncludeSelected");
        _choose.Text = UiText.Get("Comparison_Choose");
        _add.Text = UiText.Get("Preset_Add");
        _duplicate.Text = UiText.Get("Preset_Duplicate");
        _rename.Text = UiText.Get("Preset_Rename");
        _delete.Text = UiText.Get("Preset_Delete");
        if ((_pending ?? _shown) is { } comparison) ShowComparison(comparison);
    }

    /// <summary>編集中のセルを確定する（タブ移動・保存の前）。</summary>
    public void CommitPendingEdit()
    {
        if (_grid.IsCurrentCellInEditMode) _grid.EndEdit();
    }

    /// <summary>選んでいるセルの場所と値（未保存なら変更前 → 変更後）。値のセルを選んでいなければ null。</summary>
    public string? CurrentCellInfo()
    {
        if (_shown is not { } shown || _grid.CurrentCell is not { } current || CellAt(current.RowIndex, current.ColumnIndex) is not { } cell)
            return null;
        ComparisonRowDisplay row = shown.Rows[current.RowIndex];
        string location = string.Join(" ＞ ", shown.Columns[current.ColumnIndex - FixedColumns].Name, row.GroupLabel, row.RowLabel, row.ColumnLabel);
        string value = CellText(cell, marked: false);
        return cell.SavedText is { } saved
            ? UiText.Format("CellInfo_Changed", location, saved.Length == 0 ? UiText.Get("Comparison_Missing") : saved, value)
            : UiText.Format("CellInfo_Value", location, value);
    }

    public void ShowComparison(ComparisonDisplay comparison)
    {
        // 入力中は描き直さない（描き直すと入力が消える）。入力の確定・取消の後に表示する
        if (_grid.IsCurrentCellInEditMode)
        {
            _pending = comparison;
            return;
        }
        _pending = null;
        // 描き直しても、選んでいたセルの位置は保つ
        (int row, int column)? current = _grid.CurrentCell is { } currentCell ? (currentCell.RowIndex, currentCell.ColumnIndex) : null;
        int firstRow = _grid.FirstDisplayedScrollingRowIndex;
        ComparisonDisplay? previous = _shown;
        bool rebuild = _shown is null || _shown.Category != comparison.Category || _shown.Available != comparison.Available ||
            _shown.Rows.Count != comparison.Rows.Count;
        bool cellsUnchanged = !_cellsEdited && !rebuild && ReferenceEquals(_shown!.Rows, comparison.Rows) &&
            _shown.Columns.Select(c => c.Name).SequenceEqual(comparison.Columns.Select(c => c.Name));
        Dictionary<string, int> widths = new(StringComparer.Ordinal);
        if (!rebuild)
            for (int i = 0; i < _shown!.Columns.Count; i++) widths[_shown.Columns[i].Name] = _grid.Columns[FixedColumns + i].Width;
        bool fitWidths = rebuild || _widthsDirty || !widths.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(comparison.Columns.Select(c => c.Name)) ||
            !_shown!.Rows.Select(r => (r.GroupLabel, r.RowLabel, r.ColumnLabel, r.Unit))
                .SequenceEqual(comparison.Rows.Select(r => (r.GroupLabel, r.RowLabel, r.ColumnLabel, r.Unit)));
        _shown = comparison;
        _cellsEdited = false;
        _updating = true;
        _grid.SuspendLayout();
        try
        {
            if (rebuild)
            {
                _grid.Rows.Clear();
                _grid.Columns.Clear();
            }
            _grid.ReadOnly = !comparison.Editable;
            _info.Visible = !comparison.Available;
            _choose.Enabled = comparison.Available;
            _caption.Text = UiText.Format(_managementMode ? "Assignment_PresetsCaption" : "Values_Caption", comparison.CategoryLabel);
            _selectionNotice.Visible = comparison.Available && comparison.SelectedPreset is { } selected &&
                comparison.AllPresets.Contains(selected) && !comparison.Columns.Any(c => c.AssignedToSelection);
            if (!comparison.Available)
            {
                _info.Text = UiText.Get("Comparison_Unavailable");
                return;
            }

            if (rebuild)
            {
                AddColumn("group", UiText.Get("Comparison_ColGroup"), frozen: true);
                AddColumn("row", UiText.Get("Comparison_ColRow"), frozen: true);
                AddColumn("item", UiText.Get("Comparison_ColItem"), frozen: true);
            }
            _grid.Columns[0].HeaderText = UiText.Get("Comparison_ColGroup");
            _grid.Columns[1].HeaderText = UiText.Get("Comparison_ColRow");
            _grid.Columns[2].HeaderText = UiText.Get("Comparison_ColItem");
            // 比較対象数が変わった場合は末尾の列だけを追加・削除し、行は保持する。
            while (_grid.Columns.Count > FixedColumns + comparison.Columns.Count) _grid.Columns.RemoveAt(_grid.Columns.Count - 1);
            // 見出しの列は入力させない
            for (int i = 0; i < comparison.Columns.Count; i++)
            {
                PresetColumnDisplay preset = comparison.Columns[i];
                string header = _managementMode ? preset.Name : preset.Name + Environment.NewLine + (preset.AssignedToSelection ? UiText.Get("Comparison_SelectedMarker") : " ");
                if (_grid.Columns.Count <= FixedColumns + i) AddColumn($"preset_{preset.Name}", header, frozen: false);
                else
                {
                    _grid.Columns[FixedColumns + i].Name = $"preset_{preset.Name}";
                    _grid.Columns[FixedColumns + i].HeaderText = header;
                }
                DataGridViewColumn column = _grid.Columns[FixedColumns + i];
                column.ToolTipText = preset.AssignedToSelection ? UiText.Format("Comparison_Selected", preset.Name) : preset.Name;
                DataGridViewCellStyle headerStyle = column.HeaderCell.Style;
                headerStyle.BackColor = headerStyle.SelectionBackColor = preset.AssignedToSelection ? UiAppearance.Selection : UiAppearance.Background;
                headerStyle.ForeColor = headerStyle.SelectionForeColor = preset.AssignedToSelection
                    ? SystemInformation.HighContrast ? SystemColors.HighlightText : UiAppearance.Accent : UiAppearance.Text;
                headerStyle.Font = preset.AssignedToSelection ? _bold : null;
                if (!fitWidths && widths.TryGetValue(preset.Name, out int width)) _grid.Columns[FixedColumns + i].Width = width;
            }

            for (int index = 0; !cellsUnchanged && index < comparison.Rows.Count; index++)
            {
                ComparisonRowDisplay row = comparison.Rows[index];
                string item = row.Unit is null ? row.ColumnLabel : $"{row.ColumnLabel} [{row.Unit}]";
                object[] values = new object[] { row.GroupLabel, row.RowLabel, item }
                    .Concat(row.Cells.Select(c => (object)CellText(c, marked: true)))
                    .ToArray();
                if (rebuild) _grid.Rows.Add(values);
                else
                    for (int i = 0; i < values.Length; i++)
                        if (!Equals(_grid.Rows[index].Cells[i].Value, values[i])) _grid.Rows[index].Cells[i].Value = values[i];
                for (int i = 0; i < row.Cells.Count; i++)
                {
                    ComparisonCellDisplay cell = row.Cells[i];
                    if (!rebuild && i < previous!.Rows[index].Cells.Count && SameAppearance(previous.Rows[index].Cells[i], cell)) continue;
                    DataGridViewCell gridCell = _grid.Rows[index].Cells[FixedColumns + i];
                    DataGridViewCellStyle style = gridCell.Style;
                    style.BackColor = Color.Empty;
                    style.ForeColor = Color.Empty;
                    style.Font = null;
                    gridCell.ReadOnly = cell.State != ComparisonCellState.Editable;
                    if (cell.State == ComparisonCellState.Unused) style.BackColor = UnusedBack;
                    else if (cell.Missing) style.BackColor = MissingBack;
                    else if (cell.DiffersFromSelectedPreset) style.BackColor = DiffBack;
                    if (cell.Missing || cell.State != ComparisonCellState.Editable) style.ForeColor = SystemColors.GrayText;
                    if (cell.Unsaved) style.Font = _bold;
                    if (cell.State != ComparisonCellState.Editable) gridCell.ReadOnly = true;
                }
            }

            if (current is { } position && position.row < _grid.Rows.Count && position.column < _grid.Columns.Count)
            {
                _grid.CurrentCell = _grid.Rows[position.row].Cells[position.column];
                // 分割ペインを縮めて行が見えない場合はスクロール位置を設定できない。
                if (firstRow >= 0 && firstRow < _grid.Rows.Count && _grid.DisplayedRowCount(true) > 0)
                    _grid.FirstDisplayedScrollingRowIndex = firstRow;
            }

            if (fitWidths)
            {
                // 初回・比較対象・表示名の変更時だけ、見えている範囲からまとめて測る。
                // 常時自動調整には戻さず、選択変更・単セル編集では利用者が調整した幅を維持する。
                _grid.AutoResizeColumns(DataGridViewAutoSizeColumnsMode.DisplayedCells);
                // ペインが閉じている初回でも、項目名の列は実データから測る。
                // これらはスキーマの行だけで、組み合わせの大量件数は対象にしない。
                for (int i = 0; i < FixedColumns; i++)
                {
                    IEnumerable<string> texts = i switch
                    {
                        0 => comparison.Rows.Select(r => r.GroupLabel),
                        1 => comparison.Rows.Select(r => r.RowLabel),
                        _ => comparison.Rows.Select(r => r.Unit is null ? r.ColumnLabel : $"{r.ColumnLabel} [{r.Unit}]"),
                    };
                    int textWidth = texts.Append(_grid.Columns[i].HeaderText)
                        .Max(text => TextRenderer.MeasureText(text, _grid.Font).Width);
                    _grid.Columns[i].Width = Math.Max(_grid.Columns[i].Width,
                        textWidth + _grid.DefaultCellStyle.Padding.Horizontal + LogicalToDeviceUnits(8));
                }
                // 後からどの列が選択されても、太字の名前と「選択中」が二段に収まる幅を確保する。
                for (int i = 0; i < comparison.Columns.Count; i++)
                {
                    DataGridViewColumn column = _grid.Columns[FixedColumns + i];
                    int nameWidth = TextRenderer.MeasureText(comparison.Columns[i].Name, _bold).Width;
                    int markerWidth = TextRenderer.MeasureText(UiText.Get("Comparison_SelectedMarker"), _bold).Width;
                    column.Width = Math.Max(column.Width, Math.Max(nameWidth, markerWidth) + _grid.ColumnHeadersDefaultCellStyle.Padding.Horizontal + LogicalToDeviceUnits(28));
                }
                _widthsDirty = false;
            }

            _info.Text = "";
        }
        finally
        {
            _grid.ResumeLayout();
            _updating = false;
            UpdateButtons();
            CellInfoChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private static bool SameAppearance(ComparisonCellDisplay before, ComparisonCellDisplay after) =>
        before.State == after.State && before.Missing == after.Missing &&
        before.DiffersFromSelectedPreset == after.DiffersFromSelectedPreset && before.Unsaved == after.Unsaved;

    public string? AskPresetName(PresetNamePrompt prompt)
    {
        string message = prompt.Operation switch
        {
            PresetOperation.Duplicate => UiText.Format("Preset_DuplicateMessage", prompt.Source ?? ""),
            PresetOperation.Rename => prompt.ReferenceCount > 0
                ? UiText.Format("Preset_RenameMessageWithReferences", prompt.Source ?? "", prompt.ReferenceCount)
                : UiText.Format("Preset_RenameMessage", prompt.Source ?? ""),
            _ => UiText.Get("Preset_AddMessage"),
        };
        return Dialogs.AskText(this, UiText.Get("Preset_" + prompt.Operation), message, prompt.Initial);
    }

    public bool ConfirmDeletePreset(string preset) =>
        MessageBox.Show(this, UiText.Format("Preset_DeleteConfirm", preset), UiText.Get("Preset_Delete"),
            MessageBoxButtons.OKCancel, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) == DialogResult.OK;

    /// <summary>
    /// 共有プリセットを編集する前に使用先の組み合わせを確認する。既定のボタンはキャンセル。
    /// </summary>
    public SharedEditAnswer ConfirmSharedEdit(SharedEditPrompt prompt)
    {
        string presets = string.Join(UiText.Get("SharedEdit_Separator"), prompt.Presets);
        using Form dialog = Dialogs.Create(UiText.Get("SharedEdit_Title"));
        TableLayoutPanel layout = Dialogs.Layout(4);
        Label heading = new()
        {
            Text = UiText.Format("SharedEdit_Heading", presets),
            AutoSize = true, MaximumSize = new Size(560, 0), ForeColor = Color.FromArgb(0, 51, 153),
            Font = new Font(Font.FontFamily, Font.Size * 1.15f), Margin = new Padding(0, 0, 0, 4),
        };
        bool multiple = prompt.Presets.Count > 1;
        ListView list = new()
        {
            View = View.Details, FullRowSelect = true, HeaderStyle = ColumnHeaderStyle.Nonclickable, MultiSelect = false,
            Size = new Size(560, 150), Margin = new Padding(0, 0, 0, 8),
        };
        UiAppearance.ApplyList(list);
        if (multiple) list.Columns.Add(UiText.Get("SharedEdit_ColPreset"), 110);
        foreach (string label in prompt.SpecLabels) list.Columns.Add(label, 90);
        foreach (SharedUsageRow row in prompt.Rows.OrderByDescending(r => r.Selected))
        {
            IEnumerable<string> cells = (multiple ? new[] { row.Preset } : []).Concat(row.Values);
            ListViewItem item = new(cells.ToArray());
            if (!row.Selected) item.BackColor = Color.FromArgb(255, 244, 206);
            list.Items.Add(item);
        }
        if (prompt.Truncated)
            list.Items.Add(new ListViewItem(UiText.Get("SharedEdit_Truncated")) { ForeColor = SystemColors.GrayText });
        Label question = new() { Text = UiText.Format("SharedEdit_Question", presets), AutoSize = true, MaximumSize = new Size(560, 0), Margin = new Padding(0, 0, 0, 10) };
        Button cancel = new() { Text = UiText.Get("Button_Cancel"), AutoSize = true, DialogResult = DialogResult.Cancel };
        Button proceed = new() { Text = UiText.Get("SharedEdit_Proceed"), AutoSize = true, DialogResult = DialogResult.Yes };
        layout.Controls.Add(heading, 0, 0);
        layout.Controls.Add(list, 0, 1);
        layout.Controls.Add(question, 0, 2);
        layout.Controls.Add(Dialogs.Buttons(cancel, proceed), 0, 3);
        dialog.Controls.Add(layout);
        // 既定はキャンセル（Enter でも変更しない）
        dialog.AcceptButton = cancel;
        dialog.CancelButton = cancel;
        dialog.Shown += (_, _) => cancel.Focus();
        bool proceeded = dialog.ShowDialog(this) == DialogResult.Yes;
        heading.Font.Dispose();
        return new SharedEditAnswer(proceeded);
    }

    public void ShowEditFeedback(EditFeedback feedback) =>
        MessageBox.Show(this, Feedback.Edit(feedback), UiText.Get("App_Title"), MessageBoxButtons.OK, MessageBoxIcon.Warning);

    public void ShowPasteOutOfRange(PasteOutOfRange range) =>
        MessageBox.Show(this, UiText.Format("Paste_OutOfRange", range.PasteRows, range.PasteColumns, range.AvailableRows, range.AvailableColumns),
            UiText.Get("App_Title"), MessageBoxButtons.OK, MessageBoxIcon.Warning);

    private void AddColumn(string name, string header, bool frozen) =>
        _grid.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = name,
            HeaderText = header,
            Frozen = frozen,
            ReadOnly = frozen,
            SortMode = DataGridViewColumnSortMode.NotSortable,
        });

    /// <summary>セルの表示。marked なら未保存の変更に「*」を付ける（色だけに頼らない）。</summary>
    private static string CellText(ComparisonCellDisplay cell, bool marked)
    {
        string text = cell.State switch
        {
            ComparisonCellState.Unused => UiText.Get("Comparison_Unused"),
            ComparisonCellState.Uneditable => UiText.Get("Comparison_Uneditable"),
            _ => cell.Missing ? UiText.Get("Comparison_Missing") : cell.Text,
        };
        return marked && cell.Unsaved ? text + " *" : text;
    }

    private static Label Legend(Color back) => new()
    {
        AutoSize = true,
        BackColor = back,
        BorderStyle = BorderStyle.None,
        Padding = new Padding(4, 1, 4, 1),
        TabStop = false,
        Margin = new Padding(6, 7, 3, 3),
    };

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing) _bold.Dispose();
    }

    private ComparisonCellDisplay? CellAt(int row, int column) =>
        _shown is { } shown && row >= 0 && row < shown.Rows.Count && column >= FixedColumns && column - FixedColumns < shown.Columns.Count
            ? shown.Rows[row].Cells[column - FixedColumns]
            : null;

    // 編集を始めるときは、表示用の文言（未入力など）ではなく値そのものを編集させる
    private void OnCellBeginEdit(object? sender, DataGridViewCellCancelEventArgs e)
    {
        if (CellAt(e.RowIndex, e.ColumnIndex) is not { State: ComparisonCellState.Editable } cell || _shown is null)
        {
            e.Cancel = true;
            return;
        }
        // 共有プリセットなら、入力を始める前に確認する（やめたら入力を始めない）
        CellEditStartingEventArgs starting = new(e.RowIndex, _shown.Columns[e.ColumnIndex - FixedColumns].Name);
        CellEditStarting?.Invoke(this, starting);
        if (starting.Cancel)
        {
            e.Cancel = true;
            return;
        }
        _grid.Rows[e.RowIndex].Cells[e.ColumnIndex].Value = cell.Text;
        _cellsEdited = true;
    }

    private void OnCellEndEdit(object? sender, DataGridViewCellEventArgs e)
    {
        if (_updating || _shown is not { } shown || CellAt(e.RowIndex, e.ColumnIndex) is not { } cell) return;
        string text = _grid.Rows[e.RowIndex].Cells[e.ColumnIndex].Value?.ToString() ?? "";
        string preset = shown.Columns[e.ColumnIndex - FixedColumns].Name;
        int row = e.RowIndex;
        // 表示の描き直しはグリッドのイベントの外で行う
        BeginInvoke(() =>
        {
            // 入力中に保留した表示があれば、ここで表示する。変更を知らせたときは、Presenter が結果に応じて描き直す
            ComparisonDisplay latest = _pending ?? _shown ?? shown;
            if (text == cell.Text)
            {
                ShowComparison(latest);
                return;
            }
            _pending = null;
            // 入力した表示の版とカテゴリを添える（保留していた表示より古いことがある）
            CellEdited?.Invoke(this, new CellEditedEventArgs(shown.Category, shown.Version, row, preset, text));
        });
    }

    private void OnGridKeyDown(object? sender, KeyEventArgs e)
    {
        if (!e.Control || _shown is null) return;
        if (e.KeyCode == Keys.V && !_grid.ReadOnly && _grid.CurrentCell is { } current && current.ColumnIndex >= FixedColumns)
        {
            e.Handled = true;
            string text = Clipboard.ContainsText() ? Clipboard.GetText() : "";
            if (text.Length > 0)
                PasteRequested?.Invoke(this, new PasteRequestedEventArgs(current.RowIndex, current.ColumnIndex - FixedColumns, text));
        }
        else if (e.KeyCode == Keys.C && _grid.SelectedCells.Count > 0)
        {
            e.Handled = true;
            CopySelection();
        }
    }

    /// <summary>選択範囲を Excel 互換の表としてコピーする。値そのもの（未入力・入力できないセルは空）をコピーし、貼り戻せるようにする。</summary>
    private void CopySelection()
    {
        DataGridViewCell[] cells = _grid.SelectedCells.Cast<DataGridViewCell>().ToArray();
        int top = cells.Min(c => c.RowIndex), bottom = cells.Max(c => c.RowIndex);
        int left = cells.Min(c => c.ColumnIndex), right = cells.Max(c => c.ColumnIndex);
        IEnumerable<IEnumerable<string>> rows = Enumerable.Range(top, bottom - top + 1).Select(r =>
            Enumerable.Range(left, right - left + 1).Select(c => CellAt(r, c) is { } cell
                ? cell.State == ComparisonCellState.Editable ? cell.Text : ""
                : _grid.Rows[r].Cells[c].Value?.ToString() ?? ""));
        Clipboard.SetText(TabularText.Format(rows));
    }

    private string? CurrentPreset() =>
        _shown is { } shown && _grid.CurrentCell is { ColumnIndex: >= FixedColumns } cell && cell.ColumnIndex - FixedColumns < shown.Columns.Count
            ? shown.Columns[cell.ColumnIndex - FixedColumns].Name
            : null;

    private void UpdateButtons()
    {
        bool editable = _managementMode && _shown is { Available: true, Editable: true };
        _add.Enabled = editable;
        _duplicate.Enabled = _rename.Enabled = _delete.Enabled = editable && CurrentPreset() is not null;
    }

    private void RequestOperation(PresetOperation operation)
    {
        if (!_managementMode) return;
        CommitPendingEdit();
        PresetOperationRequested?.Invoke(this, new PresetOperationRequestedEventArgs(operation, operation == PresetOperation.Add ? null : CurrentPreset()));
    }

    private void ChoosePresets()
    {
        if (_shown is null) return;
        using Form dialog = new()
        {
            Text = UiText.Get("Comparison_ChooseTitle"),
            StartPosition = FormStartPosition.CenterParent,
            AutoScaleMode = AutoScaleMode.Dpi,
            Size = new Size(360, 420),
            MinimizeBox = false,
            MaximizeBox = false,
        };
        CheckedListBox list = new() { Dock = DockStyle.Fill, CheckOnClick = true };
        HashSet<string> shown = _shown.Columns.Select(c => c.Name).ToHashSet();
        foreach (string preset in _shown.AllPresets) list.Items.Add(preset, shown.Contains(preset));
        FlowLayoutPanel buttons = new() { Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = FlowDirection.RightToLeft };
        Button cancel = new() { Text = UiText.Get("Button_Cancel"), DialogResult = DialogResult.Cancel, AutoSize = true };
        Button ok = new() { Text = UiText.Get("Button_OK"), DialogResult = DialogResult.OK, AutoSize = true };
        buttons.Controls.AddRange([cancel, ok]);
        dialog.Controls.Add(list);
        dialog.Controls.Add(buttons);
        dialog.AcceptButton = ok;
        dialog.CancelButton = cancel;
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        string[] chosen = list.CheckedItems.Cast<string>().ToArray();
        if (chosen.Length > 0) PresetsChosen?.Invoke(this, new PresetsChosenEventArgs(chosen));
    }
}

