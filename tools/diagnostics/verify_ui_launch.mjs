// Executes the browser entry point with a synthetic runtime; never starts a browser or HTTP request.
import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
const launchSource = await fs.readFile(new URL('../../src/Alas.UI.Browser/wwwroot/launch-options.js', import.meta.url), 'utf8');
const { uiLaunchArguments } = await import('data:text/javascript;base64,' + Buffer.from(launchSource).toString('base64'));

for (const [query, expected] of [
    ['', []], ['?ui-only=1', ['--ui-only']], ['?other=x&ui-only=1', ['--ui-only']],
    ['?ui-only=0', []], ['?ui-only', []], ['?ui-only=false', []], ['?other=ui-only=1', []],
]) {
    assert.deepEqual(uiLaunchArguments(query), expected);
}
const source = await fs.readFile(new URL('../../src/Alas.UI.Browser/wwwroot/main.js', import.meta.url), 'utf8');
// Strip exactly the two real imports and inject their dependencies; preserve the startup logic.
const entry = source.replace(/^import .*;\r?\n/gm, '');
assert.ok(source.includes("from './launch-options.js'"));
const AsyncFunction = Object.getPrototypeOf(async function () {}).constructor;
for (const [search, expected] of [['?ui-only=1', ['--ui-only']], ['', []]]) {
    const calls = [];
    const runtime = {
        getConfig: () => ({ mainAssemblyName: 'Alas.UI.Browser' }),
        runMain: async (...args) => calls.push(args),
    };
    const dotnet = { withDiagnosticTracing: () => ({ create: async () => runtime }) };
    await new AsyncFunction('dotnet', 'uiLaunchArguments', 'globalThis', entry)(dotnet, uiLaunchArguments, { location: { search } });
    assert.deepEqual(calls, [['Alas.UI.Browser', expected]]);
}
console.log('PASS: browser query parsing and actual startup route select UI-only before .NET composition');
