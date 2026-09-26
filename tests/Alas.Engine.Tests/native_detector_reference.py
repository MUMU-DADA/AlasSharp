"""Actual upstream detector oracle; optional saved frames stay in ignored artifacts.

No device interaction. Native image operations, fitting, View.load, and swipe
placement are called directly. Input names are replaced by ordinal filenames.
"""
import contextlib
import hashlib
import json
import os
from pathlib import Path
import random
import sys
from types import SimpleNamespace


def main():
    root, output = Path(sys.argv[1]).resolve(), Path(sys.argv[2]).resolve()
    inputs = [Path(p).resolve() for p in sys.argv[3:]]
    output.parent.mkdir(parents=True, exist_ok=True)
    os.chdir(root)
    sys.path.insert(0, str(root))
    with open(os.devnull, 'w') as quiet, contextlib.redirect_stdout(quiet):
        import cv2
        import numpy as np
        # NumPy 2 rejects generators in vstack; materialize the exact native stream.
        # This changes container plumbing only, not line fitting or detection decisions.
        original_vstack=np.vstack
        np.vstack=lambda values,*args,**kwargs: original_vstack(list(values) if hasattr(values,'__next__') else values,*args,**kwargs)
        from module.config.config_manual import ManualConfig
        from module.map_detection.homography import Homography
        from module.map_detection.perspective import Perspective
        from module.map_detection.view import View
        from module.map_detection import utils_assets as assets
        from module.base import utils
        from module.logger import logger
        from module.exception import MapDetectionError
        from campaign.campaign_main.campaign_1_1 import Config as ChapterOne
        logger.setLevel('CRITICAL')
        for name in ['ui_mask','ui_mask_os','ui_mask_stroke','ui_mask_in_map','ui_mask_os_in_map','tile_center_image','tile_corner_image_list']:
            getattr(assets.ASSETS,name)
        os.chdir(output.parent)
        def plain(v):
            if isinstance(v,np.ndarray): return v.tolist()
            if isinstance(v,np.generic): return v.item()
            raise TypeError(type(v).__name__)
        def config(chapter=False,backend='homography'):
            c=SimpleNamespace(**{k:getattr(ManualConfig,k) for k in dir(ManualConfig) if k.isupper()})
            if chapter:
                for k in dir(ChapterOne):
                    if k.isupper(): setattr(c,k,getattr(ChapterOne,k))
            c.Scheduler_Command='Campaign';c.DETECTION_BACKEND=backend
            return c
        rng=random.Random(237196)
        calibrations=[]
        for index in range(90):
            left,top=rng.uniform(100,180),rng.uniform(90,180)
            width,height=rng.uniform(700,1000),rng.uniform(300,430)
            corners=[[left,top],[left+width,top],[left-rng.uniform(20,90),top+height],[left+width+rng.uniform(20,90),top+height]]
            size=[rng.randrange(5,10),rng.randrange(3,7)];overflow=index%2==0
            h=Homography(config());h.find_homography(size,corners,overflow)
            h.homo_loca=np.array([rng.uniform(0,140),rng.uniform(0,140)])
            h.map_inner=np.array([h.homo_size[0]/2,h.homo_size[1]/2])
            raw=[]
            for i in range(-2,12):
                raw.extend([[i*140+h.homo_loca[0],0.],[i*140+h.homo_loca[1],np.pi/2]])
            raw.extend([[320,.15],[420,1.4]])
            # Native edge classification without drawing noise into image input.
            original=cv2.HoughLines
            cv2.HoughLines=lambda *args,**kwargs: np.array(raw,dtype=np.float32)[:,None,:]
            try: h.detect_edges(np.zeros((2,2),dtype=np.uint8))
            finally: cv2.HoughLines=original
            calibrations.append(dict(sample=dict(size=size,corners=corners,overflow=overflow,location=h.homo_loca,
                                                   interior=h.map_inner,lines=np.array(raw,dtype=np.float32)),
                                     expected=dict(size=h.homo_size,matrix=h.homo_data,edges=[bool(h.left_edge),bool(h.right_edge),bool(h.lower_edge),bool(h.upper_edge)],
                                                   counts=h._map_edge_count,grids=[dict(cell=l,corners=p) for l,p in h.generate()])))
        # Synthetic perspective lattice, plus optional real captured frames supplied by caller.
        synthetic=np.full((720,1280,3),(98,151,192),dtype=np.uint8)
        vanish=(640,-1800)
        for y in range(120,700,80):cv2.line(synthetic,(0,y),(1279,y),(30,30,30),3)
        for x in range(-140,1500,129):
            k=(720-vanish[1])/(360-vanish[1]);bottom=(round(vanish[0]+(x-vanish[0])*k),720)
            k=(0-vanish[1])/(360-vanish[1]);top=(round(vanish[0]+(x-vanish[0])*k),0)
            cv2.line(synthetic,top,bottom,(30,30,30),3)
        images=[('synthetic',synthetic),('blank',np.zeros((720,1280,3),dtype=np.uint8))]
        for path in inputs:
            image=cv2.imread(str(path))
            if image is None:raise ValueError('fixture_decode')
            images.append(('saved',cv2.cvtColor(image,cv2.COLOR_BGR2RGB)))
        detections=[];features=[];warps=[]
        for index,(kind,image) in enumerate(images):
            name=f'frame-{index}.png';cv2.imwrite(name,cv2.cvtColor(image,cv2.COLOR_RGB2BGR))
            for chapter in [False,True]:
                c=config(chapter)
                masked=cv2.copyTo(image,assets.ASSETS.ui_mask_in_map)
                cv2.imwrite(f'masked-{index}.png',cv2.cvtColor(masked,cv2.COLOR_RGB2BGR))
                p=Perspective(c);prepared=p.load_image(masked)
                sets=[]
                for horizontal,edge in [(True,False),(False,False),(True,True),(False,True)]:
                    peaks=p.find_peaks(prepared,horizontal,c.EDGE_LINES_FIND_PEAKS_PARAMETERS if edge else c.INTERNAL_LINES_FIND_PEAKS_PARAMETERS,
                                       pad=(c.DETECTING_AREA[2]-c.DETECTING_AREA[0] if horizontal else c.DETECTING_AREA[3]-c.DETECTING_AREA[1]) if edge else 0,
                                       mask=assets.ASSETS.ui_mask_stroke)
                    lines=cv2.HoughLines(peaks,1,np.pi/180,c.EDGE_LINES_HOUGHLINES_THRESHOLD if edge else c.INTERNAL_LINES_HOUGHLINES_THRESHOLD)
                    sets.append([] if lines is None else lines[:,0,:])
                try:
                    p.load(masked)
                    result=dict(vanish=p.vanish_point,distant=p.distant_point,grids=[dict(cell=l,corners=pts) for l,pts in p.generate()],
                                edges=[bool(p.left_edge),bool(p.right_edge),bool(p.lower_edge),bool(p.upper_edge)])
                except MapDetectionError as error:result=dict(error=str(error))
                features.append(dict(image=name,chapter=chapter,lines=sets,expected=result))
                for backend,os_mode in [('perspective',False),('homography',False),('perspective',True),('homography',True)]:
                    c=config(chapter,backend)
                    if os_mode:c.Scheduler_Command='OpsiExplore'
                    v=View(c,mode='os' if os_mode else 'main')
                    try:
                        v.load(image)
                        expected=dict(center=v.center_loca,shape=v.shape,offset=v.center_offset,swipe=v.swipe_base,
                                      edges=[bool(v.left_edge),bool(v.right_edge),bool(v.lower_edge),bool(v.upper_edge)],
                                      grids=[dict(cell=g.location,corners=g.corner) for g in v])
                    except MapDetectionError as error:expected=dict(error=str(error))
                    detections.append(dict(image=name,kind=kind,sha256=hashlib.sha256(Path(name).read_bytes()).hexdigest(),chapter=chapter,backend=backend,os=os_mode,expected=expected))
                    if backend=='homography' and not chapter and not os_mode and 'error' not in expected:
                        h=v.backend
                        gray=utils.rgb2gray(utils.crop(v.image,c.DETECTING_AREA,copy=False))
                        transformed=cv2.warpPerspective(gray,h.homo_data,h.homo_size)
                        edge=cv2.Canny(transformed,*c.HOMO_CANNY_THRESHOLD)
                        edge=cv2.bitwise_and(edge,h.ui_mask_homo_stroke)
                        kernel=cv2.getStructuringElement(cv2.MORPH_ELLIPSE,(5,5))
                        edge=cv2.morphologyEx(edge,cv2.MORPH_CLOSE,kernel)
                        filename=f'warped-{index}.png';cv2.imwrite(filename,edge)
                        line_image=cv2.bitwise_and(cv2.dilate(edge,kernel),cv2.inRange(transformed,*c.HOMO_EDGE_COLOR_RANGE))
                        line_image=cv2.bitwise_and(line_image,h.ui_mask_homo_stroke)
                        lines=cv2.HoughLines(line_image,1,np.pi/180,c.HOMO_EDGE_HOUGHLINES_THRESHOLD)
                        correlations=[]
                        for template in [assets.ASSETS.tile_center_image,*assets.ASSETS.tile_corner_image_list]:
                            values=cv2.matchTemplate(edge,template,cv2.TM_CCOEFF_NORMED)
                            _,maximum,_,location=cv2.minMaxLoc(values)
                            correlations.append(dict(maximum=maximum,location=location,points=np.argwhere(values>.8)[:,::-1]))
                        boxes=[]
                        for size in [5,10,15,20,25]:
                            k=cv2.getStructuringElement(cv2.MORPH_ELLIPSE,(size,size))
                            contours,_=cv2.findContours(cv2.morphologyEx(edge,cv2.MORPH_CLOSE,k),cv2.RETR_TREE,cv2.CHAIN_APPROX_SIMPLE)
                            boxes.append([cv2.boundingRect(cv2.convexHull(cont).astype(np.float32)) for cont in contours])
                        # Read full precision corners from Perspective to avoid storage's logging rounding.
                        p=Perspective(c);p.load(v.image)
                        corners=p.horizontal[0].add(p.horizontal[-1]).cross(p.vertical[0].add(p.vertical[-1])).points
                        warps.append(dict(image=name,warped=filename,storage=[[len(p.vertical)-1,len(p.horizontal)-1],corners],
                                          lines=[] if lines is None else lines[:,0,:],correlations=correlations,rectangles=boxes))
        # Drive the actual native fallback chain with controlled CV measurements.
        # Only the measurement functions are replaced; thresholds, offsets, fitting,
        # contour selection and View grid construction still run upstream code.
        fallbacks=[]
        storage=((8,4),[(123,55),(1243,55),(123,615),(1243,615)])
        candidates=[[348,208],[488,208],[348,348],[488,348]]
        rectangles=[[20+140*(i%4),30+140*(i//4),140,140] for i in range(11)]
        def corr(maximum, points=None):
            return dict(maximum=maximum,location=[488,348],points=candidates if points is None else points)
        cases=[
            ('center-good',[corr(.9001)],None),
            ('center-good-equal',[corr(.9)],None),
            ('center-multi',[corr(.85)],None),
            ('center-equal-corner',[corr(.8,[])]+[corr(.81,[[400+i*31,300+i*19]]) for i in range(4)],None),
            ('corner-equal-rectangles',[corr(.1,[])]+[corr(.8,[]) for _ in range(4)],[rectangles,[],[],[],rectangles]),
            ('rectangle-count-equal',[corr(.1,[])]*5,[[],[],[],[],rectangles[:10]]),
            ('empty-kernel-clears',[corr(.1,[])]*5,[rectangles,rectangles,[],[],[]]),
            ('filtered-contours-retain',[corr(.1,[])]*5,[rectangles,[[1,1,99,140]],[[1,1,146,140]],[[1,1,140,99]],[[1,1,140,146]]]),
            ('rectangle-size-rounding',[corr(.1,[])]*5,[[],[],[],[],[[x,y,145,135] for x,y,_,_ in rectangles]]),
        ]
        native_match,native_contours,native_hull,native_box=cv2.matchTemplate,cv2.findContours,cv2.convexHull,cv2.boundingRect
        try:
            for name,measurements,groups in cases:
                calls=[]; match_index=0; contour_index=0
                def match(image,template,method):
                    nonlocal match_index
                    value=measurements[match_index];match_index+=1;calls.append('center' if match_index==1 else 'corner')
                    response=np.full((image.shape[0]-template.shape[0]+1,image.shape[1]-template.shape[1]+1),-.5,dtype=float)
                    threshold=.8
                    if value['maximum']>threshold:
                        for x,y in value['points']:response[y,x]=value['maximum']-.00001
                        # The maximum location must also appear in the threshold set.
                        x,y=value['points'][0];value['location']=[x,y];response[y,x]=value['maximum']
                    else:
                        x,y=value['location'];response[y,x]=value['maximum']
                    return response
                def contours(*args):
                    nonlocal contour_index
                    result=groups[contour_index];contour_index+=1;calls.append('rectangles')
                    return [np.array(r) for r in result],None
                cv2.matchTemplate=match;cv2.findContours=contours
                cv2.convexHull=lambda value:value
                cv2.boundingRect=lambda value:tuple(int(v) for v in value)
                c=config();c.HOMO_STORAGE=storage;c.HOMO_EDGE_DETECT=False
                view=View(c)
                try:
                    view.load(synthetic)
                    expected=dict(location=view.backend.homo_loca,interior=view.backend.map_inner,center=view.center_loca,
                                  offset=view.center_offset,swipe=view.swipe_base,
                                  grids=[dict(cell=g.location,corners=g.corner) for g in view])
                except MapDetectionError as error:expected=dict(error=str(error))
                fallbacks.append(dict(name=name,storage=storage,correlations=measurements,rectangles=groups,calls=calls,expected=expected))
        finally:
            cv2.matchTemplate=native_match;cv2.findContours=native_contours;cv2.convexHull=native_hull;cv2.boundingRect=native_box
        swipes=[]
        original_randint=utils.random.randint
        for index in range(180):
            vector=[rng.uniform(-1500,1500),rng.uniform(-900,900)] if index>20 else [index-10,index%4-2]
            white=[[200,180,380,380],[650,370,1000,580]] if index%3 else None
            black=[[300,250,600,400]] if index%4 else [[0,0,1280,720]]
            draws=[rng.randrange(2**30) for _ in range(1200)];used=[]
            def randint(a,b):
                value=a+draws[len(used)]%(b-a+1);used.append([a,b,value]);return value
            utils.random.randint=randint
            a,b=utils.random_rectangle_vector_opted(vector,box=(123,159,1175,628),whitelist_area=white,blacklist_area=black)
            swipes.append(dict(vector=vector,white=white,black=black,draws=used,start=a,end=b))
        utils.random.randint=original_randint
        output.write_text(json.dumps(dict(calibrations=calibrations,features=features,warps=warps,detections=detections,fallbacks=fallbacks,swipes=swipes),default=plain),encoding='utf-8')


if __name__=='__main__':main()
