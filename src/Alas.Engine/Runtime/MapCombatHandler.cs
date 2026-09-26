namespace Alas.Engine.Runtime;

/// <summary>Connects one C# combat execution to the map interaction that triggered it.</summary>
public sealed class MapCombatHandler(Func<CancellationToken, ValueTask<CombatFlowResult>> combat,
    IMapEncounterHandler? next = null) : IMapEncounterHandler
{
    public async ValueTask<MapEncounterHandling> HandleAsync(MapEncounterKind encounter, CancellationToken token)
    {
        if (encounter != MapEncounterKind.Combat)
            return next is null ? new(MapEncounterContinuation.Unhandled) : await next.HandleAsync(encounter, token);
        var result = await combat(token);
        return new(result.Return == CombatReturn.InStage ? MapEncounterContinuation.InStage :
            MapEncounterContinuation.InMap, result);
    }
}
