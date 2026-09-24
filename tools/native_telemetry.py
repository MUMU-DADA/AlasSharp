"""Observe native scheduler state and rendered logs without selecting any tasks."""
from collections import deque
from datetime import datetime, timezone
import io
import json
import threading
import time
from time import sleep as _replacement_pause

from rich.console import Console
from module.logger import RichRenderableHandler


def write_snapshot(directory, name, value):
    temporary = directory / (name + '.tmp')
    temporary.write_text(json.dumps(value, ensure_ascii=False, indent=2), encoding='utf-8')
    # Windows readers may briefly deny delete/replace access. Retry only the
    # same already-written snapshot; keep the previous JSON intact until the
    # atomic replacement succeeds. Permanent faults still reach the caller.
    for attempt in range(6):
        try:
            temporary.replace(directory / name)
            return
        except PermissionError as error:
            if getattr(error, 'winerror', None) not in (5, 32, 33) or attempt == 5:
                raise
            _replacement_pause(.02 * (attempt + 1))


def observe_config(config):
    """Read the lists already computed by native get_next(); never recompute them."""
    def tasks(name):
        return [dict(name=item.command, next_run=str(item.next_run))
                for item in getattr(config, name, [])]
    dashboard = getattr(config, 'data', {}).get('Dashboard', {})
    resources = [dict(name=name, value=values.get('Value'), limit=values.get('Limit'),
                      total=values.get('Total'), record=str(values.get('Record') or ''))
                 for name, values in dashboard.items() if isinstance(values, dict) and 'Value' in values]
    return dict(pending=tasks('pending_task'), waiting=tasks('waiting_task'), resources=resources)


class NativeLogCapture(RichRenderableHandler):
    """Use the native Rich renderable path, including logger.print/rule/tracebacks.

    Keep the full JSONL locally and publish a bounded tail at most four times per
    second. Phase boundaries flush the last tail even when logging becomes idle.
    No UI thread calls into the Python host while its scheduler is running.
    """
    def __init__(self, directory, instance):
        self.directory = directory
        self.instance = instance
        self._entries = deque(maxlen=400)
        self._sequence = 0
        self._updated = 0.0
        self._gate = threading.RLock()
        self._record = None
        self._output = (directory / 'native-log.jsonl').open('w', encoding='utf-8', buffering=1)
        console = Console(file=io.StringIO(), width=160, color_system=None)
        super().__init__(func=self._capture, console=console, show_path=False, show_time=False,
                         show_level=False, rich_tracebacks=True, tracebacks_show_locals=False)

    def emit(self, record):
        with self._gate:
            self._record = record
            try:
                super().emit(record)
            finally:
                self._record = None

    def _capture(self, renderable):
        with self._gate:
            if self._output.closed:
                return
            with self.console.capture() as captured:
                self.console.print(renderable)
            message = captured.get().rstrip('\r\n')
            record = self._record
            self._sequence += 1
            entry = dict(id=self._sequence, instance=self.instance,
                         time=datetime.fromtimestamp(record.created, timezone.utc).isoformat()
                         if record else datetime.now(timezone.utc).isoformat(),
                         level=record.levelname if record else 'INFO', scope='upstream', message=message)
            self._output.write(json.dumps(entry, ensure_ascii=False) + '\n')
            # Match the upstream UI's 12,000-character presentation limit; full
            # log content above remains available in the original local artifact.
            self._entries.append(dict(entry, message=message[:12000]))
            if time.monotonic() - self._updated >= .25:
                self.flush_snapshot()

    def flush_snapshot(self):
        with self._gate:
            write_snapshot(self.directory, 'logs.json', dict(instance=self.instance,
                           cursor=self._sequence, entries=list(self._entries)))
            self._updated = time.monotonic()

    def close(self):
        try:
            with self._gate:
                if not self._output.closed:
                    try:
                        self.flush_snapshot()
                    finally:
                        self._output.close()
        finally:
            super().close()
