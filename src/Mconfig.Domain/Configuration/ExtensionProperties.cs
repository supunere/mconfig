using System.Collections.ObjectModel;

namespace Mconfig.Domain.Configuration;

/// <summary>
/// 保存表現にあるが業務モデルが解釈しないプロパティ。値は保存側が解釈する
/// 不透明な JSON 文字列で、業務操作は元の要素に付けたまま引き継ぐだけで読み書きしない。
/// </summary>
public sealed class ExtensionProperties
{
    public static ExtensionProperties None { get; } = new([]);

    public ExtensionProperties(IEnumerable<KeyValuePair<string, string>> properties)
    {
        ArgumentNullException.ThrowIfNull(properties);
        KeyValuePair<string, string>[] items = properties.ToArray();
        HashSet<string> keys = new(StringComparer.Ordinal);
        foreach ((string key, string value) in items)
        {
            ArgumentNullException.ThrowIfNull(key);
            ArgumentNullException.ThrowIfNull(value);
            if (!keys.Add(key))
                throw new ArgumentException($"Duplicate extension property '{key}'", nameof(properties));
        }
        Items = new ReadOnlyCollection<KeyValuePair<string, string>>(items);
    }

    /// <summary>プロパティ名と、その値の保存表現。読み込んだ順序を保つ。</summary>
    public IReadOnlyList<KeyValuePair<string, string>> Items { get; }

    public bool IsEmpty => Items.Count == 0;
}
