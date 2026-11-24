#!/usr/bin/env python3
"""Reference for Shutterbox's on-device vision (People and "search by what's in the photo").

The Android (Dart) and Windows (C#) apps implement the same steps; this file is what they're checked against.

  detect   YuNet (face_detection_yunet_2023mar.onnx): the picture is scaled down (never up) to fit 640x640, top-left, the rest black,
           fed as BGR 0-255 NCHW. Each stride (8, 16, 32) gives cls, obj, bbox and kps per cell:
             score = sqrt(clamp(cls) * clamp(obj));  cx = (col + b0) * s;  cy = (row + b1) * s;  w = exp(b2) * s ...
           then score >= 0.8 and non-maximum suppression at IoU 0.3.
  align    5 landmarks -> similarity transform (Umeyama, no reflection) onto ArcFace's 112x112 template,
           bilinear sampling.
  embed    SFace (face_recognition_sface_2021dec_int8.onnx): the aligned face as RGB 0-255 NCHW -> 128 numbers,
           scaled to length 1. Same person: cosine >= 0.36 (People groups at 0.40).
  classify MobileNetV2 (image_classification_mobilenetv2_2022apr_int8.onnx): the picture resized to 256 on its
           short side, center 224x224, RGB, (x/255 - mean) / std. softmax; the top 5 with p >= 0.06 are kept.

Usage:
  vision_ref.py check MODELS_DIR IMAGE...     compares this file with OpenCV's own YuNet/SFace and prints results
  vision_ref.py expect MODELS_DIR OUT.json IMAGE...   writes what the apps' self-tests should find
"""
import json
import math
import sys

import numpy as np

TEMPLATE = np.array([[38.2946, 51.6963], [73.5318, 51.5014], [56.0252, 71.7366], [41.5493, 92.3655], [70.7299, 92.2041]], dtype=np.float64)
MEAN = np.array([0.485, 0.456, 0.406])
STD = np.array([0.229, 0.224, 0.225])
SIZE = 640


def _session(path):
    import onnxruntime as ort
    return ort.InferenceSession(path, providers=["CPUExecutionProvider"])


def detect(sess, rgb, threshold=0.8, nms=0.3):
    """rgb: HxWx3 uint8. Returns [(score, x, y, w, h, [(x,y)*5])] in the picture's pixels."""
    h, w = rgb.shape[:2]
    scale = min(1.0, SIZE / w, SIZE / h)
    nw, nh = max(1, round(w * scale)), max(1, round(h * scale))
    small = resize_bilinear(rgb, nw, nh)
    canvas = np.zeros((SIZE, SIZE, 3), dtype=np.float32)
    canvas[:nh, :nw] = small[:, :, ::-1]  # BGR
    blob = canvas.transpose(2, 0, 1)[None]
    out = dict(zip([o.name for o in sess.get_outputs()], sess.run(None, {"input": blob})))
    faces = []
    for s in (8, 16, 32):
        cols = SIZE // s
        cls = out[f"cls_{s}"][0, :, 0]
        obj = out[f"obj_{s}"][0, :, 0]
        bbox = out[f"bbox_{s}"][0]
        kps = out[f"kps_{s}"][0]
        score = np.sqrt(np.clip(cls, 0, 1) * np.clip(obj, 0, 1))
        for i in np.nonzero(score >= threshold)[0]:
            r, c = divmod(int(i), cols)
            cx, cy = (c + bbox[i, 0]) * s, (r + bbox[i, 1]) * s
            bw, bh = math.exp(bbox[i, 2]) * s, math.exp(bbox[i, 3]) * s
            pts = [((kps[i, 2 * k] + c) * s / scale, (kps[i, 2 * k + 1] + r) * s / scale) for k in range(5)]
            faces.append((float(score[i]), (cx - bw / 2) / scale, (cy - bh / 2) / scale, bw / scale, bh / scale, pts))
    faces.sort(key=lambda f: -f[0])
    kept = []
    for f in faces:
        if all(iou(f, k) < nms for k in kept):
            kept.append(f)
    return kept


def iou(a, b):
    ax2, ay2, bx2, by2 = a[1] + a[3], a[2] + a[4], b[1] + b[3], b[2] + b[4]
    iw = max(0.0, min(ax2, bx2) - max(a[1], b[1]))
    ih = max(0.0, min(ay2, by2) - max(a[2], b[2]))
    inter = iw * ih
    return inter / (a[3] * a[4] + b[3] * b[4] - inter + 1e-9)


def similarity(src):
    """Umeyama: the 2x3 matrix mapping src (5x2) onto TEMPLATE with rotation, uniform scale and shift."""
    src = np.asarray(src, dtype=np.float64)
    ms, md = src.mean(0), TEMPLATE.mean(0)
    s, d = src - ms, TEMPLATE - md
    cov = d.T @ s / len(src)
    u, sv, vt = np.linalg.svd(cov)
    e = np.eye(2)
    if np.linalg.det(cov) < 0:
        e[1, 1] = -1
    r = u @ e @ vt
    var = (s ** 2).sum() / len(src)
    c = (sv * np.diag(e)).sum() / var
    t = md - c * r @ ms
    return np.hstack([c * r, t[:, None]])


def warp(rgb, m, size=112):
    """Bilinear sample of rgb at the inverse of m for each output pixel (pixels outside are black)."""
    a = np.vstack([m, [0, 0, 1]])
    inv = np.linalg.inv(a)[:2]
    ys, xs = np.mgrid[0:size, 0:size].astype(np.float64)
    sx = inv[0, 0] * xs + inv[0, 1] * ys + inv[0, 2]
    sy = inv[1, 0] * xs + inv[1, 1] * ys + inv[1, 2]
    return sample(rgb, sx, sy)


def sample(img, sx, sy):
    h, w = img.shape[:2]
    x0, y0 = np.floor(sx).astype(int), np.floor(sy).astype(int)
    fx, fy = (sx - x0)[..., None], (sy - y0)[..., None]
    f = img.astype(np.float64)

    def px(x, y):
        ok = ((x >= 0) & (x < w) & (y >= 0) & (y < h))[..., None]
        return np.where(ok, f[np.clip(y, 0, h - 1), np.clip(x, 0, w - 1)], 0.0)
    top = px(x0, y0) * (1 - fx) + px(x0 + 1, y0) * fx
    bot = px(x0, y0 + 1) * (1 - fx) + px(x0 + 1, y0 + 1) * fx
    return top * (1 - fy) + bot * fy


def resize_bilinear(rgb, nw, nh):
    """Half-pixel-centred bilinear resize (the apps use the platform's resampler; small differences are fine)."""
    h, w = rgb.shape[:2]
    ys, xs = np.mgrid[0:nh, 0:nw].astype(np.float64)
    sx = (xs + 0.5) * w / nw - 0.5
    sy = (ys + 0.5) * h / nh - 0.5
    return sample(rgb, np.clip(sx, 0, w - 1), np.clip(sy, 0, h - 1)).astype(np.float32)


def embed(sess, rgb, landmarks):
    face = warp(rgb, similarity(landmarks)).astype(np.float32)
    blob = face.transpose(2, 0, 1)[None]
    v = sess.run(None, {"data": blob})[0][0].astype(np.float64)
    return v / (np.linalg.norm(v) + 1e-12)


def classify(sess, rgb, top=5, minimum=0.06):
    h, w = rgb.shape[:2]
    scale = 256 / min(w, h)
    nw, nh = max(224, round(w * scale)), max(224, round(h * scale))
    img = resize_bilinear(rgb, nw, nh)
    x0, y0 = (nw - 224) // 2, (nh - 224) // 2
    crop = img[y0:y0 + 224, x0:x0 + 224] / 255.0
    blob = ((crop - MEAN) / STD).astype(np.float32).transpose(2, 0, 1)[None]
    logits = sess.run(None, {"input": blob})[0][0].astype(np.float64)
    p = np.exp(logits - logits.max())
    p /= p.sum()
    order = np.argsort(-p)[:top]
    return [(int(i), float(p[i])) for i in order if p[i] >= minimum]


def load(path):
    import cv2
    bgr = cv2.imread(path, cv2.IMREAD_COLOR)
    return bgr[:, :, ::-1].copy()


def main():
    cmd, models = sys.argv[1], sys.argv[2]
    det = _session(f"{models}/face_detection_yunet_2023mar.onnx")
    rec = _session(f"{models}/face_recognition_sface_2021dec_int8.onnx")
    cls = _session(f"{models}/image_classification_mobilenetv2_2022apr_int8.onnx")
    words = f"{models}/vision_words.txt"
    import os
    labels = [l.split("|")[0] for l in open(words, encoding="utf-8").read().splitlines()] if os.path.exists(words) else []
    images = sys.argv[3:] if cmd == "check" else sys.argv[4:]
    results = {}
    for path in images:
        rgb = load(path)
        faces = detect(det, rgb)
        embs = [embed(rec, rgb, f[5]) for f in faces]
        tags = classify(cls, rgb)
        name = path.replace("\\", "/").split("/")[-1]
        results[name] = {
            "size": [rgb.shape[1], rgb.shape[0]],
            "faces": [{"score": round(f[0], 3), "box": [round(float(v), 1) for v in f[1:5]], "landmarks": [[round(float(x), 1), round(float(y), 1)] for x, y in f[5]], "embedding": [round(float(x), 5) for x in e]} for f, e in zip(faces, embs)],
            "labels": [{"id": i, "p": round(p, 3)} for i, p in tags],
        }
        print(name, rgb.shape[1], "x", rgb.shape[0], "faces:", [(round(f[0], 2), [round(v) for v in f[1:5]]) for f in faces],
              "labels:", [(labels[i] if labels else i, round(p, 2)) for i, p in tags])
        if cmd == "check":
            import cv2
            yn = cv2.FaceDetectorYN.create(f"{models}/face_detection_yunet_2023mar.onnx", "", (rgb.shape[1], rgb.shape[0]), 0.8, 0.3)
            _, cvf = yn.detect(rgb[:, :, ::-1].copy())
            cvf = [] if cvf is None else cvf
            print("  opencv faces:", [[round(v) for v in f[:4]] for f in cvf])
            sf = cv2.FaceRecognizerSF.create(f"{models}/face_recognition_sface_2021dec_int8.onnx", "")
            for f, e in zip(faces, embs):
                best = None
                for g in cvf:
                    if abs(g[0] - f[1]) < 0.2 * f[3] and abs(g[1] - f[2]) < 0.2 * f[4]:
                        best = g
                if best is None:
                    print("  no opencv match for", [round(v) for v in f[1:5]])
                    continue
                crop = sf.alignCrop(rgb[:, :, ::-1].copy(), best)
                v = sf.feature(crop)[0].astype(np.float64)
                v /= np.linalg.norm(v)
                print("  cosine with opencv's embedding:", round(float(v @ e), 4))
    if cmd == "expect":
        with open(sys.argv[3], "w", encoding="utf-8") as fh:
            json.dump(results, fh, indent=1)
    if cmd == "check":
        names = list(results)
        for i in range(len(names)):
            for j in range(i + 1, len(names)):
                for a in results[names[i]]["faces"]:
                    for b in results[names[j]]["faces"]:
                        print(f"  {names[i]} vs {names[j]}: {round(float(np.dot(a['embedding'], b['embedding'])), 3)}")


if __name__ == "__main__":
    main()
