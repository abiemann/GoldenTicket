#!/usr/bin/env python3
"""Local ONNX diagnostic on image files; never accesses a camera or app.

Optional screenshot mode extracts only the user-provided camera image region.
These crops are diagnostic examples, not independent labelled camera tests.
"""
import argparse
import json
from pathlib import Path
import time

import cv2
import numpy as np
import onnxruntime as ort

from train_board_corners import decode, letterbox, save_json, sha256


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("model",type=Path)
    parser.add_argument("output",type=Path)
    parser.add_argument("images",nargs="*",type=Path)
    parser.add_argument("--screenshots",action="store_true")
    args=parser.parse_args()
    args.output.mkdir(parents=True,exist_ok=True)
    ort.disable_telemetry_events()
    session=ort.InferenceSession(str(args.model/"board-corners.onnx"),providers=["CPUExecutionProvider"])
    manifest=json.loads((args.model/"manifest.json").read_text())
    inputs=[(path,None) for path in args.images]
    if args.screenshots:
        folder=Path("C:/Users/abiem/AppData/Local/Temp")
        inputs += [
            (folder/"codex-clipboard-b5342d93-e796-45ef-b8f2-bdd4304918d3.png",[406,542,1604,1215]),
            # Native2560x1600 screenshot, full preview contains manual corner handles.
            (folder/"codex-clipboard-4594a476-213e-4c2a-b5ba-84134c467bfd.png",[669,218,1866,892]),
            # Zoomed, incomplete board is a rejection diagnostic.
            (folder/"codex-clipboard-5d144efd-6575-4b45-b632-430d35a4902e.png",[48,267,1875,697])]
    results=[]
    for path,region in inputs:
        pixels=cv2.imread(str(path))
        if pixels is None:
            raise ValueError(f"Cannot decode {path}")
        if region:
            x1,y1,x2,y2=region
            pixels=pixels[y1:y2,x1:x2].copy()
        boxed,geometry=letterbox(pixels)
        values=np.transpose(boxed[:,:,::-1],(2,0,1))[None].astype(np.float32)/255
        started=time.perf_counter()
        output=session.run(None,{"images":values})[0][0]
        elapsed=(time.perf_counter()-started)*1000
        corners,confidence=decode(output,geometry)
        accepted=bool((confidence>=manifest["confidenceThreshold"]).all())
        result={"fileName":path.name,"sha256":sha256(path),"screenshotRegion":region,
                "imageWidth":pixels.shape[1],"imageHeight":pixels.shape[0],"cornersPixels":corners.tolist(),
                "confidence":confidence.tolist(),"allCornersConfident":accepted,"milliseconds":elapsed,
                "diagnosticOnly":bool(region)}
        results.append(result)
        cv2.imwrite(str(args.output/(path.stem+"-source.png")),pixels)
        overlay=pixels.copy()
        for index,(x,y) in enumerate(corners):
            if 0<=x<pixels.shape[1] and 0<=y<pixels.shape[0]:
                cv2.circle(overlay,(int(x+.5),int(y+.5)),9,(0,255,255),2)
                cv2.putText(overlay,f"{index+1}: {confidence[index]:.2f}",(int(x+12),int(y+24)),
                            cv2.FONT_HERSHEY_SIMPLEX,.6,(0,255,255),2)
        cv2.polylines(overlay,[np.rint(corners).astype(np.int32)],True,(0,255,255),2)
        cv2.imwrite(str(args.output/(path.stem+"-corners.jpg")),overlay,[cv2.IMWRITE_JPEG_QUALITY,90])
        print(json.dumps(result),flush=True)
    save_json(args.output/"diagnostics.json",results)


if __name__=="__main__":
    main()
