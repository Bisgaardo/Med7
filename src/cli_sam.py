import argparse, os, sys, io, base64, json
from typing import List, Tuple
from PIL import Image
import numpy as np

_sam_import = None
try:
    from segment_anything import sam_model_registry as meta_registry
    from segment_anything import SamPredictor as MetaSamPredictor
    _sam_import = 'segment_anything'
except Exception:
    try:
        from mobile_sam import sam_model_registry as mobile_registry
        from mobile_sam import SamPredictor as MobileSamPredictor
        _sam_import = 'mobile_sam'
    except Exception:
        _sam_import = None

import torch

def load_predictor():
    if _sam_import is None:
        raise RuntimeError('No SAM library available. Install segment-anything or mobile-sam in this Python.')
    ckpt = os.getenv('SAM_CHECKPOINT', '').strip()
    if not ckpt or not os.path.isfile(ckpt):
        raise RuntimeError(f'SAM_CHECKPOINT missing or invalid: {ckpt!r}')
    device = 'cuda' if torch.cuda.is_available() and os.getenv('SAM_DEVICE','').strip().lower()!='cpu' else 'cpu'
    model_type = os.getenv('SAM_MODEL', 'vit_h' if _sam_import=='segment_anything' else 'vit_t')
    if _sam_import == 'segment_anything':
        sam = meta_registry[model_type](checkpoint=ckpt)
        sam.to(device=device)
        return MetaSamPredictor(sam)
    else:
        sam = mobile_registry[model_type](checkpoint=ckpt)
        sam.to(device=device)
        return MobileSamPredictor(sam)

def parse_points(s: str) -> Tuple[np.ndarray, np.ndarray]:
    if not s:
        return None, None
    vals = s.split(',')
    if len(vals) % 2 != 0:
        raise ValueError('points must be even number of comma-separated values: x1,y1,x2,y2,...')
    pts = []
    for i in range(0,len(vals),2):
        x = float(vals[i]); y = float(vals[i+1])
        pts.append([x,y])
    if not pts:
        return None, None
    coords = np.array(pts, dtype=np.float32)
    labels = np.ones((coords.shape[0],), dtype=np.int32)
    return coords, labels

def run_once(image, points_str, out_path):
    img = Image.open(image).convert('RGB')
    W, H = img.size
    np_img = np.array(img)
    predictor = load_predictor()
    predictor.set_image(np_img)
    pc, pl = parse_points(points_str)
    if pc is not None:
        pc[:,0] *= W
        pc[:,1] *= H
        masks, _, _ = predictor.predict(point_coords=pc, point_labels=pl, box=None, multimask_output=False)
        m = masks[0]
    else:
        m = np.zeros((H,W), dtype=bool)
    out = Image.fromarray(np.where(m,255,0).astype(np.uint8), mode='L')
    out.save(out_path)
    return {"width": W, "height": H, "out": out_path}

def main():
    ap = argparse.ArgumentParser(description='Run SAM once without an HTTP server')
    ap.add_argument('--image', required=False, help='input image path (PNG/JPG)')
    ap.add_argument('--points', default='', help='normalized TL coords as x1,y1,x2,y2,... in [0,1]')
    ap.add_argument('--out', required=False, help='output mask PNG path')
    ap.add_argument('--loop', action='store_true', help='keep process alive; read JSON lines from stdin')
    args = ap.parse_args()
    if args.loop:
        # Loop: read JSON per line with keys: image, points, out
        predictor = load_predictor()
        import sys
        for line in sys.stdin:
            line = line.strip()
            if not line:
                continue
            try:
                obj = json.loads(line)
                image = obj.get('image')
                points = obj.get('points','')
                out_path = obj.get('out')
                if not image or not out_path:
                    print(json.dumps({"error":"missing image/out"}), flush=True)
                    continue
                img = Image.open(image).convert('RGB')
                W,H = img.size
                np_img = np.array(img)
                predictor.set_image(np_img)
                pc, pl = parse_points(points)
                if pc is not None:
                    pc[:,0] *= W
                    pc[:,1] *= H
                    masks, _, _ = predictor.predict(point_coords=pc, point_labels=pl, box=None, multimask_output=False)
                    m = masks[0]
                else:
                    m = np.zeros((H,W), dtype=bool)
                Image.fromarray(np.where(m,255,0).astype(np.uint8), mode='L').save(out_path)
                print(json.dumps({"ok":True, "out": out_path, "w": W, "h": H}), flush=True)
            except Exception as e:
                print(json.dumps({"error": str(e)}), flush=True)
        return
    else:
        if not args.image or not args.out:
            ap.error('the following arguments are required (non-loop mode): --image, --out')
        meta = run_once(args.image, args.points, args.out)
        print(json.dumps(meta))

if __name__ == '__main__':
    main()
