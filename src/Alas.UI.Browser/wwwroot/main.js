import { dotnet } from './_framework/dotnet.js';

try {
    const runtime = await dotnet.withDiagnosticTracing(false).create();
    await runtime.runMain(runtime.getConfig().mainAssemblyName, []);
} catch (error) {
    console.error(error);
    const loading = document.querySelector('.loading');
    if (loading) loading.textContent = '界面载入失败，请刷新后重试。';
}
