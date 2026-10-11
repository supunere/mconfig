using System.Text;

namespace Mconfig.Presentation.Common;

/// <summary>
/// Excel 互換のタブ・改行区切りの表。Excel はタブ・改行・引用符を含むセルを引用符で囲むので、それも解釈する。
/// 末尾の改行（Excel のコピーが付ける）は行として数えない。
/// </summary>
public static class TabularText
{
    public static IReadOnlyList<IReadOnlyList<string>> Parse(string? text)
    {
        List<IReadOnlyList<string>> rows = [];
        if (string.IsNullOrEmpty(text)) return rows;

        List<string> row = [];
        StringBuilder cell = new();
        int i = 0;
        bool cellStart = true;
        while (i < text.Length)
        {
            char c = text[i];
            if (cellStart && c == '"')
            {
                // 引用符で囲まれたセル。"" は " を表す
                i++;
                while (i < text.Length)
                {
                    if (text[i] == '"')
                    {
                        if (i + 1 < text.Length && text[i + 1] == '"')
                        {
                            cell.Append('"');
                            i += 2;
                            continue;
                        }
                        i++;
                        break;
                    }
                    cell.Append(text[i++]);
                }
                cellStart = false;
                continue;
            }

            cellStart = false;
            if (c == '\t')
            {
                row.Add(cell.ToString());
                cell.Clear();
                cellStart = true;
                i++;
            }
            else if (c is '\r' or '\n')
            {
                row.Add(cell.ToString());
                cell.Clear();
                rows.Add(row);
                row = [];
                cellStart = true;
                i += c == '\r' && i + 1 < text.Length && text[i + 1] == '\n' ? 2 : 1;
            }
            else
            {
                cell.Append(c);
                i++;
            }
        }

        // 最後の行が改行で終わっていなければ、その行も加える
        if (!cellStart || row.Count > 0) 
        {
            row.Add(cell.ToString());
            rows.Add(row);
        }
        return rows;
    }

    /// <summary>表をタブ・改行（CRLF）区切りにする。タブ・改行・引用符を含むセルは引用符で囲む。</summary>
    public static string Format(IEnumerable<IEnumerable<string>> rows)
    {
        StringBuilder builder = new();
        foreach (IEnumerable<string> row in rows)
        {
            builder.Append(string.Join('\t', row.Select(Quote)));
            builder.Append("\r\n");
        }
        return builder.ToString();
    }

    private static string Quote(string cell) =>
        cell.IndexOfAny(['\t', '\r', '\n', '"']) >= 0 ? "\"" + cell.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : cell;
}
