using Mconfig.Application.Ports;
using Mconfig.Application.Workspaces.Editing;
using Mconfig.Domain.Configuration;

namespace Mconfig.Application.Assignments.Commands;

/// <summary>どの割り当て行にも一致しない（未割当の）組み合わせを、Path の CSV ファイルに書き出す。</summary>
public sealed record ExportUncoveredCombinationsCommand(string Path);

/// <summary>書き出した件数（カテゴリと組み合わせの組）。</summary>
public sealed record UncoveredExportResult(long Rows);

/// <summary>
/// 未割当の組み合わせを、1 件ずつ評価しながら CSV に書き出す（全件を保持しない）。列はカテゴリと仕様のキー（仕様の順）。
/// 写しを取ってからロックの外で処理する。書込は <see cref="ICsvFileWriter"/> に任せ、最後まで書けたときだけ出力先を置き換える。
/// 取消されたら OperationCanceledException を送出し、出力先の既存ファイルは変わらない。E/W/C は変更しない。
/// </summary>
public sealed class ExportUncoveredCombinationsHandler(EditSession session, ICsvFileWriter files)
{
    public UncoveredExportResult Handle(
        ExportUncoveredCombinationsCommand command, CancellationToken cancellationToken = default,
        IProgress<CoverageProgress>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.Path);
        SessionSnapshot snapshot = session.Capture();
        return files.Write(command.Path, writer => Write(snapshot, writer, cancellationToken, progress));
    }

    private static UncoveredExportResult Write(
        SessionSnapshot snapshot, TextWriter writer, CancellationToken cancellationToken, IProgress<CoverageProgress>? progress)
    {
        CombinationSpace space = CombinationSpace.Of(snapshot.Content.Spec);
        (string Name, CategoryData Category)[] categories = snapshot.Content.Settings.Categories
            .Where(c => !c.Value.IsDiagnosticOnly)
            .Select(c => (c.Key, c.Value))
            .ToArray();
        IReadOnlyList<string> keys = space.Keys;

        writer.WriteLine(string.Join(",", new[] { "category" }.Concat(keys).Select(Csv)));
        long rows = 0, checkedCount = 0;
        System.Numerics.BigInteger totalCount = space.Count * categories.Length;
        long total = totalCount > long.MaxValue ? long.MaxValue : (long)totalCount;
        progress?.Report(new CoverageProgress(0, total));
        foreach ((string name, CategoryData category) in categories)
        {
            foreach (IReadOnlyDictionary<string, string> combination in space.Enumerate(cancellationToken))
            {
                if (++checkedCount % CoverageProgress.ReportInterval == 0) progress?.Report(new CoverageProgress(checkedCount, total));
                if (AssignmentEvaluation.FindFirstMatchingRow(category.Assignments, combination) >= 0) continue;
                writer.WriteLine(string.Join(",", new[] { name }.Concat(keys.Select(k => combination[k])).Select(Csv)));
                rows++;
            }
        }
        progress?.Report(new CoverageProgress(total, total));
        return new UncoveredExportResult(rows);
    }

    // Excel で開けるよう、区切り・引用符・改行を含む値だけを引用符で囲む
    private static string Csv(string value) =>
        value.IndexOfAny([',', '"', '\r', '\n']) < 0 ? value : "\"" + value.Replace("\"", "\"\"") + "\"";
}
