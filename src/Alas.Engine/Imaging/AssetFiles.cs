using System.Collections.Concurrent;
using System.Security.Cryptography;
using Alas.Engine.Rules;

namespace Alas.Engine.Imaging;

/// <summary>Loads a pinned asset snapshot. The root is an assets directory, not a Python repository.</summary>
public sealed class AssetFiles
{
    private readonly string _root;
    private readonly ConcurrentDictionary<string, byte[]> _cache = new(StringComparer.Ordinal);
    public AssetFiles(string assetDirectory) => _root = Path.GetFullPath(assetDirectory);
    public async ValueTask<ReadOnlyMemory<byte>> ReadAsync(AssetVariant variant, CancellationToken token = default)
    {
        if (variant.File is null || variant.Sha256 is null) throw new NotSupportedException("Asset has no pinned image file");
        string portable = variant.File.Replace('\\', '/');
        if (portable.StartsWith("./", StringComparison.Ordinal)) portable = portable[2..];
        if (!portable.StartsWith("assets/", StringComparison.Ordinal)) throw new InvalidDataException("Invalid asset root");
        string relative = portable[7..];
        if (relative.Split('/').Any(p => p is ".." or "." or "") || relative.Contains(':', StringComparison.Ordinal))
            throw new InvalidDataException("Invalid asset path");
        string key = portable + ":" + variant.Sha256;
        if (_cache.TryGetValue(key, out var cached)) return cached;
        string file = Path.GetFullPath(Path.Combine(_root, relative));
        if (new FileInfo(file).Length > 16 * 1024 * 1024) throw new InvalidDataException("Asset image exceeds size limit");
        byte[] bytes = await File.ReadAllBytesAsync(file, token);
        if (!Convert.ToHexStringLower(SHA256.HashData(bytes)).Equals(variant.Sha256, StringComparison.Ordinal))
            throw new InvalidDataException($"Asset snapshot hash mismatch: {portable}");
        _cache.TryAdd(key, bytes);
        return bytes;
    }
}
