using Mconfig.Application.Workspaces.Editing;
using Mconfig.Domain.Configuration;
using Mconfig.Domain.Definitions;

namespace Mconfig.Application.Assignments.Commands;

/// <summary>割り当て行の内容。Preset が null の行は Disabled。</summary>
public sealed record AssignmentRow(IReadOnlyDictionary<string, string> Conditions, string? Preset);

public sealed record AddAssignmentCommand(string Category, AssignmentRow Row, SessionVersion ExpectedVersion);

public sealed record ChangeAssignmentCommand(string Category, int Index, AssignmentRow Row, SessionVersion ExpectedVersion);

public sealed record RemoveAssignmentCommand(string Category, int Index, SessionVersion ExpectedVersion);

/// <summary>割り当て行の優先順位を変える（From の行を To の位置へ移す）。</summary>
public sealed record MoveAssignmentCommand(string Category, int From, int To, SessionVersion ExpectedVersion);

/// <summary>
/// 割り当ての業務操作。条件と仕様の照合、存在しないプリセットへの参照、
/// 範囲外の行番号などの規則はカテゴリの業務操作が判定し、拒否されたら何も変えない（Invalid）。
/// </summary>
public sealed class AssignmentCommandHandlers(EditSession session)
{
    public CommandResult Handle(AddAssignmentCommand command) =>
        Run(command.ExpectedVersion, command.Category, (category, spec) =>
            category.AddAssignment(Conditions(command.Row), command.Row.Preset, spec));

    public CommandResult Handle(ChangeAssignmentCommand command) =>
        Run(command.ExpectedVersion, command.Category, (category, spec) =>
            category.ChangeAssignment(command.Index, Conditions(command.Row), command.Row.Preset, spec));

    public CommandResult Handle(RemoveAssignmentCommand command) =>
        Run(command.ExpectedVersion, command.Category, (category, _) => category.RemoveAssignment(command.Index));

    public CommandResult Handle(MoveAssignmentCommand command) =>
        Run(command.ExpectedVersion, command.Category, (category, _) => category.MoveAssignment(command.From, command.To));

    private CommandResult Run(SessionVersion expectedVersion, string categoryName, Action<CategoryData, SpecDefinition> change) =>
        session.Apply(expectedVersion, content =>
        {
            if (!content.Settings.Categories.TryGetValue(categoryName, out CategoryData? category))
                throw new PresetOperationException(PresetOperationFailure.InvalidCategory);
            change(category, content.Spec);
        });

    private static IReadOnlyDictionary<string, string> Conditions(AssignmentRow row) =>
        (row ?? throw new ArgumentNullException(nameof(row))).Conditions ?? throw new ArgumentNullException(nameof(row));
}
