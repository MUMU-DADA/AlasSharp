import { dotnet } from './_framework/dotnet.js';
import { uiLaunchArguments } from './launch-options.js';

try {
    const runtime = await dotnet.withDiagnosticTracing(false).create();
    const args = uiLaunchArguments(globalThis.location.search);
    await runtime.runMain(runtime.getConfig().mainAssemblyName, args);
} catch (error) {
    console.error(error);
    const loading = document.querySelector('.loading');
    if (loading) loading.textContent = '界面载入失败，请刷新后重试。';
}
