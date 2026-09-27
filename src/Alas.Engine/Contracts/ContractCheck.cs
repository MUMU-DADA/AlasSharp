using System.Text.Json;
using System.Text.Json.Nodes;

namespace Alas.Engine.Contracts;

/// <summary>Offline fixture runner for the Engine-owned sortie contract.</summary>
public static class ContractCheck
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    public static int Run(string fixture, string? artifactRoot, string? jsonOut)
    {
        if (!File.Exists(fixture)) { Console.Error.WriteLine($"找不到夹具: {fixture}"); return 2; }
        JsonNode root;
        try { root = JsonNode.Parse(File.ReadAllText(fixture)) ?? new JsonObject(); }
        catch (JsonException error) { Console.Error.WriteLine($"夹具不是合法 JSON: {error.Message}"); return 2; }
        var cases = root["cases"]?.AsArray();
        if (cases is null) { Console.Error.WriteLine("夹具缺少 cases 数组"); return 2; }
        var reported = new JsonArray();
        int failed = 0;
        Console.WriteLine($"合同: {SortieContract.Version}    用例: {cases.Count}");
        foreach (var node in cases)
        {
            string name = node?["name"]?.GetValue<string>() ?? "<未命名>";
            JsonNode? document = node?["document"];
            JsonNode? expect = node?["expect"];
            if (document is null) { Console.WriteLine($"  {name,-28} 缺 document"); failed++; continue; }
            SortieResult? result;
            try { result = document.Deserialize<SortieResult>(Json); }
            catch (JsonException error) { Console.WriteLine($"  {name,-28} document 无法反序列化: {error.Message}"); failed++; continue; }
            var violations = SortieContract.Violations(result, artifactRoot);
            var problems = new List<string>();
            if (expect is not null)
            {
                string? expectedOutcome = expect["outcome"]?.GetValue<string>();
                if (expectedOutcome is not null && expectedOutcome != result!.Outcome) problems.Add($"outcome 期望 {expectedOutcome} 实为 {result.Outcome ?? "<无>"}");
                if (expect["cleared"] is JsonNode expectedCleared && expectedCleared.GetValue<bool>() != (result!.Cleared == true))
                    problems.Add($"cleared 期望 {expectedCleared.GetValue<bool>()} 实为 {result.Cleared == true}");
                if (expect["violations"] is JsonArray expectedViolations)
                {
                    var expected = Sorted(expectedViolations.Select(item => item!.GetValue<string>()));
                    var actual = Sorted(violations);
                    if (!expected.SequenceEqual(actual, StringComparer.Ordinal)) problems.Add($"违例不符 期望[{string.Join(",", expected)}] 实为[{string.Join(",", actual)}]");
                }
            }
            bool ok = problems.Count == 0;
            if (!ok) failed++;
            Console.WriteLine($"  {(ok ? "ok  " : "FAIL")} {name,-28} outcome={result!.Outcome ?? "<无>",-14} cleared={result.Cleared == true,-5} 违例={violations.Count}" + (ok ? "" : "  ← " + string.Join("; ", problems)));
            reported.Add(new JsonObject { ["name"] = name, ["outcome"] = result.Outcome is null ? null : JsonValue.Create(result.Outcome), ["cleared"] = result.Cleared == true, ["violations"] = new JsonArray(violations.Select(item => (JsonNode)JsonValue.Create(item)!).ToArray()), ["ok"] = ok });
        }
        if (jsonOut is not null)
        {
            var payload = new JsonObject { ["contract"] = SortieContract.Version, ["cases"] = reported };
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(jsonOut))!);
            File.WriteAllText(jsonOut, payload.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"裁决已写入: {jsonOut}");
        }
        Console.WriteLine();
        Console.WriteLine(failed == 0 ? "结果: OK（全部用例与期望一致）" : $"结果: FAIL（{failed} 例不符）");
        return failed == 0 ? 0 : 1;
    }

    private static List<string> Sorted(IEnumerable<string> values) => values.OrderBy(value => value, StringComparer.Ordinal).ToList();
}
