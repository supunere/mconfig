using System.Collections.ObjectModel;
using System.Numerics;

namespace Mconfig.Domain.Configuration;

/// <summary>
/// 仕様値の直積（組み合わせの集合）。組み合わせを保持せず、並び順の位置（添字）から求める。
/// 並びは先の仕様ほど変化が遅い。値を持たない仕様は直積全体を空にしないよう除く（空の値集合は整合性検査の対象）。
/// 件数は仕様値の数の積で、桁あふれしないよう <see cref="BigInteger"/> で求める。
/// 仕様の値の一部に絞った部分集合（<see cref="Restrict"/>）も同じ並びの直積として扱える。
/// </summary>
public sealed class CombinationSpace
{
    /// <summary>これを超える組み合わせは、表示と検証に時間がかかることを事前に知らせる。</summary>
    public const int LargeThreshold = 50_000;

    private readonly (string Key, IReadOnlyList<string> Values)[] _axes;

    public CombinationSpace(IEnumerable<(string Key, IReadOnlyList<string> Values)> axes)
    {
        ArgumentNullException.ThrowIfNull(axes);
        _axes = axes.Where(a => a.Values.Count > 0).ToArray();
        Count = _axes.Aggregate(BigInteger.One, (product, axis) => product * axis.Values.Count);
    }

    // 一致する組み合わせがない集合
    private CombinationSpace()
    {
        _axes = [];
        Count = BigInteger.Zero;
    }

    public static CombinationSpace Of(Definitions.SpecDefinition spec) =>
        new(spec.Axes.Select(a => (a.Key, a.Values)));

    /// <summary>組み合わせの数（仕様値の数の積）。</summary>
    public BigInteger Count { get; }

    /// <summary><see cref="LargeThreshold"/> を超えるか。</summary>
    public bool IsLarge => Count > LargeThreshold;

    /// <summary>添字で扱える数（long に収まる）か。</summary>
    public bool IsAddressable => Count <= long.MaxValue;

    public IReadOnlyList<string> Keys => _axes.Select(a => a.Key).ToArray();

    /// <summary>位置 index の組み合わせ。</summary>
    public IReadOnlyDictionary<string, string> At(long index)
    {
        if (index < 0 || index >= Count) throw new ArgumentOutOfRangeException(nameof(index));
        Dictionary<string, string> values = new(_axes.Length, StringComparer.Ordinal);
        for (int i = _axes.Length - 1; i >= 0; i--)
        {
            int radix = _axes[i].Values.Count;
            values[_axes[i].Key] = _axes[i].Values[(int)(index % radix)];
            index /= radix;
        }
        return new ReadOnlyDictionary<string, string>(values);
    }

    /// <summary>組み合わせの位置。この集合に含まれなければ null。</summary>
    public long? IndexOf(IReadOnlyDictionary<string, string> combination)
    {
        ArgumentNullException.ThrowIfNull(combination);
        if (Count.IsZero || !IsAddressable || combination.Count != _axes.Length) return null;
        long index = 0;
        foreach ((string key, IReadOnlyList<string> values) in _axes)
        {
            if (!combination.TryGetValue(key, out string? value)) return null;
            int position = IndexOf(values, value);
            if (position < 0) return null;
            index = index * values.Count + position;
        }
        return index;
    }

    /// <summary>
    /// 一部の仕様を指定した値だけに絞った部分集合（組み合わせの一覧の絞り込みや、割り当て行の条件に一致する組み合わせ）。
    /// 値が any（<see cref="AssignmentEvaluation.AnyValue"/>）の指定は絞らない。この集合にない仕様や値を指定すると、any でも空になる
    /// （割り当て評価の <see cref="AssignmentEvaluation.Matches"/> と同じ）。
    /// </summary>
    public CombinationSpace Restrict(IReadOnlyDictionary<string, string> fixedValues)
    {
        ArgumentNullException.ThrowIfNull(fixedValues);
        List<(string Key, IReadOnlyList<string> Values)> axes = [];
        foreach ((string key, IReadOnlyList<string> values) in _axes)
        {
            if (!fixedValues.TryGetValue(key, out string? value) || value == AssignmentEvaluation.AnyValue)
                axes.Add((key, values));
            else if (IndexOf(values, value) >= 0)
                axes.Add((key, [value]));
            else
                return Empty;
        }
        // この集合にない仕様を指定したら、any でも一致する組み合わせはない
        return fixedValues.Keys.Any(key => _axes.All(a => a.Key != key)) ? Empty : new CombinationSpace(axes);
    }

    /// <summary>組み合わせを 1 件ずつ求める（全件を保持しない）。取消されたら OperationCanceledException。</summary>
    public IEnumerable<IReadOnlyDictionary<string, string>> Enumerate(CancellationToken cancellationToken = default)
    {
        if (Count.IsZero) yield break;
        int[] digits = new int[_axes.Length];
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Dictionary<string, string> values = new(_axes.Length, StringComparer.Ordinal);
            for (int i = 0; i < _axes.Length; i++) values[_axes[i].Key] = _axes[i].Values[digits[i]];
            yield return new ReadOnlyDictionary<string, string>(values);

            int position = _axes.Length - 1;
            while (position >= 0 && ++digits[position] == _axes[position].Values.Count)
            {
                digits[position] = 0;
                position--;
            }
            if (position < 0) yield break;
        }
    }

    /// <summary>
    /// 上位の条件に覆われる部分木を除き、残る組み合わせだけを並び順で列挙する。
    /// 複数行の合成による被覆も除く。結果がない部分木は同じ条件集合で再探索しない。
    /// </summary>
    public IEnumerable<IReadOnlyDictionary<string, string>> EnumerateExcept(
        IEnumerable<IReadOnlyDictionary<string, string>> conditions, CancellationToken cancellationToken = default)
    {
        if (Count.IsZero) yield break;
        IReadOnlyDictionary<string, string>[] exclusions = conditions.Where(c => !Restrict(c).Count.IsZero).ToArray();
        HashSet<string> empty = new(StringComparer.Ordinal);
        Dictionary<string, string> values = new(StringComparer.Ordinal);
        foreach (var combination in Walk(0, Enumerable.Range(0, exclusions.Length).ToArray())) yield return combination;

        IEnumerable<IReadOnlyDictionary<string, string>> Walk(int depth, int[] active)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string state = $"{depth}:{string.Join(',', active)}";
            if (empty.Contains(state)) yield break;
            // 一つの上位行が残りの直積をすべて覆うなら、末端まで降りない。
            if (active.Any(i => _axes.Skip(depth).All(a =>
                !exclusions[i].TryGetValue(a.Key, out string? v) || v == AssignmentEvaluation.AnyValue ||
                (a.Values.Count == 1 && a.Values[0] == v)))) yield break;
            if (depth == _axes.Length)
            {
                yield return new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(values, StringComparer.Ordinal));
                yield break;
            }
            bool found = false;
            var axis = _axes[depth];
            foreach (string value in axis.Values)
            {
                values[axis.Key] = value;
                int[] next = active.Where(i => !exclusions[i].TryGetValue(axis.Key, out string? v) ||
                    v == AssignmentEvaluation.AnyValue || v == value).ToArray();
                foreach (var combination in Walk(depth + 1, next))
                {
                    found = true;
                    yield return combination;
                }
            }
            if (!found) empty.Add(state);
        }
    }

    private static CombinationSpace Empty { get; } = new();

    private static int IndexOf(IReadOnlyList<string> values, string value)
    {
        for (int i = 0; i < values.Count; i++) if (values[i] == value) return i;
        return -1;
    }
}
