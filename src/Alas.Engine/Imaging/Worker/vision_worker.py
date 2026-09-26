"""Pure CV worker: image bytes in, image measurements out. No upstream imports."""
import base64
import hashlib
import io
import json
import sys
from pathlib import Path

import cv2
import numpy as np
import imageio.v2 as imageio
from scipy import signal

MODEL_ROOT = None
OCR_SESSIONS = {}


def ocr_preprocess(image, letter, threshold, mode):
    if mode == "letters":
        # Preserve extract_letters' separate positive/negative uint8 scaling and rounding.
        diff = image.astype(np.int16) - np.array(letter, dtype=np.int16)
        positive = np.maximum(diff, 0).max(axis=2).astype(np.uint8)
        negative = (-np.minimum(diff, 0).min(axis=2)).astype(np.uint8)
        return cv2.addWeighted(positive, 255.0 / threshold, negative, 255.0 / threshold, 0)
    if mode == "luma":
        image_y = cv2.cvtColor(image, cv2.COLOR_RGB2YUV)[:, :, 0]
        letter_y = int(cv2.cvtColor(np.array([[letter]], dtype=np.uint8), cv2.COLOR_RGB2YUV)[0, 0, 0])
        return cv2.multiply(cv2.absdiff(image_y, np.full(image_y.shape, letter_y, dtype=np.uint8)), 255.0 / threshold)
    if mode == "grayscale":
        from PIL import Image
        return np.array(Image.fromarray(image).convert("L"))
    raise ValueError("ocr_preprocessing")


def infer_ocr(image, request):
    name, digest = request["model"], request["model_sha256"]
    if MODEL_ROOT is None:
        raise ValueError("ocr_models_unconfigured")
    if not isinstance(name, str) or not name or any(c not in "abcdefghijklmnopqrstuvwxyz_" for c in name):
        raise ValueError("ocr_model_name")
    if not isinstance(digest, str) or len(digest) != 64 or any(c not in "0123456789abcdef" for c in digest):
        raise ValueError("ocr_model_hash")
    classes = request["num_classes"]
    if type(classes) is not int or not 1 <= classes <= 10000:
        raise ValueError("ocr_classes")
    key = (name, digest)
    if key not in OCR_SESSIONS:
        import onnxruntime as ort
        data = (MODEL_ROOT / (name + ".onnx")).read_bytes()
        if hashlib.sha256(data).hexdigest() != digest:
            raise ValueError("ocr_model_hash_mismatch")
        options = ort.SessionOptions()
        options.intra_op_num_threads = 1
        options.inter_op_num_threads = 1
        options.log_severity_level = 3
        OCR_SESSIONS[key] = ort.InferenceSession(data, sess_options=options, providers=["CPUExecutionProvider"])
    session = OCR_SESSIONS[key]
    letter, threshold = request["letter"], request["threshold"]
    if not isinstance(letter, list) or len(letter) != 3 or any(type(v) is not int or not 0 <= v <= 255 for v in letter):
        raise ValueError("ocr_letter")
    if type(threshold) is not int or not 1 <= threshold <= 255:
        raise ValueError("ocr_threshold")
    prepared = ocr_preprocess(image, letter, threshold, request["preprocessing"])
    width = max(1, min(round(32 / prepared.shape[0] * prepared.shape[1]), 280))
    prepared = cv2.resize(prepared, (width, 32), interpolation=cv2.INTER_LINEAR)
    prepared = np.pad(prepared, ((0, 0), (0, 280 - width)), constant_values=0)
    tensor = prepared.astype(np.float32)[None, None, :, :] / 255.0
    probabilities = session.run(None, {session.get_inputs()[0].name: tensor})[0]
    if probabilities.shape != (70, classes) or not np.isfinite(probabilities).all():
        raise ValueError("ocr_output_shape")
    candidates = request["candidates"]
    if candidates is not None:
        if (not isinstance(candidates, list) or not candidates or candidates[0] != 0
                or any(type(v) is not int or not 0 <= v < classes for v in candidates)):
            raise ValueError("ocr_candidates")
        mask = np.zeros(classes, dtype=np.int8)
        mask[candidates] = 1
        probabilities *= mask
    # Return measurements; C# handles confidence threshold, padding cutoff and CTC decoding.
    return dict(classes=np.argmax(probabilities, axis=-1).tolist(), probabilities=np.max(probabilities, axis=-1).tolist(),
                width=width, model_sha256=digest)


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


def patch_measure(image, request):
    size = request["size"]
    if (not isinstance(size, list) or len(size) != 2 or any(type(v) is not int or v < 1 for v in size)
            or size[0] * size[1] > 1024 * 1024):
        raise ValueError("patch_size")
    image = cv2.resize(image, tuple(size), interpolation=cv2.INTER_CUBIC)
    measure, processing = request["measure"], request["processing"]
    if measure == "hsvcount":
        lower, upper = request["lower"], request["upper"]
        if any(not isinstance(v, list) or len(v) != 3 or not np.isfinite(v).all() for v in (lower, upper)):
            raise ValueError("hsv_bounds")
        return int(cv2.countNonZero(cv2.inRange(cv2.cvtColor(image, cv2.COLOR_RGB2HSV), tuple(lower), tuple(upper))))
    if processing == "colorsimilarity":
        color = request["color"]
        if not isinstance(color, list) or len(color) != 3 or any(type(v) is not int or not 0 <= v <= 255 for v in color):
            raise ValueError("patch_color")
        diff = image.astype(np.int16) - np.array(color, dtype=np.int16)
        distance = np.maximum(diff, 0).max(axis=2) - np.minimum(diff, 0).min(axis=2)
        image = (255 - np.minimum(distance, 255)).astype(np.uint8)
    elif processing == "gray":
        # Native rgb2gray rounds each half separately, then adds with saturation.
        high, low = image.max(axis=2), image.min(axis=2)
        image = cv2.add(cv2.convertScaleAbs(high, alpha=0.5), cv2.convertScaleAbs(low, alpha=0.5))
    elif processing != "color":
        raise ValueError("patch_processing")
    if measure == "similaritycount":
        minimum = request["minimum"]
        if image.ndim != 2 or type(minimum) is not int or not 0 <= minimum <= 255:
            raise ValueError("patch_minimum")
        return int(cv2.countNonZero(cv2.inRange(image, minimum, 255)))
    if measure != "template":
        raise ValueError("patch_measure")
    encoded = base64.b64decode(request["template"], validate=True)
    if len(encoded) > 16 * 1024 * 1024:
        raise ValueError("image_size")
    # Native Template preserves grayscale PNGs and includes mirrored GIF frames.
    if encoded[:6] in (b"GIF87a", b"GIF89a"):
        frames = imageio.mimread(io.BytesIO(encoded), format="GIF", memtest="256MB")
        if not frames or len(frames) > 512:
            raise ValueError("template_frames")
        channels = frames[0].ndim
        frames = [f[:, :, :3].copy() if channels == 3 else f[:, :, 0].copy() if f.ndim == 3 else f for f in frames]
        frames = [variant for f in frames for variant in (f, cv2.flip(f, 1))]
    else:
        template = cv2.imdecode(np.frombuffer(encoded, np.uint8), cv2.IMREAD_UNCHANGED)
        if template is None:
            raise ValueError("image_decode")
        frames = [cv2.cvtColor(template, cv2.COLOR_BGR2RGB) if template.ndim == 3 else template]
    values = []
    for template in frames:
        if template.shape[0] > image.shape[0] or template.shape[1] > image.shape[1] or template.ndim != image.ndim:
            raise ValueError("patch_template_shape")
        values.append(float(cv2.minMaxLoc(cv2.matchTemplate(image, template, cv2.TM_CCOEFF_NORMED))[1]))
    return max(values)


def pair_measure(image, request):
    sizes = [request["size"], request["second_size"]]
    for size in sizes:
        if (not isinstance(size, list) or len(size) != 2 or any(type(v) is not int or v < 1 for v in size)
                or size[0] * size[1] > 1024 * 1024):
            raise ValueError("patch_size")
    if sizes[0][0] > sizes[1][0] or sizes[0][1] > sizes[1][1]:
        raise ValueError("pair_shape")
    area = request["second_area"]
    if (not isinstance(area, list) or len(area) != 4 or any(type(v) is not int for v in area)
            or area[2] < 1 or area[3] < 1 or area[2] * area[3] > 16 * 1024 * 1024):
        raise ValueError("pair_area")
    if type(request["second_frame"]) is not int or request["second_frame"] < 1:
        raise ValueError("pair_identity")
    second = crop(decode(request["second_image"]), *area)
    def prepare(patch, size):
        patch = cv2.resize(patch, tuple(size), interpolation=cv2.INTER_CUBIC)
        return cv2.add(cv2.convertScaleAbs(patch.max(axis=2), alpha=0.5),
                       cv2.convertScaleAbs(patch.min(axis=2), alpha=0.5))
    value = cv2.minMaxLoc(cv2.matchTemplate(prepare(second, sizes[1]), prepare(image, sizes[0]), cv2.TM_CCOEFF_NORMED))[1]
    return dict(value=float(value), second_frame=request["second_frame"])


def gray(image):
    return cv2.add(cv2.convertScaleAbs(image.max(axis=2), alpha=0.5), cv2.convertScaleAbs(image.min(axis=2), alpha=0.5))


def decode_gray(value):
    # Native Mask preserves grayscale PNG values. Expanding to RGB then applying
    # rgb2gray rounds odd grayscale values twice and changes template scores.
    encoded = base64.b64decode(value, validate=True)
    if len(encoded) > 16 * 1024 * 1024: raise ValueError('image_size')
    image = cv2.imdecode(np.frombuffer(encoded, np.uint8), cv2.IMREAD_UNCHANGED)
    if image is None: raise ValueError('image_decode')
    return image if image.ndim == 2 else gray(cv2.cvtColor(image, cv2.COLOR_BGR2RGB))


def png(image):
    ok, data = cv2.imencode('.png', image if image.ndim == 2 else cv2.cvtColor(image, cv2.COLOR_RGB2BGR))
    if not ok:
        raise ValueError('image_encode')
    return base64.b64encode(data).decode('ascii')


def feature_measure(request):
    operation = request['operation']
    fields = {'protocol', 'id', 'frame', 'operation', 'image'}
    additional = {
        'image_mask': {'mask', 'origin'},
        'line_features': {'mask', 'area', 'inner_peaks', 'edge_peaks', 'inner_hough', 'edge_hough'},
        'warp_features': {'mask', 'area', 'matrix', 'size', 'canny', 'edge_color', 'hough'},
        'correlation_features': {'template', 'threshold', 'flip'},
        'contour_features': {'kernels'},
    }
    if set(request) != fields | additional[operation]:
        raise ValueError('request_fields')
    if request['protocol'] != 'alas-cv/1' or any(type(request[k]) is not int or request[k] < 1 for k in ('id', 'frame')):
        raise ValueError('request_identity')
    image = decode_gray(request['image']) if operation in ('correlation_features','contour_features') else decode(request['image'])
    if operation == 'image_mask':
        origin = request['origin']
        if not isinstance(origin, list) or len(origin) != 2 or any(type(v) is not int for v in origin):
            raise ValueError('mask_origin')
        mask = crop(decode(request['mask']), -origin[0], -origin[1], image.shape[1], image.shape[0])
        return dict(image=png(cv2.copyTo(image, gray(mask))))
    if operation == 'correlation_features':
        template = decode_gray(request['template'])
        flip = request['flip']
        if flip is not None:
            if type(flip) is not int or flip not in (-1,0,1): raise ValueError('template_flip')
            template = cv2.flip(template, flip)
        threshold = request['threshold']
        if type(threshold) not in (int,float) or not np.isfinite(threshold) or not -1 <= threshold <= 1:
            raise ValueError('correlation_threshold')
        if template.shape[0] > image.shape[0] or template.shape[1] > image.shape[1]: raise ValueError('template_bounds')
        result = cv2.matchTemplate(image, template, cv2.TM_CCOEFF_NORMED)
        _, maximum, _, location = cv2.minMaxLoc(result)
        points = np.argwhere(result > threshold)[:, ::-1]
        if len(points) > 1000000: raise ValueError('feature_count')
        return dict(maximum=maximum, location=location, points=points.tolist())
    if operation == 'contour_features':
        kernels = request['kernels']
        if not isinstance(kernels,list) or len(kernels)>16 or any(type(k) is not int or not 1 <= k <= 255 for k in kernels):
            raise ValueError('contour_kernels')
        result = []
        for size in kernels:
            kernel=cv2.getStructuringElement(cv2.MORPH_ELLIPSE,(size,size))
            contours,_=cv2.findContours(cv2.morphologyEx(image,cv2.MORPH_CLOSE,kernel),cv2.RETR_TREE,cv2.CHAIN_APPROX_SIMPLE)
            result.append([cv2.boundingRect(cv2.convexHull(c).astype(np.float32)) for c in contours])
        return dict(rectangles=result)
    area = request['area']
    if (not isinstance(area,list) or len(area)!=4 or any(type(v) is not int for v in area) or
            area[2]<1 or area[3]<1 or area[2]*area[3]>16*1024*1024): raise ValueError('area_bounds')
    image = gray(crop(image,*area))
    mask = decode_gray(request['mask'])
    def hough(pixels, threshold):
        if type(threshold) is not int or threshold < 1: raise ValueError('hough_threshold')
        lines = cv2.HoughLines(pixels,1,np.pi/180,threshold)
        return [] if lines is None else lines[:,0,:].tolist()
    if operation == 'line_features':
        if mask.shape != image.shape: raise ValueError('mask_shape')
        image = cv2.bitwise_not(cv2.bitwise_and(image,mask))
        stroke = cv2.erode(mask,cv2.getStructuringElement(cv2.MORPH_RECT,(3,3)))
        def peaks(horizontal,parameters,pad):
            if not isinstance(parameters,dict) or set(parameters)!={'height','width','prominence','distance','wlen'}:
                raise ValueError('peak_parameters')
            source = image.T if horizontal else image
            if pad: source=np.pad(source,((0,0),(0,pad)),constant_values=255)
            out=np.zeros(source.size,dtype=np.uint8)
            locations,_=signal.find_peaks(source.ravel(),**parameters);out[locations]=255;out=out.reshape(source.shape)
            if pad:out=out[:,:-pad]
            if horizontal:out=out.T
            return cv2.bitwise_and(out,stroke)
        return dict(inner_h=hough(peaks(True,request['inner_peaks'],0),request['inner_hough']),
                    inner_v=hough(peaks(False,request['inner_peaks'],0),request['inner_hough']),
                    edge_h=hough(peaks(True,request['edge_peaks'],area[2]),request['edge_hough']),
                    edge_v=hough(peaks(False,request['edge_peaks'],area[3]),request['edge_hough']))
    matrix = np.array(request['matrix'],dtype=float)
    size = request['size']
    if matrix.shape != (9,) or not np.isfinite(matrix).all(): raise ValueError('warp_matrix')
    if not isinstance(size,list) or len(size)!=2 or any(type(v) is not int or v<1 for v in size) or size[0]*size[1]>16*1024*1024:
        raise ValueError('warp_size')
    for key in ('canny','edge_color'):
        v=request[key]
        if not isinstance(v,list) or len(v)!=2 or not np.isfinite(v).all() or v[0]>v[1]:raise ValueError('image_threshold')
    matrix=matrix.reshape(3,3)
    warped=cv2.warpPerspective(image,matrix,tuple(size))
    stroke=cv2.warpPerspective(mask,matrix,tuple(size))
    stroke=cv2.erode(stroke,cv2.getStructuringElement(cv2.MORPH_RECT,(5,5))).astype(np.uint8)
    stroke[:2,:]=stroke[-2:,:]=stroke[:,:2]=stroke[:,-2:]=0
    kernel=cv2.getStructuringElement(cv2.MORPH_ELLIPSE,(5,5))
    edge=cv2.morphologyEx(cv2.bitwise_and(cv2.Canny(warped,*request['canny']),stroke),cv2.MORPH_CLOSE,kernel)
    lines=[]
    if request['hough'] is not None:
        filtered=cv2.bitwise_and(cv2.dilate(edge,kernel),cv2.inRange(warped,*request['edge_color']))
        lines=hough(cv2.bitwise_and(filtered,stroke),request['hough'])
    return dict(image=png(edge),lines=lines)


def match(request):
    if request.get('operation') in ('image_mask','line_features','warp_features','correlation_features','contour_features'):
        return feature_measure(request)
    if request.get("protocol") != "alas-cv/1" or request.get("operation") not in ("template_match", "color_mean", "color_bands", "ocr_infer", "image_patch", "image_pair"):
        raise ValueError("unsupported_operation")
    fields = {"protocol", "id", "operation", "frame", "image", "area"}
    if request["operation"] == "template_match":
        fields |= {"template", "preprocessing", "template_area"}
    elif request["operation"] == "color_bands":
        fields |= {"color", "closing_size", "row_threshold", "peak_height", "peak_width", "peak_distance", "relative_height"}
    elif request["operation"] == "ocr_infer":
        fields |= {"model", "model_sha256", "num_classes", "candidates", "letter", "threshold", "preprocessing"}
    elif request["operation"] == "image_patch":
        fields |= {"size", "measure", "processing", "color", "template", "minimum", "lower", "upper"}
    elif request["operation"] == "image_pair":
        fields |= {"size", "second_image", "second_frame", "second_area", "second_size"}
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
    if request["operation"] == "ocr_infer":
        return infer_ocr(crop(image, x, y, width, height), request)
    if request["operation"] == "image_patch":
        return {"value": patch_measure(crop(image, x, y, width, height), request)}
    if request["operation"] == "image_pair":
        return pair_measure(crop(image, x, y, width, height), request)
    if request["operation"] == "color_mean":
        return {"color": list(cv2.mean(crop(image, x, y, width, height))[:3])}
    if request["operation"] == "color_bands":
        color = request["color"]
        if not isinstance(color, list) or len(color) != 3 or any(type(v) is not int or not 0 <= v <= 255 for v in color):
            raise ValueError("color")
        closing = request["closing_size"]
        threshold = request["row_threshold"]
        if type(closing) is not int or not 1 <= closing <= 255 or type(threshold) is not int or not 0 <= threshold <= 255:
            raise ValueError("color_band_parameters")
        diff = crop(image, x, y, width, height).astype(np.int16) - np.array(color, dtype=np.int16)
        distance = np.maximum(diff, 0).max(axis=2) - np.minimum(diff, 0).min(axis=2)
        similarity = (255 - np.minimum(distance, 255)).astype(np.uint8)
        cv2.morphologyEx(similarity, cv2.MORPH_CLOSE, kernel=np.ones((closing, closing), dtype=np.uint8), dst=similarity)
        line = cv2.reduce(similarity, 1, cv2.REDUCE_AVG).flatten()
        line[line < threshold] = 0
        line[line >= threshold] = 255
        _, properties = signal.find_peaks(line, height=request["peak_height"], width=request["peak_width"],
                                          distance=request["peak_distance"], rel_height=request["relative_height"])
        return {"bands": [[int(a), int(b)] for a, b in zip(properties["left_bases"], properties["right_bases"])]}
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
    if len(sys.argv) == 3 and sys.argv[1] == "--models":
        MODEL_ROOT = Path(sys.argv[2]).resolve()
    elif len(sys.argv) != 1:
        raise ValueError("worker_arguments")
    main()
