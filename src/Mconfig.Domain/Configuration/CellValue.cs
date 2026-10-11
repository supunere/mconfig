namespace Mconfig.Domain.Configuration;

public enum CellValueKind { Missing, Null, Text, Number, Boolean, Unsupported }

/// <summary>
/// セルの値。欠落・null・文字列・数値・真偽値を区別する。
/// 数値は精度や表記を変えないよう、保存表現の数値表記を文字列のまま持つ。
/// 理解できない構造（オブジェクト・配列など）は通常値へ変換せず Unsupported とする。既定値は Missing。
/// </summary>
public readonly record struct CellValue
{
    private CellValue(CellValueKind kind, string? text, bool flag)
    {
        Kind = kind;
        Text = text;
        Flag = flag;
    }

    public CellValueKind Kind { get; }

    /// <summary>Text では文字列、Number では数値表記、Unsupported では保存表現のままの値。それ以外は null。</summary>
    public string? Text { get; }

    /// <summary>Boolean のときの値。</summary>
    public bool Flag { get; }

    public static CellValue Missing { get; } = new(CellValueKind.Missing, null, false);

    public static CellValue Null { get; } = new(CellValueKind.Null, null, false);


    public static CellValue FromText(string text) =>
        new(CellValueKind.Text, text ?? throw new ArgumentNullException(nameof(text)), false);

    public static CellValue FromNumberLiteral(string literal) =>
        string.IsNullOrWhiteSpace(literal)
            ? throw new ArgumentException("数値表記が空です。", nameof(literal))
            : new(CellValueKind.Number, literal, false);

    public static CellValue FromBoolean(bool value) => new(CellValueKind.Boolean, null, value);

    /// <summary>
    /// 理解できない値。保存時に元どおり書き戻せるよう、保存表現を不透明な文字列として持つ。
    /// Domain はこの文字列を解釈しない。
    /// </summary>
    public static CellValue FromUnsupported(string representation) =>
        string.IsNullOrEmpty(representation)
            ? throw new ArgumentException("保存表現が空です。", nameof(representation))
            : new(CellValueKind.Unsupported, representation, false);
}
