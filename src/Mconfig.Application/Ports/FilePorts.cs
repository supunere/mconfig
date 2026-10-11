namespace Mconfig.Application.Ports;

/// <summary>
/// 利用者が選んだ場所への CSV ファイルの出力。実装は Infrastructure が持ち、Excel で開ける文字コードで書く。
/// 同じフォルダの一時ファイルに書き、write が最後まで終わったときだけ path を置き換える。
/// write が例外（取消を含む）で終わったら一時ファイルだけを消して例外をそのまま伝え、既存の path は変えない。
/// </summary>
public interface ICsvFileWriter
{
    T Write<T>(string path, Func<TextWriter, T> write);
}
