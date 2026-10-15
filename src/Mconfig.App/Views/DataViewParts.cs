using Mconfig.App.Resources;
using Mconfig.Application.History;
using Mconfig.Domain.Configuration;
using Mconfig.Presentation.DataManagement;

namespace Mconfig.App.Views;

/// <summary>保存と履歴・出力の画面で共通の部品（一覧・変更の表示・進捗の文言）。</summary>
internal static class DataViewParts
{
    public static string ProgressText(ProgressDisplay progress)
    {
        string what = progress.Operation is { } operation ? UiText.Get("Operation_" + operation) : UiText.Get("Data_Validate");
        return progress.Total <= 0
            ? UiText.Format("Progress_Starting", what)
            : UiText.Format("Progress_Checking", what, progress.Checked.ToString("N0", UiText.Culture), progress.Total.ToString("N0", UiText.Culture), progress.Percent);
    }

    public static void AddChangeColumns(ListView list)
    {
        list.Columns.Add("", 130);
        list.Columns.Add("", 280);
        list.Columns.Add("", 120);
        list.Columns.Add("", 120);
    }

    public static void SetChangeHeaders(ListView list)
    {
        list.Columns[0].Text = UiText.Get("Data_ColChange");
        list.Columns[1].Text = UiText.Get("Data_ColLocation");
        list.Columns[2].Text = UiText.Get("Data_ColBefore");
        list.Columns[3].Text = UiText.Get("Data_ColAfter");
    }

    public static void FillChanges(ListView list, IReadOnlyList<ChangeDisplay> changes)
    {
        list.BeginUpdate();
        list.Items.Clear();
        foreach (ChangeDisplay change in changes)
        {
            list.Items.Add(new ListViewItem([
                UiText.Get("Change_" + change.Kind),
                change.Location,
                change.AssignmentBefore is { } before ? Assignment(before) : change.Before ?? "",
                change.AssignmentAfter is { } after ? Assignment(after) : change.After ?? "",
            ]));
        }
        list.EndUpdate();
    }

    private static string Assignment(AssignmentSnapshot snapshot) =>
        string.Join(", ", snapshot.Conditions.Select(c => $"{c.Key}={(c.Value == AssignmentEvaluation.AnyValue ? UiText.Get("Assignment_Any") : c.Value)}"))
        + " → " + (snapshot.Preset ?? UiText.Get("Assignment_Disabled"));

    public static string FirstLine(string text)
    {
        int end = text.IndexOfAny(['\r', '\n']);
        return end < 0 ? text : text[..end];
    }

    public static ListView CreateList()
    {
        ListView list = new TableListView()
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            MultiSelect = false,
            HideSelection = false,
            ShowItemToolTips = true,
        };
        UiAppearance.ApplyList(list);
        return list;
    }

    public static FlowLayoutPanel Row(params Control[] controls)
    {
        FlowLayoutPanel row = new() { AutoSize = true, WrapContents = true, Dock = DockStyle.Top };
        row.Controls.AddRange(controls);
        return row;
    }
}

/// <summary>進捗の表示（文言・進捗バー・中止）。処理中だけ見せる。</summary>
internal sealed class ProgressStrip : FlowLayoutPanel
{
    private readonly Label _text = new() { AutoSize = true, Margin = new Padding(3, 8, 8, 3) };
    private readonly ProgressBar _bar = new() { Width = 260, Margin = new Padding(3, 8, 8, 3), Maximum = 100 };
    private readonly Button _cancel = new() { AutoSize = true };

    public ProgressStrip()
    {
        Dock = DockStyle.Top;
        AutoSize = true;
        WrapContents = true;
        Visible = false;
        Padding = new Padding(8, 2, 8, 2);
        Font = UiAppearance.BodyFont;
        BackColor = UiAppearance.Background;
        _text.ForeColor = UiAppearance.Text;
        UiAppearance.ApplyButton(_cancel);
        Controls.AddRange([_text, _bar, _cancel]);
        _cancel.Click += (_, _) => CancelClicked?.Invoke(this, EventArgs.Empty);
    }

    public event EventHandler? CancelClicked;

    public void Show(ProgressDisplay? progress)
    {
        Visible = progress is not null;
        if (progress is null) return;
        _text.Text = DataViewParts.ProgressText(progress);
        _cancel.Text = UiText.Get("Progress_Cancel");
        _cancel.Enabled = progress.CanCancel;
        _bar.Style = progress.Total <= 0 ? ProgressBarStyle.Marquee : ProgressBarStyle.Continuous;
        if (progress.Total > 0) _bar.Value = progress.Percent;
    }
}

