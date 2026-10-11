using Mconfig.Application.Workspaces;
using Mconfig.Application.Workspaces.Editing;
using Mconfig.Domain.Configuration;
using Mconfig.Domain.Definitions;

namespace Mconfig.Application.Presets.Commands;

/// <summary>スキーマの行を持つ、値が未入力のプリセットを追加する。</summary>
public sealed record AddPresetCommand(string Category, string Name, SessionVersion ExpectedVersion);

/// <summary>値を複製した別名のプリセットを追加する。</summary>
public sealed record DuplicatePresetCommand(string Category, string Source, string NewName, SessionVersion ExpectedVersion);

/// <summary>プリセットの名前を変え、同じカテゴリの割り当ての参照をまとめて追従させる。</summary>
public sealed record RenamePresetCommand(string Category, string OldName, string NewName, SessionVersion ExpectedVersion);

/// <summary>割り当てから参照されていないプリセットを削除する。</summary>
public sealed record DeletePresetCommand(string Category, string Name, SessionVersion ExpectedVersion);

/// <summary>
/// プリセットの業務操作。規則はカテゴリの業務操作が持ち、
/// Handler は対象の取得と版の確認だけを行う（名前の重複や参照追従の規則を複製しない）。
/// </summary>
public sealed class PresetCommandHandlers(EditSession session)
{
    public CommandResult Handle(AddPresetCommand command) =>
        Run(command.ExpectedVersion, command.Category, (content, category) =>
        {
            SchemaDefinition schema = content.Schemas.FirstOrDefault(s => s.Name == command.Category)
                ?? throw new PresetOperationException(PresetOperationFailure.InvalidCategory);
            category.AddPreset(Preset.CreateBlank(command.Name,
                schema.Groups.Select(g => KeyValuePair.Create(g.Key, (IEnumerable<string>)g.Rows))));
        });

    public CommandResult Handle(DuplicatePresetCommand command) =>
        Run(command.ExpectedVersion, command.Category, (_, category) => category.DuplicatePreset(command.Source, command.NewName));

    public CommandResult Handle(RenamePresetCommand command) =>
        Run(command.ExpectedVersion, command.Category, (_, category) => category.RenamePreset(command.OldName, command.NewName));

    public CommandResult Handle(DeletePresetCommand command) =>
        Run(command.ExpectedVersion, command.Category, (_, category) => category.DeletePreset(command.Name));

    private CommandResult Run(SessionVersion expectedVersion, string categoryName, Action<WorkspaceContent, CategoryData> operation) =>
        session.Apply(expectedVersion, content =>
        {
            if (!content.Settings.Categories.TryGetValue(categoryName, out CategoryData? category))
                throw new PresetOperationException(PresetOperationFailure.InvalidCategory);
            operation(content, category);
        });
}
