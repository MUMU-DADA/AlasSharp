using System.Text;

namespace Alas.Campaign;

/// <summary>
/// 文本优先级过滤器的 C# 移植，对应上游 <c>module/base/filter.py</c> 的 <c>Filter</c>
/// （上游在 <c>module/map/map.py:11</c> 以 <c>Filter(regex=re.compile('^(.*?)$'), attr=('str',))</c> 构造
/// <c>ENEMY_FILTER</c>）。
///
/// 语义（逐条对应上游）：
/// <list type="number">
///   <item><c>load</c>：去掉空白、把各种"像 &gt; 的 Unicode 字符"归一成 <c>&gt;</c>、按 <c>&gt;</c> 切组；</item>
///   <item>每组 token 小写化后作为匹配值（上游 <c>parse_filter</c> 用 <c>^(.*?)$</c> 取整段）；</item>
///   <item><c>apply</c>：**按组顺序**输出（优先级从高到低），组内保持输入顺序，跨组去重。</item>
/// </list>
/// **两处与上游的差异（都有依据，如实写明）**：
/// <list type="bullet">
///   <item>**preset 机制不移植**：上游 <c>Filter(preset=(...))</c> 允许过滤串里出现"内置串"，
///         命中时 <c>apply</c> 会把**字符串本身**放进结果（而不是格子）。已核对本仓库全库用法：
///         `clear_filter_enemy` 共 5 个过滤器串 / 15 个不同 token，**全部**是 <c>{档位}{类型}</c> 形状
///         （如 <c>3L</c>/<c>2M</c>），没有 preset、也没有非法 token，所以这条路径在战役里不会被走到；</item>
///   <item>**非法 token 不记 warning**：上游遇到既不匹配正则也不是 preset 的 token 会
///         <c>logger.warning</c> 并当成"永远匹配不上"的值；这里保留小写 token 去比对，
///         同样匹配不上任何格子的编码，行为等价，只是少了那条日志。</item>
/// </list>
/// </summary>
public sealed class CampaignTextFilter
{
    /// <summary>与上游 <c>&gt;</c> 等价的 Unicode 变体（照抄上游 load 的替换表）。</summary>
    private static readonly char[] GreaterVariants = ['＞', '﹥', '›', '˃', 'ᐳ', '❯'];

    private readonly List<string> _raw = [];
    private readonly List<string> _groups = [];

    public IReadOnlyList<string> Groups => _groups;

    public bool IsEmpty => _groups.Count == 0;

    /// <summary>装载过滤串（如 <c>"3L &gt; 3M &gt; 3E &gt; 3C &gt; 2L &gt; ..."</c>）。</summary>
    public CampaignTextFilter Load(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var normalized = new StringBuilder(text.Length);
        foreach (char ch in text)
        {
            if (ch is ' ' or '\t' or '\r' or '\n') continue;
            normalized.Append(GreaterVariants.Contains(ch) ? '>' : ch);
        }
        _raw.Clear();
        _groups.Clear();
        foreach (string token in normalized.ToString().Split('>'))
        {
            string value = token.Trim().ToLowerInvariant();
            if (value.Length == 0) continue;
            _raw.Add(token);
            _groups.Add(value);
        }
        return this;
    }

    /// <summary>
    /// 按优先级过滤：先取匹配第 1 组的格子，再取匹配第 2 组的，依此类推；跨组去重、组内保持输入顺序。
    /// </summary>
    public IReadOnlyList<CampaignGrid> Apply(IEnumerable<CampaignGrid> grids)
    {
        var pool = grids.ToList();
        var output = new List<CampaignGrid>(pool.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (string group in _groups)
        {
            foreach (var grid in pool)
            {
                if (!string.Equals(grid.FilterKey.ToLowerInvariant(), group, StringComparison.Ordinal)) continue;
                if (seen.Add(grid.Location)) output.Add(grid);
            }
        }
        return output;
    }
}
