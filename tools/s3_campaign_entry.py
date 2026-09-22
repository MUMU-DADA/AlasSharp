"""Upstream CampaignRun preparation for a reusable campaign device.

Each sortie starts with fresh stuck/click records. A previous sortie left in
the map must be withdrawn before chapter navigation. CampaignEnd from that
withdrawal is expected cleanup and says nothing about the new sortie's result.
"""


def clear_campaign_device_records(inst):
    inst.device.stuck_record_clear()
    inst.device.click_record_clear()


def prepare_campaign_navigation(inst):
    from module.exception import CampaignEnd

    clear_campaign_device_records(inst)
    if not inst.device.has_cached_image:
        inst.device.screenshot()
    withdrew = False
    if inst.is_in_map():
        try:
            inst.withdraw()
        except CampaignEnd:
            pass
        withdrew = True
    return {'withdrew_previous_sortie': withdrew}
