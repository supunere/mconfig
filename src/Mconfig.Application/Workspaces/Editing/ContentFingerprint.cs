namespace Mconfig.Application.Workspaces.Editing;

/// <summary>
/// ワークスペース内容（設定・仕様・スキーマ一式）の正規化指紋。整形差に依存しない内容比較に使う。
/// 計算方法は保存表現を知る Infrastructure が決め、Application では不透明な値として比較だけを行う。
/// </summary>
public sealed record ContentFingerprint
{
    public ContentFingerprint(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("指紋が空です。", nameof(value));
        Value = value;
    }

    public string Value { get; }
}
