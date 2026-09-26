using System.Collections;
using System.Text.Json.Nodes;

namespace Alas.Campaign;

/// <summary>上游拾取列表的集合视图；所有读取来自原生状态，支持原语使用的幂等追加。</summary>
internal sealed class NativePickedGrids(ICampaignCallChannel channel, string name) : ISet<string>
{
    private HashSet<string> Snapshot()
    {
        if (channel.Read(name) is not JsonArray entries)
            throw new InvalidDataException($"上游缺少拾取列表 {name}");
        var values = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            if (entry is not JsonValue value || !value.TryGetValue<string>(out var location)
                || location is null || !CampaignLocations.TryParse(location, out _, out _))
                throw new InvalidDataException($"上游拾取列表 {name} 含非法节点");
            values.Add(location);
        }
        return values;
    }

    public bool Add(string item)
    {
        if (!CampaignLocations.TryParse(item, out _, out _))
            throw new InvalidDataException($"非法拾取节点 {item}");
        if (Contains(item)) return false;
        channel.Call($"{name}.append", [JsonValue.Create($"#{item}")], []);
        if (!Contains(item)) throw new InvalidDataException($"上游未确认拾取记账 {name}/{item}");
        return true;
    }

    void ICollection<string>.Add(string item) => Add(item);
    public bool Contains(string item) => Snapshot().Contains(item);
    public int Count => Snapshot().Count;
    public bool IsReadOnly => false;
    public IEnumerator<string> GetEnumerator() => Snapshot().GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    public void CopyTo(string[] array, int index) => Snapshot().CopyTo(array, index);
    public bool IsSubsetOf(IEnumerable<string> other) => Snapshot().IsSubsetOf(other);
    public bool IsSupersetOf(IEnumerable<string> other) => Snapshot().IsSupersetOf(other);
    public bool IsProperSubsetOf(IEnumerable<string> other) => Snapshot().IsProperSubsetOf(other);
    public bool IsProperSupersetOf(IEnumerable<string> other) => Snapshot().IsProperSupersetOf(other);
    public bool Overlaps(IEnumerable<string> other) => Snapshot().Overlaps(other);
    public bool SetEquals(IEnumerable<string> other) => Snapshot().SetEquals(other);
    public bool Remove(string item) => throw new NotSupportedException("拾取记账仅支持原生追加");
    public void Clear() => throw new NotSupportedException("拾取列表由原生 map_data_init 初始化");
    public void UnionWith(IEnumerable<string> other) { foreach (var item in other) Add(item); }
    public void IntersectWith(IEnumerable<string> other) => throw new NotSupportedException("拾取记账仅支持原生追加");
    public void ExceptWith(IEnumerable<string> other) => throw new NotSupportedException("拾取记账仅支持原生追加");
    public void SymmetricExceptWith(IEnumerable<string> other) => throw new NotSupportedException("拾取记账仅支持原生追加");
}
