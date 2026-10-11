using Mconfig.Domain.Configuration;
using Mconfig.Domain.Definitions;

namespace Mconfig.Application.Workspaces;

/// <summary>
/// ワークスペースの内容一式（設定・仕様・スキーマ）。保存・検証・履歴の単位。
/// 仕様とスキーマは不変、設定は業務操作を通じてだけ変わる。スキーマの一覧は複製して読み取り専用で持つので、
/// 渡した一覧や Query の結果から一覧を変えても内容は変わらない。
/// </summary>
public sealed record WorkspaceContent(SettingsData Settings, SpecDefinition Spec, IReadOnlyList<SchemaDefinition> Schemas)
{
    private readonly IReadOnlyList<SchemaDefinition> _schemas = ReadOnly(Schemas);

    public IReadOnlyList<SchemaDefinition> Schemas
    {
        get => _schemas;
        init => _schemas = ReadOnly(value);
    }

    /// <summary>
    /// 定義ファイルの相対パスと正規化した保存表現。ファイル名や、業務モデルが解釈しない未知プロパティも
    /// 履歴・復元では内容の一部なので、指紋で区別するために保持する。
    /// </summary>
    public IReadOnlyDictionary<string, string> DefinitionSources { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>
    /// 定義ファイルごとの、業務モデルが解釈しないプロパティの正規化表現。
    /// 意味差分と混在した未知内容の変更も、復元プレビューで独立して示す。
    /// </summary>
    public IReadOnlyDictionary<string, string> DefinitionUnknownProperties { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    private static IReadOnlyList<SchemaDefinition> ReadOnly(IReadOnlyList<SchemaDefinition> schemas) =>
        Array.AsReadOnly((schemas ?? throw new ArgumentNullException(nameof(schemas))).ToArray());

    /// <summary>
    /// 参照のための写し。カテゴリの一覧だけを写すので速く、編集セッションのロックの中で取り、
    /// 重い検証や評価はロックの外で写しに対して行う。
    /// </summary>
    public WorkspaceContent Snapshot() => this with
    {
        Settings = new SettingsData(Settings.Categories.ToDictionary(c => c.Key, c => c.Value.Snapshot(), StringComparer.Ordinal), Settings.Extensions),
    };
}
