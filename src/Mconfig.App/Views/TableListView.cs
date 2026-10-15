namespace Mconfig.App.Views;

/// <summary>標準のListView描画を保ち、データ表の外周だけ共通の薄い枠で囲む。</summary>
internal sealed class TableListView : ListView
{
    protected override void WndProc(ref Message m)
    {
        base.WndProc(ref m);
        // ListViewはネイティブ描画なので、通常のPaintイベントでは枠を追加できない。
        if (m.Msg != 0x000F || IsDisposed || !IsHandleCreated) return; // WM_PAINT
        using Graphics graphics = Graphics.FromHwnd(Handle);
        UiAppearance.DrawTableBorder(this, graphics);
    }
}
