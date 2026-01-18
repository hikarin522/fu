using System.Collections.Frozen;

using Fu.Core.Models.Dto;

namespace Fu.Core.Models;

/// <summary>
/// 盤面座標を表すドメインモデル
/// </summary>
public readonly record struct Position(int Col, int Row)
{
    private const int BoardSize = BoardConstants.Size;

    // 段の漢数字マッピング
    private static readonly FrozenDictionary<int, string> RowKanjiMap =
        new Dictionary<int, string> {
            [0] = "一",
            [1] = "二",
            [2] = "三",
            [3] = "四",
            [4] = "五",
            [5] = "六",
            [6] = "七",
            [7] = "八",
            [8] = "九"
        }.ToFrozenDictionary();

    /// <summary>盤面内の有効な座標かどうか</summary>
    public bool IsValid => this.Col is >= 0 and < BoardSize && this.Row is >= 0 and < BoardSize;

    /// <summary>先手から見た段 (0が一段目/相手陣)</summary>
    public int SenteRow => this.Row;

    /// <summary>後手から見た段 (0が一段目/相手陣)</summary>
    public int GoteRow => BoardSize - 1 - this.Row;

    /// <summary>棋譜表記 (例: 7六)</summary>
    public string ToNotation() => $"{BoardSize - this.Col}{RowKanjiMap.GetValueOrDefault(this.Row, "")}";

    // DTO変換
    public PositionDto ToDto() => new(this.Col, this.Row);
    public static Position FromDto(PositionDto dto) => new(dto.Col, dto.Row);

    public override string ToString() => this.ToNotation();
}
