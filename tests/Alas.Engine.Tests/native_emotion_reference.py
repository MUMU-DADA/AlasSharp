"""Offline emotion oracle: native code, simulated time and in-memory configuration only."""
import contextlib
import hashlib
import json
import os
from datetime import datetime, timedelta, timezone
from pathlib import Path
import random
import sys
from types import SimpleNamespace


def main():
    root, output = [Path(arg).resolve() for arg in sys.argv[1:]]
    os.chdir(output.parent)
    sys.path.insert(0, str(root))
    with open(os.devnull, 'w') as quiet, contextlib.redirect_stdout(quiet):
        import module.combat.emotion as native
        from module.config.config import AzurLaneConfig
        from module.exception import ScriptEnd, RequestHumanTakeover
        from module.logger import logger
        logger.setLevel('CRITICAL')

        # A timezone-aware test clock also supports pre-epoch instants on Windows.
        # Only time transport is replaced; native floor division remains untouched.
        def instant(seconds):
            return datetime(1970, 1, 1, tzinfo=timezone.utc) + timedelta(seconds=float(seconds))

        class Clock:
            time = instant(0)
            @staticmethod
            def now():
                return Clock.time
            fromtimestamp = staticmethod(instant)

        native.datetime = Clock
        controls = list(native.DIC_LIMIT)
        recoveries = list(native.DIC_RECOVER)
        orders = ['fleet1_mob_fleet2_boss', 'fleet1_boss_fleet2_mob',
                  'fleet1_all_fleet2_standby', 'fleet1_standby_fleet2_all']

        def config(control='prevent_green_face', recovery='not_in_dormitory', oath=False, value=119, record=0):
            fields = dict(Emotion_Mode='calculate', Fleet_FleetOrder=orders[0], Campaign_Use2xBook=False)
            for fleet in [1, 2]:
                for key, val in dict(Control=control, Recover=recovery, Oath=oath, Value=value,
                                     Record=instant(record)).items():
                    fields[f'Emotion_Fleet{fleet}{key}'] = val
            return SimpleNamespace(**fields)

        arithmetic = []
        rng = random.Random(46903)
        for control in controls:
            for recovery in recoveries:
                for oath in [False, True]:
                    for index in range(72):
                        now = [-721.9, -360.1, -.5, 0, .5, 359.9, 360, 361, 719.9, 720, 1700000000.5][index % 11]
                        record = now + [0, -359, -360, -361, 60, 3600, -72000][index % 7]
                        value = [-200, -4, 0, 1, 30, 40, 119, 120, 149, 150, 400, rng.randrange(-50, 180)][index % 12]
                        expected = [-4, -2, 0, 2, 4, 29, 30, 100][index % 8]
                        Clock.time = instant(now)
                        fleet = native.FleetEmotion(config(control, recovery, oath, value, record), 1)
                        fleet.update()
                        try:
                            recovered = fleet.get_recovered(expected).timestamp()
                        except RequestHumanTakeover:
                            recovered = None
                        arithmetic.append(dict(control=control, recovery=recovery, oath=oath, now=now,
                                               record=record, value=value, expected=expected, current=fleet.current,
                                               recovered=recovered, speed=fleet.speed, maximum=fleet.max, limit=fleet.limit))

        traces = []
        for order in orders:
            for double in [False, True]:
                for requested in [False, True]:
                    for battles in [0, 1, 4, 8]:
                        Clock.time = instant(1700000040)
                        cfg = config(value=40, record=Clock.time.timestamp())
                        cfg.Emotion_Fleet2Value = 44
                        cfg.Fleet_FleetOrder, cfg.Campaign_Use2xBook = order, requested
                        events = []

                        def record_values(**values):
                            for key, value in values.items():
                                setattr(cfg, key, int(value))
                                setattr(cfg, key.replace('Value', 'Record'), Clock.time.replace(microsecond=0))
                            events.append(dict(operation='record', at=Clock.time.timestamp(),
                                               values=[cfg.Emotion_Fleet1Value, cfg.Emotion_Fleet2Value]))

                        def task_delay(target):
                            events.append(dict(operation='delay', at=Clock.time.timestamp(), target=target.timestamp()))

                        def sleep(seconds):
                            events.append(dict(operation='sleep', at=Clock.time.timestamp(), seconds=seconds))
                            Clock.time += timedelta(seconds=seconds)

                        cfg.set_record, cfg.task_delay = record_values, task_delay
                        native.sleep = sleep
                        emotion = native.Emotion(cfg)
                        emotion.map_is_2x_book = double
                        try:
                            emotion.check_reduce(battles)
                            deferred = False
                        except ScriptEnd:
                            deferred = True
                        entry_events = list(events)
                        events.clear()
                        emotion.wait(1)
                        emotion.reduce(1)
                        emotion.wait(2)
                        emotion.reduce(2)
                        traces.append(dict(order=order, double=double, requested=requested, battles=battles,
                                           deferred=deferred, entry=entry_events, combat=events,
                                           totalReduced=emotion.total_reduced))

        # Native bind chooses field owners; values are deliberately split across groups.
        groups = ['General', 'Alas', 'OpsiGeneral', 'TaskBalancer', 'EventGeneral',
                  'Main', 'OpsiExplore', 'EventA', 'RaidA', 'CoalitionA', 'MaritimeEscort', 'GemsFarming']
        binding_data = {name: {'Emotion': {f'Fleet{i}Value': 40 + index + i for i in [1, 2]}}
                        for index, name in enumerate(groups)}
        binding_data['General']['Emotion'].pop('Fleet2Value')
        bindings = []
        for task in groups[5:]:
            for removed in [[], ['General'], ['General', 'Alas'], ['General', 'Alas', 'TaskBalancer']]:
                data = {key: val for key, val in binding_data.items() if key not in removed}
                cfg = object.__new__(AzurLaneConfig)
                for key, value in dict(data=data, bound={}, overridden={}).items():
                    object.__setattr__(cfg, key, value)
                cfg.bind(task)
                bindings.append(dict(task=task, data=data, values=[cfg.Emotion_Fleet1Value, cfg.Emotion_Fleet2Value],
                                     owners=[cfg.bound[f'Emotion_Fleet{i}Value'].split('.')[0] for i in [1, 2]]))

    result = dict(source=hashlib.sha256((root / 'module/combat/emotion.py').read_bytes()).hexdigest(),
                  configSource=hashlib.sha256((root / 'module/config/config.py').read_bytes()).hexdigest(),
                  arithmetic=arithmetic, traces=traces, bindings=bindings)
    output.write_text(json.dumps(result, ensure_ascii=False), encoding='utf-8')


if __name__ == '__main__':
    main()
