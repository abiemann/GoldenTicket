#!/usr/bin/env python3
"""Evaluate fixed ONNX weights on fresh synthetic placement seeds, locally."""
import argparse
import json
from pathlib import Path

import cv2
import numpy as np
import onnxruntime as ort

from train_board_corners import SyntheticFrames, decode, save_json, sha256


def plausible(corners,width,height):
    if not np.isfinite(corners).all() or (corners<0).any():
        return False
    if (corners[:,0]>width-1).any() or (corners[:,1]>height-1).any():
        return False
    points=corners/np.array([width-1,height-1])
    edges=np.roll(points,-1,axis=0)-points
    cross=edges[:,0]*np.roll(edges,-1,axis=0)[:,1]-edges[:,1]*np.roll(edges,-1,axis=0)[:,0]
    area=.5*np.sum(points[:,0]*np.roll(points,-1,axis=0)[:,1]-points[:,1]*np.roll(points,-1,axis=0)[:,0])
    ordered = points[0,0] < points[1,0] and points[3,0] < points[2,0] and points[0,1] < points[3,1] and points[1,1] < points[2,1]
    return bool(ordered and (cross>.003).all() and area>=.08)


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument("model",type=Path)
    parser.add_argument("output",type=Path)
    parser.add_argument("--photos",type=Path,default=Path("C:/temp"))
    parser.add_argument("--seed",type=int,default=20260915)
    parser.add_argument("--count",type=int,default=600)
    args=parser.parse_args()
    args.output.mkdir(parents=True,exist_ok=True)
    manifest=json.loads((args.model/"manifest.json").read_text())
    photos=[]
    for photo in manifest["training"]["validationPhotos"]:
        path=args.photos/photo["fileName"]
        if sha256(path)!=photo["sha256"]:
            raise ValueError(f"Source photo changed: {path}")
        photos.append({"pixels":cv2.resize(cv2.imread(str(path)),(768,480))})
    generator=SyntheticFrames(photos,args.seed)
    ort.disable_telemetry_events()
    session=ort.InferenceSession(str(args.model/"board-corners.onnx"),providers=["CPUExecutionProvider"])
    complete=accepted=negative=rejected=confidence_only_false_positive=0
    distances=[]
    failures=[]
    for index in range(args.count):
        image,_,target,visible,geometry=generator.sample()
        inputs=image[:,:,::-1].transpose(2,0,1)[None].astype(np.float32)/255
        heatmaps=session.run(None,{"images":inputs})[0][0]
        points,confidence=decode(heatmaps,geometry)
        confident=bool((confidence>=manifest["confidenceThreshold"]).all())
        proposed=confident and plausible(points,geometry[4],geometry[5])
        if visible.all():
            complete+=1
            if proposed:
                accepted+=1
                distances.extend(np.linalg.norm(points-target,axis=1).tolist())
        else:
            negative+=1
            rejected+=int(not proposed)
            confidence_only_false_positive+=int(confident)
            if proposed:
                failures.append({"index":index,"visible":visible.tolist(),"target":target.tolist() if target is not None else None,
                                 "proposal":points.tolist(),"confidence":confidence.tolist()})
                cv2.imwrite(str(args.output/f"negative-accepted-{index}.png"),image)
    report={"role":"Fresh synthetic seed only; no real camera accuracy claim", "seed":args.seed,"frames":args.count,
            "modelSha256":manifest["modelSha256"],"completeBoards":complete,"acceptedCompleteBoards":accepted,
            "incompleteOrAbsentBoards":negative,"rejectedIncompleteOrAbsentBoards":rejected,
            "confidenceOnlyFalsePositives":confidence_only_false_positive,
            "acceptedCornerMedianErrorAt384":float(np.median(distances)),
            "acceptedCornerP95ErrorAt384":float(np.percentile(distances,95)),"falseAcceptances":failures}
    save_json(args.output/"summary.json",report)
    print(json.dumps(report),flush=True)


if __name__=="__main__":
    main()
