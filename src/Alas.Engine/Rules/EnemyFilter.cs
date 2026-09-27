using System.Collections.Immutable;
using Alas.Engine.Runtime;

namespace Alas.Engine.Rules;

/// <summary>Ordered GridInfo.str selectors from native ENEMY_FILTER; data, not executable steps.</summary>
public sealed class EnemyFilter
{
    public static readonly SourceFile Source = new("module/base/filter.py",
        "2879f33b23dbfe863ede4015cc8d5a96cbbd090fc0faf81cba6b1416acde3730");
    public static readonly EnemyFilter StrongestFirst = new("3L > 3M > 3E > 3C > 2L > 2M > 2E > 2C > 1L > 1M > 1E > 1C");
    public static readonly EnemyFilter WeakestFirst = new("1L > 1M > 1E > 1C > 2L > 2M > 2E > 2C > 3L > 3M > 3E > 3C");
    public string Expression { get; }
    private readonly ImmutableArray<string> _selectors;

    public EnemyFilter(string expression)
    {
        ArgumentNullException.ThrowIfNull(expression);
        Expression = expression;
        string normalized = new(expression.Where(c => c is not (' ' or '\t' or '\r' or '\n')).Select(c =>
            "＞﹥›˃ᐳ❯".Contains(c) ? '>' : "‐‑‒–—―−－﹣﹘⁃".Contains(c) ? '-' : c).ToArray());
        _selectors = normalized.ToLowerInvariant().Split('>').Where(value => value.Length > 0)
            .Distinct(StringComparer.Ordinal).ToImmutableArray();
    }

    public IReadOnlyList<CellState> Apply(IEnumerable<CellState> ordered, int preserve = 0)
    {
        ArgumentNullException.ThrowIfNull(ordered);
        var groups = ordered.ToLookup(grid => grid.Encode().ToLowerInvariant(), StringComparer.Ordinal);
        var selected = _selectors.SelectMany(selector => groups[selector]).Distinct().ToArray();
        // Python list slicing also accepts negative offsets, even though shipped
        // campaign rules use nonnegative preservation counts.
        int start = preserve < 0 ? (int)Math.Max(0L, (long)selected.Length + preserve) : Math.Min(selected.Length, preserve);
        return selected[start..];
    }
}
