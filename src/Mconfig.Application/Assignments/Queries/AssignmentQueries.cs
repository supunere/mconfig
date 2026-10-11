using System.Collections;
using System.Numerics;
using System.Collections.ObjectModel;
using Mconfig.Application.Workspaces;
using Mconfig.Application.Workspaces.Editing;
using Mconfig.Domain.Configuration;
using Mconfig.Domain.Definitions;

namespace Mconfig.Application.Assignments.Queries;

public sealed record GetAssignmentsQuery(string Category, bool SelectFirstIfMissing = false);

/// <summary>
/// 割り当て行。ShadowedByEarlierRow は上位の単一行に覆われる簡易判定。
/// AppliedCount はこの行が最初に一致する（使われる）組み合わせの数で、全組み合わせを評価して求める。0 なら上位の行に覆われて使われない。
/// Unsaved は一時保存の時点になかった行（追加した行と、条件か割り当て先を変えた行）。
/// </summary>
public sealed record AssignmentListRow(IReadOnlyDictionary<string, string> Conditions, string? Preset, bool ShadowedByEarlierRow)
{
    /// <summary>この行が使われる組み合わせの数。組み合わせが多すぎて数えていなければ null。</summary>
    public int? AppliedCount { get; init; }
    public bool Unreachable => AppliedCount == 0;
    public bool Unsaved { get; init; }
}

/// <summary>
/// カテゴリの一覧に示す要約。Unassigned は未割当の組み合わせの数、UnreachableRows は使われない行の数
/// （組み合わせが多すぎて数えていなければ null）。
/// </summary>
public sealed record AssignmentCategorySummary(string Category, bool Available, int? Unassigned, int? UnreachableRows, bool HasUnsavedChanges);

/// <summary>
/// 組み合わせごとの割り当て先の、一時保存の時点（Before）といま（After）の比較。DecidingRow はいまの結果を決めた行（0 始まり、一致する行がなければ null）。
/// </summary>
public sealed record AssignmentChange(
    IReadOnlyDictionary<string, string> Combination, AssignmentOutcome Before, AssignmentOutcome After, int? DecidingRow)
{
    public bool Changed => Before != After;
}

/// <summary>カテゴリの割り当て結果の集計。</summary>
public sealed record AssignmentTotals(int Assigned, int Disabled, int Unassigned, int UnreachableRows);

/// <summary>
/// 割り当て一覧のリードモデル。Spec・Categories・Presets・Schemas は同じ時点の内容（不変）で、
/// 条件の選択肢・割り当て先の選択肢・表示名に使う。
/// Summaries は全カテゴリの要約、Changes は選んだカテゴリの全組み合わせの割り当て先の変化（一時保存の時点との比較）、Totals はその集計。
/// </summary>
public sealed record AssignmentListReadModel(string Category, SessionVersion Version, bool Available, IReadOnlyList<AssignmentListRow> Rows)
{
    public SpecDefinition Spec { get; init; } = SpecDefinition.Empty;
    public IReadOnlyList<string> Categories { get; init; } = [];
    public IReadOnlyList<string> Presets { get; init; } = [];
    public IReadOnlyList<SchemaDefinition> Schemas { get; init; } = [];
    public IReadOnlyList<AssignmentCategorySummary> Summaries { get; init; } = [];
    public IReadOnlyList<AssignmentChange> Changes { get; init; } = [];
    public AssignmentTotals Totals { get; init; } = new(0, 0, 0, 0);

    /// <summary>
    /// 組み合わせが <see cref="CombinationSpace.LargeThreshold"/> を超えるため、適用件数・変わる組み合わせ・集計を求めていない。
    /// 全件の確認は検証で行う。
    /// </summary>
    public bool ImpactSkipped { get; init; }

    /// <summary>組み合わせの数。</summary>
    public BigInteger CombinationCount { get; init; }
}

/// <summary>
/// 割り当て一覧（GetAssignments）。E と、一時保存 W の時点の割り当てを同じ時点で投影し、
/// 変更で割り当て先が変わる組み合わせを一時保存の前に示せるようにする。
/// </summary>
public sealed class GetAssignmentsHandler(EditSession session)
{
    private readonly object _projectionGate = new();
    private (Snapshot Snapshot, AssignmentListReadModel Model)? _impact;

    public AssignmentListReadModel Handle(GetAssignmentsQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        SessionSnapshot captured = session.Capture();
        string category = query.SelectFirstIfMissing && !captured.Content.Settings.Categories.ContainsKey(query.Category)
            ? captured.Content.Settings.Categories.Keys.FirstOrDefault() ?? query.Category : query.Category;
        Snapshot snapshot = Take(captured, category);
        // 保持はこのワークスペースの直近のカテゴリ一つだけ。値だけの編集では被覆と変更前後の評価を再利用する。
        // 仕様、全カテゴリの割り当て、選択カテゴリ、保存時の割り当てが一致する場合だけ使う。
        lock (_projectionGate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AssignmentListReadModel model;
            if (_impact is { } old && old.Model.Category == category && SameImpact(old.Snapshot, snapshot))
            {
                model = old.Model with
                {
                    Schemas = snapshot.Schemas,
                    Presets = snapshot.Presets,
                    Summaries = old.Model.Summaries.Select(s => s with
                    {
                        HasUnsavedChanges = snapshot.Categories.First(c => c.Name == s.Category).HasUnsavedChanges,
                    }).ToArray(),
                    Rows = old.Model.Rows.Select((r, i) => r with { Unsaved = snapshot.UnsavedRows.Contains(i) }).ToArray(),
                };
            }
            else model = Project(snapshot, category, cancellationToken);
            model = model with { Version = snapshot.Version };
            _impact = (snapshot, model);
            return model;
        }
    }

    private static bool SameImpact(Snapshot before, Snapshot after) =>
        ReferenceEquals(before.Spec, after.Spec) && before.Categories.Length == after.Categories.Length &&
        before.Categories.Zip(after.Categories).All(c => c.First.Name == c.Second.Name &&
            c.First.Diagnostic == c.Second.Diagnostic && SameRules(c.First.Rules, c.Second.Rules)) &&
        SameRules(before.SavedRules, after.SavedRules);

    private static bool SameRules(Assignment[] before, Assignment[] after) =>
        before.Length == after.Length && before.Zip(after).All(r => ReferenceEquals(r.First, r.Second) ||
            (r.First.Preset == r.Second.Preset && r.First.Specs.Count == r.Second.Specs.Count &&
             r.First.Specs.All(s => r.Second.Specs.TryGetValue(s.Key, out string? value) && value == s.Value)));

    // 同期区間では割り当ての写しだけを取り、組み合わせの評価はロックの外で行う
    private sealed record CategorySnapshot(string Name, bool Diagnostic, Assignment[] Rules, bool HasUnsavedChanges);

    private sealed record Snapshot(
        SessionVersion Version, SpecDefinition Spec, IReadOnlyList<SchemaDefinition> Schemas, CategorySnapshot[] Categories,
        string[] Presets, Assignment[] SavedRules, IReadOnlySet<int> UnsavedRows);

    private static AssignmentListReadModel Project(Snapshot snapshot, string categoryName, CancellationToken cancellationToken)
    {
        CombinationSpace space = CombinationSpace.Of(snapshot.Spec);
        bool skip = space.IsLarge;
        string[] categories = snapshot.Categories.Select(c => c.Name).ToArray();
        Dictionary<string, AssignmentCoverageReport> reports = new(StringComparer.Ordinal);
        AssignmentCategorySummary[] summaries = snapshot.Categories.Select(c =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (c.Diagnostic) return new AssignmentCategorySummary(c.Name, false, 0, 0, false);
            if (skip) return new AssignmentCategorySummary(c.Name, true, null, null, c.HasUnsavedChanges);
            AssignmentCoverageReport coverage = AssignmentCoverage.Analyze(c.Rules, space.Enumerate(cancellationToken), cancellationToken);
            reports[c.Name] = coverage;
            return new AssignmentCategorySummary(c.Name, true, (int)coverage.UncoveredCount, coverage.UnreachableRows.Count, c.HasUnsavedChanges);
        }).ToArray();

        CategorySnapshot? category = snapshot.Categories.FirstOrDefault(c => c.Name == categoryName);
        if (category is null || category.Diagnostic)
            return new AssignmentListReadModel(categoryName, SessionVersion.Initial, false, [])
            {
                Spec = snapshot.Spec, Categories = categories, Schemas = snapshot.Schemas, Summaries = summaries,
                ImpactSkipped = skip, CombinationCount = space.Count,
            };

        Assignment[] rules = category.Rules;
        IReadOnlySet<int> shadowed = AssignmentEvaluation.FindRowsShadowedByEarlierRow(rules, snapshot.Spec.Axes.Select(a => a.Key));
        AssignmentCoverageReport? report = reports.GetValueOrDefault(categoryName);
        AssignmentChange[] changes = skip ? [] : space.Enumerate(cancellationToken).Select(c =>
        {
            int row = AssignmentEvaluation.FindFirstMatchingRow(rules, c);
            AssignmentOutcome after = row < 0 ? AssignmentOutcome.Unassigned : AssignmentEvaluation.OutcomeOf(rules[row]);
            return new AssignmentChange(c, AssignmentEvaluation.Evaluate(snapshot.SavedRules, c), after,
                row < 0 ? null : row);
        }).ToArray();

        return new AssignmentListReadModel(categoryName, SessionVersion.Initial, true, rules
            .Select((a, i) => new AssignmentListRow(Copy(a.Specs), a.Preset, shadowed.Contains(i))
            {
                AppliedCount = report?.SelectionCounts[i],
                Unsaved = snapshot.UnsavedRows.Contains(i),
            })
            .ToArray())
        {
            Spec = snapshot.Spec,
            Categories = categories,
            Presets = snapshot.Presets,
            Schemas = snapshot.Schemas,
            Summaries = summaries,
            Changes = changes,
            Totals = new AssignmentTotals(
                changes.Count(c => c.After.Kind == AssignmentOutcomeKind.Assigned),
                changes.Count(c => c.After.Kind == AssignmentOutcomeKind.Disabled),
                changes.Count(c => c.After.Kind == AssignmentOutcomeKind.Unassigned),
                report?.UnreachableRows.Count ?? 0),
            ImpactSkipped = skip,
            CombinationCount = space.Count,
        };
    }

    private static Snapshot Take(SessionSnapshot s, string categoryName)
    {
        CategoryData? selected = s.Content.Settings.Categories.GetValueOrDefault(categoryName);
        bool usable = selected is { IsDiagnosticOnly: false };
        return new Snapshot(
            s.Status.Version, s.Content.Spec, s.Content.Schemas,
            s.Content.Settings.Categories.Select(c => new CategorySnapshot(
                c.Key, c.Value.IsDiagnosticOnly, c.Value.Assignments.ToArray(), s.Saved.CountChanges(c.Key, c.Value) > 0)).ToArray(),
            usable ? selected!.Presets.Select(p => p.Name).ToArray() : [],
            usable ? s.Saved.SavedAssignments(categoryName, selected!).ToArray() : [],
            usable ? s.Saved.UnsavedAssignmentRows(categoryName, selected!) : new HashSet<int>());
    }

    internal static IReadOnlyDictionary<string, string> Copy(IReadOnlyDictionary<string, string> values) =>
        new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(values, StringComparer.Ordinal));
}

/// <summary>全カテゴリについて、仕様値の直積の各組み合わせの割り当て結果を返す（GetCombinations）。</summary>
public sealed record GetCombinationsQuery;

public sealed record CombinationRow(IReadOnlyDictionary<string, string> Values, IReadOnlyDictionary<string, AssignmentOutcome> Outcomes);

/// <summary>
/// 組み合わせの一覧。取得した時点の割り当て（不変）の写しから、求められた位置の行だけをその場で評価する。
/// 全行を保持しないので、976 万件でも表示中の行の分しかメモリを使わない。<see cref="Restrict"/> で仕様の値に絞った一覧を作れる。
/// 件数が int に収まらないときは、int の最大値までの行だけを扱う（<see cref="Truncated"/>）。
/// </summary>
public sealed class CombinationRows : IReadOnlyList<CombinationRow>
{
    private readonly IReadOnlyList<(string Category, Assignment[] Rules)> _categories;

    internal CombinationRows(CombinationSpace space, IReadOnlyList<(string Category, Assignment[] Rules)> categories)
    {
        Space = space;
        _categories = categories;
    }

    public CombinationSpace Space { get; }

    /// <summary>組み合わせの数（桁あふれしない）。</summary>
    public BigInteger TotalCount => Space.Count;

    public int Count => Space.Count > int.MaxValue ? int.MaxValue : (int)Space.Count;

    public bool Truncated => Space.Count > int.MaxValue;

    public CombinationRow this[int index] => At(index);

    public CombinationRow At(long index)
    {
        IReadOnlyDictionary<string, string> values = Space.At(index);
        return new CombinationRow(values, new ReadOnlyDictionary<string, AssignmentOutcome>(_categories.ToDictionary(
            c => c.Category, c => AssignmentEvaluation.Evaluate(c.Rules, values), StringComparer.Ordinal)));
    }

    public long? IndexOf(IReadOnlyDictionary<string, string> values) => Space.IndexOf(values);

    /// <summary>仕様の値で絞った一覧（値が null の指定は絞らない）。同じ写しを使うので、読み直さない。</summary>
    public CombinationRows Restrict(IReadOnlyDictionary<string, string?> filters) =>
        new(Space.Restrict(filters.Where(f => f.Value is not null).ToDictionary(f => f.Key, f => f.Value!, StringComparer.Ordinal)), _categories);

    /// <summary>全行を評価せず、行ごとの条件集合から採用される最初の位置を求める。</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> RelatedPresets(CancellationToken cancellationToken = default) =>
        new ReadOnlyDictionary<string, IReadOnlyList<string>>(_categories.ToDictionary(c => c.Category, c =>
        {
            Dictionary<string, long> first = new(StringComparer.Ordinal);
            for (int i = 0; i < c.Rules.Length; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (c.Rules[i].Preset is not { } preset) continue;
                var combination = Space.Restrict(c.Rules[i].Specs)
                    .EnumerateExcept(c.Rules.Take(i).Select(r => r.Specs), cancellationToken).FirstOrDefault();
                if (combination is null || Space.IndexOf(combination) is not { } index) continue;
                if (!first.TryGetValue(preset, out long old) || index < old) first[preset] = index;
            }
            return (IReadOnlyList<string>)first.OrderBy(p => p.Value).Select(p => p.Key).ToArray();
        }, StringComparer.Ordinal));

    public IEnumerator<CombinationRow> GetEnumerator()
    {
        for (int i = 0; i < Count; i++) yield return At(i);
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>
/// 組み合わせ一覧のリードモデル。構造が壊れたカテゴリは評価できないので Outcomes に含めず、UnavailableCategories に挙げる。
/// Spec と Schemas は同じ時点の定義（不変）で、表示名の解決に使う。Rows は行をその場で求める一覧（全行を保持しない）。
/// </summary>
public sealed record CombinationListReadModel(
    SessionVersion Version,
    IReadOnlyList<string> SpecKeys,
    IReadOnlyList<string> Categories,
    IReadOnlyList<string> UnavailableCategories,
    CombinationRows Rows)
{
    public SpecDefinition Spec { get; init; } = SpecDefinition.Empty;
    public IReadOnlyList<SchemaDefinition> Schemas { get; init; } = [];
}

public sealed class GetCombinationsHandler(EditSession session)
{
    /// <summary>短い同期区間で割り当てを写し、行の評価はその写しから必要なときに行う（編集中の集約を列挙しながら変えない）。</summary>
    public CombinationListReadModel Handle(GetCombinationsQuery query)
    {
        (CombinationListReadModel model, SessionVersion version) = session.Read(content =>
        {
            (string Name, CategoryData Category)[] categories = content.Settings.Categories
                .Select(c => (c.Key, c.Value)).ToArray();
            (string, Assignment[])[] usable = categories
                .Where(c => !c.Category.IsDiagnosticOnly)
                .Select(c => (c.Name, c.Category.Assignments.ToArray()))
                .ToArray();

            return new CombinationListReadModel(SessionVersion.Initial,
                content.Spec.Axes.Select(a => a.Key).ToArray(),
                categories.Select(c => c.Name).ToArray(),
                categories.Where(c => c.Category.IsDiagnosticOnly).Select(c => c.Name).ToArray(),
                new CombinationRows(CombinationSpace.Of(content.Spec), usable))
            {
                Spec = content.Spec,
                Schemas = content.Schemas,
            };
        });
        return model with { Version = version };
    }
}
