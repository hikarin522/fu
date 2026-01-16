using System.Text.Json.Serialization;

namespace ShogiGame.Models;

public readonly struct Position : IEquatable<Position>
{
    // 将棋の座標系: 右上が1一(0,0)、左下が9九(8,8)
    // Colは筋(1-9)、Rowは段(一-九)
    [JsonInclude]
    public int Col { get; init; }  // 0-8 (内部表現)
    [JsonInclude]
    public int Row { get; init; }  // 0-8 (内部表現)

    [JsonConstructor]
    public Position(int Col, int Row)  // パラメータ名を大文字にしてJSONプロパティ名と一致させる
    {
        this.Col = Col;
        this.Row = Row;
    }

    public bool IsValid => Col >= 0 && Col < 9 && Row >= 0 && Row < 9;

    // 先手から見た段 (0が一段目/相手陣)
    public int SenteRow => Row;
    // 後手から見た段 (0が一段目/相手陣)
    public int GoteRow => 8 - Row;

    // 表示用 (9一 形式)
    public string ToNotation() => $"{9 - Col}{RowToKanji(Row)}";

    private static string RowToKanji(int row) => row switch
    {
        0 => "一",
        1 => "二",
        2 => "三",
        3 => "四",
        4 => "五",
        5 => "六",
        6 => "七",
        7 => "八",
        8 => "九",
        _ => ""
    };

    public bool Equals(Position other) => Col == other.Col && Row == other.Row;
    public override bool Equals(object? obj) => obj is Position other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(Col, Row);
    public static bool operator ==(Position left, Position right) => left.Equals(right);
    public static bool operator !=(Position left, Position right) => !left.Equals(right);

    public override string ToString() => ToNotation();
}
