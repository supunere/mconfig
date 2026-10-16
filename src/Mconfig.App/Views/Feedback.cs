using Mconfig.App.Resources;
using Mconfig.Application.Presets.Commands;
using Mconfig.Application.Workspaces;
using Mconfig.Application.Workspaces.Editing;
using Mconfig.Domain.Configuration;
using Mconfig.Presentation.Common;
using Mconfig.Presentation.DataManagement;

namespace Mconfig.App.Views;

/// <summary>
/// 利用者へ示す結果の文言。何が・どこで・なぜ問題かと次に取れる操作を示し、
/// 内部の例外やスタックトレースは通常の文言に含めない（技術的な要約は「詳細」としてだけコピーできる）。
/// </summary>
internal static class Feedback
{
    public static string Edit(EditFeedback feedback)
    {
        if (feedback.CellProblems.Count > 0)
        {
            const int Shown = 20;
            IEnumerable<string> lines = feedback.CellProblems.Take(Shown).Select(p => UiText.Format(
                p.Kind switch
                {
                    CellInputProblemKind.NotANumber => "Edit_NotANumber",
                    CellInputProblemKind.NotABoolean => "Edit_NotABoolean",
                    _ => "Edit_NotEditable",
                }, p.Location, p.Input));
            string more = feedback.CellProblems.Count > Shown ? Environment.NewLine + UiText.Format("Edit_MoreProblems", feedback.CellProblems.Count - Shown) : "";
            return UiText.Get("Edit_NothingChanged") + Environment.NewLine + Environment.NewLine + string.Join(Environment.NewLine, lines) + more;
        }
        return feedback.Status switch
        {
            CommandStatus.Conflict => UiText.Get("Edit_Conflict"),
            CommandStatus.Rejected => UiText.Get("Edit_Rejected"),
            _ => feedback.Failure switch
            {
                PresetOperationFailure.InvalidName => UiText.Get("Preset_InvalidName"),
                PresetOperationFailure.DuplicateName => UiText.Get("Preset_DuplicateName"),
                PresetOperationFailure.MissingPreset => UiText.Get("Preset_Missing"),
                PresetOperationFailure.InUse => UiText.Get("Preset_InUse"),
                PresetOperationFailure.InvalidCondition => UiText.Get("Assignment_InvalidCondition"),
                _ => UiText.Get("Edit_Invalid"),
            },
        };
    }

    public static string Issue(IssueDisplay issue)
    {
        string[] args = issue.Args.ToArray();
        string text = UiText.Format("Issue_" + issue.Code, args.Cast<object>().ToArray());
        return issue.Location.Length == 0 ? text : $"{issue.Location}: {text}";
    }

    public static string Severity(ValidationSeverity severity) =>
        UiText.Get(severity == ValidationSeverity.Error ? "Severity_Error" : "Severity_Warning");

    /// <summary>操作の結果。完了した段階に応じた文言にし、JSON 保存だけで確定保存の成功と言わない。</summary>
    public static string Operation(OperationDisplay result)
    {
        // 変更理由が空なだけなら、内容のエラーとは言わずに理由を求める
        if (result.Status == OperationStatus.ValidationFailed && result.Issues.Count > 0 &&
            result.Issues.All(i => i.Code == ValidationCode.ReasonEmpty))
            return UiText.Get("Issue_ReasonEmpty");
        string op = UiText.Get("Operation_" + result.Operation);
        string message = result.Status switch
        {
            OperationStatus.Succeeded => result.Operation switch
            {
                DataOperation.Publish => UiText.Format(result.Reason == OperationReason.ExistingVersionExported ? "Publish_CurrentSucceeded" : "Publish_Succeeded", result.Output ?? ""),
                DataOperation.SaveDraft => UiText.Get("Result_DraftSaved"),
                DataOperation.Discard => UiText.Get("Result_Discarded"),
                DataOperation.Restore => UiText.Get("Result_Restored"),
                _ => UiText.Format("Result_Exported", result.Output ?? ""),
            },
            OperationStatus.NoChanges => UiText.Get(result.Operation is DataOperation.Publish ? "Result_NoChangesCommit" : "Result_NoChanges"),
            OperationStatus.ValidationFailed => UiText.Format("Result_ValidationFailed", op),
            OperationStatus.ConfirmationRequired => UiText.Format("Result_NotConfirmed", op),
            OperationStatus.Conflict => UiText.Format("Result_Conflict", op),
            OperationStatus.Rejected => UiText.Format("Result_Rejected", op),
            OperationStatus.Canceled => UiText.Format("Result_Canceled", op),
            OperationStatus.RecoveryPending => UiText.Format("Result_RecoveryPending", op),
            _ => UiText.Format("Result_TechnicalFailure", op),
        };
        if (result.FixedPublicationMetadata is { } fixedMetadata)
            message += Environment.NewLine + UiText.Format("Publish_FixedMetadata", fixedMetadata);
        // 業務上の前提を満たさなかった理由と、次に取れる操作
        if (result.Reason is { } reason)
            message += Environment.NewLine + UiText.Get("Reason_" + reason);
        // 一時保存は済んだが履歴の保存が終わらなかった場合を区別する
        if (result.Status != OperationStatus.Succeeded && result.Stage == OperationStage.DraftSaved)
            message += Environment.NewLine + UiText.Get(result.Operation is DataOperation.Publish ? "Result_DraftSavedButNotCommitted" : "Result_DraftSavedOnly");
        if (result.Issues.Count > 0 && result.Status is OperationStatus.ValidationFailed)
            message += Environment.NewLine + Environment.NewLine + string.Join(Environment.NewLine,
                result.Issues.Where(i => i.Severity == ValidationSeverity.Error || result.Operation == DataOperation.Publish).Take(20).Select(i => "・" + Issue(i)));
        return message;
    }

    public static void ShowOperation(IWin32Window? owner, OperationDisplay result)
    {
        bool ok = result.Status is OperationStatus.Succeeded or OperationStatus.NoChanges;
        if (ok || string.IsNullOrEmpty(result.Detail))
        {
            MessageBox.Show(owner, Operation(result), UiText.Get("App_Title"), MessageBoxButtons.OK,
                ok ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
            return;
        }
        ShowWithDetails(owner, Operation(result), result.Detail!);
    }

    /// <summary>文言と、コピーできる診断（技術的な要約）を示す。</summary>
    public static void ShowWithDetails(IWin32Window? owner, string message, string details)
    {
        using Form dialog = Dialogs.Create(UiText.Get("App_Title"));
        TableLayoutPanel layout = Dialogs.Layout(3);
        Label text = new() { Text = message, AutoSize = true, MaximumSize = new Size(560, 0), Margin = new Padding(0, 0, 0, 8) };
        TextBox detail = new()
        {
            Text = details, ReadOnly = true, Multiline = true, ScrollBars = ScrollBars.Vertical,
            Width = 560, Height = 120, Margin = new Padding(0, 0, 0, 8),
        };
        Button copy = new() { Text = UiText.Get("Button_CopyDetails"), AutoSize = true };
        copy.Click += (_, _) => Clipboard.SetText(message + Environment.NewLine + details);
        Button ok = new() { Text = UiText.Get("Button_OK"), AutoSize = true, DialogResult = DialogResult.OK };
        layout.Controls.Add(text, 0, 0);
        layout.Controls.Add(detail, 0, 1);
        layout.Controls.Add(Dialogs.Buttons(ok, copy), 0, 2);
        dialog.Controls.Add(layout);
        dialog.AcceptButton = ok;
        dialog.CancelButton = ok;
        dialog.ShowDialog(owner);
    }
}

/// <summary>内容の大きさに合わせるダイアログの部品（AutoSize のフォームに Dock した子を置くと縮むので表で組む）。</summary>
internal static class Dialogs
{
    public static Form Create(string title) => new()
    {
        Text = title,
        StartPosition = FormStartPosition.CenterParent,
        AutoScaleMode = AutoScaleMode.Dpi,
        AutoSize = true,
        AutoSizeMode = AutoSizeMode.GrowAndShrink,
        FormBorderStyle = FormBorderStyle.FixedDialog,
        MinimizeBox = false,
        MaximizeBox = false,
        ShowInTaskbar = false,
    };

    public static TableLayoutPanel Layout(int rows) => new()
    {
        AutoSize = true,
        AutoSizeMode = AutoSizeMode.GrowAndShrink,
        ColumnCount = 1,
        RowCount = rows,
        Padding = new Padding(12),
        Location = new Point(0, 0),
    };

    /// <summary>右寄せのボタン列。先頭のボタンが右端になる。</summary>
    public static FlowLayoutPanel Buttons(params Button[] buttons)
    {
        FlowLayoutPanel panel = new()
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.RightToLeft,
            Anchor = AnchorStyles.Right,
            Margin = new Padding(0),
            WrapContents = false,
        };
        panel.Controls.AddRange(buttons);
        return panel;
    }

    /// <summary>1 行の文字列を尋ねる。やめたら null。</summary>
    public static string? AskText(IWin32Window owner, string title, string message, string initial)
    {
        using Form dialog = Create(title);
        TableLayoutPanel layout = Layout(3);
        Label label = new() { Text = message, AutoSize = true, MaximumSize = new Size(420, 0), Margin = new Padding(0, 0, 0, 6) };
        TextBox input = new() { Text = initial, Width = 420, Margin = new Padding(0, 0, 0, 10) };
        Button cancel = new() { Text = UiText.Get("Button_Cancel"), AutoSize = true, DialogResult = DialogResult.Cancel };
        Button ok = new() { Text = UiText.Get("Button_OK"), AutoSize = true, DialogResult = DialogResult.OK };
        layout.Controls.Add(label, 0, 0);
        layout.Controls.Add(input, 0, 1);
        layout.Controls.Add(Buttons(cancel, ok), 0, 2);
        dialog.Controls.Add(layout);
        dialog.AcceptButton = ok;
        dialog.CancelButton = cancel;
        dialog.Shown += (_, _) => input.SelectAll();
        return dialog.ShowDialog(owner) == DialogResult.OK ? input.Text : null;
    }
}
