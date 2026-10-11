namespace Mconfig.Domain.Configuration;

public enum CellEntryState
{
    /// <summary>グループが未使用（Unused）なので入力を求めない。</summary>
    NotRequired,

    /// <summary>未入力。欠落、null、空白のみの文字列。</summary>
    Blank,

    /// <summary>入力済み。0 や false も入力済みとする。</summary>
    Filled,

    /// <summary>編集できない構造。確定保存を止める対象。</summary>
    Unsupported,
}

public static class CellEntry
{
    /// <summary>使用中グループのセルが入力済みかを判定する。</summary>
    public static CellEntryState Classify(bool groupUnused, CellValue value)
    {
        if (groupUnused) return CellEntryState.NotRequired;

        return value.Kind switch
        {
            CellValueKind.Missing or CellValueKind.Null => CellEntryState.Blank,
            CellValueKind.Text => string.IsNullOrWhiteSpace(value.Text) ? CellEntryState.Blank : CellEntryState.Filled,
            CellValueKind.Number or CellValueKind.Boolean => CellEntryState.Filled,
            _ => CellEntryState.Unsupported,
        };
    }
}
