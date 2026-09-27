using System.Collections.Immutable;

namespace Alas.Engine.Rules;

/// <summary>Native select_grids scale membership and strongest/weakest filters, in that order.</summary>
public sealed record EnemySelection(ImmutableArray<int> Scales = default, bool Strongest = false, bool Weakest = false);
