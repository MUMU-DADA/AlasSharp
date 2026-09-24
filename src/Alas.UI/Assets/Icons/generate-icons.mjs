// 从 lucide-react 生成 Avalonia 用的图标几何资源。
//
// 用法（在包含 lucide-react 的 node_modules 目录下运行）：
//   node generate-icons.mjs <lucide-react 包目录> <输出 Icons.axaml 路径>
//
// 上游 AzurPilot 用 lucide-react 1.45.0（ISC 许可）渲染界面图标；本脚本只提取每个图标的
// 24×24 路径数据并改写为 Avalonia StreamGeometry 迷你语言，不改动形状与坐标。
import { readFileSync, writeFileSync, mkdirSync } from 'node:fs'
import { dirname, join } from 'node:path'
import { pathToFileURL } from 'node:url'

const lucideRoot = process.argv[2]
const output = process.argv[3]
if (!lucideRoot || !output) throw new Error('用法: node generate-icons.mjs <lucide-react 目录> <输出路径>')

// 键是 Avalonia 资源名后缀，值是 lucide 图标名（PascalCase）。
const ICONS = {
  House: 'House', Download: 'Download', Palette: 'Palette', Globe: 'Globe', FileJson: 'FileJson',
  Settings2: 'Settings2', Code2: 'Code2', ExternalLink: 'ExternalLink', LayoutDashboard: 'LayoutDashboard',
  ChartNoAxesCombined: 'ChartNoAxesCombined', Search: 'Search', ChevronRight: 'ChevronRight',
  ChevronDown: 'ChevronDown', Menu: 'Menu', CalendarClock: 'CalendarClock', PanelTop: 'PanelTop',
  GalleryHorizontal: 'GalleryHorizontal', Maximize2: 'Maximize2', Minimize2: 'Minimize2',
  CalendarDays: 'CalendarDays', Swords: 'Swords', Sparkles: 'Sparkles', Gift: 'Gift', Compass: 'Compass',
  Palmtree: 'Palmtree', Ship: 'Ship', Wrench: 'Wrench', Anchor: 'Anchor', Box: 'Box', Terminal: 'Terminal',
  Image: 'Image', Pause: 'Pause', Play: 'Play', ArrowDownUp: 'ArrowDownUp', Trash2: 'Trash2', X: 'X',
  CirclePlay: 'CirclePlay', Square: 'Square', TriangleAlert: 'TriangleAlert', Clock3: 'Clock3',
  ListTodo: 'ListTodo', Hourglass: 'Hourglass', GripVertical: 'GripVertical', Plus: 'Plus', Sun: 'Sun', Moon: 'Moon',
}

const kebab = name => name
  .replace(/([a-z0-9])([A-Z])/g, '$1-$2')
  .replace(/([a-z])([0-9])/g, '$1-$2')
  .toLowerCase()
const round = value => {
  const number = Number(value)
  if (!Number.isFinite(number)) throw new Error(`非数字坐标: ${value}`)
  return Math.round(number * 1000) / 1000
}

/** 把 lucide 的 SVG 子元素数组转成一条路径数据。 */
function toPath(children) {
  const parts = []
  for (const [tag, attributes] of children) {
    const a = attributes ?? {}
    if (tag === 'path') parts.push(a.d)
    else if (tag === 'circle') {
      const cx = round(a.cx), cy = round(a.cy), r = round(a.r)
      parts.push(`M ${cx - r},${cy} a ${r},${r} 0 1,0 ${2 * r},0 a ${r},${r} 0 1,0 ${-2 * r},0`)
    } else if (tag === 'ellipse') {
      const cx = round(a.cx), cy = round(a.cy), rx = round(a.rx), ry = round(a.ry)
      parts.push(`M ${cx - rx},${cy} a ${rx},${ry} 0 1,0 ${2 * rx},0 a ${rx},${ry} 0 1,0 ${-2 * rx},0`)
    } else if (tag === 'rect') {
      const x = round(a.x), y = round(a.y), w = round(a.width), h = round(a.height)
      const rx = a.rx === undefined ? 0 : round(a.rx)
      if (rx > 0) {
        parts.push(`M ${x + rx},${y} H ${x + w - rx} A ${rx},${rx} 0 0 1 ${x + w},${y + rx} V ${y + h - rx}` +
          ` A ${rx},${rx} 0 0 1 ${x + w - rx},${y + h} H ${x + rx} A ${rx},${rx} 0 0 1 ${x},${y + h - rx}` +
          ` V ${y + rx} A ${rx},${rx} 0 0 1 ${x + rx},${y} Z`)
      } else parts.push(`M ${x},${y} H ${x + w} V ${y + h} H ${x} Z`)
    } else if (tag === 'line') {
      parts.push(`M ${round(a.x1)},${round(a.y1)} L ${round(a.x2)},${round(a.y2)}`)
    } else if (tag === 'polyline' || tag === 'polygon') {
      const points = String(a.points).trim().split(/\s+/).map(pair => pair.split(',').map(round))
      parts.push('M ' + points.map(([x, y]) => `${x},${y}`).join(' L ') + (tag === 'polygon' ? ' Z' : ''))
    } else throw new Error(`未支持的 SVG 子元素: ${tag}`)
  }
  return parts.join(' ')
}

/** 少数图标是别名文件（只 re-export 另一个图标），按来源链取到真正的节点数据。 */
async function loadNode(file) {
  const module = await import(pathToFileURL(file).href)
  if (Array.isArray(module.__iconData?.node)) return module.__iconData.node
  const alias = readFileSync(file, 'utf8').match(/export\s*\{\s*default\s*\}\s*from\s*'\.\/([\w-]+)\.mjs'/)
  if (!alias) throw new Error(`无法读取图标节点: ${file}`)
  return loadNode(join(dirname(file), `${alias[1]}.mjs`))
}

const entries = []
for (const [key, name] of Object.entries(ICONS)) {
  const file = join(lucideRoot, 'dist', 'esm', 'icons', `${kebab(name)}.mjs`)
  entries.push([key, toPath(await loadNode(file))])
}

const lines = [
  '<ResourceDictionary xmlns="https://github.com/avaloniaui" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">',
  '  <!-- 由 Assets/Icons/generate-icons.mjs 依据 lucide-react 生成；图标形状与上游 24x24 网格一致。',
  '       许可：ISC License, Copyright (c) 2026 Lucide Icons and Contributors；',
  '       其中源自 Feather 的图标另有 MIT License, Copyright (c) 2013-present Cole Bemis。',
  '       完整许可文本见 Assets/Icons/LICENSE-lucide.txt，来源说明见 Assets/Icons/SOURCE.md。 -->',
]
for (const [key, data] of entries) lines.push(`  <StreamGeometry x:Key="Icon${key}">${data}</StreamGeometry>`)
lines.push('</ResourceDictionary>', '')
mkdirSync(dirname(output), {recursive: true})
writeFileSync(output, lines.join('\n'))
console.log(JSON.stringify({icons: entries.length, output}, null, 1))
