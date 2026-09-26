"""Observe only the current native run; never infer a run from a shared daily log."""
import logging
import re
import threading


class CampaignShadowObservation(logging.Handler):
    def __init__(self, instance, limit=10000):
        super().__init__()
        self.instance = instance
        self.owner = threading.get_ident()
        self.limit = limit
        self.document = {'source': 'native_run_logger/1', 'lines': [], 'variants': []}

    def emit(self, record):
        if record.thread != self.owner or self.document.get('error'):
            return
        try:
            message = record.getMessage().strip()
            if not (re.fullmatch(r'BATTLE_\d+', message)
                    or message.startswith('Using function:')
                    or 'No combat executed' in message
                    or 'Campaign end' in message
                    or 'Battle function exhausted' in message):
                return
            if len(self.document['lines']) >= self.limit:
                raise ValueError('shadow observation limit exceeded')
            self.document['lines'].append(message)
            if re.fullmatch(r'BATTLE_\d+', message):
                config = self.instance.config
                clear_all = config.MAP_CLEAR_ALL_THIS_TIME
                poor_map = config.POOR_MAP_DATA
                if type(clear_all) is not bool or type(poor_map) is not bool:
                    raise ValueError('native battle variant flags must be boolean')
                variant = ('clear_all' if clear_all else
                           'battle_with_poor_map_data' if poor_map else 'default_hooks')
                if variant not in self.document['variants']:
                    self.document['variants'].append(variant)
        except Exception as error:
            # Observation must never break a native action or manufacture evidence.
            self.document['error'] = f'{type(error).__name__}: {error}'
