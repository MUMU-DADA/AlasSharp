"""Retired hand-written page driver; historical observations stay unchanged.

The original driver inferred alternate assets and selected click coordinates.
Current navigation must run the upstream UI flow through the task queue.
"""


def main():
    print('旧逐段点击验证已退役，不连接设备、不修改历史页面证据。')
    print('只读归档：python tools/diagnostics/report_pages.py')
    print('原生导航回归：python tools/diagnostics/regress_pages.py（会操作游戏）')
    print('指定目标：通过队列 navigate 任务调用上游 UI.ui_ensure。')
    return 2


if __name__ == '__main__':
    raise SystemExit(main())
