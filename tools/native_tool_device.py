"""Keep native ModuleBase task factories inside the current session device.

The host serializes native operations. This temporary type facade preserves
ModuleBase's isinstance branch and delegates only construction; native config
binding, constructors and tool methods remain unchanged.
"""
from contextlib import contextmanager


@contextmanager
def native_tool_device_scope(acquire, device=None):
    import module.base.base as base

    original = base.Device
    snapshots = []

    def remember(current):
        if not any(saved is current for saved, _ in snapshots):
            snapshots.append((current, {
                name: (name in vars(current), vars(current).get(name))
                for name in ('stuck_record_check', 'click_record_check')}))
        return current

    def scoped_acquire(config):
        return remember(acquire(config))

    if device is not None:
        remember(device)

    class SessionDeviceType(type):
        def __instancecheck__(cls, instance):
            return isinstance(instance, original)

        def __subclasscheck__(cls, subclass):
            return issubclass(subclass, original)

        def __call__(cls, config):
            return scoped_acquire(config)

    class SessionDevice(metaclass=SessionDeviceType):
        pass

    base.Device = SessionDevice
    try:
        yield scoped_acquire
    finally:
        base.Device = original
        # DaemonBase replaces instance methods even when passed an existing
        # device. Restore absence as well as custom overrides at every task.
        for current, checks in reversed(snapshots):
            for name, (existed, value) in checks.items():
                if existed:
                    vars(current)[name] = value
                else:
                    vars(current).pop(name, None)
