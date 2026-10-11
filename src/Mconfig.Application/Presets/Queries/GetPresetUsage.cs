using Mconfig.Application.Workspaces.Editing;
using Mconfig.Domain.Configuration;
using Mconfig.Domain.Definitions;

namespace Mconfig.Application.Presets.Queries;

/// <summary>
/// カテゴリのプリセットを、どの組み合わせが使っているか（割り当ての評価結果）を返す。共有プリセットの編集前の確認に使う。
/// Selected は組み合わせの表で選んでいる組み合わせ（なければ null）。
/// </summary>
public sealed record GetPresetUsageQuery(string Category, IReadOnlyList<string> Presets, IReadOnlyDictionary<string, string>? Selected = null);

/// <summary>
/// UsedBySelected は選んでいる組み合わせもこのプリセットを使うこと、Others はそれ以外に使う組み合わせ（並び順、最大
/// <see cref="GetPresetUsageHandler.ListLimit"/> 件）、MoreOthers はそれより多いこと。
/// </summary>
public sealed record PresetUsage(string Preset, bool UsedBySelected, IReadOnlyList<IReadOnlyDictionary<string, string>> Others, bool MoreOthers)
{
    public bool UsedByOthers => Others.Count > 0;
}

/// <summary>Spec は同じ時点の仕様（不変）で、組み合わせの表示名と並びの解決に使う。</summary>
public sealed record PresetUsageReadModel(string Category, SessionVersion Version, IReadOnlyList<PresetUsage> Usages)
{
    public SpecDefinition Spec { get; init; } = SpecDefinition.Empty;
}

/// <summary>
/// プリセットを使う組み合わせを求める。割り当て行の条件から、上位の条件で覆われる部分木を除外して列挙する。
/// 未使用の行でも全組み合わせを走査しない。一覧は <see cref="ListLimit"/> 件で打ち切る。
/// </summary>
public sealed class GetPresetUsageHandler(EditSession session)
{
    public const int ListLimit = 200;

    public PresetUsageReadModel Handle(GetPresetUsageQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        // 同期区間では割り当ての写しだけを取る
        (Assignment[]? rules, SpecDefinition spec, SessionVersion version) = session.Inspect(s =>
            (s.Content.Settings.Categories.TryGetValue(query.Category, out CategoryData? category) && !category.IsDiagnosticOnly
                ? category.Assignments.ToArray()
                : null,
             s.Content.Spec,
             s.Status.Version));

        CombinationSpace space = CombinationSpace.Of(spec);
        PresetUsage[] usages = query.Presets.Select(p => rules is null
            ? new PresetUsage(p, false, [], false)
            : UsageOf(p, rules, space, query.Selected, cancellationToken)).ToArray();
        return new PresetUsageReadModel(query.Category, version, usages) { Spec = spec };
    }

    private static PresetUsage UsageOf(string preset, Assignment[] rules, CombinationSpace space, IReadOnlyDictionary<string, string>? selected,
        CancellationToken cancellationToken)
    {
        long? selectedIndex = selected is null ? null : space.IndexOf(selected);
        bool usedBySelected = selected is not null && selectedIndex is not null &&
            AssignmentEvaluation.Evaluate(rules, selected).PresetName == preset;
        List<(long Index, IReadOnlyDictionary<string, string> Combination)> others = [];
        bool more = false;
        for (int row = 0; row < rules.Length && !more; row++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (rules[row].Preset != preset) continue;
            foreach (IReadOnlyDictionary<string, string> combination in space.Restrict(rules[row].Specs)
                .EnumerateExcept(rules.Take(row).Select(r => r.Specs), cancellationToken))
            {
                long? index = space.IndexOf(combination);
                if (index is null || index == selectedIndex) continue;
                if (others.Count == ListLimit)
                {
                    more = true;
                    break;
                }
                others.Add((index.Value, combination));
            }
        }
        return new PresetUsage(preset, usedBySelected, others.OrderBy(o => o.Index).Select(o => o.Combination).ToArray(), more);
    }
}
