#!/usr/bin/env python3
"""Local-only YOLOX-Nano experiment, object evaluation, and fixed ONNX export.

Requires the pinned official YOLOX checkout and independently downloaded official
COCO checkpoint; this program performs no network requests. Source photos and
reviewed annotations are read-only. Splits are respected unless --all-reviewed is
explicit, in which case the manifest makes the absence of held-out metrics clear.
"""
from __future__ import annotations

import argparse
from collections import Counter, defaultdict
from copy import deepcopy
from datetime import datetime, timezone
import hashlib
import json
import math
from pathlib import Path
import random
import subprocess
import sys
import time

import cv2
import numpy as np
import torch
from torch.utils.data import Dataset, DataLoader
from torchvision.ops import batched_nms

from prepare_dataset import read_labels, validate_labels

SOURCE_REVISION = "6ddff4824372906469a7fae2dc3206c7aa4bbaee"
PRETRAINED_SHA256 = "cd28f55fbbc1829f99d9ac9b38a16d259a22889739c8728ea877610201feff7b"
CLASSES = ["train", "player-marker"]
BOARD_W, BOARD_H, TILE, STRIDE = 1920, 1200, 640, 512
USE_TILE_OWNERSHIP = False


def sha256(path):
    return hashlib.sha256(Path(path).read_bytes()).hexdigest()


def save_json(path, data):
    Path(path).write_text(json.dumps(data, indent=2) + "\n", encoding="utf-8")


def tile_starts(length):
    end = max(length - TILE, 0)
    starts = list(range(0, end + 1, STRIDE))
    if starts[-1] != end:
        starts.append(end)
    return starts


def ownership_bounds(starts, index, length):
    """Bisect overlaps so a physical piece is taken from an uncut tile view."""
    start = starts[index]
    low = 0 if index == 0 else (starts[index-1] + TILE + start)/2
    high = length if index == len(starts)-1 else (start + TILE + starts[index+1])/2
    return low, high


def load_boards(labels, source):
    result = []
    for item in labels:
        path = source / item["fileName"]
        if sha256(path) != item["sha256"]:
            raise ValueError(f"Source hash mismatch: {path.name}")
        raw = cv2.imread(str(path), cv2.IMREAD_COLOR)
        if raw is None or raw.shape[:2] != (item["height"], item["width"]):
            raise ValueError(f"Source cannot be fully decoded at labelled size: {path.name}")
        pixels = cv2.resize(raw, (BOARD_W, BOARD_H), interpolation=cv2.INTER_LINEAR)
        sx, sy = BOARD_W / item["width"], BOARD_H / item["height"]
        boxes = np.array([[b["x"]*sx, b["y"]*sy,
                           (b["x"]+b["width"])*sx, (b["y"]+b["height"])*sy,
                           CLASSES.index(b["kind"])] for b in item["boxes"]], dtype=np.float32).reshape(-1, 5)
        result.append({"item": item, "pixels": pixels, "boxes": boxes})
    return result


class TrainingTiles(Dataset):
    """Random crops, including positives and empty background, only from train groups."""
    def __init__(self, boards, samples):
        self.boards, self.samples = boards, samples

    def __len__(self):
        return self.samples

    def __getitem__(self, index):
        board = random.choice(self.boards)
        side = random.randint(512, 768)
        boxes = board["boxes"].copy()
        if len(boxes) and random.random() < .8:
            chosen = random.choice(boxes)
            x = round((chosen[0]+chosen[2])/2 - side/2 + random.uniform(-side*.3, side*.3))
            y = round((chosen[1]+chosen[3])/2 - side/2 + random.uniform(-side*.3, side*.3))
            x, y = int(np.clip(x, 0, BOARD_W-side)), int(np.clip(y, 0, BOARD_H-side))
        else:
            x, y = random.randint(0, BOARD_W-side), random.randint(0, BOARD_H-side)
        pixels = board["pixels"][y:y+side, x:x+side].copy()
        boxes[:, [0,2]] -= x
        boxes[:, [1,3]] -= y
        boxes[:, :4] = boxes[:, :4].clip(0, side)
        boxes = boxes[(boxes[:,2]-boxes[:,0] >= 3) & (boxes[:,3]-boxes[:,1] >= 3)]
        pixels = cv2.resize(pixels, (TILE, TILE), interpolation=cv2.INTER_LINEAR)
        boxes[:, :4] *= TILE/side
        if random.random() < .5:
            pixels = pixels[:, ::-1]
            boxes[:, [0,2]] = TILE - boxes[:, [2,0]]
        if random.random() < .5:
            pixels = pixels[::-1]
            boxes[:, [1,3]] = TILE - boxes[:, [3,1]]
        for _ in range(random.randrange(4)):
            pixels = np.rot90(pixels)
            boxes[:, :4] = boxes[:, [1,2,3,0]]
            boxes[:, [1,3]] = TILE - boxes[:, [1,3]]
        # Preserve physical colors while varying exposure and contrast.
        gain = random.uniform(.7, 1.3)
        offset = random.uniform(-18, 18)
        pixels = np.clip(pixels.astype(np.float32)*gain + offset, 0, 255).astype(np.uint8)
        if random.random() < .2:
            pixels = cv2.GaussianBlur(pixels, (3,3), random.uniform(.2, .8))
        labels = np.zeros((128,5), dtype=np.float32)
        if len(boxes) > len(labels):
            raise ValueError("Tile contains more than 128 objects; increase target capacity")
        labels[:len(boxes),0] = boxes[:,4]
        labels[:len(boxes),1:3] = (boxes[:,:2]+boxes[:,2:4])/2
        labels[:len(boxes),3:5] = boxes[:,2:4]-boxes[:,:2]
        return torch.from_numpy(np.ascontiguousarray(pixels.transpose(2,0,1), dtype=np.float32)), torch.from_numpy(labels)


def create_model(checkout, checkpoint=None):
    revision = subprocess.check_output(["git", "-C", str(checkout), "rev-parse", "HEAD"], text=True).strip()
    if revision != SOURCE_REVISION:
        raise ValueError(f"YOLOX source revision must be {SOURCE_REVISION}, got {revision}")
    sys.path.insert(0, str(checkout.resolve()))
    from yolox.models import YOLOX, YOLOPAFPN, YOLOXHead
    model = YOLOX(YOLOPAFPN(.33, .25, depthwise=True), YOLOXHead(2, .25, depthwise=True))
    for module in model.modules():
        if isinstance(module, torch.nn.BatchNorm2d):
            module.eps, module.momentum = 1e-3, .03
    model.head.initialize_biases(1e-2)
    if checkpoint:
        state = torch.load(checkpoint, map_location="cpu", weights_only=True)["model"]
        own = model.state_dict()
        usable = {k:v for k,v in state.items() if k in own and v.shape == own[k].shape}
        missing = model.load_state_dict(usable, strict=False).missing_keys
        print(f"Transferred {len(usable)} tensors; reinitialized {len(missing)} class-head tensors", flush=True)
    return model


def predict_board(model, pixels, device, minimum=.01):
    tiles, positions = [], []
    xs, ys = tile_starts(BOARD_W), tile_starts(BOARD_H)
    for yi,y in enumerate(ys):
        for xi,x in enumerate(xs):
            tiles.append(pixels[y:y+TILE, x:x+TILE].transpose(2,0,1))
            positions.append((x,y,ownership_bounds(xs,xi,BOARD_W),ownership_bounds(ys,yi,BOARD_H)))
    candidates = []
    with torch.inference_mode():
        for start in range(0, len(tiles), 4):
            batch = torch.from_numpy(np.ascontiguousarray(tiles[start:start+4], dtype=np.float32)).to(device)
            output = model(batch).float().cpu()
            for row, (x,y,xb,yb) in zip(output, positions[start:start+4]):
                probability, kind = row[:,5:].max(1)
                confidence = row[:,4] * probability
                good = (confidence >= minimum) & torch.isfinite(row).all(1)
                row, confidence, kind = row[good], confidence[good], kind[good]
                coords = torch.cat((row[:,:2]-row[:,2:4]/2, row[:,:2]+row[:,2:4]/2), dim=1)
                coords[:,[0,2]] = coords[:,[0,2]].clamp(0,TILE) + x
                coords[:,[1,3]] = coords[:,[1,3]].clamp(0,TILE) + y
                good = (coords[:,2]-coords[:,0]>=1) & (coords[:,3]-coords[:,1]>=1)
                if USE_TILE_OWNERSHIP:
                    center = (coords[:,:2]+coords[:,2:4])/2
                    good &= (center[:,0]>=xb[0]) & (center[:,0]<xb[1]) & (center[:,1]>=yb[0]) & (center[:,1]<yb[1])
                candidates.append(torch.cat((coords[good], confidence[good,None], kind[good,None]), dim=1))
    all_boxes = torch.cat(candidates) if candidates else torch.zeros((0,6))
    if len(all_boxes)>4096:
        all_boxes = all_boxes[torch.argsort(all_boxes[:,4],descending=True)[:4096]]
    keep = batched_nms(all_boxes[:,:4], all_boxes[:,4], all_boxes[:,5], .45)
    return all_boxes[keep[:512]].numpy()


def iou(box, others):
    intersection = np.maximum(0, np.minimum(box[2:4], others[:,2:4]) - np.maximum(box[:2],others[:,:2])).prod(1)
    union = (box[2]-box[0])*(box[3]-box[1]) + (others[:,2]-others[:,0])*(others[:,3]-others[:,1]) - intersection
    return intersection / np.maximum(union, 1e-9)


def score_predictions(boards, predictions, threshold):
    totals = Counter(tp=0, fp=0, fn=0)
    per_class = {kind:Counter(tp=0,fp=0,fn=0) for kind in CLASSES}
    per_color = defaultdict(lambda: Counter(matched=0,total=0))
    images = []
    for board, original_predictions in zip(boards, predictions):
        detections = original_predictions[original_predictions[:,4] >= threshold]
        gt = board["boxes"]
        used, matched, false = set(), [], []
        for pred in detections:
            same = [k for k,g in enumerate(gt) if g[4] == pred[5] and k not in used]
            overlaps = iou(pred, gt[same]) if same else np.zeros(0)
            if len(overlaps) and overlaps.max() >= .5:
                gi = same[int(overlaps.argmax())]
                used.add(gi)
                matched.append({"labelIndex":gi,"prediction":pred.tolist(),"iou":float(overlaps.max())})
                per_class[CLASSES[int(pred[5])]]["tp"] += 1
            else:
                false.append(pred.tolist())
                per_class[CLASSES[int(pred[5])]]["fp"] += 1
        missed = [k for k in range(len(gt)) if k not in used]
        for k in missed:
            per_class[CLASSES[int(gt[k,4])]]["fn"] += 1
        for k,b in enumerate(board["item"]["boxes"]):
            color_key = f"{b['kind']}/{b['color']}"
            per_color[color_key]["total"] += 1
            per_color[color_key]["matched"] += k in used
        counts = {"tp":len(matched),"fp":len(false),"fn":len(missed)}
        totals.update(counts)
        images.append({"fileName":board["item"]["fileName"],"group":board["item"]["group"],
                       "groundTruthCount":len(gt),"predictionCount":len(detections),**counts,
                       "missedLabelIndices":missed,"falsePositives":false,"matched":matched})
    def rates(counts):
        tp,fp,fn = (counts[k] for k in ("tp","fp","fn"))
        return {**counts,"precision":tp/max(1,tp+fp),"recall":tp/max(1,tp+fn),"f1":2*tp/max(1,2*tp+fp+fn)}
    return {"confidenceThreshold":threshold,"matchingIou":.5,"summary":rates(totals),
            "perClass":{k:rates(v) for k,v in per_class.items()},
            "perColorRecall":{k:{**v,"recall":v["matched"]/v["total"]} for k,v in per_color.items()},
            "images":images}


def evaluate(model, boards, device):
    model.eval()
    start = time.perf_counter()
    predictions = [predict_board(model,b["pixels"],device) for b in boards]
    elapsed = time.perf_counter()-start
    # Threshold choice is explicitly validation tuning, never an untouched test score.
    candidates = [score_predictions(boards,predictions,t) for t in (.10,.15,.20,.25,.30,.40,.50,.60)]
    best = max(candidates, key=lambda x:(x["summary"]["f1"],x["summary"]["precision"]))
    best["thresholdSweep"] = [{"threshold":r["confidenceThreshold"],**r["summary"]} for r in candidates]
    best["inferenceSeconds"] = elapsed
    best["millisecondsPerBoard"] = elapsed/max(1,len(boards))*1000
    return best, predictions


def render_evaluation(boards, evaluation, output):
    """Local diagnostic overlays: reviewed boxes left, ML predictions right."""
    output.mkdir(parents=True,exist_ok=True)
    thumbnails = []
    rows = {i["fileName"]:i for i in evaluation["images"]}
    for board in sorted(boards,key=lambda b:-(rows[b["item"]["fileName"]]["fp"]+rows[b["item"]["fileName"]]["fn"])):
        row = rows[board["item"]["fileName"]]
        truth, predicted = board["pixels"].copy(), board["pixels"].copy()
        for box in board["boxes"]:
            cv2.rectangle(truth,tuple(np.rint(box[:2]).astype(int)),tuple(np.rint(box[2:4]).astype(int)),(255,255,0),2)
        for match in row["matched"]:
            box = match["prediction"]
            cv2.rectangle(predicted,tuple(np.rint(box[:2]).astype(int)),tuple(np.rint(box[2:4]).astype(int)),(255,255,255),2)
        for box in row["falsePositives"]:
            cv2.rectangle(predicted,tuple(np.rint(box[:2]).astype(int)),tuple(np.rint(box[2:4]).astype(int)),(0,0,255),3)
        for index in row["missedLabelIndices"]:
            box = board["boxes"][index]
            cv2.rectangle(predicted,tuple(np.rint(box[:2]).astype(int)),tuple(np.rint(box[2:4]).astype(int)),(0,165,255),3)
        header = np.full((64,BOARD_W*2,3),245,np.uint8)
        caption = f"{row['fileName']}  reviewed={row['groundTruthCount']}  matched={row['tp']}  false positives={row['fp']}  missed={row['fn']}"
        cv2.putText(header,caption,(20,24),cv2.FONT_HERSHEY_SIMPLEX,.8,(20,20,20),2)
        cv2.putText(header,"LEFT: reviewed boxes (cyan). RIGHT: matches (white), false positives (red), missed labels (orange).",(20,52),cv2.FONT_HERSHEY_SIMPLEX,.7,(20,20,20),2)
        combined = np.vstack((header,np.hstack((truth,predicted))))
        cv2.imwrite(str(output/(Path(row["fileName"]).stem+"-review.png")),combined)
        thumb = cv2.resize(combined,(1280,421),interpolation=cv2.INTER_AREA)
        thumbnails.append(thumb)
    if thumbnails:
        cv2.imwrite(str(output/"most-errors-contact-sheet.png"),np.vstack(thumbnails[:4]))


def export_model(model, output, manifest, example):
    import onnx
    import onnxruntime as ort
    ort.disable_telemetry_events()
    model = deepcopy(model).cpu().eval()
    model.head.decode_in_inference = True
    tensor = torch.from_numpy(np.ascontiguousarray(example[:TILE,:TILE].transpose(2,0,1)[None],dtype=np.float32))
    path = output/"piece-detector.onnx"
    with torch.inference_mode():
        expected = model(tensor).numpy()
        torch.onnx.export(model,tensor,str(path),input_names=["images"],output_names=["detections"],
                          opset_version=17,dynamo=False,do_constant_folding=True)
    onnx.checker.check_model(onnx.load(path))
    options = ort.SessionOptions()
    options.intra_op_num_threads = 4
    session = ort.InferenceSession(str(path),sess_options=options,providers=["CPUExecutionProvider"])
    actual = session.run(["detections"],{"images":tensor.numpy()})[0]
    if actual.shape != (1,8400,7) or not np.isfinite(actual).all():
        raise ValueError("Invalid ONNX decoded detections")
    if not np.allclose(expected,actual,rtol=1e-3,atol=2e-3):
        raise ValueError(f"ONNX parity failed; max difference {np.max(np.abs(expected-actual))}")
    manifest["modelSha256"] = sha256(path)
    manifest["onnxParity"] = {"provider":"CPUExecutionProvider","shape":list(actual.shape),
                              "maxAbsoluteDifference":float(np.max(np.abs(expected-actual))),"rtol":1e-3,"atol":2e-3}
    save_json(output/"manifest.json",manifest)
    # A raw local fixture permits the C# reader to check the exact contract.
    cv2.imwrite(str(output/"parity-board.png"),example)
    (output/"parity-tensor.f32").write_bytes(tensor.numpy().tobytes())
    (output/"parity-output.f32").write_bytes(actual.tobytes())
    return manifest


def main():
    global USE_TILE_OWNERSHIP
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--labels",type=Path,required=True)
    parser.add_argument("--images",type=Path,required=True)
    parser.add_argument("--yolox-source",type=Path,required=True)
    parser.add_argument("--pretrained",type=Path,required=True)
    parser.add_argument("--output",type=Path,required=True)
    parser.add_argument("--epochs",type=int,default=60)
    parser.add_argument("--samples-per-epoch",type=int,default=256)
    parser.add_argument("--batch-size",type=int,default=16)
    parser.add_argument("--seed",type=int,default=20260913)
    parser.add_argument("--all-reviewed",action="store_true")
    parser.add_argument("--tile-ownership",action="store_true",help="Suppress overlap fragments using center ownership")
    parser.add_argument("--confidence",type=float,help="Explicit preview threshold; retain the validation threshold sweep")
    parser.add_argument("--initialize",type=Path,help="Optional prior local two-class checkpoint, after provenance verification")
    parser.add_argument("--export-checkpoint",type=Path,help="Only evaluate/export this local two-class checkpoint")
    args = parser.parse_args()
    USE_TILE_OWNERSHIP = args.tile_ownership
    if not torch.cuda.is_available():
        raise RuntimeError("CUDA GPU required for this bounded training experiment")
    if min(args.epochs,args.samples_per_epoch,args.batch_size) < 1:
        raise ValueError("Training dimensions must be positive")
    if args.confidence is not None and not 0<args.confidence<1:
        raise ValueError("Confidence must be between zero and one")
    if not args.export_checkpoint and args.output.exists() and any(args.output.iterdir()):
        raise FileExistsError("Training output already contains a run; choose a new directory to preserve baseline evidence")
    args.output.mkdir(parents=True,exist_ok=True)
    random.seed(args.seed); np.random.seed(args.seed); torch.manual_seed(args.seed); torch.cuda.manual_seed_all(args.seed)
    torch.set_num_threads(4)
    cv2.setNumThreads(1)
    torch.backends.cudnn.benchmark = False
    torch.backends.cudnn.deterministic = True
    data,labels_sha = read_labels(args.labels)
    items = validate_labels(data)
    boards = load_boards(items,args.images)
    train = boards if args.all_reviewed else [b for b in boards if b["item"]["split"] == "train"]
    validation = [b for b in boards if b["item"]["split"] == "validation"]
    if not train or not validation:
        raise ValueError("Expected training and validation groups")
    if sha256(args.pretrained) != PRETRAINED_SHA256:
        raise ValueError("Official pretrained checkpoint hash mismatch")
    model = create_model(args.yolox_source,args.pretrained).cuda()
    if args.initialize:
        model.load_state_dict(torch.load(args.initialize,map_location="cpu",weights_only=True)["model"],strict=True)
    if args.export_checkpoint:
        checkpoint = torch.load(args.export_checkpoint,map_location="cpu",weights_only=True)
        model.load_state_dict(checkpoint["model"],strict=True)
    provenance = {"startedAt":datetime.now(timezone.utc).isoformat(),"command":sys.argv,
                  "labelsSha256":labels_sha,"seed":args.seed,"allReviewed":args.all_reviewed,
                  "trainingScriptSha256":sha256(__file__),"tileOwnership":args.tile_ownership,
                  "sourceRevision":SOURCE_REVISION,"sourceUrl":"https://github.com/Megvii-BaseDetection/YOLOX",
                  "pretrainedUrl":"https://github.com/Megvii-BaseDetection/YOLOX/releases/download/0.1.1rc0/yolox_nano.pth",
                  "pretrainedSha256":PRETRAINED_SHA256,"initializeSha256":sha256(args.initialize) if args.initialize else None,
                  "exportCheckpointSha256":sha256(args.export_checkpoint) if args.export_checkpoint else None,
                  "selectedCheckpointEpoch":checkpoint.get("epoch") if args.export_checkpoint else None,
                  "pythonVersion":sys.version,"torchVersion":torch.__version__,"cudaVersion":torch.version.cuda,
                  "gpu":torch.cuda.get_device_name(0),"epochs":args.epochs,"samplesPerEpoch":args.samples_per_epoch,
                  "batchSize":args.batch_size,"optimizer":"AdamW lr0.001 weight_decay0.0005; 3epoch warmup + cosine to0.00005",
                  "augmentation":"positive-focused/uniform random512..768 crops; horizontal/vertical flips; 90degree rotations; gain0.7..1.3 offset-18..18; 20%Gaussianblur",
                  "trainingFiles":[b["item"]["fileName"] for b in train],
                  "validationFiles":[b["item"]["fileName"] for b in validation],
                  "sources":[{"fileName":i["fileName"],"sha256":i["sha256"],"group":i["group"],"split":i["split"]} for i in items]}
    save_json(args.output/"run.json",provenance)
    (args.output/"environment-lock.txt").write_text(subprocess.check_output([sys.executable,"-m","pip","freeze"],text=True),encoding="utf-8")
    if not args.export_checkpoint:
        from yolox.utils import ModelEMA
        ema = ModelEMA(model,decay=.9998)
        loader = DataLoader(TrainingTiles(train,args.samples_per_epoch),batch_size=args.batch_size,
                            shuffle=False,num_workers=0,pin_memory=True)
        optimizer = torch.optim.AdamW(model.parameters(),lr=.001,weight_decay=.0005)
        scaler = torch.amp.GradScaler("cuda")
        best_f1, history = -1, []
        start_time = time.perf_counter()
        for epoch in range(args.epochs):
            model.train()
            losses = []
            for step,(pixels,targets) in enumerate(loader):
                progress = epoch+step/len(loader)
                lr = .001*min(1,(progress+1)/3) if progress < 3 else .00005+.00095*(1+math.cos(math.pi*(progress-3)/max(1,args.epochs-3)))/2
                for group in optimizer.param_groups:
                    group["lr"] = lr
                optimizer.zero_grad(set_to_none=True)
                with torch.amp.autocast("cuda"):
                    loss = model(pixels.cuda(non_blocking=True),targets.cuda(non_blocking=True))["total_loss"]
                if not torch.isfinite(loss):
                    raise RuntimeError("Nonfinite training loss")
                scaler.scale(loss).backward()
                scaler.unscale_(optimizer)
                torch.nn.utils.clip_grad_norm_(model.parameters(),10)
                scaler.step(optimizer); scaler.update(); ema.update(model)
                losses.append(float(loss.detach()))
            record = {"epoch":epoch+1,"loss":float(np.mean(losses)),"elapsedSeconds":time.perf_counter()-start_time}
            if (epoch+1)%10 == 0 or epoch == args.epochs-1:
                evaluation,_ = evaluate(ema.ema,validation,"cuda")
                record["validation"] = evaluation["summary"]
                record["confidenceThreshold"] = evaluation["confidenceThreshold"]
                # All-data runs are training diagnostics only, not held-out selection evidence.
                metric = evaluation["summary"]["f1"]
                if metric > best_f1:
                    best_f1 = metric
                    torch.save({"model":ema.ema.state_dict(),"epoch":epoch+1},args.output/"best.pth")
                    save_json(args.output/"best-evaluation.json",evaluation)
                torch.save({"model":ema.ema.state_dict(),"epoch":epoch+1},args.output/"last.pth")
            history.append(record)
            save_json(args.output/"history.json",history)
            print(json.dumps(record),flush=True)
        model.load_state_dict(torch.load(args.output/"best.pth",map_location="cpu",weights_only=True)["model"])
    evaluation,predictions = evaluate(model,validation,"cuda")
    if args.confidence is not None:
        selection = evaluation
        evaluation = score_predictions(validation,predictions,args.confidence)
        evaluation["thresholdSweep"] = selection["thresholdSweep"]
        evaluation["automaticF1Threshold"] = selection["confidenceThreshold"]
        evaluation["inferenceSeconds"] = selection["inferenceSeconds"]
        evaluation["millisecondsPerBoard"] = selection["millisecondsPerBoard"]
        evaluation["thresholdSelection"] = "Explicit preview operating point; selected from validation sweep and in-sample empty-board diagnostic"
    evaluation["evaluationRole"] = "in-sample diagnostic; all reviewed images were used to train" if args.all_reviewed else "held-out capture-date group used for model/threshold selection; not an independent test session"
    evaluation["limitations"] = ["Same physical board/camera setup across both date groups", "No untouched independent test session", "Validation has no empty-board images or explicit glare trials"]
    save_json(args.output/"evaluation.json",evaluation)
    render_evaluation(validation,evaluation,args.output/"review")
    # False positives on known empty frames are useful diagnostics, not held-out claims.
    empty = [b for b in boards if not len(b["boxes"])]
    empty_preds = [predict_board(model,b["pixels"],"cuda") for b in empty]
    empty_eval = score_predictions(empty,empty_preds,evaluation["confidenceThreshold"])
    empty_eval["evaluationRole"] = "in-sample empty-board diagnostic"
    save_json(args.output/"empty-board-diagnostic.json",empty_eval)
    manifest = {"version":1,"modelId":f"goldenticket-yolox-nano-{args.output.name}",
                "modelFile":"piece-detector.onnx","modelSha256":"", "architecture":"yolox-nano-decoded",
                "classes":CLASSES,"inputName":"images","outputName":"detections","inputSize":TILE,
                "boardWidth":BOARD_W,"boardHeight":BOARD_H,"tileStride":STRIDE,"channelOrder":"BGR",
                "pixelScale":1,"pixelOffset":0,"paddingValue":114,"resize":"bilinear-half-pixel",
                "confidenceThreshold":evaluation["confidenceThreshold"],"nmsThreshold":.45,"opset":17,
                "tileOwnership":"center-midpoint" if USE_TILE_OWNERSHIP else "none",
                "experimental":True,"trainedOnAllReviewedPhotos":args.all_reviewed,
                "evaluationRole":evaluation["evaluationRole"],"evaluationSummary":evaluation["summary"],
                "trainingRun":provenance,"colorRecognition":False}
    final_manifest = export_model(model,args.output,manifest,validation[0]["pixels"])
    # Verify training did not mutate the original images or labels.
    if sha256(args.labels) != labels_sha:
        raise RuntimeError("Annotation source changed during training")
    for item in items:
        if sha256(args.images/item["fileName"]) != item["sha256"]:
            raise RuntimeError("Source photo changed during training")
    print(json.dumps({"done":True,"model":str(args.output/"piece-detector.onnx"),"metrics":evaluation["summary"],
                      "sha256":final_manifest["modelSha256"]}),flush=True)


if __name__ == "__main__":
    main()
