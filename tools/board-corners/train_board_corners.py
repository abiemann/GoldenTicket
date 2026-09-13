#!/usr/bin/env python3
"""Train an experimental, local-only corner heatmap model on projective photos.

The board photographs are already manually rectified. Their image boundaries
provide synthetic corner labels when pasted into larger camera-like frames.
This is not a substitute for a held-out collection of real camera frames.
No network calls, uploads, changes to source photos, or camera access occur.
"""
from __future__ import annotations

import argparse
from datetime import datetime, timezone
import hashlib
import json
import math
from pathlib import Path
import random
import time

import cv2
import numpy as np
import torch
from torch import nn
from torch.nn import functional as F

SIZE, HEATMAP = 384, 192
SEED = 20260913
ORDER = ["top-left", "top-right", "bottom-right", "bottom-left"]


def sha256(path):
    return hashlib.sha256(Path(path).read_bytes()).hexdigest()


def save_json(path, value):
    Path(path).write_text(json.dumps(value, indent=2) + "\n", encoding="utf-8")


def letterbox(image):
    height, width = image.shape[:2]
    scale = SIZE / max(width, height)
    resized_width, resized_height = int(width * scale + .5), int(height * scale + .5)
    left, top = (SIZE - resized_width) // 2, (SIZE - resized_height) // 2
    result = np.full((SIZE, SIZE, 3), 114, np.uint8)
    result[top:top + resized_height, left:left + resized_width] = cv2.resize(
        image, (resized_width, resized_height), interpolation=cv2.INTER_LINEAR)
    return result, (left, top, resized_width, resized_height, width, height)


def decode(heatmaps, geometry):
    left, top, rw, rh, width, height = geometry
    corners, confidence = [], []
    for heatmap in heatmaps:
        py, px = np.unravel_index(heatmap.argmax(), heatmap.shape)
        x1, x2, y1, y2 = max(0, px - 2), min(HEATMAP, px + 3), max(0, py - 2), min(HEATMAP, py + 3)
        weights = heatmap[y1:y2, x1:x2].astype(np.float64)
        yy, xx = np.mgrid[y1:y2, x1:x2]
        cx, cy = (xx * weights).sum() / weights.sum(), (yy * weights).sum() / weights.sum()
        corners.append([((cx + .5) * 2 - left) * width / rw - .5,
                        ((cy + .5) * 2 - top) * height / rh - .5])
        confidence.append(float(heatmap[py, px]))
    return np.array(corners), np.array(confidence)


class Block(nn.Module):
    def __init__(self, incoming, outgoing, stride=1):
        super().__init__()
        self.layers = nn.Sequential(nn.Conv2d(incoming, outgoing, 3, stride, 1), nn.ReLU(),
                                    nn.Conv2d(outgoing, outgoing, 3, padding=1), nn.ReLU())

    def forward(self, pixels):
        return self.layers(pixels)


class CornerNet(nn.Module):
    def __init__(self):
        super().__init__()
        self.e0, self.e1, self.e2, self.e3, self.e4 = Block(3, 12, 2), Block(12, 24, 2), Block(24, 48, 2), Block(48, 72, 2), Block(72, 96, 2)
        self.d3, self.d2, self.d1, self.d0 = Block(168, 72), Block(120, 48), Block(72, 24), Block(36, 12)
        self.head = nn.Conv2d(12, 4, 1)
        nn.init.constant_(self.head.bias, -4.6)

    def forward(self, pixels):
        a = self.e0(pixels)
        b, c, d, e = self.e1(a), None, None, None
        c = self.e2(b)
        d = self.e3(c)
        e = self.e4(d)
        for block, skip in [(self.d3, d), (self.d2, c), (self.d1, b), (self.d0, a)]:
            e = block(torch.cat([F.interpolate(e, scale_factor=2, mode="bilinear", align_corners=False), skip], 1))
        return self.head(e).sigmoid()


class SyntheticFrames:
    def __init__(self, photos, seed, real_background=None):
        self.photos = photos
        self.rng = np.random.default_rng(seed)
        self.real_background = real_background
        self.yy, self.xx = np.mgrid[:HEATMAP, :HEATMAP]

    def sample(self):
        rng = self.rng
        width = SIZE
        height = int(rng.choice([216, 240, 256, 288, 320, 384], p=[.55, .08, .07, .2, .05, .05]))
        base = rng.uniform(25, 205, 3)
        if self.real_background is not None and rng.random() < .35:
            background = cv2.resize(self.real_background, (width, height)).astype(np.float32)
            background = background * rng.uniform(.6, 1.5) + rng.uniform(-25, 25)
        else:
            background = np.full((height, width, 3), base, np.float32)
        noise = rng.normal(0, rng.uniform(1, 14), (height, width, 1))
        gradient = np.linspace(rng.uniform(-25, 25), rng.uniform(-25, 25), width)[None, :, None]
        background = np.clip(background + noise + gradient, 0, 255).astype(np.uint8)
        # Background lines and fabric/wood-like variation break a plain-paper shortcut.
        for _ in range(int(rng.integers(0, 7))):
            start, end = tuple(rng.integers(0, width, 2)), tuple(rng.integers(0, height, 2))
            color = tuple(int(i) for i in np.clip(base + rng.uniform(-45, 45, 3), 0, 255))
            cv2.line(background, (start[0], end[0]), (start[1], end[1]), color, int(rng.integers(1, 6)))
        mode = rng.random()
        corners = None
        if mode >= .15:
            photo = self.photos[int(rng.integers(len(self.photos)))]["pixels"]
            # Mirror/half-turn texture without changing screen-ordered labels.
            if rng.random() < .25:
                photo = photo[::-1, ::-1]
            bh = rng.uniform(.52, .94) * height
            bw = min(width * .94, bh * rng.uniform(1.4, 1.85))
            bh = min(bh, bw / 1.4)
            cx = rng.uniform(bw / 2 + 3, width - bw / 2 - 3)
            cy = rng.uniform(bh / 2 + 3, height - bh / 2 - 3)
            jitter = min(bh * .09, 13)
            corners = np.array([[cx-bw/2, cy-bh/2], [cx+bw/2, cy-bh/2],
                                [cx+bw/2, cy+bh/2], [cx-bw/2, cy+bh/2]], np.float32)
            corners += rng.uniform(-jitter, jitter, (4, 2)).astype(np.float32)
            corners[:, 0] = corners[:, 0].clip(1, width-2)
            corners[:, 1] = corners[:, 1].clip(1, height-2)
            if mode >= .27 and rng.random() < .4:
                # The user's real overhead frame almost fills the image height.
                # Teach visible edge corners rather than requiring a wide margin.
                edge = rng.uniform(.5, 7)
                bw = min(width-2*edge, (height-2*edge)*rng.uniform(1.42,1.65))
                x = rng.uniform(edge,max(edge,width-bw-edge))
                corners = np.array([[x,edge],[x+bw,edge],[x+bw,height-edge-1],[x,height-edge-1]],np.float32)
                corners += rng.uniform(-edge*.4,edge*.4,(4,2)).astype(np.float32)
            if mode < .27:  # Cut-off boards: missing corners have no positive label.
                corners += np.array([rng.choice([-1, 1]) * width * rng.uniform(.15, .4),
                                     rng.choice([-1, 1]) * height * rng.uniform(.05, .3)], np.float32)
            src = np.array([[0, 0], [photo.shape[1]-1, 0], [photo.shape[1]-1, photo.shape[0]-1], [0, photo.shape[0]-1]], np.float32)
            matrix = cv2.getPerspectiveTransform(src, corners)
            board = cv2.warpPerspective(photo, matrix, (width, height), flags=cv2.INTER_LINEAR)
            mask = cv2.warpPerspective(np.full(photo.shape[:2], 255, np.uint8), matrix, (width, height), flags=cv2.INTER_LINEAR).astype(np.float32)/255
            # Soft cast shadow outside the actual board boundary.
            shadow = cv2.GaussianBlur(mask, (9, 9), 2)
            background = (background.astype(np.float32) * (1-.28*shadow[..., None])).astype(np.uint8)
            background = np.clip(background*(1-mask[..., None])+board*mask[..., None], 0, 255).astype(np.uint8)
        background = np.clip(background.astype(np.float32)*rng.uniform(.65, 1.35)+rng.uniform(-15, 15), 0, 255).astype(np.uint8)
        if rng.random() < .3:
            background = cv2.GaussianBlur(background, (3, 3), rng.uniform(.3, 1.1))
        if rng.random() < .15:
            ok, jpg = cv2.imencode('.jpg', background, [cv2.IMWRITE_JPEG_QUALITY, int(rng.integers(45, 95))])
            if ok:
                background = cv2.imdecode(jpg, cv2.IMREAD_COLOR)
        image, geometry = letterbox(background)
        targets = np.zeros((4, HEATMAP, HEATMAP), np.float32)
        visible = np.zeros(4, bool)
        if corners is not None:
            left, top, rw, rh, width, height = geometry
            for index, (x, y) in enumerate(corners):
                if 0 <= x <= width-1 and 0 <= y <= height-1:
                    visible[index] = True
                    hx, hy = (((x+.5)*rw/width + left)/2-.5), (((y+.5)*rh/height + top)/2-.5)
                    target = np.exp(-((self.xx-hx)**2+(self.yy-hy)**2)/(2*1.8**2))
                    target[int(hy+.5), int(hx+.5)] = 1
                    targets[index] = target
        return image, targets, corners, visible, geometry


def focal_loss(prediction, target):
    prediction = prediction.clamp(1e-5, 1-1e-5)
    positive = target.eq(1).float()
    negative = (1-target).pow(4) * (1-positive)
    loss = -positive * (1-prediction).pow(2) * prediction.log() - negative * prediction.pow(2) * (1-prediction).log()
    return loss.sum() / positive.sum().clamp(min=1)


def tensor(images, device):
    return torch.from_numpy(np.stack(images)[:, :, :, ::-1].transpose(0,3,1,2).copy()).to(device).float()/255


def evaluate(model, frames, device, threshold=.55):
    errors, accepted_errors, true_full, accepted_full, rejected_partial, partial_count = [], [], 0, 0, 0, 0
    confidence_complete, confidence_incomplete = [], []
    model.eval()
    with torch.no_grad():
        for start in range(0, len(frames), 16):
            batch = frames[start:start+16]
            maps = model(tensor([f[0] for f in batch], device)).cpu().numpy()
            for frame, heatmaps in zip(batch, maps):
                _, _, corners, visible, geometry = frame
                predictions, confidence = decode(heatmaps, geometry)
                complete = bool(visible.all())
                accepted = bool((confidence >= threshold).all())
                if complete:
                    distance = np.linalg.norm(predictions-corners, axis=1)
                    errors.extend(distance.tolist())
                    confidence_complete.append(float(confidence.min()))
                    true_full += 1
                    if accepted:
                        accepted_full += 1
                        accepted_errors.extend(distance.tolist())
                else:
                    partial_count += 1
                    rejected_partial += int(not accepted)
                    confidence_incomplete.append(float(confidence.min()))
    return {"acceptanceRule":"confidence-only; app additionally checks in-frame coordinates and board geometry",
            "frames":len(frames), "completeBoards":true_full, "acceptedCompleteBoards":accepted_full,
            "incompleteOrAbsentBoards":partial_count, "rejectedIncompleteOrAbsentBoards":rejected_partial,
            "allCornerMedianErrorAt384":float(np.median(errors)), "allCornerP95ErrorAt384":float(np.percentile(errors,95)),
            "acceptedCornerMedianErrorAt384":float(np.median(accepted_errors)) if accepted_errors else None,
            "acceptedCornerP95ErrorAt384":float(np.percentile(accepted_errors,95)) if accepted_errors else None,
            "minimumCompleteConfidenceP10":float(np.percentile(confidence_complete,10)),
            "maximumIncompleteConfidence":max(confidence_incomplete,default=0)}


def export(model, output, provenance, evaluation):
    model = model.cpu().eval()
    model_path = output / "board-corners.onnx"
    torch.onnx.export(model, torch.zeros(1,3,SIZE,SIZE), model_path, input_names=["images"],
                      output_names=["heatmaps"], opset_version=17, dynamo=False)
    import onnx
    import onnxruntime as ort
    graph = onnx.load(model_path)
    onnx.checker.check_model(graph)
    ort.disable_telemetry_events()
    session = ort.InferenceSession(str(model_path), providers=["CPUExecutionProvider"])
    check = np.random.default_rng(SEED).random((1,3,SIZE,SIZE), dtype=np.float32)
    with torch.no_grad():
        expected = model(torch.from_numpy(check)).numpy()
    actual = session.run(None, {"images":check})[0]
    error = float(np.abs(actual-expected).max())
    if error > 2e-4:
        raise RuntimeError(f"ONNX parity failure: {error}")
    manifest = {"version":1, "modelId":"goldenticket-board-corners-unet-v1", "modelFile":model_path.name,
                "modelSha256":sha256(model_path), "architecture":"board-corner-unet", "inputName":"images",
                "outputName":"heatmaps", "inputSize":SIZE, "heatmapSize":HEATMAP, "cornerOrder":ORDER,
                "channelOrder":"RGB", "pixelScale":1/255, "pixelOffset":0, "paddingValue":114,
                "resize":"bilinear-half-pixel", "letterbox":"center-round-half-up", "decoder":"peak-centroid-5x5",
                "confidenceThreshold":.55, "opset":17, "experimental":True,
                "evaluationRole":"Synthetic projective placements of held-out photo dates and seeds; not real camera accuracy",
                "training":provenance, "syntheticValidation":evaluation, "onnxMaximumAbsoluteError":error,
                "operators":sorted({node.op_type for node in graph.graph.node})}
    save_json(output / "manifest.json", manifest)
    return manifest


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--photos", type=Path, default=Path("C:/temp"))
    parser.add_argument("--output", type=Path, default=Path("artifacts/board-corners/model"))
    parser.add_argument("--steps", type=int, default=1400)
    parser.add_argument("--batch", type=int, default=16)
    parser.add_argument("--resume", type=Path)
    args = parser.parse_args()
    args.output.mkdir(parents=True, exist_ok=True)
    cv2.setNumThreads(1)
    torch.set_num_threads(4)
    torch.manual_seed(SEED)
    np.random.seed(SEED)
    random.seed(SEED)
    device = "cuda" if torch.cuda.is_available() else "cpu"
    photos = []
    for path in sorted(args.photos.glob("GoldenTicket-board-*.png")):
        image = cv2.imread(str(path))
        photos.append({"path":path, "pixels":cv2.resize(image,(768,480)), "sha256":sha256(path)})
    training = [p for p in photos if "20260912" in p["path"].name]
    validation = [p for p in photos if "20260913" in p["path"].name]
    if not training or not validation:
        raise ValueError("Both capture-date groups are required")
    # Only this background strip is consumed, excluding the board and UI.
    screenshot = Path("C:/Users/abiem/AppData/Local/Temp/codex-clipboard-b5342d93-e796-45ef-b8f2-bdd4304918d3.png")
    background = cv2.imread(str(screenshot))[600:1100, 1450:1590] if screenshot.exists() else None
    generator = SyntheticFrames(training, SEED, background)
    validator = SyntheticFrames(validation, SEED+1, background)
    validation_frames = [validator.sample() for _ in range(240)]
    model = CornerNet().to(device)
    if args.resume:
        model.load_state_dict(torch.load(args.resume, map_location=device, weights_only=True))
    optimizer = torch.optim.AdamW(model.parameters(), lr=.001, weight_decay=.0001)
    started = datetime.now(timezone.utc).isoformat()
    timer, best_score, history = time.perf_counter(), float("inf"), []
    for step in range(1,args.steps+1):
        model.train()
        samples = [generator.sample() for _ in range(args.batch)]
        images = tensor([s[0] for s in samples], device)
        target = torch.from_numpy(np.stack([s[1] for s in samples])).to(device)
        optimizer.zero_grad(set_to_none=True)
        prediction = model(images)
        loss = focal_loss(prediction, target)
        loss.backward()
        torch.nn.utils.clip_grad_norm_(model.parameters(),10)
        optimizer.step()
        for group in optimizer.param_groups:
            group["lr"] = .001*(.15+.85*(1-step/args.steps))
        if step % 50 == 0:
            print(json.dumps({"step":step,"loss":round(float(loss.detach()),4),"elapsedSeconds":round(time.perf_counter()-timer,1)}),flush=True)
        if step % 200 == 0 or step == args.steps:
            metrics = evaluate(model,validation_frames,device)
            score = metrics["allCornerP95ErrorAt384"] + 100*(1-metrics["acceptedCompleteBoards"]/max(1,metrics["completeBoards"]))
            history.append({"step":step,**metrics})
            print(json.dumps(history[-1]),flush=True)
            if score < best_score:
                best_score = score
                torch.save(model.state_dict(),args.output/"best.pth")
                save_json(args.output/"synthetic-validation.json", history[-1])
    save_json(args.output/"history.json",history)
    model.load_state_dict(torch.load(args.output/"best.pth",map_location=device,weights_only=True))
    provenance = {"startedAt":started,"elapsedSeconds":time.perf_counter()-timer,"seed":SEED,"steps":args.steps,
                  "batchSize":args.batch,"architecture":"Compact U-Net trained from scratch, 4 corner heatmaps",
                  "device":torch.cuda.get_device_name(0) if device=="cuda" else "CPU", "torch":torch.__version__,
                  "resumeCheckpointSha256":sha256(args.resume) if args.resume else None,
                  "opencv":cv2.__version__,"numpy":np.__version__,"backgroundScreenshotSha256":sha256(screenshot) if screenshot.exists() else None,
                  "backgroundScreenshotRegion":[1450,600,1590,1100],
                  "trainingPhotos":[{"fileName":p["path"].name,"sha256":p["sha256"]} for p in training],
                  "validationPhotos":[{"fileName":p["path"].name,"sha256":p["sha256"]} for p in validation]}
    manifest=export(model,args.output,provenance,json.loads((args.output/"synthetic-validation.json").read_text()))
    print(json.dumps({"exported":str(args.output),"sha256":manifest["modelSha256"],"operators":manifest["operators"]}),flush=True)


if __name__ == "__main__":
    main()
