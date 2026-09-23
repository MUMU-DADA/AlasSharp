# -*- coding: utf-8 -*-
"""量一个素材在**某张真实帧**上到底匹不匹配（模板分 + 颜色判据，一次给全）。

为什么要有它：这一夜我两次从"日志里的一个低分"推出"整类素材不匹配"，
两次都被真机帧复测推翻（`STRATEGY_OPEN` 在真机地图帧上模板分其实是 **1.000**）。
推错的成本很高（会让后来人去改上游素材或放宽闸门），而**量一次的成本极低** ——
所以把它做成一条命令，别再三写临时代码。

用法：
    python tools/diagnostics/probe_asset_match.py <帧.png> <素材 id> [更多素材...]
    python tools/diagnostics/probe_asset_match.py <帧.png> --list handler      # 列某模块带判据的素材

输出每行：模板分（若有 file）、颜色相似度（若有 color）、该帧上两者是否判"在"。
**注意口径**：上游产品路径判控件用 `appear`（颜色，阈值 10）；导航挑边用**模板分**。
两者不一致时（本例就是），要看**这个调用方**用的是哪个，不要拿另一个当证据。
"""

from __future__ import annotations

import os
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
ENGINE = ROOT / '.runtime' / 'engine'
sys.path.insert(0, str(ROOT / 'tools'))

try:
    sys.stdout.reconfigure(encoding='utf-8', errors='replace')
except Exception:
    pass

# 记下**调用方**的工作目录：脚本随后要 chdir 到 engine（宿主需要），
# 相对路径必须按调用方解析 —— 否则 data\x.png 会被解析到 engine\data\ 下（实测踩过）。
CALLER_CWD = Path.cwd()
os.chdir(ENGINE)
import alas_vision as av                                        # noqa: E402
from module.base.utils import color_similarity, get_color       # noqa: E402


def describe(asset_id: str) -> str:
    try:
        button = av._resolve(asset_id)
    except Exception as error:
        return f'{asset_id:34s} 取不到素材: {type(error).__name__}: {error}'
    image = av._state.get('image')
    if image is None:
        return f'{asset_id:34s} 还没载入帧（先给 <帧.png>）'
    has_file = bool(getattr(button, 'file', None))
    expected = av._color_of(button)
    parts = [f'{asset_id:34s}']
    if has_file:
        try:
            score = float(button.match(image, offset=(0, 0), similarity=0.85))
            parts.append(f'模板={score:.3f}{"✓" if score >= 0.85 else "✗"}')
        except Exception as error:
            parts.append(f'模板=取不到({type(error).__name__})')
    else:
        parts.append('模板=（该素材没有 file）')
    if expected:
        try:
            got = get_color(image, button.area)
            tolerance = float(color_similarity(got, expected))
            parts.append(f'颜色={tolerance:.2f}{"✓" if tolerance < 10 else "✗"}')
            parts.append(f'实测色=[{",".join(f"{v:.0f}" for v in got)}]')
        except Exception as error:
            parts.append(f'颜色=取不到({type(error).__name__})')
    else:
        parts.append('颜色=（该素材没有 color）')
    return '  '.join(parts)


def list_module(module: str) -> int:
    prefix = f'{module}/'
    names = sorted(n for n in av._asset_id_map().values() if n.startswith(prefix))
    if not names:
        print(f'（{module} 下没有已导入的素材；宿主是懒加载的，可先在 op 里用过该模块）')
        return 1
    for name in names[:60]:
        print('  ' + name)
    print(f'  共 {len(names)} 个（只列前 60）')
    return 0


def main() -> int:
    args = [a for a in sys.argv[1:]]
    if len(args) < 2:
        print(__doc__)
        return 2
    given = Path(args[0])
    frame = given if given.is_absolute() else (CALLER_CWD / given)
    if not frame.is_file():
        print(f'**失败**：帧不存在 {frame}')
        return 1
    av.op_screenshot_load({'path': str(frame)})
    print(f'帧: {frame.name}')
    if args[1] == '--list':
        return list_module(args[2] if len(args) > 2 else 'handler')
    for asset_id in args[1:]:
        print('  ' + describe(asset_id))
    return 0


if __name__ == '__main__':
    sys.exit(main())
