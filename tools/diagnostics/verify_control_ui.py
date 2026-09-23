"""Offline checks for the single-file queue control UI.

Usage:
    python tools/diagnostics/verify_control_ui.py
    python tools/diagnostics/verify_control_ui.py --browser

The browser check uses Node Playwright from NODE_PATH and an installed Chromium/Edge.
It intercepts the same-origin API, so no ALAS server or device is started.
"""

from __future__ import annotations

import os
import shutil
import subprocess
import sys
from html.parser import HTMLParser
from pathlib import Path


PAGE = Path(__file__).resolve().parents[1] / "control_ui.html"


class PageParser(HTMLParser):
    def __init__(self) -> None:
        super().__init__()
        self.ids: set[str] = set()
        self.external: list[str] = []
        self.scripts: list[str] = []
        self.styles: list[str] = []
        self._capture: str | None = None

    def handle_starttag(self, tag: str, attrs: list[tuple[str, str | None]]) -> None:
        values = dict(attrs)
        if values.get("id"):
            self.ids.add(values["id"] or "")
        if tag in ("script", "link", "img", "iframe", "object"):
            for attr in ("src", "href", "data"):
                if values.get(attr):
                    self.external.append(f"{tag}.{attr}={values[attr]}")
        if tag in ("script", "style"):
            self._capture = tag
            (self.scripts if tag == "script" else self.styles).append("")

    def handle_endtag(self, tag: str) -> None:
        if tag == self._capture:
            self._capture = None

    def handle_data(self, data: str) -> None:
        if self._capture == "script":
            self.scripts[-1] += data
        elif self._capture == "style":
            self.styles[-1] += data


BROWSER_CHECK = r"""
const fs = require('fs');
const assert = require('assert').strict;
const { chromium } = require('playwright');

(async () => {
  const browser = await chromium.launch({
    headless: true,
    ...(process.env.ALAS_BROWSER_EXECUTABLE
      ? { executablePath: process.env.ALAS_BROWSER_EXECUTABLE }
      : process.platform === 'win32' ? { channel: 'msedge' } : {})
  });
  const page = await browser.newPage({ viewport: { width: 1280, height: 800 } });
  const html = fs.readFileSync(process.argv[1], 'utf8');
  const runPosts = [];
  const stopPosts = [];
  const token = 'test-token';
  const apiState = {
    running: false, mode: null,
    queue: { tasks: [{ id: 'first', kind: 'observe', input: { arbitrary: 'value' } }] },
    report: {
      queue_outcome: 'failed', evidence_complete: false,
      directory: 'test-run', stopped_early: true, stop_reason: 'fixture',
      totals: { tasks_failed: 1, log_errors: 1 },
      items: [{ level: 'task', id: 'first', kind: 'observe', outcome: 'failed',
        error: '<script>window.__injected=1</script>', artifact: 'task-first.json' }],
      findings: [{ code: 'test_finding', detail: '<img src=x onerror="window.__injected=1">' }]
    },
    logs: ['<script>window.__injected=1</script>'], error: null, token
  };
  page.route('http://localhost:32943/', route => route.fulfill({
    status: 200, contentType: 'text/html; charset=utf-8', body: html
  }));
  page.route('http://localhost:32943/api/**', async route => {
    const request = route.request();
    const path = new URL(request.url()).pathname;
    if (path === '/api/state') {
      return route.fulfill({ json: apiState });
    }
    assert.equal(request.headers()['x-alas-token'], token);
    const body = request.postDataJSON();
    if (path === '/api/run') {
      runPosts.push(body);
      apiState.running = true;
      apiState.mode = body.mode;
    } else if (path === '/api/stop') {
      stopPosts.push(body);
      apiState.running = false;
      apiState.mode = null;
    } else throw new Error(`Unexpected API path: ${path}`);
    route.fulfill({ json: {} });
  });
  try {
    await page.goto('http://localhost:32943/');
    await page.waitForFunction(() => document.querySelector('#status').textContent === '空闲');
    const screenshotDir = process.env.ALAS_UI_SCREENSHOT_DIR;
    if (screenshotDir) {
      fs.mkdirSync(screenshotDir, { recursive: true });
      await page.screenshot({ path: require('path').join(screenshotDir, 'desktop.png'), fullPage: true });
    }
    assert.equal(await page.locator('input[name="mode"]:checked').inputValue(), 'dry_run');
    assert.equal(await page.locator('#run').isEnabled(), true);
    assert.equal(await page.locator('#stop').isDisabled(), true);
    assert.equal(await page.locator('#items .item').count(), 1);
    assert.equal(await page.locator('#findings .finding').count(), 1);
    assert.equal(await page.evaluate(() => window.__injected), undefined);
    assert.equal(await page.locator('#findings img').count(), 0);

    await page.locator('#queue-json').fill('{');
    assert.equal(await page.locator('#run').isDisabled(), true);
    assert.match(await page.locator('#queue-error').textContent(), /JSON/);
    await page.locator('#queue-json').fill(JSON.stringify({
      tasks: [{ id: 'edited', kind: 'generic_kind', input: { any_key: ['x'] } }]
    }));
    await page.locator('#serial').fill('MOCK-DEVICE');
    await page.locator('#max-seconds').fill('0');
    await page.locator('#run').click();
    assert.equal(runPosts.length, 0);
    assert.match(await page.locator('#notice').textContent(), /时间上限/);
    await page.locator('#refresh').click();
    await page.waitForFunction(() => !document.querySelector('#refresh').disabled);
    assert.match(await page.locator('#notice').textContent(), /时间上限/);
    await page.locator('#max-seconds').fill('12.5');
    await page.locator('#max-rounds').fill('7');
    await page.locator('#resume').check();
    await page.locator('input[value="read_only"]').check();
    await page.locator('#run').click();
    await page.waitForFunction(() => document.querySelector('#status').textContent === '运行中');
    assert.equal(runPosts.length, 1);
    assert.equal(runPosts[0].mode, 'read_only');
    assert.equal(runPosts[0].serial, 'MOCK-DEVICE');
    assert.equal(runPosts[0].max_seconds, 12.5);
    assert.equal(runPosts[0].max_rounds, 7);
    assert.equal(runPosts[0].resume, true);
    await page.locator('#stop').click();
    await page.waitForFunction(() => document.querySelector('#status').textContent === '空闲');
    await page.locator('input[value="actions"]').check();
    await page.locator('#run').click();
    assert.equal(await page.locator('#actions-dialog').isVisible(), true);
    assert.equal(runPosts.length, 1);
    await page.locator('#cancel-actions').click();
    assert.equal(runPosts.length, 1);
    await page.locator('#run').click();
    await page.locator('#confirm-actions').click();
    await page.waitForFunction(() => document.querySelector('#status').textContent === '运行中');
    assert.equal(runPosts.length, 2);
    assert.equal(runPosts[1].mode, 'actions');
    assert.equal(runPosts[1].queue.tasks[0].input.any_key[0], 'x');
    assert.equal(await page.locator('#run').isDisabled(), true);
    assert.equal(await page.locator('#stop').isEnabled(), true);
    await page.locator('#stop').click();
    await page.waitForFunction(() => document.querySelector('#status').textContent === '空闲');
    assert.deepEqual(stopPosts, [{}, {}]);
    assert.equal(await page.locator('#stop').isDisabled(), true);
    assert.equal(await page.evaluate(() => window.__injected), undefined);

    for (const width of [320, 375, 1280]) {
      await page.setViewportSize({ width, height: 812 });
      const dimensions = await page.evaluate(() => ({
        documentWidth: document.documentElement.scrollWidth,
        viewportWidth: window.innerWidth,
        editorWidth: document.querySelector('#queue-json').getBoundingClientRect().width
      }));
      assert.ok(dimensions.documentWidth <= dimensions.viewportWidth + 1,
        `horizontal overflow at ${width}px: ${JSON.stringify(dimensions)}`);
      assert.ok(dimensions.editorWidth > 250);
      if (screenshotDir && width === 375) {
        await page.screenshot({ path: require('path').join(screenshotDir, 'mobile.png'), fullPage: true });
      }
    }
    console.log('mock API: run/stop/token/actions confirmation/JSON/XSS/responsive OK');
  } finally {
    await browser.close();
  }
})().catch(error => { console.error(error); process.exitCode = 1; });
"""


def verify_static() -> None:
    parser = PageParser()
    parser.feed(PAGE.read_text(encoding="utf-8"))
    assert not parser.external, f"external resources: {parser.external}"
    assert len(parser.scripts) == 1 and len(parser.styles) == 1
    required_ids = {
        "queue-json", "queue-error", "run", "stop", "status", "notice",
        "report-metrics", "items", "findings", "logs", "actions-dialog",
        "confirm-actions", "raw-report",
    }
    assert required_ids <= parser.ids, f"missing ids: {required_ids - parser.ids}"
    script = parser.scripts[0]
    assert not any(unsafe in script for unsafe in ("innerHTML", "outerHTML", "document.write", "insertAdjacentHTML"))
    assert all(value in script for value in ("/api/state", "/api/run", "/api/stop", "X-Alas-Token"))
    assert "showModal()" in script and "parseQueue" in script and "textContent" in script
    assert "@media (max-width: 560px)" in parser.styles[0]
    node = shutil.which("node")
    if node:
        result = subprocess.run([node, "--check"], input=script, text=True, capture_output=True)
        assert result.returncode == 0, result.stderr
    print("static: single-file assets/API/DOM safety/mobile CSS/JavaScript syntax OK")


def verify_browser() -> None:
    node = shutil.which("node")
    if not node:
        raise RuntimeError("Node.js is required for --browser")
    result = subprocess.run([node, "-e", BROWSER_CHECK, str(PAGE)],
                            text=True, capture_output=True, env=os.environ.copy())
    if result.returncode:
        raise RuntimeError(result.stdout + result.stderr)
    print(result.stdout.strip())


def main() -> int:
    try:
        verify_static()
        if "--browser" in sys.argv[1:]:
            verify_browser()
    except (AssertionError, RuntimeError) as error:
        print(f"FAIL: {error}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
