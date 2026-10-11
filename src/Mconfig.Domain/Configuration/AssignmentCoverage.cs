namespace Mconfig.Domain.Configuration;

/// <summary>
/// 全件検証の結果。全組み合わせを実際に評価して得たもので、
/// 単一の上位行だけを見る簡易判定（FindRowsShadowedByEarlierRow）とは区別する。
/// </summary>
public sealed class AssignmentCoverageReport
{
    internal AssignmentCoverageReport(
        IReadOnlyList<int> selectionCounts, IReadOnlyList<IReadOnlyDictionary<string, string>> uncovered, long uncoveredCount)
    {
        SelectionCounts = selectionCounts;
        UncoveredCombinations = uncovered;
        UncoveredCount = uncoveredCount;
        UnreachableRows = selectionCounts
            .Select((count, row) => (count, row))
            .Where(item => item.count == 0)
            .Select(item => item.row)
            .ToArray();
    }

    /// <summary>行ごとに、最初に一致した行として採用された組み合わせの件数。</summary>
    public IReadOnlyList<int> SelectionCounts { get; }

    /// <summary>一度も採用されない行。複数の上位行の合成で到達しなくなる行も含む。</summary>
    public IReadOnlyList<int> UnreachableRows { get; }

    /// <summary>
    /// 一致する行がない（Unassigned）組み合わせの先頭 <see cref="AssignmentCoverage.SampleLimit"/> 件（並び順）。Disabled は含めない。
    /// 全件は保持しない。件数は <see cref="UncoveredCount"/>。
    /// </summary>
    public IReadOnlyList<IReadOnlyDictionary<string, string>> UncoveredCombinations { get; }

    /// <summary>一致する行がない組み合わせの数。</summary>
    public long UncoveredCount { get; }
}

public static class AssignmentCoverage
{
    /// <summary>未カバーの組み合わせとして保持する件数の上限（件数は別に数える）。</summary>
    public const int SampleLimit = 1_000;

    /// <summary>
    /// 全組み合わせを先勝ちで評価して集計する。取消された場合は途中の結果を返さず、
    /// OperationCanceledException を送出する（途中終了を成功として扱わない）。
    /// </summary>
    public static AssignmentCoverageReport Analyze(
        IReadOnlyList<IAssignmentRule> rules,
        IEnumerable<IReadOnlyDictionary<string, string>> combinations,
        CancellationToken cancellationToken = default)
    {
        int[] selectionCounts = new int[rules.Count];
        List<IReadOnlyDictionary<string, string>> uncovered = [];
        long uncoveredCount = 0;

        foreach (IReadOnlyDictionary<string, string> combination in combinations)
        {
            cancellationToken.ThrowIfCancellationRequested();

            int selected = AssignmentEvaluation.FindFirstMatchingRow(rules, combination);
            if (selected < 0)
            {
                if (uncovered.Count < SampleLimit) uncovered.Add(combination);
                uncoveredCount++;
            }
            else
                selectionCounts[selected]++;
        }

        return new AssignmentCoverageReport(selectionCounts, uncovered.AsReadOnly(), uncoveredCount);
    }
}
