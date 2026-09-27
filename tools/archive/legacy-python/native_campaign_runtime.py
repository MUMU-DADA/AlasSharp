"""Initialize existing campaign compatibility on native task-owned campaign paths.

No task or map dispatch lives here. The original loader owns Config/MAP and the
original Campaign.run owns entry and battles. Temporary class hooks restore on
every exit; the host serializes native tasks in one session.
"""
from contextlib import contextmanager
from functools import wraps


@contextmanager
def native_campaign_scope(host):
    from module.campaign.run import CampaignRun
    from module.campaign.campaign_base import CampaignBase
    from s3_camera_compat import apply_camera_previous_view_compat

    original_load = CampaignRun.load_campaign
    original_run = CampaignBase.run
    prepared = False

    def prepare():
        nonlocal prepared
        if prepared:
            return
        # Same existing compatibility as the explicit S3 entry; keep the task's
        # own configuration, fleet choices, auto-search and completion semantics.
        apply_camera_previous_view_compat()
        host.apply_fleet_bar_compat()
        host.apply_auto_search_skip_compat()
        host.apply_in_map_threshold_compat()
        host.apply_withdraw_trace_compat()
        prepared = True

    @wraps(original_load)
    def load_campaign(self, *args, **kwargs):
        prepare()
        return original_load(self, *args, **kwargs)

    @wraps(original_run)
    def run(self, *args, **kwargs):
        prepare()
        with host.campaign_button_color_compat():
            return original_run(self, *args, **kwargs)

    CampaignRun.load_campaign = load_campaign
    CampaignBase.run = run
    try:
        yield
    finally:
        CampaignBase.run = original_run
        CampaignRun.load_campaign = original_load
