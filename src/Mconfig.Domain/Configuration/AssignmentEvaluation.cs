namespace Mconfig.Domain.Configuration;

/// <summary>
/// 割り当て評価。一覧・編集・検証が同じ結果を得るため、評価規則はここだけに置く。
/// </summary>
public static class AssignmentEvaluation
{
    /// <summary>条件の値として任意値に一致する予約値。仕様値としては登録しない。</summary>
    public const string AnyValue = "any";

    /// <summary>割り当て行を上から評価し、最初に一致した行だけを採用する。</summary>
    public static AssignmentOutcome Evaluate(
        IReadOnlyList<IAssignmentRule> rules, IReadOnlyDictionary<string, string> combination)
    {
        int row = FindFirstMatchingRow(rules, combination);
        if (row < 0) return AssignmentOutcome.Unassigned;
        return OutcomeOf(rules[row]);
    }

    /// <summary>採用された行の結果。行番号も必要な呼出し元は、先勝ち検索を繰り返さずに使える。</summary>
    public static AssignmentOutcome OutcomeOf(IAssignmentRule rule) =>
        rule.PresetName is { } presetName
            ? AssignmentOutcome.Assigned(presetName)
            : AssignmentOutcome.Disabled;

    /// <summary>最初に一致した行の番号。一致する行がなければ -1。</summary>
    public static int FindFirstMatchingRow(
        IReadOnlyList<IAssignmentRule> rules, IReadOnlyDictionary<string, string> combination)
    {
        for (int row = 0; row < rules.Count; row++)
        {
            if (Matches(rules[row], combination)) return row;
        }
        return -1;
    }

    /// <summary>
    /// 条件の any と省略したキーは任意値に一致する。組み合わせにないキー（未知キー）は、値が any でも一致しない
    /// （any は仕様にあるキーの任意値で、未知キーを任意扱いしない）。
    /// 未知キー・未知値そのものは整合性検査がエラーとして扱う。
    /// </summary>
    public static bool Matches(IAssignmentRule rule, IReadOnlyDictionary<string, string> combination)
    {
        foreach ((string key, string value) in rule.Conditions)
        {
            if (!combination.TryGetValue(key, out string? actual)) return false;
            if (value != AnyValue && actual != value) return false;
        }
        return true;
    }

    /// <summary>
    /// 到達不可の簡易判定。上位の単一行に完全包含される行の番号を返す。
    /// 複数の上位行の合成で到達しなくなる行は検出しない。それは全件検証の役割。
    /// specKeys は仕様のキーで、上位行の未知キーの any を任意値として扱わないために使う。
    /// </summary>
    public static IReadOnlySet<int> FindRowsShadowedByEarlierRow(IReadOnlyList<IAssignmentRule> rules, IEnumerable<string> specKeys)
    {
        ArgumentNullException.ThrowIfNull(specKeys);
        HashSet<string> keys = new(specKeys, StringComparer.Ordinal);
        HashSet<int> shadowed = new();
        for (int lower = 1; lower < rules.Count; lower++)
        {
            for (int upper = 0; upper < lower; upper++)
            {
                if (Covers(rules[upper], rules[lower], keys))
                {
                    shadowed.Add(lower);
                    break;
                }
            }
        }
        return shadowed;
    }

    // upper が lower に一致するすべての組み合わせに一致するか。
    // upper の具体値の条件ごとに、lower も同じ具体値で絞っていなければならない。
    // lower がそのキーを省略または any にしていると、upper の外の値にも一致してしまう。
    // upper の any は仕様にあるキーなら常に満たされる。未知キーの any はどの組み合わせにも一致しないので、
    // lower も同じ未知キーを持つ（lower もどれにも一致しない）場合だけ覆う。
    private static bool Covers(IAssignmentRule upper, IAssignmentRule lower, HashSet<string> specKeys)
    {
        foreach ((string key, string value) in upper.Conditions)
        {
            if (value == AnyValue)
            {
                if (!specKeys.Contains(key) && !lower.Conditions.ContainsKey(key)) return false;
                continue;
            }
            if (!lower.Conditions.TryGetValue(key, out string? lowerValue) || lowerValue != value) return false;
        }
        return true;
    }
}
