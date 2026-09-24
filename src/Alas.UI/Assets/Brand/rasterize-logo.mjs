// 把上游 brand SVG 栅格化成 PNG，供 Avalonia 直接显示（不新增 SVG 渲染依赖）。
// 用法：node rasterize-logo.mjs <输入 svg> <输出 png> <像素尺寸>
import { chromium } from 'playwright'
import { readFileSync, mkdirSync } from 'node:fs'
import { dirname } from 'node:path'

const [input, output, sizeArg] = process.argv.slice(2)
const size = Number(sizeArg ?? 64)
const svg = readFileSync(input, 'utf8')
mkdirSync(dirname(output), {recursive: true})

const browser = await chromium.launch({headless: true})
const page = await browser.newPage({viewport: {width: size, height: size}, deviceScaleFactor: 1})
await page.setContent(`<html><body style="margin:0;background:transparent">
  <div style="width:${size}px;height:${size}px;display:grid;place-items:center">${svg.replace(/<svg([^>]*)>/, (match, attributes) =>
    `<svg${attributes.replace(/\s(width|height)="[^"]*"/g, '')} width="${size}" height="${size}" preserveAspectRatio="xMidYMid meet">`)}</div>
</body></html>`)
await page.screenshot({path: output, omitBackground: true})
await browser.close()
console.log(JSON.stringify({input, output, size}))
