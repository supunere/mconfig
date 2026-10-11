namespace Mconfig.App.Views;

/// <summary>全4画面の配色と余白。高コントラストではシステムの配色を使う。</summary>
internal static class UiAppearance
{
    public static readonly Font BodyFont = new("Segoe UI", 10.5f);
    public static readonly Font HeadingFont = new("Segoe UI", 13.5f);
    public static Color Background => SystemInformation.HighContrast ? SystemColors.Control : Color.FromArgb(247, 247, 248);
    public static Color Surface => SystemColors.Window;
    public static Color Text => SystemColors.WindowText;
    public static Color Muted => SystemInformation.HighContrast ? SystemColors.WindowText : Color.FromArgb(96, 104, 115);
    public static Color Border => SystemInformation.HighContrast ? SystemColors.WindowText : Color.FromArgb(226, 229, 233);
    public static Color Accent => SystemInformation.HighContrast ? SystemColors.Highlight : Color.FromArgb(36, 99, 180);
    public static Color Selection => SystemInformation.HighContrast ? SystemColors.Highlight : Color.FromArgb(232, 241, 252);
    public static Color Warning => SystemInformation.HighContrast ? SystemColors.Window : Color.FromArgb(255, 248, 226);
    public static Color Missing => SystemInformation.HighContrast ? SystemColors.Window : Color.FromArgb(253, 237, 238);
    public static Color Unused => SystemInformation.HighContrast ? SystemColors.Window : Color.FromArgb(241, 242, 244);

    public static void ApplyGrid(DataGridView grid)
    {
        grid.Font = BodyFont;
        grid.BorderStyle = BorderStyle.None;
        grid.BackgroundColor = Surface;
        grid.GridColor = Border;
        grid.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
        grid.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.Single;
        grid.EnableHeadersVisualStyles = false;
        grid.ColumnHeadersDefaultCellStyle.BackColor = Background;
        grid.ColumnHeadersDefaultCellStyle.ForeColor = Text;
        grid.ColumnHeadersDefaultCellStyle.SelectionBackColor = Background;
        grid.ColumnHeadersDefaultCellStyle.SelectionForeColor = Text;
        grid.ColumnHeadersDefaultCellStyle.Padding = new Padding(8, 6, 8, 6);
        grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize;
        grid.DefaultCellStyle.BackColor = Surface;
        grid.DefaultCellStyle.ForeColor = Text;
        grid.DefaultCellStyle.SelectionBackColor = Selection;
        grid.DefaultCellStyle.SelectionForeColor = SystemInformation.HighContrast ? SystemColors.HighlightText : Text;
        grid.DefaultCellStyle.Padding = new Padding(8, 4, 8, 4);
        grid.RowTemplate.Height = 32;
        grid.CellPainting += (_, e) =>
        {
            if (e.Graphics is not { } graphics || e.RowIndex < 0 || e.ColumnIndex < 0 || grid.CurrentCell is not { } cell
                || cell.RowIndex != e.RowIndex || cell.ColumnIndex != e.ColumnIndex) return;
            e.Paint(e.ClipBounds, e.PaintParts);
            using Pen pen = new(Accent, Math.Max(1, grid.DeviceDpi / 96f));
            Rectangle bounds = e.CellBounds;
            bounds.Inflate(-1, -1);
            graphics.DrawRectangle(pen, bounds);
            e.Handled = true;
        };
    }

    public static void ApplyButton(Button button)
    {
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderColor = Border;
        button.BackColor = Surface;
        button.ForeColor = Text;
        button.Padding = new Padding(8, 3, 8, 3);
    }

    public static void ApplyPrimaryButton(Button button)
    {
        ApplyButton(button);
        if (SystemInformation.HighContrast) return;
        button.BackColor = Accent;
        button.ForeColor = Color.White;
        button.FlatAppearance.BorderColor = Accent;
        button.FlatAppearance.MouseOverBackColor = Color.FromArgb(28, 80, 150);
        button.FlatAppearance.MouseDownBackColor = Color.FromArgb(20, 63, 120);
    }

    public static void ApplyList(ListView list)
    {
        list.Font = BodyFont;
        list.BorderStyle = BorderStyle.None;
        list.BackColor = Surface;
        list.ForeColor = Text;
        list.GridLines = false;
    }

    public static void ApplyGroup(GroupBox group)
    {
        group.BackColor = Surface;
        group.ForeColor = Text;
        group.Padding = new Padding(12);
        group.Margin = new Padding(0, 0, 0, 12);
    }

    public static ToolStripRenderer Renderer() => SystemInformation.HighContrast
        ? new ToolStripSystemRenderer() : new NavigationRenderer();

    private sealed class NavigationRenderer : ToolStripSystemRenderer
    {
        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
        {
            e.Graphics.Clear(Surface);
            using Pen pen = new(Border);
            e.Graphics.DrawLine(pen, 0, e.ToolStrip.Height - 1, e.ToolStrip.Width, e.ToolStrip.Height - 1);
        }

        protected override void OnRenderButtonBackground(ToolStripItemRenderEventArgs e)
        {
            if (e.Item.Tag as string != "navigation") { base.OnRenderButtonBackground(e); return; }
            Rectangle bounds = new(Point.Empty, e.Item.Size);
            using SolidBrush back = new(e.Item.Selected ? Selection : Surface);
            e.Graphics.FillRectangle(back, bounds);
            if (e.Item is ToolStripButton { Checked: true })
            {
                using SolidBrush accent = new(Accent);
                int thickness = Math.Max(2, (e.ToolStrip?.DeviceDpi ?? 96) * 2 / 96);
                e.Graphics.FillRectangle(accent, 8, bounds.Height - thickness, Math.Max(0, bounds.Width - 16), thickness);
            }
        }

        protected override void OnRenderSplitButtonBackground(ToolStripItemRenderEventArgs e)
        {
            if (e.Item.Tag as string != "primary" || !e.Item.Enabled) { base.OnRenderSplitButtonBackground(e); return; }
            ToolStripSplitButton button = (ToolStripSplitButton)e.Item;
            using SolidBrush back = new(Accent);
            e.Graphics.FillRectangle(back, new Rectangle(Point.Empty, button.Size));
            using Pen divider = new(Color.FromArgb(110, Color.White));
            e.Graphics.DrawLine(divider, button.DropDownButtonBounds.Left, 5, button.DropDownButtonBounds.Left, button.Height - 5);
            OnRenderArrow(new ToolStripArrowRenderEventArgs(e.Graphics, button, button.DropDownButtonBounds, Color.White, ArrowDirection.Down));
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            if (e.Item.Enabled && e.Item.Tag as string == "primary") e.TextColor = Color.White;
            else if (e.Item is ToolStripButton { Checked: true } && e.Item.Tag as string == "navigation") e.TextColor = Accent;
            base.OnRenderItemText(e);
        }
    }
}
