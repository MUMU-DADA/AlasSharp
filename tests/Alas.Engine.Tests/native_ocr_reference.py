"""Actual upstream OCR inference and numeric parsers, independent of the new worker."""
import contextlib
import importlib.util
import json
import os
from pathlib import Path
import sys


def main():
    root, output, worker_file = Path(sys.argv[1]).resolve(), Path(sys.argv[2]).resolve(), Path(sys.argv[3]).resolve()
    sys.path.insert(0, str(root))
    os.chdir(root)
    with open(os.devnull, "w") as quiet, contextlib.redirect_stdout(quiet):
        import cv2
        import numpy as np
        from module.ocr.ocr import Ocr, OcrYuv, Digit, DigitCounter, Duration
        from module.ocr.al_ocr import AlOcr
        from module.base.utils import crop, load_image
        from module.logger import logger
        logger.setLevel("CRITICAL")
        spec = importlib.util.spec_from_file_location("pure_image_worker", worker_file)
        worker = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(worker)
        rng = np.random.default_rng(72398)
        preprocess_count = 0
        for shape in ((18, 60, 3), (240, 160, 3), (1, 1, 3)):
            pixels = rng.integers(0, 256, size=shape, dtype=np.uint8)
            for letter in ((255, 255, 255), (255, 247, 247), (40, 110, 240)):
                for threshold in (64, 128, 255):
                    for mode, cls in (("letters", Ocr), ("luma", OcrYuv)):
                        expected = cls((0, 0, 1, 1), letter=letter, threshold=threshold).pre_process(pixels)
                        actual = worker.ocr_preprocess(pixels, letter, threshold, mode)
                        assert np.array_equal(expected, actual), (shape, letter, threshold, mode)
                        preprocess_count += 1
        cases = []
        for language in ("azur_lane", "azur_lane_jp", "cnocr", "jp", "tw"):
            for size in ((18, 60), (32, 180), (50, 650)):
                image = np.zeros((*size, 3), dtype=np.uint8)
                cv2.putText(image, "15/30", (0, size[0] - 3), cv2.FONT_HERSHEY_SIMPLEX, size[0] / 35,
                            (255, 247, 247), 1, cv2.LINE_AA)
                name = f"ocr-{language}-{len(cases)}.png"
                cv2.imwrite(str(output.parent / name), cv2.cvtColor(image, cv2.COLOR_RGB2BGR))
                for mode, cls in (("letters", Ocr), ("luma", OcrYuv)):
                    for alphabet in (None, "0123456789/IDSB", ""):
                        actor = cls((0, 0, size[1], size[0]), lang=language, letter=(255, 247, 247), threshold=64, alphabet=alphabet)
                        try:
                            expected, error = actor.ocr(image), None
                        except KeyError:
                            expected, error = None, "alphabet"
                        cases.append(dict(image=name, area=[0, 0, size[1], size[0]], language=language, mode=mode,
                            letter=[255, 247, 247], threshold=64, alphabet=alphabet, text=expected, error=error))
            # Actual upstream text asset; this is not a live game capture.
            image = load_image("assets/cn/freebies/OCR_DATA_KEY.png")
            from module.freebies.assets import OCR_DATA_KEY
            area = OCR_DATA_KEY.area
            actor = Ocr(area, lang=language, letter=(255, 247, 247), threshold=64, alphabet="0123456789/IDSB")
            name = f"ocr-{language}-asset.png"
            cv2.imwrite(str(output.parent / name), cv2.cvtColor(image, cv2.COLOR_RGB2BGR))
            try:
                expected, error = actor.ocr(image), None
            except KeyError:
                expected, error = None, "alphabet"
            cases.append(dict(image=name, area=[area[0], area[1], area[2]-area[0], area[3]-area[1]], language=language,
                mode="letters", letter=[255, 247, 247], threshold=64, alphabet="0123456789/IDSB", text=expected, error=error))
        decode = []
        model = AlOcr(name="azur_lane")
        model._ensure_loaded()
        for n in range(200):
            ids = rng.integers(0, len(model._alphabet), size=70)
            scores = rng.choice([0.1, 0.5, 0.500001, 0.9], size=70)
            probabilities = np.zeros((70, len(model._alphabet)), dtype=np.float64)
            probabilities[np.arange(70), ids] = scores
            width = (1, 3, 4, 7, 60, 279, 280)[n % 7]
            expected = "".join(model._gen_line_pred_chars(probabilities, width, 280))
            decode.append(dict(ids=ids.tolist(), scores=scores.tolist(), width=width, text=expected))
        # Use actual inherited parser methods with only image inference replaced.
        class CounterReference(DigitCounter):
            pass
        original = Ocr.ocr
        numeric = []
        for text in ("", "15/30", "35/30", "ID/SB", "bad", "001/002", "12/34/56", "000", "12:34:56", "1:23:45", "123456", "99:99:99", "9999999999999999999999999999"):
            Ocr.ocr = lambda self, *a, **kw: self.after_process(text)
            try:
                value, error = str(Digit((0, 0, 1, 1)).after_process(text)), None
            except ValueError:
                value, error = None, "FormatException"
            numeric.append(dict(text=text, digit=value, error=error,
                counter=[str(v) for v in DigitCounter((0, 0, 1, 1)).ocr(None)],
                duration=Duration((0, 0, 1, 1)).ocr(None).total_seconds()))
        Ocr.ocr = original
    output.write_text(json.dumps(dict(cases=cases, decode=decode, numeric=numeric, preprocess=preprocess_count)), encoding="utf-8")


if __name__ == "__main__":
    main()
