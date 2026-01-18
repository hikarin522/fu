using UnitGenerator;

namespace Fu.Core.Models;

/// <summary>
/// プレイヤー（参加者）を識別するID
/// </summary>
[UnitOf<string>(UnitGenerateOptions.JsonConverter | UnitGenerateOptions.JsonConverterDictionaryKeySupport)]
public readonly partial struct PlayerId;
