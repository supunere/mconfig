using Mconfig.App.Resources;

namespace Mconfig.App.Views;

/// <summary>
/// ワークスペースが読めないときの復元候補の選択。起動の途中でメイン画面より先に示す。
/// 候補は新しい順に並べ、既定では先頭を選ぶ。
/// </summary>
internal static class RecoveryDialog
{
    /// <summary>選んだ候補の位置。やめたら null。</summary>
    public static int? Choose(string problems, IReadOnlyList<string> candidates)
    {
        using Form dialog = Dialogs.Create(UiText.Get("Recovery_Title"));
        dialog.StartPosition = FormStartPosition.CenterScreen;
        dialog.ShowInTaskbar = true;
        TableLayoutPanel layout = Dialogs.Layout(4);
        Label message = new()
        {
            Text = UiText.Format("Recovery_Message", problems),
            AutoSize = true, MaximumSize = new Size(560, 0), Margin = new Padding(0, 0, 0, 8),
        };
        ListBox list = new() { Width = 560, Height = 180, Margin = new Padding(0, 0, 0, 8), HorizontalScrollbar = true };
        list.Items.AddRange(candidates.Cast<object>().ToArray());
        if (list.Items.Count > 0) list.SelectedIndex = 0;
        Label note = new()
        {
            Text = UiText.Get("Recovery_Note"),
            AutoSize = true, MaximumSize = new Size(560, 0), Margin = new Padding(0, 0, 0, 10),
        };
        Button cancel = new() { Text = UiText.Get("Button_Cancel"), AutoSize = true, DialogResult = DialogResult.Cancel };
        Button restore = new() { Text = UiText.Get("Recovery_Restore"), AutoSize = true, DialogResult = DialogResult.OK };
        list.SelectedIndexChanged += (_, _) => restore.Enabled = list.SelectedIndex >= 0;
        list.DoubleClick += (_, _) =>
        {
            if (list.SelectedIndex < 0) return;
            dialog.DialogResult = DialogResult.OK;
        };
        layout.Controls.Add(message, 0, 0);
        layout.Controls.Add(list, 0, 1);
        layout.Controls.Add(note, 0, 2);
        layout.Controls.Add(Dialogs.Buttons(cancel, restore), 0, 3);
        dialog.Controls.Add(layout);
        dialog.AcceptButton = restore;
        dialog.CancelButton = cancel;
        return dialog.ShowDialog() == DialogResult.OK && list.SelectedIndex >= 0 ? list.SelectedIndex : null;
    }
}
