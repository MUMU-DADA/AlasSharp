# -*- coding: utf-8 -*-
"""S3 可行性探针：能否在宿主里驱动上游的章节 `Campaign` 类？

为什么先探针：S3 要执行的 tier A 那 9 个调用（`battle_default` / `clear_siren` /
`clear_filter_enemy` / `fleet_boss.clear_boss` / `clear_boss` …）是 ALAS 的 **Campaign 方法**，
按铁律不能重写成 C#。所以关键问题是：**能否把上游的 Campaign 类喂起来**（给它 config 与 device），
在宿主里调用它的方法。

这个脚本不做任何游戏操作，只逐步尝试并报告"卡在哪、缺什么"：
  1. 导入 `module.device.pkg_resources`（adbutils 的桩，必须先导入）；
  2. 造配置（照 ALAS 调度器的做法绑任务）；
  3. 导入章节模块、实例化它的 `Campaign`；
  4. 成功后报告：MRO、是否持有我们的 device、MAP 形状、可用方法数。

输出就是 S3 的实现清单（缺什么补什么），而不是猜测。
"""
import io
import os
import sys
import traceback

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.normpath(os.path.join(HERE, '..', '..'))
ENGINE = os.path.normpath(os.path.join(ROOT, '.runtime', 'engine'))

sys.path.insert(0, ENGINE)
os.chdir(ENGINE)

try:
    sys.stdout.reconfigure(encoding='utf-8', errors='replace')
except Exception:
    pass


def step(title):
    print('\n=== %s ===' % title, flush=True)


def main():
    step('1. 导入设备桩（备忘要求：adbutils 的 pkg_resources 靠它顶替）')
    try:
        import module.device.pkg_resources  # noqa: F401
        print('  ok')
    except Exception as e:
        print('  FAIL %s: %s' % (type(e).__name__, e))
        return 1

    # 与 alas_vision._device_engine() 同一个坑：引擎内部有些地方调**裸 adb**
    # （adbutils 的 adb_path / adb push），裸名解析不到就 FileNotFoundError。
    # 实测：不挂 PATH 时，Campaign 实例化会死在 adbutils._safe_connect 里。
    adb_dir = os.path.normpath(os.path.join(
        ROOT, '.runtime', 'venv314', 'Lib', 'site-packages', 'adbutils', 'binaries'))
    if os.path.isdir(adb_dir) and adb_dir not in os.environ.get('PATH', ''):
        os.environ['PATH'] = adb_dir + os.pathsep + os.environ.get('PATH', '')
        print('  已把项目内 adb 目录挂到 PATH：%s' % adb_dir)

    step('2. 造配置（照调度器的做法绑 Campaign 任务）')
    cfg = None
    for how in ('bind', 'plain'):
        try:
            from module.config.config import AzurLaneConfig
            cfg = AzurLaneConfig('alas')
            if how == 'bind':
                cfg.bind('Campaign')
            print('  ok（方式=%s）task=%s' % (how, getattr(cfg, 'task', None)))
            break
        except Exception as e:
            print('  方式=%s FAIL %s: %s' % (how, type(e).__name__, str(e)[:90]))
            cfg = None
    if cfg is None:
        print('  配置造不出来，S3 需要先解决配置绑定')

    step('3. 导入章节模块并实例化 Campaign（不做任何游戏操作）')
    chapter = os.environ.get('CHAPTER', 'campaign.campaign_main.campaign_2_1')
    try:
        import importlib
        mod = importlib.import_module(chapter)
        print('  导入 ok：%s' % chapter)
        print('  MAP.shape=%s  Config 属性数=%d' % (
            getattr(mod.MAP, 'shape', None),
            len([a for a in dir(mod.Config) if not a.startswith('_')])))
    except Exception as e:
        print('  导入 FAIL %s: %s' % (type(e).__name__, str(e)[:120]))
        return 1

    inst = None
    for how in ('config_only', 'config_and_device'):
        try:
            if how == 'config_only':
                inst = mod.Campaign(cfg)
            else:
                from module.device.device import Device
                with cfg.multi_set():
                    cfg.Emulator_Serial = os.environ.get('SERIAL', '127.0.0.1:16384')
                    cfg.Emulator_ScreenshotMethod = 'scrcpy'
                    cfg.Emulator_ControlMethod = 'MaaTouch'
                inst = mod.Campaign(cfg, Device(cfg))
            print('  实例化 ok（方式=%s）' % how)
            break
        except Exception as e:
            inst = None
            print('  方式=%s FAIL %s: %s' % (how, type(e).__name__, str(e)[:110]))
            tb = traceback.format_exc().strip().splitlines()
            for line in tb[-6:]:
                print('     | ' + line.strip()[:110])

    step('4. 结果')
    if inst is None:
        print('  Campaign 实例化未成功 —— 上方的异常链就是 S3 的待补清单')
        return 1
    print('  MRO: %s' % ' -> '.join(c.__name__ for c in type(inst).__mro__[:8]))
    print('  device: %s' % type(getattr(inst, 'device', None)).__name__)
    battles = [m for m in dir(inst) if m.startswith('battle_') or m.startswith('clear_')]
    print('  可调用的战斗方法 %d 个，例如: %s' % (len(battles), ', '.join(sorted(battles)[:8])))
    return 0


if __name__ == '__main__':
    sys.exit(main())
