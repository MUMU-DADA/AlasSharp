namespace Alas.Engine.Rules;

/// <summary>Upstream module/config/server.py: to_server, VALID_PACKAGE and VALID_CHANNEL_PACKAGE.</summary>
public static class GameServerRules
{
    // Every other declared channel package is CN; upstream also treats unknown packages as CN.
    // ServerName is a login shard and is intentionally not part of this rule.
    public static GameServer FromPackage(string packageOrServer) => packageOrServer switch
    {
        "en" or "com.YoStarEN.AzurLane" => GameServer.En,
        "jp" or "com.YoStarJP.AzurLane" => GameServer.Jp,
        "tw" or "com.hkmanjuu.azurlane.gp" or "com.hkmanjuu.azurlane.gp.mc" => GameServer.Tw,
        _ => GameServer.Cn,
    };
}
