"""Offline native geometry/swipe/camera oracle. No devices or runtime business imports.

View.load and Grid projection run unchanged; only detector polygons and UI mask
application are supplied. Swipe decision tests supply raw CV results. Pixel tests
separately run actual GridPredictor markers/similarity with synthetic images.
"""
import contextlib
import itertools
import json
import os
from pathlib import Path
import random
import sys
from types import SimpleNamespace


def main():
    root, output = Path(sys.argv[1]).resolve(), Path(sys.argv[2]).resolve()
    output.parent.mkdir(parents=True, exist_ok=True)
    os.chdir(output.parent)
    sys.path.insert(0, str(root))
    with open(os.devnull, "w") as quiet, contextlib.redirect_stdout(quiet):
        import cv2
        import numpy as np
        from module.exception import MapDetectionError
        from module.logger import logger
        from module.map.camera import Camera
        from module.map.map_base import CampaignMap
        from module.map_detection.grid import Grid
        from module.map_detection.view import View
        from module.map_detection import grid_predictor as gp
        from module.config.config_manual import ManualConfig
        logger.setLevel("CRITICAL")
        for asset in (gp.TEMPLATE_FLEET_AMMO, gp.TEMPLATE_FLEET_CURRENT):
            asset.file = str((root / asset.file).resolve())
        # Upstream logger chooses its checkout directory during import.
        os.chdir(output.parent)

        class Backend:
            left_edge = right_edge = lower_edge = upper_edge = False
            def __init__(self, item): self.item = item
            def load(self, image): pass
            def generate(self):
                for g in self.item['grids']:
                    yield tuple(g['cell']), np.array(g['corners'], dtype=float)

        def geometry(item):
            config = SimpleNamespace(DETECTING_AREA=item['area'], SCREEN_CENTER=item['screen'],
                                     HOMO_TILE=item['tile'], GRID_IMAGE_A_MULTIPLY=1,
                                     MAP_ENEMY_TEMPLATE=[], MAP_HAS_SIREN=False)
            view = object.__new__(View)
            view.config, view.grid_class, view.backend = config, Grid, Backend(item)
            view._image_clear_ui = lambda image: image
            view.load(np.zeros((720, 1280, 3), dtype=np.uint8))
            return view

        def layout(name, columns=4, rows=3, offset=(0, 0), screen=(320, 270), warp=0, shift=(0, 0)):
            def point(x, y):
                d = 1 + warp * y
                return [(100 + 100 * x + 8 * y + shift[0]) / d, (100 + 85 * y + shift[1]) / d]
            return dict(name=name, area=[0, 0, 1280, 720], screen=list(screen), tile=[100, 100],
                        grids=[dict(cell=[x+offset[0], y+offset[1]], corners=[point(x,y), point(x+1,y), point(x,y+1), point(x+1,y+1)])
                               for y in range(rows) for x in range(columns)])

        layouts = [layout('base'), layout('offset', offset=(-8, 3)), layout('negative-truncate', screen=(88, 120)),
                   layout('outside-left', screen=(-100, 220)), layout('outside-right', screen=(980, 320)),
                   layout('outside-upper', screen=(200, -120)), layout('outside-bottom', screen=(220, 650))]
        hole = layout('sparse-hole', screen=(260, 220)); hole['grids'] = [g for g in hole['grids'] if g['cell'] != [1,1]]
        layouts.append(hole)
        empty = layout('empty'); empty['area'] = [0,0,20,20]; layouts.append(empty)
        excluded=layout('excluded-degenerate'); excluded['grids'].append(dict(cell=[8,8],corners=[[2000,2000]]*4)); layouts.append(excluded)
        for bound in [94,95,95.49,95.51,96]:
            s = layout(f'margin-{bound}', columns=1, rows=1, screen=(150,140), shift=(bound-100, 0))
            s['area'] = [100,100,300,300]; layouts.append(s)
        for x,y in [(100,100),(200,185),(316,270),(88,120)]:
            for epsilon in [-1e-7,0,1e-7]:
                layouts.append(layout(f'boundary-{x}-{y}-{epsilon}',screen=(x+epsilon,y+epsilon)))
        rng = random.Random(238481)
        for i in range(260):
            cols, rows = rng.randrange(2, 8), rng.randrange(2, 6)
            item = layout(f'warp-{i}', cols, rows, (rng.randrange(-8,9),rng.randrange(-8,9)),
                          (rng.uniform(50,850),rng.uniform(50,600)), rng.uniform(-0.025,0.04),
                          (rng.uniform(-20,20),rng.uniform(-20,20)))
            if i % 3 == 0: rng.shuffle(item['grids'])
            layouts.append(item)
        geometry_results = []
        def plain(value):
            if isinstance(value, np.ndarray): return value.tolist()
            if isinstance(value, np.generic): return value.item()
            raise TypeError(type(value).__name__)
        for item in layouts:
            try:
                v = geometry(item)
                result = dict(center=v.center_loca, shape=v.shape, offset=v.center_offset, swipe=v.swipe_base,
                              cells=[dict(cell=g.location, inner=g.inner, outer=g.outer,
                                          projected=g.screen2grid([[321.25,249.75]])[0],
                                          back=g.grid2screen([[0.4,0.6]])[0]) for g in v])
            except MapDetectionError as error:
                result = dict(error=str(error))
            geometry_results.append(dict(sample=item, expected=result))

        class Marker:
            def __init__(self, location, marker, matches=None):
                self.location = tuple(location); self.marker = marker; self.matches = matches or []
            def predict_fleet(self): return self.marker[0]
            def predict_current_fleet(self): return self.marker[1]
            def is_similar_to(self, other): return list(other.location) in self.matches

        def fake_view(center, markers, matches=None):
            v = object.__new__(View); v.center_loca = tuple(center)
            v.grids = {tuple(g['cell']): Marker(g['cell'], g['marker'],
                       [pair[1] for pair in (matches or []) if pair[0] == g['cell']]) for g in markers}
            return v

        swipes = []
        for i in range(420):
            markers = lambda: [dict(cell=[x,y], marker=[rng.random()<.55,rng.random()<.4]) for y in range(2) for x in range(3)]
            old, new = markers(), markers()
            if i < 40:
                for g in old + new: g['marker'] = [False,False]
                old[i % 6]['marker'] = [True, True]; new[(i//6)%6]['marker'] = [True,True]
            pairs = [[a['cell'], b['cell']] for a in old for b in new if rng.random()<.3]
            sample = dict(old=old,new=new,old_center=[rng.randrange(3),rng.randrange(2)],new_center=[rng.randrange(3),rng.randrange(2)],
                          current=i%4<2,sea=i%4>=1,matches=pairs)
            value = View.predict_swipe(fake_view(sample['old_center'],old,pairs),fake_view(sample['new_center'],new),
                                       with_current_fleet=sample['current'],with_sea_grids=sample['sea'])
            swipes.append(dict(sample=sample,expected=value))

        cameras = []
        for edges, pending, predict, predicted in itertools.product(itertools.product([False,True],repeat=4),
                 [None,[0,0],[2,-1]], [False,True], [None,[0,0],[-1,2]]):
            view = SimpleNamespace(left_edge=edges[0],right_edge=edges[1],lower_edge=edges[2],upper_edge=edges[3],
                                   center_loca=(1,1),shape=np.array([2,2]))
            c = object.__new__(Camera); c.camera=(4,3); c.map=SimpleNamespace(shape=(8,6)); c.view=view
            c.config=SimpleNamespace(MAP_SWIPE_PREDICT=predict,MAP_SWIPE_PREDICT_WITH_CURRENT_FLEET=True,
                                     MAP_SWIPE_PREDICT_WITH_SEA_GRIDS=False)
            c.predict=lambda: None; c.show_camera=lambda: None
            c._prev_view=SimpleNamespace(predict_swipe=lambda *args,**kwargs: predicted) if pending is not None else None
            c._prev_swipe=pending
            Camera._update_view_data(c)
            cameras.append(dict(sample=dict(edges=edges,pending=pending,predict=predict,predicted=predicted),
                                expected=dict(position=np.add(c.camera,[1,1]),pending=c._prev_swipe,previous=c._prev_view is not None)))

        # Exercise actual native focus/edge loops, map_swipe, displacement updates and gesture optimization.
        controls = []
        for action, preset, reverse, skip in itertools.product(['focus','edges','center'],[None,[1,-1]],[False,True],[False,True]):
            c = object.__new__(Camera); c.config=ManualConfig(); c.config.MAP_ENSURE_EDGE_INSIGHT_CORNER='bottom-left'
            c.config.MAP_SWIPE_OPTIMIZE=False; c.config.DEVICE_CONTROL_METHOD='adb'; c.config.MAP_SWIPE_PREDICT=False
            c.map=SimpleNamespace(shape=(8,6)); c.camera=(4,3)
            c._prev_view=c._prev_swipe=None; trace=[]; captures=[]
            def view_at(index):
                v=geometry(layout('control',columns=3,rows=3,screen=(240,230)))
                # At most one real swipe before edges are visible; includes initial zero-vector path.
                v.left_edge=index>=1; v.right_edge=False; v.lower_edge=index>=2; v.upper_edge=False
                return v
            c.view=view_at(0)
            def update(**kwargs):
                captures.append(len(captures)+1); c.view=view_at(len(captures))
                Camera._update_view_data(c)
                # focus needs eventually stable geometry centered enough to drop, and no persistent edge clamp.
                if action=='focus': c.view.left_edge=c.view.lower_edge=False
            c.update=update; c.predict=lambda: None; c.show_camera=lambda: None
            c.device=SimpleNamespace(swipe_vector=lambda vector,**kwargs: trace.append(dict(pixels=vector.tolist(),box=kwargs['box'])))
            # focus and center use a centered initial viewport, while edge cases exercise center correction.
            if action in ('focus','center'):
                c.view=geometry(layout('control',columns=3,rows=3,screen=(262,227.5)))
                def update_center(**kwargs):
                    captures.append(len(captures)+1)
                    c.view=geometry(layout('control',columns=3,rows=3,screen=(262,227.5)))
                    Camera._update_view_data(c)
                c.update=update_center
            if action=='focus': value=Camera.focus_to(c,(6,5))
            elif action=='center': value=Camera.focus_to_grid_center(c,0)
            else: value=Camera.ensure_edge_insight(c,preset=preset,reverse=reverse,skip_first_update=skip)
            controls.append(dict(sample=dict(action=action,preset=preset,reverse=reverse,skip=skip),
                                 expected=dict(trace=trace,captures=len(captures),position=np.add(c.camera,[1,1]),record=value)))

        # Full native swipe optimization from map/global flags and current view predictions.
        optimized=[]
        for method in ['adb','minitouch','MaaTouch']:
            v=geometry(layout('optimized',columns=3,rows=3,screen=(262,227.5)))
            patches=[dict(cell=[0,0],state=dict(is_enemy=True)),dict(cell=[1,0],state=dict(is_siren=True)),
                     dict(cell=[2,0],state=dict(is_boss=True)),dict(cell=[0,1],state=dict(is_mystery=True)),
                     dict(cell=[1,1],state=dict(is_fleet=True,is_current_fleet=False)),dict(cell=[2,1],state=dict(is_fleet=True,is_current_fleet=True))]
            for patch in patches:
                for k,value in patch['state'].items(): setattr(v[patch['cell']],k,value)
            mapping=CampaignMap('reference'); mapping.shape='I7'; mapping.map_data='\n'.join([' '.join(['--']*9)]*7)
            globals_=[dict(cell=[3,2],state=dict(is_land=True)),dict(cell=[4,3],state=dict(is_current_fleet=True)),dict(cell=[8,6],state=dict(is_land=True))]
            for patch in globals_:
                for k,value in patch['state'].items(): setattr(mapping[patch['cell']],k,value)
            c=object.__new__(Camera); c.camera=(4,3); c.view=v; c.map=mapping
            c.config=ManualConfig(); c.config.DEVICE_CONTROL_METHOD=method
            trace=[]
            c.device=SimpleNamespace(swipe_vector=lambda vector,**kwargs: trace.append(dict(pixels=vector,preferred=kwargs['whitelist_area'],forbidden=kwargs['blacklist_area'])))
            c.update=lambda **kwargs: None
            Camera.map_swipe(c,(1,-1))
            optimized.append(dict(sample=dict(method=method,patches=patches,globals=globals_),expected=trace[0]))

        settling=[]
        import module.base.timer as timer_module
        original_time=timer_module.time
        for screens,delays in [([(262,227.5),(290,227.5),(263,227.5)],[.05,.05,.05]),
                               ([(290,227.5),(263,227.5)],[.05,.05]),
                               ([(262,227.5)]*4,[.1]*4), ([(262,227.5)],[.4]),
                               ([(262,227.5)]*2,[.35,.01])]:
            now=[10.]; timer_module.time=lambda: now[0]
            c=object.__new__(Camera); c.config=ManualConfig(); c.config.MAP_SWIPE_PREDICT=False
            c.camera=(4,3); c.map=SimpleNamespace(shape=(8,6))
            c.view=geometry(layout('settling',columns=3,rows=3,screen=(262,227.5)))
            c._prev_view=c.view; c._prev_swipe=(1,0); c.predict=lambda: None; c.show_camera=lambda: None
            captures=[0]
            def screenshot():
                index=captures[0]
                if index >= len(delays): raise AssertionError(f'Unsettled native sample: {screens}, {delays}, offset={c.view.center_offset}')
                now[0]+=delays[index]; captures[0]+=1
                c.view=geometry(layout('settling',columns=3,rows=3,screen=screens[index]))
            c.device=SimpleNamespace(screenshot=screenshot,_screenshot_interval=SimpleNamespace(clear=lambda: None))
            c._update_view=lambda: True
            Camera.update(c,wait_swipe=True)
            settling.append(dict(sample=dict(screens=screens,delays=delays),expected=dict(captures=captures[0],position=np.add(c.camera,[1,1]))))
        timer_module.time=original_time

        # Actual CV pair matching and mask gate; mask and synthetic frames are local artifacts.
        nrng=np.random.default_rng(78421)
        images=[nrng.integers(0,256,(720,1280,3),dtype=np.uint8) for _ in range(2)]
        images[1][350:510,200:400]=images[0][350:510,200:400]
        for i,img in enumerate(images): cv2.imwrite(f'pair-{i}.png',cv2.cvtColor(img,cv2.COLOR_RGB2BGR))
        mask=np.full((800,1400),255,dtype=np.uint8); mask[420:480,420:510]=0
        cv2.imwrite('pair-mask.png',mask)
        gp.UI_MASK=SimpleNamespace(image=mask)
        pixels=[]
        config=SimpleNamespace(HOMO_TILE=(100,100),GRID_IMAGE_A_MULTIPLY=1,MAP_ENEMY_TEMPLATE=[],MAP_HAS_SIREN=False)
        for i in range(32):
            x,y=(240,420) if i<8 else (rng.randrange(40,1000),rng.randrange(150,520))
            a=np.array([[x,y],[x+120,y],[x,y+120],[x+120,y+120]],dtype=float)
            dx,dy=(0,0) if i%2==0 else (3,-2)
            b=a+np.array([dx,dy])
            g1=Grid((0,0),images[0],a,config); g2=Grid((0,0),images[1],b,config)
            g1.is_os=g2.is_os=False
            valid=g1.is_in_detecting_area and g2.is_in_detecting_area
            score=float(cv2.minMaxLoc(cv2.matchTemplate(g2._image_similar_full,g1._image_similar_piece,cv2.TM_CCOEFF_NORMED))[1])
            pixels.append(dict(old=a,new=b,score=score,valid=bool(valid),match=bool(g1.is_similar_to(g2)),
                               marker=[bool(g1.predict_fleet()),bool(g1.predict_current_fleet())]))
        output.write_text(json.dumps(dict(geometry=geometry_results,swipes=swipes,cameras=cameras,controls=controls,optimized=optimized,settling=settling,pixels=pixels),
                                     default=plain),encoding='utf-8')


if __name__ == '__main__': main()
