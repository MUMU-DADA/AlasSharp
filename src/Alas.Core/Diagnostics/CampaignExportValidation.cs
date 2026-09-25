using System.Text.Json;
using Alas.Core;

namespace Alas.Core.Diagnostics;

internal static class CampaignExportValidation
{
    public static bool Check(CampaignIr ir, CampaignIndexEntry entry, string repo)
    {
        var meta = ir.Campaign.AttributesMeta;
        if (meta is null || meta.Scope != "declared" || !meta.Complete || meta.Unresolved.Count != 0
            || meta.MethodAliases is null || meta.Present != entry.CampaignPresent
            || entry.CampaignComplete != (meta.Present && meta.Complete)
            || (meta.Present ? string.IsNullOrWhiteSpace(meta.ClassReference) : meta.ClassReference is not null)
            || (!meta.Present && (ir.Campaign.Attributes.Count != 0 || meta.MethodAliases.Count != 0))
            || !entry.CampaignAttributes.SequenceEqual(ir.Campaign.Attributes.Keys.Order(StringComparer.Ordinal))
            || !entry.CampaignAliases.SequenceEqual(meta.MethodAliases.Keys.Order(StringComparer.Ordinal))
            || ir.Campaign.Attributes.Keys.Intersect(meta.MethodAliases.Keys).Any()
            || !ir.Campaign.Attributes.Keys.Concat(meta.MethodAliases.Keys).ToHashSet().SetEquals(meta.Origins.Keys)
            || !ir.Campaign.Attributes.Keys.ToHashSet().SetEquals(meta.TypedValues.Keys)
            || meta.SourceFiles.Count == 0 || meta.SourceFiles.Distinct().Count() != meta.SourceFiles.Count
            || !meta.SourceFiles.Contains(ir.Source)
            || meta.SourceFiles.Any(p => !MapExportValidation.SourceExists(repo, p)))
            return false;
        foreach (var origin in meta.Origins.Values)
            if (origin.Line < 1 || string.IsNullOrWhiteSpace(origin.Expression)
                || string.IsNullOrWhiteSpace(origin.Class)
                || !meta.SourceFiles.Contains(origin.Module.Replace('.', '/') + ".py"))
                return false;
        try
        {
            foreach (var (name, value) in ir.Campaign.Attributes)
                if (!JsonElement.DeepEquals(value,
                        JsonSerializer.SerializeToElement(MapExportValidation.Plain(meta.TypedValues[name]))))
                    return false;
            foreach (var alias in meta.MethodAliases.Values)
                if (string.IsNullOrWhiteSpace(alias.GetProperty("name").GetString())
                    || !meta.SourceFiles.Contains(alias.GetProperty("module").GetString()!.Replace('.', '/') + ".py"))
                    return false;
        }
        catch (Exception error) when (error is InvalidOperationException or KeyNotFoundException
            or FormatException or OverflowException or ArgumentException or InvalidDataException)
        { return false; }
        return true;
    }
}
