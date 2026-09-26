"""Read actual upstream objects and execute page appearance rules, without a device."""
import contextlib
import hashlib
import importlib
import json
import os
from pathlib import Path
import sys
from types import SimpleNamespace


def main():
    root, server_name, output = Path(sys.argv[1]).resolve(), sys.argv[2], Path(sys.argv[3]).resolve()
    os.chdir(output.parent)
    sys.path.insert(0, str(root))
    with open(os.devnull, "w") as quiet, contextlib.redirect_stdout(quiet):
        import module.config.server as server
        server.server = server_name
        from module.base.button import Button
        from module.base.template import Template
        from module.base.utils import load_image
        import numpy as np
        from module.logger import logger
        logger.setLevel("CRITICAL")
        assets, identity = [], {}
        hashes = {}
        for file in sorted((root / "module").rglob("assets.py")):
            module_name = file.relative_to(root).as_posix()[:-3].replace("/", ".")
            module = importlib.import_module(module_name)
            for name, asset in vars(module).items():
                if not isinstance(asset, (Button, Template)):
                    continue
                key = module_name + "." + name
                identity[id(asset)] = key
                path = asset.file
                if path and path not in hashes:
                    hashes[path] = hashlib.sha256((root / path).read_bytes()).hexdigest()
                assets.append(dict(id=key, name=asset.name, kind=type(asset).__name__,
                    area=list(asset.area) if isinstance(asset, Button) and asset.area else None,
                    color=list(asset.color) if isinstance(asset, Button) and asset.color else None,
                    click=list(asset.button) if isinstance(asset, Button) and asset.button else None,
                    file=path, sha256=hashes.get(path)))
        from module.ui.page import Page
        from module.ui.ui import UI
        pages = [dict(id=page.name, check=identity.get(id(page.check_button)),
                 links=[dict(destination=target.name, button=identity[id(button)]) for target, button in page.links.items()])
                 for page in Page.iter_pages()]
        routes = {}
        for destination in Page.iter_pages():
            Page.init_connection(destination)
            parents = {}
            for source in Page.iter_pages():
                if source.parent is None:
                    continue
                visited, current = set(), source
                while current != destination:
                    if current in visited or current.parent is None:
                        raise AssertionError("Invalid native page connection")
                    visited.add(current)
                    current = current.parent
                parents[source.name] = dict(distance=len(visited), next=source.parent.name)
            routes[destination.name] = parents
        Page.clear_connection()
        os.chdir(root)
        visual = []
        checkers = {identity[id(page.check_button)]: page.check_button for page in Page.iter_pages() if page.check_button is not None}
        for key, button in sorted(checkers.items()):
            import imageio.v2 as imageio
            images = imageio.mimread(button.file) if button.is_gif else [load_image(button.file)]
            for image in images:
                image = image[:, :, :3].copy()
                shifted = np.zeros_like(image)
                shifted[4:, 6:] = image[:-4, :-6]
                for kind, frame in (("original", image), ("shifted", shifted), ("black", np.zeros_like(image))):
                    sample = output.parent / f"visual-{server_name}-{len(visual)}.png"
                    imageio.imwrite(sample, frame)
                    button.clear_offset()
                    color_value = button.appear_on(frame, threshold=10)
                    matched = button.match(frame, offset=(30, 30), similarity=0.85)
                    visual.append(dict(asset=key, case=kind, image=sample.name, color=bool(color_value), matched=bool(matched), click=[int(v) for v in button.button]))
                    button.clear_offset()
        appearances = []
        alternatives = [None, "module.ui_white.assets.MAIN_GOTO_CAMPAIGN_WHITE",
                        "module.ui.assets.MAIN_GOTO_FLEET", "module.ui.assets.ACADEMY_GOTO_MUNITIONS"]
        for page in Page.iter_pages():
            if page.check_button is None:
                continue
            for positive in [identity[id(page.check_button)], *alternatives]:
                calls = []
                def appear(button, offset, interval):
                    calls.append(dict(asset=identity[id(button)], offset=[-offset[0], -offset[1], *offset], interval=interval))
                    return identity[id(button)] == positive
                actor = SimpleNamespace(config=SimpleNamespace(SERVER=server_name), appear=appear)
                value = UI.ui_page_appear(actor, page)
                appearances.append(dict(page=page.name, positive=positive, value=value, calls=calls))
    output.write_text(json.dumps(dict(assets=sorted(assets, key=lambda a: a["id"]), pages=pages, routes=routes,
                                    appearances=appearances, visual=visual), ensure_ascii=False), encoding="utf-8")


if __name__ == "__main__":
    main()
