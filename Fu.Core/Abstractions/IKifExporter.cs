using Fu.Core.Models;

namespace Fu.Core.Abstractions;

/// <summary>
/// 棋譜エクスポーターのインターフェース
/// </summary>
public interface IKifExporter
{
    /// <summary>棋譜をKIF形式でエクスポート</summary>
    string Export(
        IReadOnlyList<Move> moves,
        GameStatus status,
        string senteNickname = "",
        string goteNickname = "",
        IReadOnlyList<TimeSpan>? moveTimes = null);
}
