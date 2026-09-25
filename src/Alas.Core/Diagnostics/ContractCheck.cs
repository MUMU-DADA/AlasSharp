using System.Text.Json;
using System.Text.Json.Nodes;
using Alas.Campaign;

namespace Alas.Core.Diagnostics;

/// <summary>
/// `Alas.Server contract`：把结果合同（sortie-result/1）拿到离线用例上跑一遍。
///
/// 为什么要有这个子命令：结果口径是**两种语言各实现一份**（生产方 Python、
/// 消费方 C#）。只做单侧单测无法发现两侧分叉，所以夹具里既写"期望裁决"，
/// 也由 Python 侧的 `tools/diagnostics/verify_result_contract.py` 把两侧裁决逐例对拍。
///
/// 夹具格式：
/// <code>
/// { "cases": [ { "name": "...", "document": {...结果文档...},
///                "expect": { "outcome": "cleared", "cleared": true, "violations": [] } } ] }
/// </code>
/// `expect` 里的 `violations` 是**码的集合**（顺序不参与比较）。
/// </summary>
internal static class ContractCheck
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public static int Run(string fixture, string? artifactRoot, string? jsonOut)
    {
        if (!File.Exists(fixture))
        {
            Console.Error.WriteLine($"找不到夹具: {fixture}");
            return 2;
        }
        JsonNode root;
        try
        {
            root = JsonNode.Parse(File.ReadAllText(fixture)) ?? new JsonObject();
        }
        catch (JsonException e)
        {
            Console.Error.WriteLine($"夹具不是合法 JSON: {e.Message}");
            return 2;
        }
        var cases = root["cases"]?.AsArray();
        if (cases is null)
        {
            Console.Error.WriteLine("夹具缺少 cases 数组");
            return 2;
        }

        var reported = new JsonArray();
        int failed = 0;
        Console.WriteLine($"合同: {SortieContract.Version}    用例: {cases.Count}");
        foreach (var node in cases)
        {
            string name = node?["name"]?.GetValue<string>() ?? "<未命名>";
            JsonNode? document = node?["document"];
            JsonNode? expect = node?["expect"];
            if (document is null)
            {
                Console.WriteLine($"  {name,-28} 缺 document");
                failed++;
                continue;
            }

            SortieResult? result;
            try
            {
                result = document.Deserialize<SortieResult>(Json);
            }
            catch (JsonException e)
            {
                Console.WriteLine($"  {name,-28} document 无法反序列化: {e.Message}");
                failed++;
                continue;
            }
            var violations = SortieContract.Violations(result!, artifactRoot);

            var problems = new List<string>();
            if (expect is not null)
            {
                string? wantOutcome = expect["outcome"]?.GetValue<string>();
                if (wantOutcome is not null && wantOutcome != result!.Outcome)
                    problems.Add($"outcome 期望 {wantOutcome} 实为 {result.Outcome ?? "<无>"}");
                if (expect["cleared"] is JsonNode wantCleared &&
                    wantCleared.GetValue<bool>() != (result!.Cleared == true))
                    problems.Add($"cleared 期望 {wantCleared.GetValue<bool>()} 实为 {result.Cleared == true}");
                if (expect["violations"] is JsonArray wantViolations)
                {
                    var want = Sorted(wantViolations.Select(v => v!.GetValue<string>()));
                    var got = Sorted(violations);
                    if (!want.SequenceEqual(got, StringComparer.Ordinal))
                        problems.Add($"违例不符 期望[{string.Join(",", want)}] 实为[{string.Join(",", got)}]");
                }
            }
            bool ok = problems.Count == 0;
            if (!ok) failed++;
            Console.WriteLine($"  {(ok ? "ok  " : "FAIL")} {name,-28} " +
                              $"outcome={result!.Outcome ?? "<无>",-14} cleared={result.Cleared == true,-5} " +
                              $"违例={violations.Count}" + (ok ? "" : "  ← " + string.Join("; ", problems)));

            reported.Add(new JsonObject
            {
                ["name"] = name,
                ["outcome"] = result.Outcome is null ? null : JsonValue.Create(result.Outcome),
                ["cleared"] = result.Cleared == true,
                ["violations"] = new JsonArray(violations.Select(v => (JsonNode)JsonValue.Create(v)!).ToArray()),
                ["ok"] = ok,
            });
        }

        if (jsonOut is not null)
        {
            var payload = new JsonObject
            {
                ["contract"] = SortieContract.Version,
                ["cases"] = reported,
            };
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(jsonOut))!);
            File.WriteAllText(jsonOut, payload.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"裁决已写入: {jsonOut}");
        }
        Console.WriteLine();
        Console.WriteLine(failed == 0 ? "结果: OK（全部用例与期望一致）" : $"结果: FAIL（{failed} 例不符）");
        return failed == 0 ? 0 : 1;
    }

    private static List<string> Sorted(IEnumerable<string> items)
        => items.OrderBy(x => x, StringComparer.Ordinal).ToList();
}
