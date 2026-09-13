#!/usr/bin/env python3
"""Score saved legacy detector outputs against the same reviewed ML validation.

The legacy scene-change holds are counted as zero visible predictions. Legacy
rotated candidate outlines are reduced to bounding rectangles for comparison to
the axis-aligned reviewed boxes; the report makes this geometry limitation clear.
"""
import argparse
from collections import Counter
import json
from pathlib import Path

import numpy as np

from train_piece_detector import BOARD_W, BOARD_H, read_labels, validate_labels, load_boards, score_predictions, save_json


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--labels",type=Path,required=True)
    parser.add_argument("--images",type=Path,required=True)
    parser.add_argument("--legacy-results",type=Path,required=True)
    parser.add_argument("--ml-evaluation",type=Path,required=True)
    parser.add_argument("--output",type=Path,required=True)
    args = parser.parse_args()
    labels,_ = read_labels(args.labels)
    boards = load_boards(validate_labels(labels),args.images)
    boards = [b for b in boards if b["item"]["split"]=="validation"]
    predictions, statuses, raw_rows = [], Counter(), []
    for board in boards:
        source = args.legacy_results/Path(board["item"]["fileName"]).stem/"piece-candidates.json"
        result = json.loads(source.read_text(encoding="utf-8-sig"))
        statuses[result["Status"]] += 1
        candidates = []
        if result["Status"]=="Ready":
            for c in result["Candidates"]:
                xs = [v["X"]*BOARD_W for v in c["Outline"]]
                ys = [v["Y"]*BOARD_H for v in c["Outline"]]
                candidates.append([min(xs),min(ys),max(xs),max(ys),c["Confidence"],0 if c["Kind"]=="Train" else 1])
        predictions.append(np.array(candidates,dtype=np.float32).reshape(-1,6))
        raw_rows.append({"fileName":board["item"]["fileName"],"status":result["Status"],
                         "milliseconds":result["Milliseconds"],"predictionCount":len(candidates)})
    legacy = score_predictions(boards,predictions,0)
    legacy["statusCounts"] = statuses
    legacy["rawStatusByImage"] = raw_rows
    ml = json.loads(args.ml_evaluation.read_text(encoding="utf-8"))
    report = {"matchingIou":.5,"comparisonRole":"same eleven validation photos; ML threshold/model selection used these photos",
              "legacyReference":"GoldenTicket-board-20260912-181352.png",
              "limitations":["Legacy uses a historical empty reference, not a fresh lighting-matched empty frame",
                             "Legacy rotated outlines scored using axis-aligned bounding rectangles; ML predicts axis-aligned boxes",
                             "SceneChanged holds count as no visible predictions and all corresponding labels missed",
                             "No untouched independent test session; no validation empty-board or glare trial"],
              "legacy":legacy,"ml":{"summary":ml["summary"],"perClass":ml["perClass"],
                                        "confidenceThreshold":ml["confidenceThreshold"],"images":ml["images"]}}
    save_json(args.output,report)
    print(json.dumps({"legacy":legacy["summary"],"ml":ml["summary"],"legacyStatuses":statuses}))


if __name__=="__main__":
    main()
