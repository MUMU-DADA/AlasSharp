"""Adapter for the unmodified upstream scheduler; no task selection logic here."""
from __future__ import annotations

from datetime import datetime, timezone
from pathlib import Path
import time
import traceback

from native_telemetry import NativeLogCapture, observe_config, write_snapshot


class FileStopEvent:
    """The upstream event interface, latched only when a native boundary checks it."""
    def __init__(self, path):
        self.path = path
        self.observed = False

    def is_set(self):
        self.observed = self.observed or self.path.is_file()
        return self.observed


def run_scheduler(args, host):
    instance = args.get('instance')
    out = dict(instance=instance, constructed=False, ran=False, dispatch_count=0, failed_dispatches=0)
    if args.get('allow_actions') is not True or args.get('confirm') != instance:
        return dict(out, decision='denied', reason='调度需要动作授权及所选实例确认')
    from module.api.config_service import validate_name
    try:
        normalized = validate_name(instance)
    except Exception:
        normalized = None
    root = (Path(host.FORK) / 'config').resolve()
    if not isinstance(instance, str) or not instance or normalized != instance:
        return dict(out, decision='denied', reason='实例名无效或不是规范名称')
    config_file = root / (instance + '.json')
    if (not config_file.is_file() or config_file.is_symlink() or config_file.resolve().parent != root):
        return dict(out, decision='denied', reason='找不到有效的配置实例')
    directory_arg = args.get('artifact_directory')
    if not isinstance(directory_arg, str) or not Path(directory_arg).is_absolute():
        return dict(out, decision='denied', reason='调度器需要 Core 分配的绝对工件目录')
    directory = Path(directory_arg)
    # Core creates one new directory per scheduler task, never a user-selected file.
    if not directory.is_dir() or directory.is_symlink():
        return dict(out, decision='denied', reason='调度器工件目录不存在或是链接')
    stop = FileStopEvent(directory / 'stop.request')
    started = time.monotonic()
    device = None
    previous_config = None
    last_success = None
    observation = {}
    capture = None

    def write(name, value):
        write_snapshot(directory, name, value)

    def status(phase, config=None, **extra):
        nonlocal observation
        if config is not None:
            observation = dict(observe_config(config), observed_at=datetime.now(timezone.utc).isoformat())
        write('state.json', dict(instance=instance, phase=phase, dispatch_count=out['dispatch_count'],
                                 failed_dispatches=out['failed_dispatches'],
                                 updated_at=datetime.now(timezone.utc).isoformat(), **observation, **extra))
        if capture is not None:
            capture.flush_snapshot()

    from alas import AzurLaneAutoScript
    from cached_property import cached_property
    from module.logger import logger

    class SchedulerRunner(AzurLaneAutoScript):
        @cached_property
        def config(self):
            # Native loop invalidates this cached property after each task or edit.
            config = AzurLaneAutoScript.config.__get__(self, type(self))
            object.__setattr__(config, 'stop_event', stop)
            return config

        @property
        def device(self):
            nonlocal device, previous_config
            if not host._DEVICE_ARGS.get('serial'):
                raise RuntimeError('调度器需要已配置的实例设备串号')
            current = host._device_engine(config=self.config)
            if device is None:
                device = current
                previous_config = current.config
            current.config = self.config
            return current

        def wait_until(self, future):
            status('waiting', config=self.config, next_run=str(future), task=self.config.task.command)
            return super().wait_until(future)

        def run(self, command, skip_first_screenshot=False):
            nonlocal last_success
            out['dispatch_count'] += 1
            number = out['dispatch_count']
            record = dict(sequence=number, instance=instance, method=command,
                          scheduler_command=self.config.task.command,
                          started_at=datetime.now(timezone.utc).isoformat(),
                          native_success=False, returned=False)
            name = f'dispatch-{number:06d}.json'
            write(name, record)
            status('running', config=self.config, method=command, task=self.config.task.command,
                   next_run=str(self.config.task.next_run))
            failure = host._LoggedNativeFailure()
            logger.addHandler(failure)
            try:
                if command.startswith('opsi_'):
                    host.apply_os_combat_reentry_compat()
                with host.native_task_runtime():
                    result = super().run(command, skip_first_screenshot=skip_first_screenshot)
                record.update(native_success=result is True, returned=True)
                last_success = result is True
                return result
            except (Exception, SystemExit) as error:
                last_success = False
                record.update(error=f'{type(error).__name__}: {error}',
                              traceback_tail=traceback.format_exc().strip().splitlines()[-8:])
                raise
            finally:
                logger.removeHandler(failure)
                failure.close()
                if failure.traceback_tail:
                    record['traceback_tail'] = failure.traceback_tail
                    record['error'] = f'上游执行失败: {failure.kind}'
                if failure.error_directory is not None and failure.error_directory.is_dir():
                    error_dir = failure.error_directory
                    record['failure_frames'] = [p.relative_to(host.FORK).as_posix()
                                                 for p in sorted(error_dir.glob('*.png')) if p.is_file()]
                    record['native_error_dir'] = error_dir.relative_to(host.FORK).as_posix()
                    if (error_dir / 'log.txt').is_file():
                        record['native_error_log'] = (error_dir / 'log.txt').relative_to(host.FORK).as_posix()
                if not record['native_success']:
                    out['failed_dispatches'] += 1
                record['finished_at'] = datetime.now(timezone.utc).isoformat()
                write(name, record)
                status('selecting', config=self.config)

    try:
        capture = NativeLogCapture(directory, instance)
        logger.addHandler(capture)
        status('starting')
        runner = SchedulerRunner(config_name=instance)
        runner.stop_event = stop
        out.update(constructed=True, ran=True)
        runner.loop()
        if stop.observed and last_success is not False:
            out['decision'] = 'stopped'
        else:
            out.update(decision='failed', error='上游调度循环结束，未确认正常边界停止')
    except SystemExit as error:
        if error.code in (None, 0) and stop.observed and last_success is not False:
            out['decision'] = 'stopped'
        else:
            out.update(decision='error', error=f'SystemExit: {error.code}',
                       traceback_tail=traceback.format_exc().strip().splitlines()[-8:])
        out['exit_code'] = None if error.code is None else str(error.code)
    except Exception as error:
        out.update(decision='error', error=f'{type(error).__name__}: {error}',
                   traceback_tail=traceback.format_exc().strip().splitlines()[-8:])
    finally:
        if device is not None:
            try:
                device.config = previous_config
            except Exception as error:
                out.update(decision='error', error=f'恢复设备配置失败: {type(error).__name__}: {error}',
                           traceback_tail=traceback.format_exc().strip().splitlines()[-8:])
        out.update(stop_observed=stop.observed, elapsed_s=round(time.monotonic() - started, 3))
        def finalization_error(phase, error):
            message = f'{phase}: {type(error).__name__}: {error}'
            out.setdefault('artifact_errors', []).append(message)
            previous = out.get('error')
            out.update(decision='error', error=f'{previous}; {message}' if previous else message,
                       traceback_tail=traceback.format_exc().strip().splitlines()[-8:])

        try:
            status(out.get('decision', 'error'), error=out.get('error'), stop_observed=stop.observed)
        except Exception as error:
            finalization_error('保存调度结束状态失败', error)
        finally:
            if capture is not None:
                logger.removeHandler(capture)
                try:
                    capture.close()
                except Exception as error:
                    finalization_error('关闭原生日志失败', error)
        if out.get('artifact_errors'):
            # The log writer may have failed after state.json was replaced.
            # Publish the failure without re-entering that broken log writer.
            try:
                capture = None
                status('error', error=out['error'], stop_observed=stop.observed)
            except Exception as error:
                finalization_error('保存工件失败状态失败', error)
    return out
