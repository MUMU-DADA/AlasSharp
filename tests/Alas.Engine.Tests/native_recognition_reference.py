"""Actual upstream grid prediction on synthetic pixel frames; no device or product imports."""
import contextlib
import hashlib
import json
import os
from pathlib import Path
import sys
from types import SimpleNamespace


def main():
    root, output = Path(sys.argv[1]).resolve(), Path(sys.argv[2]).resolve()
    os.chdir(root)
    sys.path.insert(0, str(root))
    with open(os.devnull, "w") as quiet, contextlib.redirect_stdout(quiet):
        import cv2
        import numpy as np
        from PIL import Image
        from module.config import server
        server.server = "cn"
        from module.config.config_manual import ManualConfig
        from module.map_detection.grid import Grid
        from module.base.utils import load_image
        from module.logger import logger
        logger.setLevel("CRITICAL")
        from module.template import assets
        fields = ["is_submarine", "is_caught_by_siren", "is_fleet", "is_current_fleet", "is_boss", "is_siren",
                  "is_enemy", "is_mystery", "is_ammo", "is_missile_attack", "enemy_scale", "enemy_genre"]
        rng = np.random.default_rng(31284)
        corners = np.array([[240., 420.], [360., 420.], [240., 540.], [360., 540.]])
        prototypes = []

        def base():
            return np.zeros((640, 600, 3), dtype=np.uint8)

        def rect_for(grid, area):
            return np.rint(grid._image_center + np.array(area) * grid._image_a).astype(int)

        config = SimpleNamespace(**{k: getattr(ManualConfig, k) for k in dir(ManualConfig) if k.isupper()})
        blank_grid = Grid((0, 0), base(), corners, config)

        def paste(image, area, shape, name, color):
            template = load_image(getattr(assets, name).file)
            patch = np.zeros((shape[1], shape[0]), dtype=np.uint8)
            h, w = template.shape[:2]
            patch[:h, :w] = template
            # Similarity against color equals this grayscale template when all channels are scaled.
            if name == "TEMPLATE_FLEET_CURRENT":
                patch = (100 + patch.astype(float) * 155 / 255).astype(np.uint8)
            if name == "TEMPLATE_ENEMY_BOSS" and color == (255, 150, 24):
                patch = (230 + patch.astype(float) * 25 / 255).astype(np.uint8)
            rgb = np.rint(patch[:, :, None].astype(float) * np.array(color)[None, None, :] / 255).astype(np.uint8)
            x0, y0, x1, y1 = rect_for(blank_grid, area)
            image[y0:y1, x0:x1] = cv2.resize(rgb, (x1 - x0, y1 - y0), interpolation=cv2.INTER_CUBIC)

        prototypes.append(("blank", base()))
        prototypes.append(("noise", rng.integers(0, 256, (640, 600, 3), dtype=np.uint8)))
        icons = [
            ("large", (-1.115, -1.32, -.415, -.62), (50, 50), "TEMPLATE_ENEMY_L", (255, 130, 132)),
            ("medium", (-1.115, -1.32, -.415, -.62), (50, 50), "TEMPLATE_ENEMY_M", (255, 235, 156)),
            ("small", (-1.115, -1.32, -.415, -.62), (50, 50), "TEMPLATE_ENEMY_S", (255, 235, 156)),
            ("boss", (-.55, -.2, .45, .2), (50, 20), "TEMPLATE_ENEMY_BOSS", (255, 77, 82)),
            ("siren-boss", (-.55, -.2, .45, .2), (50, 20), "TEMPLATE_ENEMY_BOSS", (255, 150, 24)),
            ("submarine", (-.86, .08, -.36, .58), (50, 50), "TEMPLATE_SUBMARINE", (255, 243, 156)),
            ("fleet", (-1, -2, -.5, -1.5), (50, 50), "TEMPLATE_FLEET_AMMO", (255, 255, 255)),
            ("current", (-.5, -3.5, .5, -2.5), (60, 60), "TEMPLATE_FLEET_CURRENT", (24, 255, 107)),
            ("light", (-.5, -1, .5, 0), (60, 60), "TEMPLATE_ENEMY_Light", (255, 255, 255)),
            ("siren", (-.5, -1, .5, 0), (60, 60), "TEMPLATE_SIREN_DD", (255, 255, 255)),
        ]
        for label, area, shape, name, color in icons:
            image = base()
            paste(image, area, shape, name, color)
            prototypes.append((label, image))
        for label, area, color in [("mystery", (-.3, -2, .3, -.6), (148, 255, 247)),
                                   ("missile", (-.5, -1, .5, 0), (255, 255, 60))]:
            image = base()
            x0, y0, x1, y1 = rect_for(blank_grid, area)
            image[y0:y1, x0:x1] = color
            prototypes.append((label, image))
        combined = base()
        for _, area, shape, name, color in (icons[0], icons[5], icons[6]):
            paste(combined, area, shape, name, color)
        prototypes.append(("combined", combined))
        results = []
        for label, image in prototypes:
            image_path = output.parent / (label + ".png")
            Image.fromarray(image).save(image_path)
            for variant in range(6):
                options = dict(has_siren=bool(variant & 1), siren_has_boss_icon=variant in (2, 3),
                               siren_has_small_boss_icon=variant in (4, 5), has_mystery=variant != 2,
                               has_missile_attack=variant != 0, image_scale=1 if variant < 4 else 1.05)
                cfg = SimpleNamespace(**vars(config))
                for name, value in options.items():
                    key = "GRID_IMAGE_A_MULTIPLY" if name == "image_scale" else "MAP_" + name.upper().replace("SIREN_HAS_SMALL_BOSS_ICON", "SIREN_HAS_BOSS_ICON_SMALL")
                    setattr(cfg, key, value)
                cfg.MAP_ENEMY_GENRE_DETECTION_SCALING = {"Light": (.8, 1, 1.2)}
                grid = Grid((0, 0), image, corners, cfg)
                grid.predict()
                results.append(dict(name=f"{label}-{variant}", image=image_path.name, corners=corners.tolist(), options=options,
                                    expected={k: getattr(grid, k) for k in fields}))
        # Primitive image measurements independently exercise exact rounding, padding, HSV and GIF mirrors.
        from module.base.utils import crop, rgb2gray, color_similarity_2d, color_mask
        patches = []
        image = prototypes[1][1]
        for index in range(32):
            x, y = map(int, rng.integers(-20, 610, 2))
            w, h = map(int, rng.integers(1, 95, 2))
            width, height = map(int, rng.integers(3, 95, 2))
            color = list(map(int, rng.integers(0, 256, 3)))
            threshold = int(rng.integers(0, 256))
            patch = cv2.resize(crop(image, (x, y, x+w, y+h)), (width, height), interpolation=cv2.INTER_CUBIC)
            hsv = cv2.cvtColor(patch, cv2.COLOR_RGB2HSV)
            counts = [int(cv2.countNonZero(color_mask(patch, color, threshold))),
                      int(cv2.countNonZero(cv2.inRange(hsv, (138/2, 0, 0), (151/2+1, 256, 256))))]
            gray = rgb2gray(patch)
            template = gray[:min(5, height), :min(5, width)]
            path = output.parent / f"gray-{index}.png"
            Image.fromarray(template).save(path)
            score = float(cv2.minMaxLoc(cv2.matchTemplate(gray, template, cv2.TM_CCOEFF_NORMED))[1])
            patches.append(dict(area=[x,y,w,h], size=[width,height], color=color, minimum=255-threshold,
                                counts=counts, template=path.name, score=score))
        # Native control flow on scripted numeric CV results tests strict thresholds and short-circuit priority.
        import random
        import module.map_detection.grid_predictor as predictor
        from module.base.template import Template
        original_match, original_similarity = Template.match, predictor.color_similarity_2d
        traced = []
        randomizer = random.Random(91234)
        names = [name for name, value in vars(assets).items() if isinstance(value, Template)]
        reverse_names = {id(getattr(assets, name)): name for name in names}
        values = [0.59, 0.6, 0.61, 0.69, 0.7, 0.71, 0.74, 0.75, 0.76, 0.84, 0.85, 0.86, 1.0]
        class ScriptedGrid(Grid):
            def relative_rgb_count(self, area, color, shape=(50, 50), threshold=34):
                key = "mystery" if color == (148, 255, 247) else "missile"
                trace.append([key, numbers[key]])
                return numbers[key]
            def relative_hsv_count(self, area, h=(0, 360), s=(0, 100), v=(0, 100), shape=(50, 50)):
                key = "current" if h[0] == 138 else "small"
                trace.append([key, numbers[key]])
                return numbers[key]
        def fake_match(template, image, scaling=1.0, similarity=.85):
            name = reverse_names[id(template)]
            trace.append([name, scores[name]])
            return scores[name] > similarity
        def fake_similarity(image, color):
            image = original_similarity(image, color)
            if color == (255, 150, 24):
                image[:] = 0
                image.flat[:numbers["orange"]] = 255
                trace.append(["orange", numbers["orange"]])
            return image
        Template.match, predictor.color_similarity_2d = fake_match, fake_similarity
        try:
            for index in range(700):
                options = dict(has_siren=bool(randomizer.randrange(2)), siren_has_boss_icon=bool(randomizer.randrange(2)),
                               siren_has_small_boss_icon=bool(randomizer.randrange(2)), has_mystery=bool(randomizer.randrange(2)),
                               has_missile_attack=bool(randomizer.randrange(2)), image_scale=1)
                cfg = SimpleNamespace(**vars(config))
                for name, value in options.items():
                    key = "GRID_IMAGE_A_MULTIPLY" if name == "image_scale" else "MAP_" + name.upper().replace("SIREN_HAS_SMALL_BOSS_ICON", "SIREN_HAS_BOSS_ICON_SMALL")
                    setattr(cfg, key, value)
                cfg.MAP_ENEMY_GENRE_DETECTION_SCALING = {"Light": (.8, 1, 1.2)}
                scores = {name: randomizer.choice(values) for name in names}
                numbers = dict(orange=randomizer.choice([199, 200, 201]), small=randomizer.choice([99,100,101]),
                               current=randomizer.choice([599,600,601]), mystery=randomizer.choice([49,50,51]),
                               missile=randomizer.choice([34,35,36]))
                trace = []
                grid = ScriptedGrid((0,0), base(), corners, cfg)
                grid.predict()
                traced.append(dict(options=options, scores=scores, numbers=numbers, trace=trace,
                                   expected={k: getattr(grid, k) for k in fields}))
        finally:
            Template.match, predictor.color_similarity_2d = original_match, original_similarity
    output.write_text(json.dumps(dict(cases=results, patches=patches, traced=traced), separators=(",", ":")), encoding="utf-8")


if __name__ == "__main__":
    main()
