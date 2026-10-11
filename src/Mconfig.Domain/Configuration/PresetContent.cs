namespace Mconfig.Domain.Configuration;

/// <summary>
/// プリセットの値。グループ → 行 → 列の順序を保ち、未使用のグループ、欠落と null、
/// 数値表記、理解できない値や行を区別したまま持つ。変更は新しい内容を返し、既存の内容は変えない。
/// orphan（スキーマにないグループ・行・列）も内容として保持し、自動で削除しない。
/// </summary>
public sealed class PresetContent
{
    private readonly OrderedEntries<PresetGroup> _groups;

    public PresetContent(IEnumerable<KeyValuePair<string, PresetGroup>> groups) =>
        _groups = new OrderedEntries<PresetGroup>(groups, nameof(groups));

    public static PresetContent Empty { get; } = new([]);

    public IReadOnlyList<KeyValuePair<string, PresetGroup>> Groups => _groups.Entries;

    public PresetGroup? FindGroup(string groupKey) => _groups.Find(groupKey);

    /// <summary>
    /// セルの値と、値を入力できるか。グループが未使用なら Unused、
    /// グループがない（使用中か未使用かを決められない）・行が理解できない構造・値が理解できないなら NotEditable。
    /// 行やセルがまだないだけなら入力できる（値は Missing）。
    /// </summary>
    public CellSlot CellAt(string groupKey, string rowKey, string columnKey)
    {
        PresetGroup? group = FindGroup(groupKey);
        if (group is null) return new CellSlot(CellEditability.NotEditable, CellValue.Missing);
        if (group.IsUnused) return new CellSlot(CellEditability.Unused, CellValue.Missing);
        PresetRow? row = group.FindRow(rowKey);
        if (row is { IsSupported: false }) return new CellSlot(CellEditability.NotEditable, CellValue.Missing);
        CellValue value = row?.GetCell(columnKey) ?? CellValue.Missing;
        return new CellSlot(value.Kind == CellValueKind.Unsupported ? CellEditability.NotEditable : CellEditability.Editable, value);
    }

    /// <summary>
    /// セルの値を変えた内容を返す。行がまだなければ作る。グループが存在しないか未使用、
    /// または行・セルが編集できない構造なら変更できない。
    /// </summary>
    public PresetContent WithCell(string groupKey, string rowKey, string columnKey, CellValue value)
    {
        PresetGroup group = FindGroup(groupKey)
            ?? throw new PresetOperationException(PresetOperationFailure.CellNotEditable);
        return new PresetContent(_groups.With(groupKey, group.WithCell(groupKey, rowKey, columnKey, value)));
    }
}

/// <summary>プリセット内のグループ。未使用（null）か、順序付きの行を持つ。</summary>
public sealed class PresetGroup
{
    private readonly OrderedEntries<PresetRow>? _rows;

    private PresetGroup(OrderedEntries<PresetRow>? rows) => _rows = rows;

    /// <summary>明示的に使用しないグループ。内部のセルは検証しない。</summary>
    public static PresetGroup Unused { get; } = new(null);

    public static PresetGroup WithRows(IEnumerable<KeyValuePair<string, PresetRow>> rows) =>
        new(new OrderedEntries<PresetRow>(rows, nameof(rows)));

    public bool IsUnused => _rows is null;

    public IReadOnlyList<KeyValuePair<string, PresetRow>> Rows => _rows?.Entries ?? [];

    public PresetRow? FindRow(string rowKey) => _rows?.Find(rowKey);

    internal PresetGroup WithCell(string groupKey, string rowKey, string columnKey, CellValue value)
    {
        if (_rows is null)
            throw new PresetOperationException(PresetOperationFailure.CellNotEditable);
        PresetRow row = _rows.Find(rowKey) ?? PresetRow.WithCells([]);
        return new PresetGroup(new OrderedEntries<PresetRow>(
            _rows.With(rowKey, row.WithCell(groupKey, rowKey, columnKey, value)), nameof(rowKey)));
    }
}

/// <summary>グループ内の行。列ごとのセルを持つか、理解できない構造（保存表現のまま保持）である。</summary>
public sealed class PresetRow
{
    private readonly OrderedEntries<CellValue>? _cells;

    private PresetRow(OrderedEntries<CellValue>? cells, string? unsupportedRepresentation)
    {
        _cells = cells;
        UnsupportedRepresentation = unsupportedRepresentation;
    }

    /// <summary>セルを持つ行。欠落したセルは含めない（Missing は値ではなく「ないこと」を表す）。</summary>
    public static PresetRow WithCells(IEnumerable<KeyValuePair<string, CellValue>> cells)
    {
        OrderedEntries<CellValue> entries = new(cells, nameof(cells));
        if (entries.Entries.Any(e => e.Value.Kind == CellValueKind.Missing))
            throw new ArgumentException("欠落したセルは行に含めません。", nameof(cells));
        return new PresetRow(entries, null);
    }

    /// <summary>行として理解できない値。保存表現を不透明な文字列として持つ。</summary>
    public static PresetRow Unsupported(string representation) =>
        string.IsNullOrEmpty(representation)
            ? throw new ArgumentException("保存表現が空です。", nameof(representation))
            : new PresetRow(null, representation);

    public bool IsSupported => _cells is not null;

    /// <summary>行が理解できない構造のときの保存表現。それ以外は null。</summary>
    public string? UnsupportedRepresentation { get; }

    public IReadOnlyList<KeyValuePair<string, CellValue>> Cells => _cells?.Entries ?? [];

    /// <summary>列の値。セルがなければ Missing（CellValue の既定値）。</summary>
    public CellValue GetCell(string columnKey) => _cells is null ? CellValue.Missing : _cells.Find(columnKey);

    /// <summary>
    /// セルの値を変えた行を返す。行やセルが理解できない構造なら、その構造を保持するため変更しない。
    /// どの更新経路（単一セル・一括入力）もここを通るので、同じ条件で拒否する。
    /// </summary>
    internal PresetRow WithCell(string groupKey, string rowKey, string columnKey, CellValue value)
    {
        if (_cells is null || _cells.Find(columnKey).Kind == CellValueKind.Unsupported)
            throw new PresetOperationException(PresetOperationFailure.CellNotEditable);
        if (value.Kind == CellValueKind.Missing)
            throw new ArgumentException("欠落はセルの値として設定できません。", nameof(value));
        return new PresetRow(new OrderedEntries<CellValue>(_cells.With(columnKey, value), nameof(columnKey)), null);
    }
}

/// <summary>キーの順序を保つ読み取り専用の対応表。キーは序数比較で一意。公開する一覧は型を変えても書き換えられない。</summary>
internal sealed class OrderedEntries<T>
{
    private readonly Dictionary<string, int> _index = new(StringComparer.Ordinal);

    public OrderedEntries(IEnumerable<KeyValuePair<string, T>> entries, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(entries, parameterName);
        Entries = Array.AsReadOnly(entries.ToArray());
        for (int i = 0; i < Entries.Count; i++)
        {
            if (Entries[i].Key is null || !_index.TryAdd(Entries[i].Key, i))
                throw new ArgumentException($"キー '{Entries[i].Key}' が空か重複しています。", parameterName);
            if (Entries[i].Value is null)
                throw new ArgumentException($"キー '{Entries[i].Key}' の値がありません。", parameterName);
        }
    }

    public IReadOnlyList<KeyValuePair<string, T>> Entries { get; }

    public T? Find(string key) => _index.TryGetValue(key, out int i) ? Entries[i].Value : default;

    /// <summary>キーの値を差し替えた列挙を返す。キーがなければ末尾に加える。</summary>
    public IEnumerable<KeyValuePair<string, T>> With(string key, T value)
    {
        bool replaced = false;
        foreach (KeyValuePair<string, T> entry in Entries)
        {
            if (string.Equals(entry.Key, key, StringComparison.Ordinal))
            {
                replaced = true;
                yield return new(key, value);
            }
            else
            {
                yield return entry;
            }
        }
        if (!replaced) yield return new(key, value);
    }
}
