import argparse, os, sys, io, base64, json
import sys
sys.path.insert(0, r"C:\UnityRepos\Med_7_Project\ZoeDepth-main")

from typing import List, Tuple
from PIL import Image
import numpy as np
import matplotlib
from zoedepth.utils.misc import colorize

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

# Simple cache to avoid reloading ZoeDepth every call in --loop mode
_ZOE_CACHE = {}

# Lightweight joint-bilateral refinement guided by RGB image and optional mask
def _refine_with_joint_bilateral(rgb: np.ndarray, depth: np.ndarray, mask_img: np.ndarray = None, mask_is_binary: bool = False, radius: int = 2, sigma_s: float = 2.0, sigma_r: float = 0.1) -> np.ndarray:
    H, W = depth.shape[:2]
    if mask_img is not None:
        if mask_is_binary:
            mask = (mask_img > 0).astype(np.uint8)
        else:
            mask = (np.asarray(mask_img)[...,0] > 127).astype(np.uint8)
    else:
        mask = np.ones((H,W), dtype=np.uint8)
    rgb_f = rgb.astype(np.float32) / 255.0
    out = depth.copy().astype(np.float32)
    rr = range(-radius, radius+1)
    two_sigma_s2 = 2 * (sigma_s ** 2)
    two_sigma_r2 = 2 * (sigma_r ** 2)
    for y in range(H):
        y0 = max(0, y - radius)
        y1 = min(H, y + radius + 1)
        for x in range(W):
            if mask[y, x] == 0:
                continue
            x0 = max(0, x - radius)
            x1 = min(W, x + radius + 1)
            patch_d = depth[y0:y1, x0:x1]
            patch_rgb = rgb_f[y0:y1, x0:x1]
            # spatial weight
            yy, xx = np.mgrid[y0:y1, x0:x1]
            gs = np.exp(-((yy - y)**2 + (xx - x)**2) / two_sigma_s2)
            # range weight from RGB
            center = rgb_f[y, x]
            gr = np.exp(-np.sum((patch_rgb - center)**2, axis=2) / two_sigma_r2)
            w = gs * gr
            w_sum = np.sum(w)
            if w_sum > 1e-8:
                out[y, x] = float(np.sum(w * patch_d) / w_sum)
    return out

def load_zoe(local_root: str = None, variant: str = "ZoeD_NK", device: str = None):
    """Load ZoeDepth model.
    Priority: local hub path if provided, else try direct imports as fallback.
    """
    if device is None:
        # Allow explicit ZOE_DEVICE; fallback to CUDA if available
        dev_env = os.getenv('ZOE_DEVICE', '').strip().lower()
        if dev_env in ('cpu','cuda'):
            device = dev_env
        else:
            device = 'cuda' if torch.cuda.is_available() else 'cpu'
    zoe = None
    # 1) Try torch.hub.load from a local repo root
    if local_root and os.path.isdir(local_root):
        try:
            zoe = torch.hub.load(local_root, variant, source='local', pretrained=True)
        except Exception:
            zoe = None
    # 2) Try env ZOE_ROOT
    if zoe is None:
        env_root = os.getenv('ZOE_ROOT', '').strip()
        if env_root and os.path.isdir(env_root):
            try:
                zoe = torch.hub.load(env_root, variant, source='local', pretrained=True)
            except Exception:
                zoe = None
    # 3) Try relative common path names
    if zoe is None:
        for rel in ("ZoeDepth", "ZoeDepth-main"):
            cand = os.path.abspath(os.path.join(os.path.dirname(__file__), '..', rel))
            if os.path.isdir(cand):
                try:
                    zoe = torch.hub.load(cand, variant, source='local', pretrained=True)
                    break
                except Exception:
                    pass
    # 4) Fallback: try to import builder directly
    if zoe is None:
        try:
            sys.path.insert(0, os.path.abspath(os.path.join(os.path.dirname(__file__), '..', 'ZoeDepth-main')))
            from zoedepth.models.builder import build_model
            from zoedepth.utils.config import get_config
            conf = get_config("zoedepth", "infer")
            zoe = build_model(conf)
        except Exception as e:
            raise RuntimeError(f"Failed to load ZoeDepth locally. Set ZOE_ROOT to ZoeDepth repo. Details: {e}")
    zoe = zoe.to(device)
    zoe.eval()
    return zoe, device

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

def run_once(image, points_str, out_path, depth_out: str = None, zoe_variant: str = None, zoe_root: str = None, zoe_device: str = None, zoe_max_dim: int = 2048):
    # Ensure file handle is released promptly on Windows to avoid locking
    with Image.open(image) as _im:
        img = _im.convert('RGB')
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

    meta = {"width": W, "height": H, "out": out_path}
    if depth_out:
        try:
            variant = zoe_variant or os.getenv('ZOE_VARIANT', 'ZoeD_NK')
            cache_key = (variant, (zoe_root or ''), (zoe_device or ''))
            if cache_key in _ZOE_CACHE:
                zoe, device = _ZOE_CACHE[cache_key]
            else:
                zoe, device = load_zoe(local_root=zoe_root, variant=variant, device=zoe_device)
                _ZOE_CACHE[cache_key] = (zoe, device)

            # Optional downscale to limit VRAM
            max_dim = int(os.getenv('ZOE_MAX_DIM', zoe_max_dim or 2048))
            im = img
            if max(img.size) > max_dim:
                ratio = max_dim / float(max(img.size))
                new_size = (int(round(img.size[0]*ratio)), int(round(img.size[1]*ratio)))
                im = img.resize(new_size, Image.BILINEAR)

            # Inference (with optional TTA: horizontal flip)
            with torch.inference_mode():
                if device == 'cuda':
                    with torch.cuda.amp.autocast(dtype=torch.float16):
                        depth_small = zoe.infer_pil(im)
                        if os.getenv('ZOE_TTA','1') == '1':
                            im_flip = im.transpose(Image.FLIP_LEFT_RIGHT)
                            d_flip = zoe.infer_pil(im_flip)
                            d_flip = np.ascontiguousarray(np.flip(d_flip, axis=1))
                            depth_small = 0.5 * (depth_small + d_flip)
                        torch.cuda.synchronize()
                else:
                    depth_small = zoe.infer_pil(im)
                    if os.getenv('ZOE_TTA','1') == '1':
                        im_flip = im.transpose(Image.FLIP_LEFT_RIGHT)
                        d_flip = zoe.infer_pil(im_flip)
                        d_flip = np.ascontiguousarray(np.flip(d_flip, axis=1))
                        depth_small = 0.5 * (depth_small + d_flip)

            # Resize back if needed
            if im.size != img.size:
                depth = np.array(Image.fromarray(depth_small).resize(img.size, Image.BILINEAR))
            else:
                depth = depth_small

            # Keep raw float32 copy before normalization (for colorization/refinement)
            depth_raw = depth.astype(np.float32)

            # Optional SAM-guided joint bilateral refinement inside the mask
            if os.getenv('ZOE_REFINE','1') == '1':
                try:
                    depth_raw = _refine_with_joint_bilateral(np.array(img), depth_raw, np.array(out), mask_is_binary=True)
                except Exception:
                    pass

            # Normalize for Unity numeric sampling (16-bit)
            d = depth_raw.copy()
            finite_mask = np.isfinite(d)
            if not np.any(finite_mask):
                d16 = np.zeros_like(d, dtype=np.uint16)
            else:
                d = np.where(finite_mask, d, 0)
                d = d - np.min(d[finite_mask])
                maxv = np.max(d[finite_mask])
                if maxv > 0:
                    d = d / maxv
                d16 = np.clip(d * 65535.0, 0, 65535).astype(np.uint16)
            Image.fromarray(d16, mode='I;16').save(depth_out)

            # ZoeDepth colorized visualization using magma colormap
            try:
                cmap = os.getenv('ZOE_CMAP', 'magma_r')
                colored = colorize(depth_raw, cmap=cmap)
                if colored.shape[-1] == 4:
                    mode = 'RGBA'
                else:
                    mode = 'RGB'
                Image.fromarray(colored, mode=mode).save(depth_out.replace('.png', '_vis.png'))
            except Exception:
                pass

            # Optional 8-bit grayscale for debugging (off by default)
            if os.getenv('ZOE_SAVE_GRAY','0') == '1':
                try:
                    g8 = np.clip(d * 255.0, 0, 255).astype(np.uint8)
                    Image.fromarray(g8, mode='L').save(depth_out.replace('.png', '_gray.png'))
                except Exception:
                    pass

            if device == 'cuda' and os.getenv('ZOE_EMPTY_CACHE', '0') == '1':
                try:
                    torch.cuda.empty_cache()
                except Exception:
                    pass

            meta["depth_out"] = depth_out
            meta["depth_vis"] = depth_out.replace('.png', '_vis.png')

        except Exception as e:
            meta["depth_error"] = str(e)


def main():
    ap = argparse.ArgumentParser(description='Run SAM once without an HTTP server')
    ap.add_argument('--image', required=False, help='input image path (PNG/JPG)')
    ap.add_argument('--points', default='', help='normalized TL coords as x1,y1,x2,y2,... in [0,1]')
    ap.add_argument('--out', required=False, help='output mask PNG path')
    ap.add_argument('--loop', action='store_true', help='keep process alive; read JSON lines from stdin')
    ap.add_argument('--depth_out', required=False, help='optional output path for depth PNG (16-bit)')
    ap.add_argument('--zoe_variant', required=False, help='ZoeDepth variant key (e.g., ZoeD_N, ZoeD_K, ZoeD_NK)')
    ap.add_argument('--zoe_root', required=False, help='Local path to ZoeDepth repo for torch.hub.load(source="local")')
    args = ap.parse_args()
    if args.loop:
        # Loop: read JSON per line with keys: image, points, out, depth_out?, zoe_variant?, zoe_root?
        predictor = load_predictor()
        # Optional: preload ZoeDepth on worker start for faster first call
        try:
            if os.getenv('ZOE_PRELOAD','1') == '1':
                pre_v = os.getenv('ZOE_VARIANT', 'ZoeD_NK')
                pre_root = os.getenv('ZOE_ROOT', '') or None
                pre_dev = os.getenv('ZOE_DEVICE', '') or None
                cache_key = (pre_v, (pre_root or ''), (pre_dev or ''))
                if cache_key not in _ZOE_CACHE:
                    zoe, device = load_zoe(local_root=pre_root, variant=pre_v, device=pre_dev)
                    _ZOE_CACHE[cache_key] = (zoe, device)
                    print(json.dumps({"info":"zoe_preloaded","variant":pre_v,"device":device}), flush=True)
        except Exception as e:
            print(json.dumps({"warn":"zoe_preload_failed","error":str(e)}), flush=True)
        import sys
        for line in sys.stdin:
            line = line.strip()
            if not line:
                continue
            try:
                obj = json.loads(line)
                req_id = obj.get('req')
                image = obj.get('image')
                points = obj.get('points','')
                out_path = obj.get('out')
                depth_out = obj.get('depth_out')
                zoe_variant = obj.get('zoe_variant')
                zoe_root = obj.get('zoe_root')
                zoe_device = obj.get('zoe_device')
                # Default high quality: 2048 unless overridden by JSON or env
                zoe_max_dim = int(obj.get('zoe_max_dim', os.getenv('ZOE_MAX_DIM', 2048)) or 2048)
                if not image or not out_path:
                    print(json.dumps({"error":"missing image/out"}), flush=True)
                    continue
                with Image.open(image) as _im:
                    img = _im.convert('RGB')
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
                resp = {"ok":True, "out": out_path, "w": W, "h": H}
                if req_id is not None:
                    resp["req"] = req_id
                if depth_out:
                    try:
                        variant = zoe_variant or os.getenv('ZOE_VARIANT', 'ZoeD_NK')
                        cache_key = (variant, (zoe_root or ''), (zoe_device or ''))
                        if cache_key in _ZOE_CACHE:
                            zoe, device = _ZOE_CACHE[cache_key]
                        else:
                            zoe, device = load_zoe(local_root=zoe_root, variant=variant, device=zoe_device)
                            _ZOE_CACHE[cache_key] = (zoe, device)
                        im = img
                        if max(img.size) > zoe_max_dim:
                            ratio = zoe_max_dim / float(max(img.size))
                            new_size = (int(round(img.size[0]*ratio)), int(round(img.size[1]*ratio)))
                            im = img.resize(new_size, Image.BILINEAR)
                        import time
                        t0 = time.time()
                        with torch.inference_mode():
                            if device == 'cuda':
                                from torch.amp import autocast
                                with autocast('cuda', dtype=torch.float16):
                                    depth_small = zoe.infer_pil(im)
                                    if os.getenv('ZOE_TTA','1') == '1':
                                        im_flip = im.transpose(Image.FLIP_LEFT_RIGHT)
                                        d_flip = zoe.infer_pil(im_flip)
                                        d_flip = np.ascontiguousarray(np.flip(d_flip, axis=1))
                                        depth_small = 0.5 * (depth_small + d_flip)
                                torch.cuda.synchronize()
                            else:
                                depth_small = zoe.infer_pil(im)
                                if os.getenv('ZOE_TTA','1') == '1':
                                    im_flip = im.transpose(Image.FLIP_LEFT_RIGHT)
                                    d_flip = zoe.infer_pil(im_flip)
                                    d_flip = np.ascontiguousarray(np.flip(d_flip, axis=1))
                                    depth_small = 0.5 * (depth_small + d_flip)
                        depth_time_ms = int(round((time.time() - t0) * 1000))
                        if im.size != img.size:
                            depth = np.array(Image.fromarray(depth_small).resize(img.size, Image.BILINEAR))
                        else:
                            depth = depth_small
                        # Optional refinement inside mask (default on)
                        d = depth.astype(np.float32)
                        if os.getenv('ZOE_REFINE','1') == '1':
                            try:
                                d = _refine_with_joint_bilateral(np.array(img), d, np.where(m,255,0).astype(np.uint8), mask_is_binary=True)
                            except Exception:
                                pass
                        # Always normalized 16-bit write
                        finite_mask = np.isfinite(d)
                        if not np.any(finite_mask):
                            d16 = np.zeros_like(d, dtype=np.uint16)
                        else:
                            d = np.where(finite_mask, d, 0)
                            d = d - np.min(d[finite_mask])
                            maxv = np.max(d[finite_mask])
                            if maxv > 0:
                                d = d / maxv
                            d16 = np.clip(d * 65535.0, 0, 65535).astype(np.uint16)
                        Image.fromarray(d16, mode='I;16').save(depth_out)
                        try:
                            cmap = os.getenv('ZOE_CMAP','magma_r')
                            colored = colorize(depth.astype(np.float32), cmap=cmap)
                            mode = 'RGBA' if colored.shape[-1] == 4 else 'RGB'
                            Image.fromarray(colored, mode=mode).save(depth_out.replace('.png','_vis.png'))
                        except Exception:
                            pass
                        if os.getenv('ZOE_SAVE_GRAY','0') == '1':
                            try:
                                g8 = np.clip(d * 255.0, 0, 255).astype(np.uint8)
                                Image.fromarray(g8, mode='L').save(depth_out.replace('.png','_gray.png'))
                            except Exception:
                                pass
                        if device == 'cuda' and os.getenv('ZOE_EMPTY_CACHE','1') == '1':
                            try:
                                torch.cuda.empty_cache()
                            except Exception:
                                pass
                        resp["depth_out"] = depth_out
                        resp["depth_vis"] = depth_out.replace('.png','_vis.png')
                        resp["zoe_variant"] = variant
                        resp["zoe_device"] = device
                        resp["zoe_max_dim"] = zoe_max_dim
                        resp["zoe_tta"] = os.getenv('ZOE_TTA','1')
                        resp["zoe_refine"] = os.getenv('ZOE_REFINE','1')
                        resp["depth_ms"] = depth_time_ms
                    except Exception as e:
                        resp["depth_error"] = str(e)
                print(json.dumps(resp), flush=True)
            except Exception as e:
                print(json.dumps({"error": str(e)}), flush=True)
        return
    else:
        if not args.image or not args.out:
            ap.error('the following arguments are required (non-loop mode): --image, --out')
        meta = run_once(args.image, args.points, args.out, depth_out=args.depth_out, zoe_variant=args.zoe_variant, zoe_root=args.zoe_root)
        print(json.dumps(meta))

if __name__ == '__main__':
    main()

    if '--loop' not in sys.argv:
        import io, os, sys
        from PIL import Image

        # Compose mask and depth-vis into a single windowed preview and clean up files.
        def find_latest_with_suffix(suffix: str):
            try:
                candidates = [f for f in os.listdir(os.getcwd()) if f.endswith(suffix)]
                if not candidates:
                    return None
                return max(candidates, key=lambda f: os.path.getmtime(f))
            except Exception:
                return None

        def show_composite(mask_path: str = None, depth_vis_path: str = None):
            try:
                imgs = []
                labels = []
                if mask_path and os.path.isfile(mask_path):
                    with Image.open(mask_path) as im:
                        imgs.append(im.convert('RGB').copy())
                        labels.append('SAM mask')
                if depth_vis_path and os.path.isfile(depth_vis_path):
                    with Image.open(depth_vis_path) as im:
                        imgs.append(im.convert('RGB').copy())
                        labels.append('ZoeDepth')
                if not imgs:
                    return

                # Normalize heights and stack side-by-side
                max_h = max(im.height for im in imgs)
                norm = []
                for im in imgs:
                    if im.height != max_h:
                        w = int(round(im.width * (max_h / im.height)))
                        im = im.resize((w, max_h), Image.BILINEAR)
                    norm.append(im)
                total_w = sum(im.width for im in norm)
                canvas = Image.new('RGB', (total_w, max_h), (0, 0, 0))
                x = 0
                for im in norm:
                    canvas.paste(im, (x, 0))
                    x += im.width

                # Show via in-memory buffer to avoid persisting another temp file we manage
                bio = io.BytesIO()
                canvas.save(bio, format='PNG')
                Image.open(io.BytesIO(bio.getvalue())).show()
            finally:
                # Best-effort cleanup of source files to avoid persistence in temp
                for p in (mask_path, depth_vis_path):
                    try:
                        if p and os.path.isfile(p):
                            os.remove(p)
                    except Exception:
                        pass

        latest_mask = find_latest_with_suffix('_out.png')
        latest_depth_vis = find_latest_with_suffix('_vis.png')

        # Prefer a single composite window if possible; otherwise show whichever exists
        if latest_mask or latest_depth_vis:
            show_composite(latest_mask, latest_depth_vis)

        sys.exit(0)
