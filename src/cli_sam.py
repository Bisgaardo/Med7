import argparse, os, sys, io, base64, json, time
import sys
import os, sys
print("Running file:", __file__)
print("Working dir:", os.getcwd())
print("sys.path[0:4]:", sys.path[0:4])

sys.path.insert(0, r"C:\UnityRepos\Med_7_Project\ZoeDepth-main")

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

# Simple cache to avoid reloading ZoeDepth every call in --loop mode
_ZOE_CACHE = {}

_TRUE_SET = {'1', 'true', 'yes', 'on'}


def _normalize_variant(name: str, default: str = "ZoeD_NK") -> str:
    if not name:
        return default
    v = str(name).strip()
    return v or default


def _suffix_path(path: str, suffix: str) -> str:
    root, _ = os.path.splitext(path)
    return root + suffix


def _save_depth_meta(depth_path: str, meta: dict):
    if not depth_path:
        return
    try:
        meta_path = _suffix_path(depth_path, '_meta.json')
        with open(meta_path, 'w', encoding='utf-8') as f:
            json.dump(meta, f)
    except Exception:
        pass


def _env_flag(name: str, default: str = '1') -> bool:
    val = os.getenv(name)
    if val is None:
        val = default
    if val is None:
        return False
    return str(val).strip().lower() in _TRUE_SET


def _get_smoothing_prefs() -> Tuple[bool, float]:
    smooth_val = os.getenv('ZOE_SMOOTH_DEPTH')
    if smooth_val is None:
        smooth_val = os.getenv('ZOE_VIS_SMOOTH', '1')
    try:
        sigma = float(os.getenv('ZOE_SMOOTH_SIGMA', '0.6'))
    except Exception:
        sigma = 0.6
    enabled = str(smooth_val).strip().lower() in _TRUE_SET
    return enabled, sigma


def _maybe_smooth_depth(depth: np.ndarray, enabled: bool, sigma: float) -> np.ndarray:
    if not enabled:
        return depth
    try:
        from scipy.ndimage import gaussian_filter
        return gaussian_filter(depth, sigma=sigma)
    except Exception:
        # Extremely cheap 3x3 box blur fallback
        k = np.array([[1, 2, 1], [2, 4, 2], [1, 2, 1]], dtype=np.float32)
        k /= k.sum()
        pad = np.pad(depth, 1, mode='edge')
        out = np.zeros_like(depth)
        for dy in range(3):
            for dx in range(3):
                out += k[dy, dx] * pad[dy:dy + depth.shape[0], dx:dx + depth.shape[1]]
        return out


def _save_depth_visual(depth_raw: np.ndarray, depth_out: str) -> str:
    vis_path = _suffix_path(depth_out, '_vis.png')
    arr = np.asarray(depth_raw, dtype=np.float32)
    finite = np.isfinite(arr)
    if not np.any(finite):
        Image.fromarray(np.zeros((*arr.shape, 4), dtype=np.uint8), mode='RGBA').save(vis_path)
        return vis_path
    try:
        pmin = float(os.getenv('ZOE_VIS_PMIN', '2'))
        pmax = float(os.getenv('ZOE_VIS_PMAX', '98'))
    except Exception:
        pmin, pmax = 2.0, 98.0
    pmin = np.clip(pmin, 0.0, 100.0)
    pmax = np.clip(pmax, 0.0, 100.0)
    lo = np.percentile(arr[finite], pmin)
    hi = np.percentile(arr[finite], pmax)
    if not np.isfinite(lo) or not np.isfinite(hi) or hi <= lo:
        lo = np.min(arr[finite])
        hi = np.max(arr[finite])
    if hi <= lo:
        norm = np.zeros_like(arr, dtype=np.float32)
    else:
        norm = (np.clip(arr, lo, hi) - lo) / (hi - lo)
    if _env_flag('ZOE_VIS_INVERT', '0'):
        norm = 1.0 - norm
    try:
        import matplotlib
        matplotlib.use('Agg', force=True)
        import matplotlib.cm as cm
        cmap = cm.get_cmap(os.getenv('ZOE_CMAP', 'magma_r'))
        rgba = (cmap(norm) * 255.0).astype(np.uint8)
        mode = 'RGBA' if rgba.shape[-1] == 4 else 'RGB'
        Image.fromarray(rgba, mode=mode).save(vis_path)
        return vis_path
    except Exception:
        try:
            g8 = np.clip(norm * 255.0, 0, 255).astype(np.uint8)
            Image.fromarray(g8, mode='L').save(vis_path)
            return vis_path
        except Exception:
            return vis_path


def _zoe_forward_single(zoe, img: Image.Image, device: str, use_tta: bool) -> np.ndarray:
    with torch.inference_mode():
        if device == 'cuda':
            from torch.amp import autocast
            with autocast('cuda', dtype=torch.float16):
                depth = zoe.infer_pil(img)
                if use_tta:
                    im_flip = img.transpose(Image.FLIP_LEFT_RIGHT)
                    d_flip = zoe.infer_pil(im_flip)
                    d_flip = np.ascontiguousarray(np.flip(d_flip, axis=1))
                    depth = 0.5 * (depth + d_flip)
            torch.cuda.synchronize()
        else:
            depth = zoe.infer_pil(img)
            if use_tta:
                im_flip = img.transpose(Image.FLIP_LEFT_RIGHT)
                d_flip = zoe.infer_pil(im_flip)
                d_flip = np.ascontiguousarray(np.flip(d_flip, axis=1))
                depth = 0.5 * (depth + d_flip)
    return depth.astype(np.float32)


def _should_use_tiles(img_size: Tuple[int, int], max_dim: int) -> Tuple[bool, int, int, int]:
    mode = (os.getenv('ZOE_USE_TILES', 'off') or 'off').strip().lower()
    if mode in ('1', 'true', 'on', 'yes'):
        use_tiles = True
    elif mode in ('auto', 'smart'):
        try:
            auto_min = int(os.getenv('ZOE_TILE_AUTO_MIN', str(max_dim + 256)))
        except Exception:
            auto_min = max_dim + 256
        use_tiles = max(img_size) > auto_min
    else:
        use_tiles = False
    try:
        tile_size = int(os.getenv('ZOE_TILE_SIZE', str(max_dim)))
    except Exception:
        tile_size = max_dim
    tile_size = max(128, tile_size)
    try:
        overlap_default = max(tile_size // 8, 64)
        tile_overlap = int(os.getenv('ZOE_TILE_OVERLAP', str(overlap_default)))
    except Exception:
        tile_overlap = max(tile_size // 8, 64)
    tile_overlap = max(16, min(tile_overlap, tile_size - 1))
    guide_dim = 0
    if use_tiles:
        try:
            guide_dim = int(os.getenv('ZOE_TILE_GUIDE', str(min(tile_size, max_dim))))
        except Exception:
            guide_dim = min(tile_size, max_dim)
        if guide_dim < 128:
            guide_dim = 0
    return use_tiles, tile_size, tile_overlap, guide_dim


def _normalize_depth_to_uint16(depth_raw: np.ndarray) -> Tuple[np.ndarray, np.ndarray, float, float]:
    d = depth_raw.copy()
    finite_mask = np.isfinite(d)
    if not np.any(finite_mask):
        zeros16 = np.zeros_like(d, dtype=np.uint16)
        zeros = np.zeros_like(d, dtype=np.float32)
        return zeros16, zeros, 0.0, 1.0
    d = np.where(finite_mask, d, 0.0)
    d_min = float(np.min(d[finite_mask]))
    d_max = float(np.max(d[finite_mask]))
    range_val = max(d_max - d_min, 1e-6)
    d = (d - d_min) / range_val
    d16 = np.clip(d * 65535.0, 0, 65535).astype(np.uint16)
    return d16, d.astype(np.float32), d_min, d_max


def _compute_mask_depth_stats(depth_map: np.ndarray, mask_uint8: np.ndarray, focus_points: np.ndarray = None, focus_radius: float = 0.08) -> dict:
    try:
        mask = (mask_uint8.astype(np.uint8) > 0)
        H, W = depth_map.shape
        focus_px = None
        if focus_points is not None and focus_points.size >= 2:
            pts = np.clip(focus_points, 0.0, 1.0)
            try:
                radius_norm = float(os.getenv('ZOE_FOCUS_RADIUS', focus_radius))
            except Exception:
                radius_norm = focus_radius
            radius_norm = max(0.005, min(0.5, radius_norm))
            radius_px = radius_norm * float(max(H, W))
            yy, xx = np.ogrid[:H, :W]
            focus_mask = np.zeros((H, W), dtype=bool)
            r2 = radius_px * radius_px
            focus_px = radius_px
            for px, py in pts:
                cx = px * (W - 1)
                cy = py * (H - 1)
                dx = xx - cx
                dy = yy - cy
                focus_mask |= (dx * dx + dy * dy) <= r2
            masked = mask & focus_mask
            if masked.sum() >= 64:
                mask = masked
            else:
                focus_px = None
        count = int(mask.sum())
        if count < 16:
            return {}
        vals = depth_map[mask]
        vals = vals[np.isfinite(vals)]
        if vals.size < 8:
            return {}
        median = float(np.median(vals))
        q1 = float(np.percentile(vals, 25))
        q3 = float(np.percentile(vals, 75))
        iqr = float(max(q3 - q1, 1e-6))
        stats = {
            "zoe_band_center": median,
            "zoe_band_iqr": iqr,
            "zoe_mask_px": count,
        }
        if focus_px is not None:
            stats["zoe_focus_radius_px"] = float(focus_px)
        return stats
    except Exception:
        return {}

def _align_patch_to_guide(tile_patch: np.ndarray, guide_patch: np.ndarray) -> np.ndarray:
    if guide_patch is None:
        return tile_patch
    mask = np.isfinite(tile_patch) & np.isfinite(guide_patch)
    if mask.sum() < 24:
        return tile_patch
    tile_vals = tile_patch[mask]
    guide_vals = guide_patch[mask]
    t_mean = float(tile_vals.mean())
    g_mean = float(guide_vals.mean())
    t_var = float(np.var(tile_vals))
    if t_var < 1e-6:
        return tile_patch + (g_mean - t_mean)
    cov = float(np.mean((tile_vals - t_mean) * (guide_vals - g_mean)))
    scale = cov / t_var
    scale = float(np.clip(scale, 0.2, 5.0))
    bias = g_mean - scale * t_mean
    return tile_patch * scale + bias


def _zoe_infer_tiled(zoe, pil_img: Image.Image, device: str, tile_size: int = 1024, overlap: int = 64, use_tta: bool = True, guide_dim: int = None) -> np.ndarray:
    """Run ZoeDepth on overlapping tiles and blend results.
    Returns a float32 depth map with the same size as pil_img.
    """
    W, H = pil_img.size
    ts = max(64, int(tile_size))
    ov = int(max(0, min(overlap, ts//2)))
    step = ts - ov
    # Prepare output buffers
    acc = np.zeros((H, W), dtype=np.float32)
    wsum = np.zeros((H, W), dtype=np.float32)

    # Precompute a 1D feather window and 2D weight
    wx = np.hanning(ts) if ts >= 8 else np.ones(ts, dtype=np.float32)
    wy = wx
    w2 = (wy[:, None] * wx[None, :]).astype(np.float32)
    # Ensure central plateau if small overlap
    if ov < ts//3:
        w2 = np.clip(w2, 0.25, None)

    xs = list(range(0, max(1, W - ts + 1), step))
    ys = list(range(0, max(1, H - ts + 1), step))
    if xs[-1] != max(0, W - ts):
        xs.append(max(0, W - ts))
    if ys[-1] != max(0, H - ts):
        ys.append(max(0, H - ts))

    # Optional low-res guide to align tile scales and avoid seams
    guide = None
    try:
        if guide_dim and max(W, H) > guide_dim:
            ratio = guide_dim / float(max(W, H))
            guide_size = (max(1, int(round(W * ratio))), max(1, int(round(H * ratio))))
            guide_img = pil_img.resize(guide_size, Image.BILINEAR)
            guide_small = _zoe_forward_single(zoe, guide_img, device, use_tta)
            guide = np.array(
                Image.fromarray(guide_small.astype(np.float32), mode='F').resize((W, H), Image.LANCZOS),
                dtype=np.float32,
            )
    except Exception:
        guide = None

    with torch.inference_mode():
        for y in ys:
            for x in xs:
                crop = pil_img.crop((x, y, x + ts, y + ts))
                d = _zoe_forward_single(zoe, crop, device, use_tta)
                h_eff = min(ts, H - y)
                w_eff = min(ts, W - x)
                tile = d[:h_eff, :w_eff]
                if guide is not None:
                    g_patch = guide[y:y + h_eff, x:x + w_eff]
                    tile = _align_patch_to_guide(tile, g_patch)
                tile = np.where(np.isfinite(tile), tile, 0.0)
                acc[y:y+h_eff, x:x+w_eff] += tile * w2[:h_eff, :w_eff]
                wsum[y:y+h_eff, x:x+w_eff] += w2[:h_eff, :w_eff]

    wsum = np.where(wsum > 1e-6, wsum, 1.0)
    out = acc / wsum
    return out

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
    # Build list of candidate explicit weight paths first; if we have one, we will skip hub and go direct
    explicit_ckpt = None
    try:
        repo_root = local_root or os.getenv('ZOE_ROOT','').strip()
        if repo_root and os.path.isdir(repo_root):
            wd = os.path.join(repo_root, 'weights')
            name_map = {'ZoeD_NK':'ZoeD_M12_NK.pt','ZoeD_N':'ZoeD_M12_N.pt','ZoeD_K':'ZoeD_M12_K.pt'}
            fname = name_map.get(variant)
            p = os.path.join(wd, fname) if fname else None
            if p and os.path.isfile(p):
                explicit_ckpt = p
    except Exception:
        pass
    if not explicit_ckpt:
        weights_env = os.getenv('ZOE_WEIGHTS','').strip()
        if weights_env and os.path.isfile(weights_env):
            explicit_ckpt = weights_env

    # If no explicit checkpoint detected, we can try hub; otherwise skip hub entirely
    if not explicit_ckpt:
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
    # 4) Fallback or forced path: build and optionally load explicit weights
    if zoe is None:
        try:
            sys.path.insert(0, os.path.abspath(os.path.join(os.path.dirname(__file__), '..', 'ZoeDepth-main')))
            from zoedepth.models.builder import build_model
            from zoedepth.utils.config import get_config
            cfg_map = {
                'ZoeD_NK': 'zoedepth_nk',
                'ZoeD_N':  'zoedepth_n',
                'ZoeD_K':  'zoedepth_k',
            }
            cfg_name = cfg_map.get(variant, 'zoedepth_nk')
            conf = get_config(cfg_name, "infer")
            # Ensure the builder does NOT auto-load any pretrained weights
            try:
                if isinstance(conf, dict):
                    for k in ("pretrained_resource","pretrained","checkpoint","ckpt_path"):
                        if k in conf:
                            conf[k] = None if 'resource' in k or 'ckpt' in k or 'check' in k else False
                    if 'model' in conf and isinstance(conf['model'], dict):
                        for k in ("pretrained_resource","pretrained","checkpoint","ckpt_path"):
                            if k in conf['model']:
                                conf['model'][k] = None if 'resource' in k or 'ckpt' in k or 'check' in k else False
            except Exception:
                pass
            zoe = build_model(conf)
            # Load explicit checkpoint if available
            ckpt_path = explicit_ckpt
            if not ckpt_path:
                # also check default repo-root/weights mapping
                repo_root2 = local_root or os.getenv('ZOE_ROOT','').strip() or os.path.abspath(os.path.join(os.path.dirname(__file__), '..', 'ZoeDepth-main'))
                wd2 = os.path.join(repo_root2, 'weights')
                name_map2 = {'ZoeD_NK':'ZoeD_M12_NK.pt','ZoeD_N':'ZoeD_M12_N.pt','ZoeD_K':'ZoeD_M12_K.pt'}
                fn2 = name_map2.get(variant)
                p2 = os.path.join(wd2, fn2) if fn2 else None
                if p2 and os.path.isfile(p2):
                    ckpt_path = p2
            if ckpt_path and os.path.isfile(ckpt_path):
                import torch as _torch
                import torch.nn as _nn
                try:
                    raw = _torch.load(ckpt_path, map_location='cpu')
                    sd = raw
                    if isinstance(raw, dict):
                        for k in ('state_dict','model_state_dict','params','weights','model','module','net'):
                            if k in raw:
                                sd = raw[k]
                                break
                    # If sd is an object with .state_dict(), call it
                    if hasattr(sd, 'state_dict') and callable(getattr(sd, 'state_dict')):
                        sd = sd.state_dict()
                    tgt = zoe.state_dict()

                    def try_remap(sdict):
                        remapped = {}
                        prefixes = [
                            '', 'module.', 'model.', 'core.', 'pretrained.',
                            'core.pretrained.', 'core.core.', 'core.core.pretrained.',
                            'core.core.pretrained.model.'
                        ]
                        for k, v in sdict.items():
                            matched = False
                            for p in prefixes:
                                if k.startswith(p):
                                    kk = k[len(p):]
                                    if kk in tgt:
                                        remapped[kk] = v
                                        matched = True
                                        break
                            if not matched and k in tgt:
                                remapped[k] = v
                        return remapped

                    sd_m = try_remap(sd) if isinstance(sd, dict) else {}
                    if not sd_m:
                        # Fallback: keep only intersecting keys
                        sd_m = {k: v for k, v in (sd.items() if isinstance(sd, dict) else []) if k in tgt}
                    missing, unexpected = zoe.load_state_dict(sd_m, strict=False)
                    total = len(sd) if isinstance(sd, dict) else 0
                    print(f"[ZoeDepth] Loaded local weights {ckpt_path}. Used: {len(sd_m)}/{total} keys, Missing: {len(missing)}, Unexpected: {len(unexpected)}")
                    # Safety: some timm builds omit drop_path on certain Blocks; add Identity to avoid attribute errors
                    try:
                        for mod in zoe.modules():
                            if mod.__class__.__name__ == 'Block' and not hasattr(mod, 'drop_path'):
                                setattr(mod, 'drop_path', _nn.Identity())
                    except Exception:
                        pass
                except Exception as _e:
                    print(f"[ZoeDepth] Failed to load explicit weights {ckpt_path}: {_e}")
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
    pc_norm = pc.copy() if pc is not None else None
    if pc is not None:
        pc[:,0] *= W
        pc[:,1] *= H
        masks, _, _ = predictor.predict(point_coords=pc, point_labels=pl, box=None, multimask_output=False)
        m = masks[0]
    else:
        m = np.zeros((H,W), dtype=bool)
    mask_uint8 = np.where(m, 255, 0).astype(np.uint8)
    out = Image.fromarray(mask_uint8, mode='L')
    out.save(out_path)
    mask_arr = np.array(out)

    meta = {"width": W, "height": H, "out": out_path}
    if depth_out:
        try:
            variant = _normalize_variant(zoe_variant or os.getenv('ZOE_VARIANT', 'ZoeD_NK'))
            cache_key = (variant, (zoe_root or '').strip(), (zoe_device or '').strip())
            if cache_key in _ZOE_CACHE:
                zoe, device = _ZOE_CACHE[cache_key]
            else:
                zoe, device = load_zoe(local_root=zoe_root, variant=variant, device=zoe_device)
                _ZOE_CACHE[cache_key] = (zoe, device)

            max_dim = int(os.getenv('ZOE_MAX_DIM', zoe_max_dim or 2048))
            tta_enabled = _env_flag('ZOE_TTA', '1')
            refine_enabled = _env_flag('ZOE_REFINE', '1')
            smooth_enabled, smooth_sigma = _get_smoothing_prefs()
            use_tiles, tile_size, tile_overlap, tile_guide = _should_use_tiles(img.size, max_dim)
            downscaled_to = None
            t0 = time.time()
            if use_tiles:
                depth = _zoe_infer_tiled(zoe, img, device, tile_size=tile_size, overlap=tile_overlap, use_tta=tta_enabled, guide_dim=tile_guide)
            else:
                im = img
                if max(img.size) > max_dim:
                    ratio = max_dim / float(max(img.size))
                    new_size = (int(round(img.size[0] * ratio)), int(round(img.size[1] * ratio)))
                    im = img.resize(new_size, Image.BILINEAR)
                    downscaled_to = new_size
                depth_small = _zoe_forward_single(zoe, im, device, tta_enabled)
                if im.size != img.size:
                    depth = np.array(
                        Image.fromarray(depth_small.astype(np.float32), mode='F').resize(img.size, Image.LANCZOS),
                        dtype=np.float32,
                    )
                else:
                    depth = depth_small.astype(np.float32)
            depth_time_ms = int(round((time.time() - t0) * 1000))

            depth_raw = depth.astype(np.float32)
            if refine_enabled:
                try:
                    depth_raw = _refine_with_joint_bilateral(np.array(img), depth_raw, mask_arr, mask_is_binary=True)
                except Exception:
                    pass
            depth_raw = _maybe_smooth_depth(depth_raw, smooth_enabled, smooth_sigma)

            d16, d_norm, d_min, d_max = _normalize_depth_to_uint16(depth_raw)
            Image.fromarray(d16, mode='I;16').save(depth_out)

            vis_path = _save_depth_visual(depth_raw, depth_out)
            stats = _compute_mask_depth_stats(d_norm, mask_uint8, focus_points=pc_norm)
            if stats:
                meta.update(stats)

            if os.getenv('ZOE_SAVE_GRAY', '0') == '1':
                try:
                    g8 = np.clip(d_norm * 255.0, 0, 255).astype(np.uint8)
                    Image.fromarray(g8, mode='L').save(_suffix_path(depth_out, '_gray.png'))
                except Exception:
                    pass

            if device == 'cuda' and os.getenv('ZOE_EMPTY_CACHE', '0') == '1':
                try:
                    torch.cuda.empty_cache()
                except Exception:
                    pass

            depth_range = max(d_max - d_min, 1e-6)
            meta.update({
                "depth_out": depth_out,
                "depth_vis": vis_path,
                "depth_ms": depth_time_ms,
                "zoe_variant": variant,
                "zoe_device": device,
                "zoe_max_dim": max_dim,
                "zoe_tta": '1' if tta_enabled else '0',
                "zoe_refine": '1' if refine_enabled else '0',
                "zoe_tiled": 1 if use_tiles else 0,
                "zoe_tile_size": tile_size,
                "zoe_tile_overlap": tile_overlap,
                "depth_min": d_min,
                "depth_max": d_max,
                "depth_range": depth_range,
            })
            if downscaled_to:
                meta["zoe_downscaled_to"] = list(downscaled_to)
            if tile_guide:
                meta["zoe_tile_guide"] = tile_guide

            _save_depth_meta(depth_out, meta)

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
                pc_norm = pc.copy() if pc is not None else None
                if pc is not None:
                    pc[:,0] *= W
                    pc[:,1] *= H
                    masks, _, _ = predictor.predict(point_coords=pc, point_labels=pl, box=None, multimask_output=False)
                    m = masks[0]
                else:
                    m = np.zeros((H,W), dtype=bool)
                mask_uint8 = np.where(m,255,0).astype(np.uint8)
                Image.fromarray(mask_uint8, mode='L').save(out_path)
                resp = {"ok":True, "out": out_path, "w": W, "h": H}
                if req_id is not None:
                    resp["req"] = req_id
                if depth_out:
                    try:
                        variant = _normalize_variant(zoe_variant or os.getenv('ZOE_VARIANT', 'ZoeD_NK'))
                        cache_key = (variant, (zoe_root or '').strip(), (zoe_device or '').strip())
                        if cache_key in _ZOE_CACHE:
                            zoe, device = _ZOE_CACHE[cache_key]
                        else:
                            zoe, device = load_zoe(local_root=zoe_root, variant=variant, device=zoe_device)
                            _ZOE_CACHE[cache_key] = (zoe, device)
                        max_dim = int(os.getenv('ZOE_MAX_DIM', zoe_max_dim or 2048))
                        tta_enabled = _env_flag('ZOE_TTA', '1')
                        refine_enabled = _env_flag('ZOE_REFINE', '1')
                        smooth_enabled, smooth_sigma = _get_smoothing_prefs()
                        use_tiles, tile_size, tile_overlap, tile_guide = _should_use_tiles(img.size, max_dim)
                        downscaled_to = None
                        t0 = time.time()
                        if use_tiles:
                            depth = _zoe_infer_tiled(zoe, img, device, tile_size=tile_size, overlap=tile_overlap, use_tta=tta_enabled, guide_dim=tile_guide)
                        else:
                            im = img
                            if max(img.size) > max_dim:
                                ratio = max_dim / float(max(img.size))
                                new_size = (int(round(img.size[0] * ratio)), int(round(img.size[1] * ratio)))
                                im = img.resize(new_size, Image.BILINEAR)
                                downscaled_to = new_size
                            depth_small = _zoe_forward_single(zoe, im, device, tta_enabled)
                            if im.size != img.size:
                                depth = np.array(
                                    Image.fromarray(depth_small.astype(np.float32), mode='F').resize(img.size, Image.LANCZOS),
                                    dtype=np.float32,
                                )
                            else:
                                depth = depth_small.astype(np.float32)
                        depth_time_ms = int(round((time.time() - t0) * 1000))

                        depth_raw = depth.astype(np.float32)
                        if refine_enabled:
                            try:
                                depth_raw = _refine_with_joint_bilateral(np.array(img), depth_raw, mask_uint8, mask_is_binary=True)
                            except Exception:
                                pass
                        depth_raw = _maybe_smooth_depth(depth_raw, smooth_enabled, smooth_sigma)

                        d16, d_norm, d_min, d_max = _normalize_depth_to_uint16(depth_raw)
                        Image.fromarray(d16, mode='I;16').save(depth_out)

                        vis_path = _save_depth_visual(depth_raw, depth_out)
                        stats = _compute_mask_depth_stats(d_norm, mask_uint8, focus_points=pc_norm)

                        if os.getenv('ZOE_SAVE_GRAY','0') == '1':
                            try:
                                g8 = np.clip(d_norm * 255.0, 0, 255).astype(np.uint8)
                                Image.fromarray(g8, mode='L').save(_suffix_path(depth_out, '_gray.png'))
                            except Exception:
                                pass

                        if device == 'cuda' and os.getenv('ZOE_EMPTY_CACHE','1') == '1':
                            try:
                                torch.cuda.empty_cache()
                            except Exception:
                                pass

                        depth_range = max(d_max - d_min, 1e-6)
                        resp["depth_out"] = depth_out
                        resp["depth_vis"] = vis_path
                        resp["zoe_variant"] = variant
                        resp["zoe_device"] = device
                        resp["zoe_max_dim"] = max_dim
                        resp["zoe_tta"] = '1' if tta_enabled else '0'
                        resp["zoe_refine"] = '1' if refine_enabled else '0'
                        resp["zoe_tiled"] = 1 if use_tiles else 0
                        resp["zoe_tile_size"] = tile_size
                        resp["zoe_tile_overlap"] = tile_overlap
                        resp["depth_ms"] = depth_time_ms
                        resp["depth_min"] = d_min
                        resp["depth_max"] = d_max
                        resp["depth_range"] = depth_range
                        if downscaled_to:
                            resp["zoe_downscaled_to"] = list(downscaled_to)
                        if tile_guide:
                            resp["zoe_tile_guide"] = tile_guide
                        if stats:
                            resp.update(stats)
                        _save_depth_meta(depth_out, resp)
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
