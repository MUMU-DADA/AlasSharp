"""Pure CV worker: image bytes in, image measurements out. No upstream imports."""
import base64
import io
import json
import sys

import cv2
import numpy as np
import imageio.v2 as imageio


def decode(value):
    if not isinstance(value, str):
        raise ValueError("image_encoding")
    encoded = base64.b64decode(value, validate=True)
    if len(encoded) > 16 * 1024 * 1024:
        raise ValueError("image_size")
    image = cv2.imdecode(np.frombuffer(encoded, np.uint8), cv2.IMREAD_COLOR)
    if image is None:
        raise ValueError("image_decode")
    return cv2.cvtColor(image, cv2.COLOR_BGR2RGB)


def templates(value):
    encoded = base64.b64decode(value, validate=True)
    if len(encoded) > 16 * 1024 * 1024:
        raise ValueError("image_size")
    if encoded[:6] not in (b"GIF87a", b"GIF89a"):
        return [decode(value)]
    frames = imageio.mimread(io.BytesIO(encoded), format="GIF", memtest="256MB")
    if not frames or len(frames) > 512:
        raise ValueError("template_frames")
    return [frame[:, :, :3].copy() if frame.ndim == 3 else cv2.cvtColor(frame, cv2.COLOR_GRAY2RGB)
            for frame in frames]


def preprocess(image, mode):
    if mode == "color":
        return image
    if mode == "luma":
        return cv2.cvtColor(image, cv2.COLOR_RGB2YUV)[:, :, 0]
    if mode == "binary":
        # Upstream uses BGR2GRAY on its RGB array. Preserve those coefficients.
        gray = cv2.cvtColor(image, cv2.COLOR_BGR2GRAY)
        return cv2.threshold(gray, 0, 255, cv2.THRESH_BINARY | cv2.THRESH_OTSU)[1]
    raise ValueError("preprocessing")


def crop(image, x, y, width, height):
    # Same black-padding behavior as upstream module.base.utils.crop.
    out = np.zeros((height, width, 3), dtype=image.dtype)
    left, top = max(x, 0), max(y, 0)
    right, bottom = min(x + width, image.shape[1]), min(y + height, image.shape[0])
    if left < right and top < bottom:
        out[top - y:bottom - y, left - x:right - x] = image[top:bottom, left:right]
    return out


def match(request):
    if request.get("protocol") != "alas-cv/1" or request.get("operation") not in ("template_match", "color_mean"):
        raise ValueError("unsupported_operation")
    fields = {"protocol", "id", "operation", "frame", "image", "area"}
    if request["operation"] == "template_match":
        fields |= {"template", "preprocessing", "template_area"}
    if set(request) != fields:
        raise ValueError("request_fields")
    for key in ("id", "frame"):
        if type(request[key]) is not int or request[key] < 1:
            raise ValueError("request_identity")
    area = request["area"]
    if not isinstance(area, list) or len(area) != 4 or any(type(v) is not int for v in area):
        raise ValueError("area_type")
    x, y, width, height = area
    image = decode(request["image"])
    if width <= 0 or height <= 0 or width * height > 16 * 1024 * 1024:
        raise ValueError("area_bounds")
    if request["operation"] == "color_mean":
        return {"color": list(cv2.mean(crop(image, x, y, width, height))[:3])}
    template_area = request["template_area"]
    if template_area is not None:
        if (not isinstance(template_area, list) or len(template_area) != 4 or any(type(v) is not int for v in template_area)
                or template_area[2] <= 0 or template_area[3] <= 0 or template_area[2] * template_area[3] > 16 * 1024 * 1024):
            raise ValueError("template_area")
    image = preprocess(crop(image, x, y, width, height), request["preprocessing"])
    candidates = []
    for template in templates(request["template"]):
        if template_area is not None:
            template = crop(template, *template_area)
        if template.shape[0] > height or template.shape[1] > width:
            raise ValueError("template_bounds")
        template = preprocess(template, request["preprocessing"])
        scores = cv2.matchTemplate(image, template, cv2.TM_CCOEFF_NORMED)
        _, similarity, _, position = cv2.minMaxLoc(scores)
        candidates.append({"similarity": similarity, "location": [position[0] + x, position[1] + y]})
    return {"candidates": candidates}


def main():
    while True:
        # Two base64 images plus protocol fields, bounded even without a newline.
        line = sys.stdin.buffer.readline(45 * 1024 * 1024)
        if not line:
            return
        if not line.endswith(b"\n"):
            raise ValueError("request_size")
        request = None
        try:
            request = json.loads(line)
            if not isinstance(request, dict):
                raise ValueError("request_object")
            response = match(request)
        except Exception as error:
            # Fixed error category, no account paths, frames or arbitrary stack output.
            response = {"error": str(error) if type(error) is ValueError else type(error).__name__}
        response.update(protocol="alas-cv/1", id=request.get("id") if isinstance(request, dict) else None,
                        frame=request.get("frame") if isinstance(request, dict) else None)
        sys.stdout.write(json.dumps(response, allow_nan=False, separators=(",", ":")) + "\n")
        sys.stdout.flush()


if __name__ == "__main__":
    main()
