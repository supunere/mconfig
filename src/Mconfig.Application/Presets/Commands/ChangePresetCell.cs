using Mconfig.Application.Workspaces.Editing;
using Mconfig.Domain.Configuration;

namespace Mconfig.Application.Presets.Commands;

/// <summary>プリセットのセルの値を変える。</summary>
public sealed record ChangePresetCellCommand(
    string Category, string Preset, string Group, string Row, string Column, CellValue Value,
    SessionVersion ExpectedVersion);

public sealed class ChangePresetCellHandler(EditSession session)
{
    public CommandResult Handle(ChangePresetCellCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        return session.Apply(command.ExpectedVersion, content =>
        {
            if (!content.Settings.Categories.TryGetValue(command.Category, out CategoryData? category))
                throw new PresetOperationException(PresetOperationFailure.InvalidCategory);
            category.ChangeCell(command.Preset, command.Group, command.Row, command.Column, command.Value);
        });
    }
}
