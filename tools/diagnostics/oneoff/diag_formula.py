# -*- coding: utf-8 -*-
"""逐一定位 OpenCV TM_CCOEFF_NORMED 的确切公式（多通道均值扣减的作用域）。"""
import json
import os
import sys

import cv2
import numpy as np

ROOT = os.path.normpath(os.path.join(os.path.dirname(os.path.abspath(__file__)),
                                     '..', '..', '..'))
FORK = os.path.join(ROOT, '.runtime', 'engine')
os.chdir(FORK)
sys.path.insert(0, FORK)
import module.device.pkg_resources  # noqa: F401

from module.base.utils import crop, load_image

DATA = os.path.join(ROOT, 'data')
fx = json.load(open(os.path.join(DATA, 'fixtures', 'matching.json'), encoding='utf-8'))


def integral(a):
    h, w = a.shape
    ii = np.zeros((h + 1, w + 1))
    ii[1:, 1:] = a.cumsum(0).cumsum(1)
    return ii


def win(ii, y, x, th, tw):
    return ii[y + th, x + tw] - ii[y, x + tw] - ii[y + th, x] + ii[y, x]


def candidates(image, templ):
    """返回 {候选名: 结果矩阵}。image/templ 均为 float64 (h, w, c)。"""
    ih, iw = image.shape[:2]
    th, tw = templ.shape[:2]
    rh, rw = ih - th + 1, iw - tw + 1
    c = image.shape[2]
    N = th * tw                  # 每通道像素数
    Ntot = N * c

    out = {}

    # 候选 A：全部按全局（通道合并）统计 —— 我最初的实现
    sumT, sumT2 = templ.sum(), (templ * templ).sum()
    denomT_A = sumT2 - sumT * sumT / Ntot
    sI = integral(image.sum(2))
    sI2 = integral((image * image).sum(2))
    res = np.zeros((rh, rw))
    for y in range(rh):
        for x in range(rw):
            s = win(sI, y, x, th, tw)
            s2 = win(sI2, y, x, th, tw)
            cross = float((templ * image[y:y + th, x:x + tw]).sum())
            num = cross - sumT * s / Ntot
            dI = s2 - s * s / Ntot
            res[y, x] = num / np.sqrt(denomT_A * dI) if denomT_A * dI > 0 else 0.0
    out['A_全局'] = res

    # 候选 B：模板按**通道**扣均值，窗口按**通道**扣均值
    meanT_c = templ.reshape(-1, c).mean(0)
    tpl_c = templ - meanT_c
    sumT_c = tpl_c.sum(0)                       # 每通道扣均值后的和（≈0）
    denomT_B = (tpl_c * tpl_c).sum()            # 通道合并的平方和
    res = np.zeros((rh, rw))
    for y in range(rh):
        for x in range(rw):
            w_img = image[y:y + th, x:x + tw]
            meanI_c = w_img.reshape(-1, c).mean(0)
            img_c = w_img - meanI_c
            num = float((tpl_c * img_c).sum())
            denomI = float((img_c * img_c).sum())
            res[y, x] = num / np.sqrt(denomT_B * denomI) if denomT_B * denomI > 0 else 0.0
    out['B_逐通道扣均值'] = res

    # 候选 C：模板按通道扣均值，窗口用**全局**统计（OpenCV 积分图路径的典型写法）
    res = np.zeros((rh, rw))
    winSums = np.zeros((rh, rw, c))
    for ch in range(c):
        ii = integral(image[:, :, ch])
        for y in range(rh):
            for x in range(rw):
                winSums[y, x, ch] = win(ii, y, x, th, tw)
    for y in range(rh):
        for x in range(rw):
            w_img = image[y:y + th, x:x + tw]
            cross = float((templ * w_img).sum())
            num = cross - float((meanT_c * winSums[y, x]).sum())
            s = winSums[y, x].sum()
            s2 = win(sI2, y, x, th, tw)
            dI = s2 - s * s / Ntot
            res[y, x] = num / np.sqrt(denomT_B * dI) if denomT_B * dI > 0 else 0.0
    out['C_模板逐通道_窗口全局'] = res

    return out


for case in fx['match'][:3]:
    img = load_image(os.path.join(FORK, case['file'].replace('./', '').replace('/', os.sep)))
    area = np.array(case['area'])
    offset = case['offset']
    template = load_image(os.path.join(FORK, case['file'].replace('./', '').replace('/', os.sep)),
                          case['area']).astype(np.float64)
    search = crop(img, np.array((-3, -offset, 3, offset)) + area, copy=False).astype(np.float64)
    ref = cv2.matchTemplate(template.astype(np.uint8), search.astype(np.uint8),
                            cv2.TM_CCOEFF_NORMED).astype(np.float64)
    print(f"--- {case['id']}  templ={template.shape} search={search.shape} ref_sum={ref.sum():.4f}")
    for name, res in candidates(search, template).items():
        d = np.abs(res - ref)
        print(f"    {name:<24} maxdiff={d.max():.6g}  meandiff={d.mean():.6g}  sum={res.sum():.4f}")
