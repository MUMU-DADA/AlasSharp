using System.Collections.Immutable;
using Alas.Engine.Rules;

namespace Alas.Engine.Runtime;

public sealed record MovableEnemyMatch(ImmutableArray<Cell> Before, ImmutableArray<Cell> After,
    long Score, long ColumnSum);

/// <summary>Native match_movable scoring, solved by rectangular Hungarian assignment instead of permutations.</summary>
public static class MovableEnemyMatcher
{
    public static readonly SourceFile Source = new("module/map/utils.py",
        "74b9fb3440cf3000336a6a056419b35ea03b8735c935849f6b85c1f0830eaf94");

    public static MovableEnemyMatch Match(IReadOnlyList<Cell> before, IReadOnlyList<Cell> spawn,
        IReadOnlyList<Cell> after, IReadOnlyList<Cell> fleets, int step = 2, CancellationToken token = default)
    {
        if (step < 0) throw new ArgumentOutOfRangeException(nameof(step));
        token.ThrowIfCancellationRequested();
        var rows = before.Concat(spawn).ToArray();
        var columns = after.Concat(fleets).ToArray();
        int n = rows.Length, m = columns.Length, width = checked(m + n);
        if (n == 0) return new([], [], 0, 0);
        // A one-point score improvement dominates every possible column sum.
        long scale = checked((long)n * (m + 1L) + 1);
        long Score(int i, int j)
        {
            long value = (long)step - Math.Abs((long)rows[i].Column - columns[j].Column) - Math.Abs((long)rows[i].Row - columns[j].Row);
            if (value < 0) value = -10000;
            return Math.Max(-10000, value - (i >= before.Count ? 100 : 0) - (j >= after.Count ? 100 : 0));
        }
        long Cost(int i, int j)
        {
            long score = j < m ? Score(i, j) : -10000;
            // Forbidden edges must lose even to a skip. Dummy columns permit every row to skip.
            if (j < m && score < -100) return checked(10001 * scale + m);
            return checked(-score * scale + (j < m ? j : m));
        }
        var u = new long[n + 1]; var v = new long[width + 1];
        var owner = new int[width + 1]; var way = new int[width + 1];
        for (int row = 1; row <= n; row++)
        {
            token.ThrowIfCancellationRequested();
            owner[0] = row;
            var best = Enumerable.Repeat(long.MaxValue, width + 1).ToArray();
            var used = new bool[width + 1];
            int column = 0;
            do
            {
                token.ThrowIfCancellationRequested();
                used[column] = true;
                int current = owner[column], next = 0;
                long delta = long.MaxValue;
                for (int j = 1; j <= width; j++)
                {
                    if (used[j]) continue;
                    long cost = checked(Cost(current - 1, j - 1) - u[current] - v[j]);
                    if (cost < best[j]) { best[j] = cost; way[j] = column; }
                    if (best[j] < delta) { delta = best[j]; next = j; }
                }
                for (int j = 0; j <= width; j++)
                    if (used[j]) { u[owner[j]] = checked(u[owner[j]] + delta); v[j] = checked(v[j] - delta); }
                    else if (best[j] != long.MaxValue) best[j] = checked(best[j] - delta);
                column = next;
            } while (owner[column] != 0);
            do { int previous = way[column]; owner[column] = owner[previous]; column = previous; } while (column != 0);
        }
        var assigned = Enumerable.Repeat(m, n).ToArray();
        for (int j = 1; j <= width; j++) if (owner[j] != 0) assigned[owner[j] - 1] = Math.Min(j - 1, m);
        var matchedBefore = ImmutableArray.CreateBuilder<Cell>(); var matchedAfter = ImmutableArray.CreateBuilder<Cell>();
        long total = 0, sum = 0;
        for (int i = 0; i < n; i++)
        {
            int j = assigned[i]; sum += j;
            total = checked(total + (j < m ? Score(i, j) : -10000));
            if (j < m) { matchedBefore.Add(rows[i]); matchedAfter.Add(columns[j]); }
        }
        // NumPy's equal-score/equal-column-sum argsort ties are unspecified. Use stable traversal here.
        return new(matchedBefore.ToImmutable(), matchedAfter.ToImmutable(), total, sum);
    }
}
