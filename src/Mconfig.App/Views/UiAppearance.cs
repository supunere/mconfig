namespace Mconfig.App.Views;

/// <summary>標準WinFormsの外観。コントロールの描画はWindowsに任せ、余白と状態色を揃える。</summary>
internal static class UiAppearance
{
    public static readonly Font BodyFont = SystemFonts.MessageBoxFont!;
    public static readonly Font HeadingFont = new(BodyFont, FontStyle.Bold);
    public static readonly Font SectionHeadingFont = BodyFont;
    public static Color Background => SystemColors.Control;
    public static Color Surface => SystemColors.Window;
    public static Color PaneHeader => SystemColors.Control;
    public static Color Text => SystemColors.WindowText;
    public static Color Muted => SystemInformation.HighContrast ? SystemColors.WindowText : SystemColors.ControlDarkDark;
    public static Color Border => SystemColors.ControlDark;
    public static Color GroupBorder => SystemColors.ControlDark;
    public static Color Accent => SystemColors.Highlight;
    public static Color Selection => SystemInformation.HighContrast ? SystemColors.Highlight : SystemColors.GradientActiveCaption;
    // 差分・未入力は業務上の状態表示として残す。
    public static Color Warning => SystemInformation.HighContrast ? SystemColors.Window : Color.FromArgb(255, 248, 226);
    public static Color Missing => SystemInformation.HighContrast ? SystemColors.Window : Color.FromArgb(253, 237, 238);
    public static Color Unused => SystemColors.Control;

    // 領域見出しの印。標準PanelとLabelだけで構成し、選択色と区別する。
    public static Control SectionTitle(Label label)
    {
        label.Font = HeadingFont;
        label.Margin = Padding.Empty;
        label.Anchor = AnchorStyles.Left;
        TableLayoutPanel title = new()
        {
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2, RowCount = 1, Margin = Padding.Empty,
        };
        title.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        title.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        title.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        Panel marker = new()
        {
            Size = new Size(3, label.Font.Height), Anchor = AnchorStyles.Left,
            BackColor = SystemInformation.HighContrast ? SystemColors.WindowText : Color.FromArgb(75, 145, 125),
            Margin = new Padding(0, 0, 6, 0), TabStop = false,
        };
        title.Controls.Add(marker, 0, 0);
        title.Controls.Add(label, 1, 0);
        return title;
    }

    public static void ApplyPaneHeader(Control header)
    {
        header.BackColor = SystemColors.Control;
        header.Padding = new Padding(8, 4, 8, 4);
    }

    public static void ArrangeSectionHeader(TableLayoutPanel header, Control[] labels, Control[] actions)
    {
        header.BackColor = SystemColors.Control;
        header.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        header.Padding = new Padding(8, 4, 8, 4);
        header.ColumnCount = labels.Length + actions.Length + 1;
        header.RowCount = 1;
        header.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        int column = 0;
        foreach (Control label in labels)
        {
            label.Margin = new Padding(0, 0, 8, 0);
            header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            label.Anchor = AnchorStyles.Left;
            header.Controls.Add(label, column++, 0);
        }
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        column++;
        foreach (Control action in actions)
        {
            action.Margin = new Padding(6, 0, 0, 0);
            header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            action.Anchor = AnchorStyles.Right;
            header.Controls.Add(action, column++, 0);
        }
    }

    public static void ApplyGrid(DataGridView grid)
    {
        grid.Font = BodyFont;
        grid.BorderStyle = BorderStyle.Fixed3D;
        grid.BackgroundColor = SystemColors.Window;
        grid.GridColor = SystemColors.ControlDark;
        grid.EnableHeadersVisualStyles = true;
        grid.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.Raised;
        grid.ColumnHeadersDefaultCellStyle.BackColor = SystemColors.Control;
        grid.ColumnHeadersDefaultCellStyle.ForeColor = SystemColors.ControlText;
        grid.ColumnHeadersDefaultCellStyle.SelectionBackColor = SystemColors.Control;
        grid.ColumnHeadersDefaultCellStyle.SelectionForeColor = SystemColors.ControlText;
        grid.ColumnHeadersDefaultCellStyle.Padding = new Padding(3, 2, 3, 2);
        grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize;
        grid.DefaultCellStyle.BackColor = SystemColors.Window;
        grid.DefaultCellStyle.ForeColor = SystemColors.WindowText;
        grid.DefaultCellStyle.SelectionBackColor = SystemColors.Highlight;
        grid.DefaultCellStyle.SelectionForeColor = SystemColors.HighlightText;
        grid.DefaultCellStyle.Padding = new Padding(3, 1, 3, 1);
        grid.RowTemplate.Height = 24;
    }

    public static void ApplyButton(Button button)
    {
        button.FlatStyle = FlatStyle.Standard;
        button.UseVisualStyleBackColor = true;
        button.ForeColor = SystemColors.ControlText;
        button.Padding = new Padding(3, 1, 3, 1);
    }

    public static void ApplyPrimaryButton(Button button) => ApplyButton(button);

    public static void ApplyList(ListView list)
    {
        list.Font = BodyFont;
        list.BorderStyle = BorderStyle.Fixed3D;
        list.BackColor = SystemColors.Window;
        list.ForeColor = SystemColors.WindowText;
        list.GridLines = true;
        list.OwnerDraw = false;
    }

    public static void ApplyNavigationList(ListView list)
    {
        ApplyList(list);
        list.GridLines = false;
        list.HeaderStyle = ColumnHeaderStyle.None;
        list.HideSelection = false;
    }

    public static void ApplyGroup(GroupBox group)
    {
        group.BackColor = SystemColors.Control;
        group.ForeColor = SystemColors.ControlText;
        group.Padding = new Padding(6);
        group.Margin = new Padding(0, 0, 0, 6);
    }

    public static ToolStripRenderer Renderer() => new ToolStripSystemRenderer();
}
