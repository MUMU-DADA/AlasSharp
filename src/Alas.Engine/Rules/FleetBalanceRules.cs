using System.Collections.Immutable;
using Alas.Engine.Imaging;

namespace Alas.Engine.Rules;

/// <summary>HPBalancer's order and input semantics. No observed HP is changed by requesting a drag.</summary>
public static class FleetBalanceRules
{
    public static readonly SourceFile Source = FleetHealthRules.Source;
    public static ImmutableArray<PixelPoint> ScoutPositions { get; } = [new(403, 421), new(625, 369), new(821, 326)];

    public static ImmutableArray<int> ExpectedOrder(IReadOnlyList<double> hp, FleetHealthOptions options)
    {
        _ = options.Weights();
        if (hp.Count != 3 || hp.Any(value => !double.IsFinite(value) || value is < 0 or > 1))
            throw new ArgumentException("Scout balance requires three weighted HP values");
        double threshold = options.HpBalanceThreshold;
        int count = hp.Count(value => value != 0);
        int[] order = [0, 1, 2];
        if (count == 3)
        {
            // numpy's three-element ascending argsort, reversed, includes reverse-index ties.
            var sort = Enumerable.Range(0, 3).OrderByDescending(i => hp[i]).ThenByDescending(i => i).ToArray();
            if (hp[sort[0]] - hp[sort[2]] <= threshold) return [.. order];
            if (hp[sort[1]] - hp[sort[2]] <= threshold / 2)
            { order[0] = sort[0]; order[sort[0]] = 0; }
            else if (hp[sort[0]] - hp[sort[1]] <= threshold / 2)
            { order[1] = sort[2]; order[sort[2]] = 1; }
            else order = [sort[0], sort[2], sort[1]];
        }
        else if (count == 2 && hp[1] - hp[0] > threshold) order = [1, 0, 2];
        return [.. order];
    }

    public static ImmutableArray<(int From, int To)> ExchangeSteps(IReadOnlyList<int> target, bool minitouch)
    {
        if (target.Count != 3 || !target.Order().SequenceEqual(new[] { 0, 1, 2 }))
            throw new ArgumentException("Target must be a permutation of three scout slots");
        var diff = Enumerable.Range(0, 3).Where(i => target[i] != i).ToArray();
        int zero = Enumerable.Range(0, 3).Single(i => target[i] == 0);
        if (diff.Length == 3)
            return minitouch ? [zero == 1 ? (2, 0) : (0, 2)] : [(2, 0), zero == 1 ? (2, 1) : (1, 0)];
        if (diff.Length == 2)
            return minitouch && zero == 2 ? [(0, 2), (1, 0)] : [(diff[0], diff[1])];
        return [];
    }

    public static bool NeedsEmergencyRepair(IReadOnlyList<double> hp, FleetHealthOptions options)
    {
        _ = options.Weights();
        if (hp.Count == 0) return false;
        if (hp.Count != 6 || hp.Any(value => !double.IsFinite(value) || value is < 0 or > 1))
            throw new ArgumentException("Emergency repair requires six weighted HP values");
        double main = hp.Take(3).Max(), scout = hp.Skip(3).Max();
        if (main <= .001 || scout <= .001) return false;
        return hp.Where(value => value > .001).Min() < options.RepairUseSingleThreshold ||
            main < options.RepairUseMultiThreshold || scout < options.RepairUseMultiThreshold;
    }
}
