using System.Security.Cryptography;

using UnitGenerator;

namespace Fu.Core.Models;

/// <summary>
/// P2P接続のルームIDを表す値オブジェクト
/// </summary>
[UnitOf<string>(UnitGenerateOptions.JsonConverter | UnitGenerateOptions.JsonConverterDictionaryKeySupport)]
public readonly partial struct RoomId
{
    private const string Chars = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
    private const int Length = 6;

    /// <summary>新しいルームIDを生成</summary>
    public static RoomId Generate()
    {
        Span<char> result = stackalloc char[Length];
        for (var i = 0; i < Length; i++) {
            result[i] = Chars[RandomNumberGenerator.GetInt32(Chars.Length)];
        }
        return new RoomId(new string(result));
    }
}
