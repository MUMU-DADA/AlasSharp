"""Keep native ModuleBase tool factories inside the current session device.

The host serializes native operations. This temporary type facade preserves
ModuleBase's isinstance branch and delegates only construction; native config
binding, constructors and tool methods remain unchanged.
"""
from contextlib import contextmanager


@contextmanager
def native_tool_device_scope(acquire):
    import module.base.base as base

    original = base.Device

    class SessionDeviceType(type):
        def __instancecheck__(cls, instance):
            return isinstance(instance, original)

        def __subclasscheck__(cls, subclass):
            return issubclass(subclass, original)

        def __call__(cls, config):
            return acquire(config)

    class SessionDevice(metaclass=SessionDeviceType):
        pass

    base.Device = SessionDevice
    try:
        yield
    finally:
        base.Device = original
