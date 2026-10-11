using Mconfig.Domain.Configuration;

namespace Mconfig.Presentation.Common;

/// <summary>セルの値の表示文字列。数値は保存されている表記のまま見せる（例: 60.0 を 60 にしない）。</summary>
public static class CellText
{
    public static string Format(CellValue value) => value.Kind switch
    {
        CellValueKind.Text => value.Text!,
        CellValueKind.Number => value.Text!,
        CellValueKind.Boolean => value.Flag.ToString(),
        // 編集できない値は保存表現のまま見せる
        CellValueKind.Unsupported => value.Text!,
        _ => "",
    };
}
