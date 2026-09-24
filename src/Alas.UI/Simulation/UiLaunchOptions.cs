using Alas.UI.Overview;
using Alas.UI.Theming;
using Alas.UI.ViewModels;

namespace Alas.UI.Simulation;

/// <summary>Choose the data boundary before constructing any platform backend or preference store.</summary>
public sealed record UiLaunchOptions(bool UiOnly = false)
{
    public static UiLaunchOptions Parse(IEnumerable<string> args)
        => new(args.Contains("--ui-only", StringComparer.Ordinal));

    public IAlasUiBackend CreateBackend(Func<IAlasUiBackend> liveFactory)
        => UiOnly ? new SimulatedUiBackend() : liveFactory();

    public IThemeStore CreateThemeStore(Func<IThemeStore> liveFactory)
        => UiOnly ? new MemoryThemeStore() : liveFactory();

    public IResourceSelectionStore CreateResourceStore(Func<IResourceSelectionStore> liveFactory)
        => UiOnly ? new MemoryResourceSelectionStore() : liveFactory();
}
