"""外驱上游关卡对象：C# 决定"做哪个原语"，这里调**上游自己的方法**去执行。

为什么要有这一层（路线 a）：实测 C# 生产路径上没有任何寻路/原语调用点，而地图点击几何、相机对齐、
进战流程这些机制都在上游（`Fleet.goto` / `Camera.ensure_edge_insight` / `MapOperation.withdraw` …）。
在 C# 里复刻它们就是另建一套逐地图/逐界面适配，项目纪律明确禁止。所以正确的接缝是：

    C# 决定"做哪个原语、对哪个格子"  →  上游自己的方法负责"怎么在设备上做"

纪律（与 `s3_stub_campaign.py` 同一条）：
  * 本模块**只做两件事**——按 `CampaignBase.run()` 的**同一批上游方法、同一顺序**做准备；
    以及 `getattr(instance, op)` 调用上游方法。**不复刻任何地图/战斗/相机逻辑**。
  * 准备阶段的顺序来自上游 `module/campaign/campaign_base.py:119 run()` 的前半段（含 auto search 分支），
    离线回归用替身核对"事件序列与上游 run() 的准备阶段一致"。

用法（宿主侧）：
    driver = CampaignStepDriver(instance)
    driver.prepare()
    driver.call('clear_chosen_enemy', grid, expected='combat')
    driver.state()          # 读回 battle_count / 百分百 / 是否在图中，供 C# 决策
    driver.steps            # 调用轨迹（原语名 + 参数摘要 + 返回值），进工件
"""
from __future__ import annotations

import json

# 与 C# 原语注册表一致的舰队前缀：`fleet_2.clear_boss` ≡ 切到 2 队后调 `clear_boss`
FLEET_PREFIXES = ('fleet_1.', 'fleet_2.', 'fleet_boss.', 'fleet_submarine.')


def strip_fleet_prefix(op: str) -> tuple[str, str | None]:
    """`fleet_2.clear_boss` → `('clear_boss', 'fleet_2')`；没有前缀返回 `(op, None)`。"""
    for prefix in FLEET_PREFIXES:
        if op.startswith(prefix):
            return op[len(prefix):], prefix[:-1]
    return op, None


def _summarize(value, limit: int = 120) -> str:
    """参数/返回值的可读摘要（工件里要能看懂，但不塞进整个地图对象）。"""
    try:
        text = json.dumps(value, ensure_ascii=False, default=str)
    except (TypeError, ValueError):
        text = str(value)
    return text if len(text) <= limit else text[:limit] + '…'


class CampaignStepDriver:
    """把"一个已构造好的上游关卡对象"变成可逐步驱动的对象。"""

    def __init__(self, instance):
        self.instance = instance
        self.steps: list[dict] = []
        self.prepared = False

    # ------------------------------------------------------------------ 准备
    def prepare(self) -> dict:
        """照 `CampaignBase.run()` 的前半段做准备：**同一批上游方法、同一顺序**。

        返回事件摘要，便于离线回归与工件留档。重复调用是幂等的（上游 `run()` 只会准备一次）。
        """
        if self.prepared:
            return {'prepared': 'already'}
        inst = self.instance
        events = []
        inst.emotion.check_reduce(inst._map_battle)
        events.append('emotion.check_reduce')
        inst.ENTRANCE.area = inst.ENTRANCE.button
        inst.enter_map(inst.ENTRANCE, mode=inst.config.Campaign_Mode)
        events.append('enter_map')
        if not inst.map_is_auto_search:
            inst.handle_map_fleet_lock()
            inst.map_init(inst.MAP)
            events.append('handle_map_fleet_lock')
            events.append('map_init')
        else:
            inst.map = inst.MAP
            inst.battle_count = 0
            inst.lv_reset()
            inst.lv_get()
            events.append('auto_search_map_init')
        self.prepared = True
        return {'prepared': events}

    # ------------------------------------------------------------------ 执行
    def call(self, op: str, *args, **kwargs):
        """调用上游自己的方法执行一个原语。舰队前缀只做**记账**，不在这里切队。

        切队在 C# 侧是 `ensure_fleet` 原语（上游 `Fleet.switch_to` 本身就是 `pass`），
        所以这里把前缀记进轨迹，方法名取前缀之后的部分。
        """
        if not self.prepared:
            raise RuntimeError('先调用 prepare()：未进图就调原语与上游语义不符')
        name, fleet = strip_fleet_prefix(op)
        method = getattr(self.instance, name, None)
        if method is None or not callable(method):
            raise AttributeError(f'上游对象没有可调用的原语方法：{op} → {name}')
        try:
            result = method(*args, **kwargs)
        except BaseException as error:      # noqa: BLE001 —— 上游的 CampaignEnd 等信号必须**照常抛出**
            self.steps.append({
                'op': op,
                'method': f'{type(self.instance).__name__}.{name}',
                'fleet_prefix': fleet,
                'args': [_summarize(arg) for arg in args],
                'kwargs': {key: _summarize(value) for key, value in kwargs.items()},
                'return': type(error).__name__,
                'raised': True,
            })
            raise                            # 驱动器不吞上游信号：结束判定交给上层（C# 关卡循环）
        self.steps.append({
            'op': op,
            'method': f'{type(self.instance).__name__}.{name}',
            'fleet_prefix': fleet,
            'args': [_summarize(arg) for arg in args],
            'kwargs': {key: _summarize(value) for key, value in kwargs.items()},
            'return': _summarize(result),
        })
        return result

    # ------------------------------------------------------------------ 状态
    def state(self) -> dict:
        """读回 C# 决策需要的状态（只读属性，不做任何设备动作）。"""
        inst = self.instance
        return {
            'battle_count': int(getattr(inst, 'battle_count', 0) or 0),
            'map_clear_percentage': getattr(inst, 'map_clear_percentage', None),
            'in_stage': bool(getattr(inst, 'is_in_stage', lambda: False)()),
            'fleet_current_index': getattr(inst, 'fleet_current_index', None),
            'steps': len(self.steps),
        }

    # ------------------------------------------------------------------ 收尾
    def finish(self) -> dict:
        """收尾：只报告，不做设备动作（结束判定仍走 sortie-result/1 的合同）。"""
        return {'battle_count': int(getattr(self.instance, 'battle_count', 0) or 0),
                'steps': list(self.steps)}
