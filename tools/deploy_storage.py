"""Keep upstream deployment reads/writes compatible with the Core config store.

The original reader, defaults, redirects and setters still run. Only the file
transaction changes: preserve template-external fields, merge actual edits and
coordinate with Core's deploy.yaml.lock. No devices or services are started.
"""
from __future__ import annotations

from contextlib import contextmanager
import copy
import os
from pathlib import Path
import threading
import time

_gate = threading.RLock()
_held = threading.local()
_MISSING = object()


@contextmanager
def deploy_transaction(file, timeout=15):
    """Same lock file and OS lock as .NET FileStream(FileShare.None)."""
    path = os.path.abspath(os.fspath(file)) + '.lock'
    with _gate:
        depths = getattr(_held, 'paths', None)
        if depths is None:
            depths = _held.paths = set()
        if path in depths:
            yield
            return
        Path(path).parent.mkdir(parents=True, exist_ok=True)
        deadline = time.monotonic() + timeout
        stream = None
        while stream is None:
            try:
                stream = _open_exclusive(path)
            except OSError as error:
                if time.monotonic() >= deadline or not _is_lock_contention(error):
                    raise
                time.sleep(.02)
        depths.add(path)
        try:
            yield
        finally:
            depths.remove(path)
            stream.close()


def _is_lock_contention(error):
    if os.name == 'nt':
        return getattr(error, 'winerror', None) in (32, 33)
    import errno
    return error.errno in (errno.EACCES, errno.EAGAIN)


def _open_exclusive(path):
    if os.name == 'nt':
        import ctypes
        from ctypes import wintypes
        import msvcrt
        kernel = ctypes.WinDLL('kernel32', use_last_error=True)
        create = kernel.CreateFileW
        create.argtypes = [wintypes.LPCWSTR, wintypes.DWORD, wintypes.DWORD, ctypes.c_void_p,
                           wintypes.DWORD, wintypes.DWORD, wintypes.HANDLE]
        create.restype = wintypes.HANDLE
        handle = create(path, 0x80000000 | 0x40000000, 0, None, 4, 0x80, None)
        if handle == ctypes.c_void_p(-1).value:
            raise ctypes.WinError(ctypes.get_last_error())
        try:
            descriptor = msvcrt.open_osfhandle(handle, os.O_RDWR | os.O_BINARY)
        except BaseException:
            close = kernel.CloseHandle
            close.argtypes = [wintypes.HANDLE]
            close(handle)
            raise
        return os.fdopen(descriptor, 'r+b')
    import fcntl
    stream = open(path, 'a+b')
    try:
        fcntl.flock(stream.fileno(), fcntl.LOCK_EX | fcntl.LOCK_NB)
        return stream
    except BaseException:
        stream.close()
        raise


def render_deploy(values, template):
    """Literal replacements retain upstream scalars without regex backreferences."""
    def scalar(value):
        if value is None:
            return 'null'
        if isinstance(value, bool):
            return 'true' if value else 'false'
        result = str(value)
        if any(c in result for c in '\0\r\n\v\f\x1c\x1d\x1e\x85\u2028\u2029'):
            raise ValueError('部署配置值不能包含换行')
        return result

    rendered = {key: scalar(value) for key, value in values.items()}
    remaining = set(rendered)
    lines = []
    for line in template.replace('\\', '/').rstrip('\r\n').splitlines():
        stripped = line.lstrip(' \t')
        key = stripped.split(':', 1)[0] if ':' in stripped else None
        if not stripped.startswith('#') and key in rendered:
            lines.append(line[:len(line) - len(stripped)] + f'{key}: {rendered[key]}')
            remaining.discard(key)
        else:
            lines.append(line)
    lines.extend(f'{key}: {rendered[key]}' for key in sorted(remaining))
    return '\n'.join(lines) + '\n'


def install_on(config_type, utils):
    """Install once on the upstream base class; existing subclasses inherit it."""
    if config_type.__dict__.get('_alas_deploy_storage'):
        return
    original_read = config_type.read

    def read(self):
        with deploy_transaction(self.file):
            object.__setattr__(self, '_alas_deploy_snapshot', copy.deepcopy(utils.poor_yaml_read(self.file)))
            original_read(self)
            object.__setattr__(self, '_alas_deploy_snapshot', copy.deepcopy(self.config))

    def write(self):
        with deploy_transaction(self.file):
            baseline = getattr(self, '_alas_deploy_snapshot', {})
            current = utils.poor_yaml_read(self.file)
            desired = self.config
            for key in baseline.keys() | desired.keys():
                before, after = baseline.get(key, _MISSING), desired.get(key, _MISSING)
                if before == after:
                    continue
                latest = current.get(key, _MISSING)
                if latest != before and latest != after:
                    raise RuntimeError('部署配置已被其他写入者修改，请重新读取')
                if after is _MISSING:
                    current.pop(key, None)
                else:
                    current[key] = copy.deepcopy(after)
            template_file = getattr(self, 'template_file', utils.DEPLOY_TEMPLATE)
            # Existing text preserves comments and fields added by a newer UI.
            text = utils.atomic_read_text(self.file) or utils.atomic_read_text(template_file)
            output = render_deploy(current, text)
            # Removing a key must not resurrect its original template line.
            removed = baseline.keys() - desired.keys()
            if removed:
                output = '\n'.join(line for line in output.splitlines()
                                   if line.strip().split(':', 1)[0] not in removed) + '\n'
            utils.atomic_write(self.file, output)
            object.__setattr__(self, 'config', current)
            object.__setattr__(self, '_alas_deploy_snapshot', copy.deepcopy(current))

    config_type.read = read
    config_type.write = write
    config_type._alas_deploy_storage = True


def install():
    from deploy.config import DeployConfig
    import deploy.utils as utils
    install_on(DeployConfig, utils)
