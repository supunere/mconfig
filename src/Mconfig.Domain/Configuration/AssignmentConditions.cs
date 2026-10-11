using Mconfig.Domain.Definitions;

namespace Mconfig.Domain.Configuration;

/// <summary>
/// 割り当ての条件と仕様の照合。未知キー・未知値は任意扱いせず誤りとする。
/// 未知キーは値が any でも誤り（any は仕様にあるキーの任意値を表す）。
/// 割り当ての業務操作（追加・変更の拒否）と全体検証（エラーの報告）が同じ規則を使う。
/// </summary>
public static class AssignmentConditions
{
    /// <summary>条件の誤り。未知キー（UnknownSpecKey、Value は null）をすべて示してから、未知値（UnknownSpecValue）を示す。</summary>
    public static IEnumerable<(ValidationCode Code, string Key, string? Value)> Problems(
        SpecDefinition spec, IReadOnlyDictionary<string, string> conditions)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(conditions);
        Dictionary<string, SpecAxis> axes = spec.Axes.ToDictionary(a => a.Key, StringComparer.Ordinal);
        foreach (string key in conditions.Keys)
        {
            if (!axes.ContainsKey(key)) yield return (ValidationCode.UnknownSpecKey, key, null);
        }
        foreach ((string key, string value) in conditions)
        {
            if (value == AssignmentEvaluation.AnyValue || !axes.TryGetValue(key, out SpecAxis? axis)) continue;
            if (!axis.Values.Contains(value, StringComparer.Ordinal))
                yield return (ValidationCode.UnknownSpecValue, key, value);
        }
    }
}
