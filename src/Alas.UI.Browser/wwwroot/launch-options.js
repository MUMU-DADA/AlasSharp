// Read only the explicit isolation switch before the .NET composition root runs.
export function uiLaunchArguments(search) {
    return new URLSearchParams(search).get('ui-only') === '1' ? ['--ui-only'] : [];
}
