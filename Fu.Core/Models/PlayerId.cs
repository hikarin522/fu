using System.Security.Cryptography;

using UnitGenerator;

namespace Fu.Core.Models;

/// <summary>
/// プレイヤー（参加者）を識別するID
/// </summary>
[UnitOf<string>(UnitGenerateOptions.JsonConverter | UnitGenerateOptions.JsonConverterDictionaryKeySupport)]
public readonly partial struct PlayerId
{
    private const string Chars = "abcdefghijklmnopqrstuvwxyz0123456789";
    private const int RandomPartLength = 6;

    /// <summary>ニックネームからプレイヤーIDを生成</summary>
    public static PlayerId Generate(string nickname)
    {
        Span<char> randomPart = stackalloc char[RandomPartLength];
        for (var i = 0; i < RandomPartLength; i++) {
            randomPart[i] = Chars[RandomNumberGenerator.GetInt32(Chars.Length)];
        }
        return new PlayerId($"{nickname}_{new string(randomPart)}");
    }
}
