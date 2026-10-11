using System.Collections.ObjectModel;

namespace Mconfig.Domain.Configuration;

/// <summary>割り当て行（不変）。条件（仕様キー → 値）に一致した組み合わせへ Preset を割り当てる。</summary>
public sealed class Assignment : IAssignmentRule
{
    private static readonly IReadOnlyDictionary<string, string> NoSpecs =
        new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(StringComparer.Ordinal));

    private readonly IReadOnlyDictionary<string, string> _specs = NoSpecs;

    /// <summary>条件。設定時に複製するので、渡した辞書を後で変えても影響しない。null は壊れたデータ（診断用）。</summary>
    public IReadOnlyDictionary<string, string> Specs
    {
        get => _specs;
        init => _specs = value is null
            ? null!
            : new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(value, StringComparer.Ordinal));
    }

    /// <summary>null = 無効（この組み合わせは存在しない）。</summary>
    public string? Preset { get; init; }

    private readonly ExtensionProperties _extensions = ExtensionProperties.None;

    /// <summary>保存表現にあるが業務モデルが解釈しないプロパティ。業務操作で引き継ぐ。</summary>
    public ExtensionProperties Extensions
    {
        get => _extensions;
        init => _extensions = value ?? ExtensionProperties.None;
    }

    IReadOnlyDictionary<string, string> IAssignmentRule.Conditions => Specs;
    string? IAssignmentRule.PresetName => Preset;
}
