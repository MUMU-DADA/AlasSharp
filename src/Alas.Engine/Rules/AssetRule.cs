using System.Collections.Immutable;
using Alas.Engine.Imaging;

namespace Alas.Engine.Rules;

public enum GameServer { Cn, En, Jp, Tw }
public enum AssetKind { Button, Template }
public readonly record struct Rgb(int R, int G, int B);
public readonly record struct Rectangle(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;
    public int Height => Bottom - Top;
    public PixelArea Area => new(Left, Top, Width, Height);
    public Rectangle Offset(int x, int y) => new(checked(Left + x), checked(Top + y), checked(Right + x), checked(Bottom + y));
}

public sealed record ServerValues<T>(T Cn, T En, T Jp, T Tw)
{
    public T For(GameServer server) => server switch
    {
        GameServer.Cn => Cn, GameServer.En => En, GameServer.Jp => Jp, GameServer.Tw => Tw,
        _ => throw new ArgumentOutOfRangeException(nameof(server))
    };
}

/// <summary>Declared upstream values. Recognition offsets belong to a session, not shared rules.</summary>
public sealed record AssetVariant(Rectangle? Area, Rgb? Color, Rectangle? ClickArea, string? File, string? Sha256);
public sealed record AssetRule(string Id, string Name, AssetKind Kind, SourceFile Source, int SourceLine,
    ServerValues<AssetVariant> Variants)
{
    public AssetVariant For(GameServer server) => Variants.For(server);
}

public static partial class UiAssets
{
    public static ImmutableArray<AssetRule> All { get; } = CreateAll();
    private static partial ImmutableArray<AssetRule> CreateAll();
}
