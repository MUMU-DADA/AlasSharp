namespace Alas.Engine.Runtime;

/// <summary>Connects one C# combat execution to the map interaction that triggered it.</summary>
public sealed class MapCombatHandler(Func<CancellationToken, ValueTask<CombatFlowResult>> combat,
    IMapEncounterHandler? next = null,
    Func<CancellationToken, ValueTask>? readFleetStatus = null) : IMapEncounterHandler
{
    public async ValueTask<MapEncounterHandling> HandleAsync(MapEncounterKind encounter, CancellationToken token)
    {
        if (encounter != MapEncounterKind.Combat)
            return next is null ? new(MapEncounterContinuation.Unhandled) : await next.HandleAsync(encounter, token);
        var result = await combat(token);
        if (result.Return == CombatReturn.InMap && readFleetStatus is not null) await readFleetStatus(token);
        return new(result.Return == CombatReturn.InStage ? MapEncounterContinuation.InStage :
            MapEncounterContinuation.InMap, result);
    }
}
