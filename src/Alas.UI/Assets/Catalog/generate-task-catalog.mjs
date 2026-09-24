// 由上游静态目录生成 Avalonia 侧的任务分组目录（侧栏 TaskNav 的子项来源）。
//
// 用法：node generate-task-catalog.mjs <上游仓库根> <输出 C# 文件>
//
// 数据来源：<上游>/module/config/argument/menu.json（分组顺序与任务顺序）
//           <上游>/module/config/i18n/zh-CN.json（Menu.<组>.name 与 Task.<任务>.name）
// 只做搬运与转义，不改写名称、不重排顺序。
import { readFileSync, writeFileSync, mkdirSync } from 'node:fs'
import { dirname } from 'node:path'

const [upstream, output] = process.argv.slice(2)
if (!upstream || !output) throw new Error('用法: node generate-task-catalog.mjs <上游仓库根> <输出 C# 文件>')

const menu = JSON.parse(readFileSync(`${upstream}/module/config/argument/menu.json`, 'utf8'))
const zh = JSON.parse(readFileSync(`${upstream}/module/config/i18n/zh-CN.json`, 'utf8'))
const menuNames = zh.Menu ?? {}
const taskNames = zh.Task ?? {}
const escape = value => String(value).replace(/\\/g, '\\\\').replace(/"/g, '\\"')

const lines = [
  '// 由 Assets/Catalog/generate-task-catalog.mjs 依据上游静态目录生成，请勿手改。',
  '// 来源：上游 AzurPilot f67259dcd 的 module/config/argument/menu.json 与',
  '//       module/config/i18n/zh-CN.json（Menu.<组>.name / Task.<任务>.name）。',
  '',
  'namespace Alas.UI.ViewModels;',
  '',
  '/// <summary>侧栏任务分组的静态目录：分组顺序与任务顺序与上游 menu.json 一致。</summary>',
  'internal static class TaskCatalog',
  '{',
  '    public static IReadOnlyList<(string Group, string GroupLabel, string Icon, string[] Tasks, string[] TaskLabels)> Groups { get; } =',
  '        new (string, string, string, string[], string[])[]',
  '        {',
]

// 分组图标沿用上游 TaskNavTree 的 groupIcons 映射。
const groupIcons = {
  Alas: 'Settings2', Farm: 'Swords', Event: 'Sparkles', EventDaily: 'CalendarDays',
  Reward: 'Gift', DailyMission: 'CalendarDays', Opsi: 'Compass', Island: 'Palmtree',
  FleetManagement: 'Ship', Tool: 'Wrench',
}

for (const [group, entry] of Object.entries(menu)) {
  const groupLabel = menuNames[group]?.name ?? group
  const icon = groupIcons[group] ?? 'Settings2'
  const tasks = Array.isArray(entry.tasks) ? entry.tasks : []
  const labels = tasks.map(task => taskNames[task]?.name ?? task)
  lines.push(`            ("${escape(group)}", "${escape(groupLabel)}", "${escape(icon)}",`)
  lines.push(`                new[] { ${tasks.map(task => `"${escape(task)}"`).join(', ')} },`)
  lines.push(`                new[] { ${labels.map(label => `"${escape(label)}"`).join(', ')} }),`)
}

lines.push('        };', '}', '')
mkdirSync(dirname(output), {recursive: true})
writeFileSync(output, lines.join('\n'))
console.log(JSON.stringify({groups: Object.keys(menu).length, output}, null, 1))
