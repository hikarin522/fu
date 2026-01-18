using Fu.Core.Abstractions;

namespace Fu.Core.Models;

/// <summary>
/// 候補手の情報
/// </summary>
public record CandidateMove(
    int Rank,
    string Move,
    int? Evaluation,
    int? MateIn,
    string? PrincipalVariation
);

/// <summary>
/// 局面のキャッシュされた評価情報
/// </summary>
public record CachedEvaluation(
    int? Evaluation,
    int? MateIn,
    Turn MateTurn,
    string? BestMove,
    string? PrincipalVariation,
    int Depth,
    IReadOnlyList<CandidateMove> Candidates
);
