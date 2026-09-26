"""Image-only fixtures scored by actual upstream info/stage extraction methods."""
import contextlib
import json
import os
from pathlib import Path
import sys
from types import SimpleNamespace


def main():
    root, server_name, output = Path(sys.argv[1]).resolve(), sys.argv[2], Path(sys.argv[3]).resolve()
    output.parent.mkdir(parents=True, exist_ok=True)
    os.chdir(root); sys.path.insert(0, str(root))
    with open(os.devnull, 'w') as quiet, contextlib.redirect_stdout(quiet):
        import cv2
        import numpy as np
        import module.config.server as server
        server.server = server_name
        from module.campaign.campaign_ocr import CampaignOcr
        from module.handler.info_handler import InfoHandler
        from module.handler.assets import INFO_BAR_AREA
        from module.template import assets
        from module.base.utils import crop
        from module.logger import logger
        logger.setLevel('CRITICAL')
        def save(image, name):
            filename=f'{server_name}-{name}.png'
            cv2.imwrite(str(output.parent / filename), cv2.cvtColor(image,cv2.COLOR_RGB2BGR))
            return filename
        stages=[]; profiles=[]
        names=['normal','half','blue','green','20240725']
        patterns=[('normal',assets.TEMPLATE_STAGE_CLEAR,None),('normal',assets.TEMPLATE_STAGE_PERCENT,None),
                  ('half',assets.TEMPLATE_STAGE_HALF_PERCENT,None),('blue',assets.TEMPLATE_STAGE_BLUE_PERCENT,(255,255,255)),
                  ('blue',assets.TEMPLATE_STAGE_BLUE_CLEAR,(99,223,239)),('green',assets.TEMPLATE_STAGE_GREEN_CLEAR,None),
                  ('20240725',assets.TEMPLATE_STAGE_CLEAR_20240725,None)]
        for i in range(19):
            image=np.full((720,1280,3),37,dtype=np.uint8)
            selected=[] if i==0 else patterns if i>=15 else [patterns[(i-1)//2]]
            for j,(kind,template,letter) in enumerate(selected):
                raw=template.image[(i+j)%len(template.image)] if template.is_gif else template.image
                x,y=140+270*(j%3),155+150*(j//3)
                if letter is None: patch=np.repeat(raw[:,:,None],3,axis=2)
                else:
                    diff=np.round(raw.astype(float)*153/255).astype(np.int16)
                    patch=np.clip(np.array(letter,dtype=np.int16)-diff[:,:,None],0,255).astype(np.uint8)
                h,w=patch.shape[:2]; image[y:y+h,x:x+w]=patch
                cv2.putText(image,'3-4 X',(x+85,y+19),cv2.FONT_HERSHEY_SIMPLEX,.4,(255,255,255),1)
                if i%2==0:
                    image[y+63:y+63+h,x+12:x+12+w]=patch
            kinds=31 if i>=15 or i==0 else 1<<names.index(selected[0][0])
            c=object.__new__(CampaignOcr)
            c.config=SimpleNamespace(STAGE_ENTRANCE=[n for j,n in enumerate(names) if kinds & 1<<j],SERVER=server_name)
            c.device=SimpleNamespace(image=image)
            buttons=c.campaign_extract_name_image(image)
            stages.append(dict(image=save(image,f'stage-{i}'),kinds=kinds,
                expected=[dict(icon=[int(v) for v in b.button],name=[int(v) for v in b.area]) for b in buttons]))
        rng=np.random.default_rng(247)
        for i in range(32):
            image=np.full((720,1280,3),34,dtype=np.uint8)
            x,y,r,b=INFO_BAR_AREA.area
            rows=([0,56,112,174] if i==0 else [22,22+(49 if i%2 else 50),138]) if i%4 else []
            for row in rows:
                if row >= b-y:continue
                color=np.clip(np.array([107,158,255]) + (i%7)*5,0,255)
                image[y+row:y+row+1,x:r]=color
                if i%3==0:image[y+row,x:x+150]=rng.integers(0,256,(150,3),dtype=np.uint8)
            c=object.__new__(InfoHandler);c.device=SimpleNamespace(image=image)
            c.image_crop=lambda button,copy=False:crop(c.device.image,button.area,copy=copy)
            profiles.append(dict(image=save(image,f'info-{i}'),count=c.info_bar_count()))
    output.write_text(json.dumps(dict(stages=stages,profiles=profiles)),encoding='utf-8')


if __name__=='__main__':main()
