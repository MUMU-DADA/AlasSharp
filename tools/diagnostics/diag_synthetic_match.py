# -*- coding: utf-8 -*-
"""受控实验：OpenCV TM_CCOEFF_NORMED 在「模板含常量通道」时到底怎么归一化。

构造已知结构的合成图，逐一变因，看 cv2 结果与哪个公式吻合。
"""
import json
import sys

import cv2
import numpy as np


def ccoeff(image, templ, per_channel=True):
    """per_channel=True: 模板与窗口都按通道扣均值（已验证在常规图上与 cv2 一致）。"""
    ih, iw = image.shape[:2]
    th, tw = templ.shape[:2]
    c = image.shape[2]
    N = th * tw
    I = image.astype(np.float64)
    T = templ.astype(np.float64)
    sumT = T.reshape(-1, c).sum(0)
    sumT2 = (T * T).reshape(-1, c).sum(0)
    meanT = sumT / N
    if per_channel:
        denomT = float(np.sum(sumT2 - N * meanT * meanT))
    else:
        denomT = float(((T - T.mean()) ** 2).sum())
    out = np.zeros((ih - th + 1, iw - tw + 1))
    for y in range(out.shape[0]):
        for x in range(out.shape[1]):
            w = I[y:y + th, x:x + tw]
            num = 0.0
            denomI = 0.0
            if per_channel:
                for ch in range(c):
                    wc = w[:, :, ch]
                    mI = wc.mean()
                    num += float(((T[:, :, ch] - meanT[ch]) * (wc - mI)).sum())
                    denomI += float(((wc - mI) ** 2).sum())
            else:
                wm = w.mean()
                num = float(((T - T.mean()) * (w - wm)).sum())
                denomI = float(((w - wm) ** 2).sum())
            out[y, x] = num / np.sqrt(denomT * denomI) if denomT > 0 and denomI > 0 else (
                1.0 if denomT <= 0 else 0.0)
    return out


def build(const_channels, seed=0):
    rng = np.random.default_rng(seed)
    img = rng.integers(20, 240, size=(40, 40, 3)).astype(np.uint8)
    for ch in const_channels:
        img[:, :, ch] = 255
    return img


results = {}
for label, const_ch in (('三个通道都变化', ()),
                        ('通道0常量', (0,)),
                        ('通道0和2常量', (0, 2)),
                        ('通道1常量', (1,))):
    img = build(const_ch)
    templ = img[5:15, 5:25].copy()
    ref = cv2.matchTemplate(img, templ, cv2.TM_CCOEFF_NORMED).astype(np.float64)
    mine_pc = ccoeff(img, templ, True)
    mine_gl = ccoeff(img, templ, False)
    results[label] = {
        'template_per_channel_var': [round(float(v), 4) for v in
                                     templ.reshape(-1, 3).var(0) * templ.shape[0] * templ.shape[1]],
        'cv2_max': round(float(ref.max()), 6),
        'perchannel_max': round(float(mine_pc.max()), 6),
        'global_max': round(float(mine_gl.max()), 6),
        'perchannel_maxdiff': round(float(np.abs(mine_pc - ref).max()), 8),
        'global_maxdiff': round(float(np.abs(mine_gl - ref).max()), 8),
    }

print(json.dumps(results, ensure_ascii=False, indent=2))
