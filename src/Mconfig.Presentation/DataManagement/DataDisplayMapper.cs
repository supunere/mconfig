using System.Globalization;
using Mconfig.Application.History;
using Mconfig.Application.Ports;
using Mconfig.Application.Workspaces;
using Mconfig.Application.Workspaces.Queries;
using Mconfig.Domain.Configuration;
using Mconfig.Domain.Definitions;
using Mconfig.Presentation.Common;

namespace Mconfig.Presentation.DataManagement;

/// <summary>
/// 検証結果・変更・履歴・操作結果を、保存と履歴・出力の画面の表示に写す（両方の画面で同じ示し方にする）。
/// 対象の位置は表示言語の表示名で示す。
/// </summary>
public sealed class DataDisplayMapper(DisplayLanguage language, GetDefinitionsHandler definitions)
{
    public static ValidationResultItem Item(ValidationIssue issue, ValidationOperation operation) =>
        new(issue, ValidationPolicy.SeverityFor(issue.Code, operation), ValidationPolicy.RequiresConfirmation(issue.Code));

    /// <summary>操作の結果の検証結果（履歴に保存の規則の重さで示す）。</summary>
    public IReadOnlyList<IssueDisplay> Issues(IReadOnlyList<ValidationIssue> issues) =>
        Issues(issues.Select(i => Item(i, ValidationOperation.Commit)).ToArray());

    /// <remarks>対象は表示名で示す。schemas を渡さなければ、いまの定義を使う。</remarks>
    public IReadOnlyList<IssueDisplay> Issues(IReadOnlyList<ValidationResultItem> items, IReadOnlyList<SchemaDefinition>? schemas = null)
    {
        IReadOnlyList<SchemaDefinition> current = schemas ?? definitions.Handle().Schemas;
        return items.Select(item => new IssueDisplay(
            item.Severity, item.Issue.Code, Location(item.Issue.Target, current),
            item.Issue.Code == ValidationCode.UnreachableAssignment
                ? item.Issue.Args.Select(a => int.TryParse(a, NumberStyles.Integer, CultureInfo.InvariantCulture, out int row)
                    ? (row + 1).ToString(CultureInfo.InvariantCulture) : a).ToArray()
                : item.Issue.Args,
            item.RequiresConfirmation) { Target = item.Issue.Target })
        .OrderBy(i => i.Severity == ValidationSeverity.Error ? 0 : 1)
        .ToArray();
    }

    /// <summary>操作の結果の表示。</summary>
    public OperationDisplay Result(DataOperation operation, OperationResult result) =>
        new(operation, result.Status, result.CompletedStage, Issues(result.Issues), result.Output, result.Detail) { Reason = result.Reason };

    /// <summary>履歴や定義を読めずに始められなかったときの表示（Detail は技術的な要約）。</summary>
    public static OperationDisplay Unavailable(DataOperation operation, string detail) =>
        new(operation, OperationStatus.Rejected, OperationStage.NotStarted, [], null, detail);

    public static RevisionDisplay Revision(RevisionInfo info) => new(info.Id, info.When, info.Author, info.Message.Trim());

    public static IReadOnlyList<ChangeDisplay> Changes(IReadOnlyList<ContentChange> changes) =>
        changes.Select(c => new ChangeDisplay(
            c.Kind,
            string.Join(" / ", new[]
            {
                c.Location.DefinitionPath, c.Location.SpecKey, c.Location.Schema, c.Location.Category, c.Location.Preset,
                c.Location.Group, c.Location.Row, c.Location.Column,
                c.Location.Index is int i ? "#" + (i + 1).ToString(CultureInfo.InvariantCulture) : null,
            }.OfType<string>()),
            c.CellBefore is { } before ? CellText.Format(before) : c.ItemsBefore is { } itemsBefore ? string.Join(", ", itemsBefore) : null,
            c.CellAfter is { } after ? CellText.Format(after) : c.ItemsAfter is { } itemsAfter ? string.Join(", ", itemsAfter) : null)
        {
            AssignmentBefore = c.AssignmentBefore,
            AssignmentAfter = c.AssignmentAfter,
        }).ToArray();

    private string Location(ValidationTarget target, IReadOnlyList<SchemaDefinition> schemas) =>
        string.IsNullOrEmpty(target.Category)
            ? ""
            : DefinitionLabels.Location(schemas, language.Code, target.Category, target.Preset, target.Group, target.Row, target.Column);
}
