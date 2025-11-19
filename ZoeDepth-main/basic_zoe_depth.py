# MIT License
#
# Copyright (c) 2022 Intelligent Systems Lab Org
#
# A super small helper that loads a ZoeDepth model from this repo,
# runs it on a single image, and writes the raw / visualized depth maps.

import argparse
import json
from pathlib import Path

import numpy as np
import torch
from PIL import Image

from zoedepth.models.builder import build_model
from zoedepth.models.model_io import load_state_from_resource
from zoedepth.utils.config import get_config
from zoedepth.utils.misc import colorize


def _build_model(variant: str, weights: str, device: torch.device):
    """Instantiate a ZoeDepth model and load weights."""
    variant = variant.lower()
    if variant == "n":
        conf = get_config("zoedepth", "infer")
    elif variant == "k":
        conf = get_config("zoedepth", "infer", config_version="kitti")
    else:  # nk or default
        conf = get_config("zoedepth_nk", "infer")

    model = build_model(conf)
    resource = f"local::{weights}" if weights else conf.pretrained_resource
    load_state_from_resource(model, resource)
    model.to(device).eval()
    return model


def _save_depths(depth_np: np.ndarray, out_stem: Path):
    out_stem.parent.mkdir(parents=True, exist_ok=True)
    npy_path = Path(f"{out_stem}.npy")
    np.save(npy_path, depth_np.astype(np.float32))

    # 16-bit depth image
    depth16 = depth_np / max(depth_np.max(), 1e-6)
    depth16 = (depth16 * 65535).clip(0, 65535).astype(np.uint16)
    depth_img = Image.fromarray(depth16, mode="I;16")
    depth_img.save(Path(f"{out_stem}_16bit.png"))

    # Simple colorized preview
    color = colorize(depth_np, cmap="magma_r")
    Image.fromarray(color, mode="RGBA").save(Path(f"{out_stem}_vis.png"))


def _sample_points(depth_np: np.ndarray, samples):
    h, w = depth_np.shape
    out = []
    for sx, sy in samples:
        ix = int(np.clip(sx * (w - 1), 0, w - 1))
        iy = int(np.clip((1.0 - sy) * (h - 1), 0, h - 1))
        out.append({"u_norm": sx, "v_norm": sy, "pixel": [ix, iy], "depth": float(depth_np[iy, ix])})
    return out


def parse_args():
    p = argparse.ArgumentParser(description="Minimal ZoeDepth inference helper.")
    p.add_argument("--image", required=True, help="Path to RGB image.")
    p.add_argument(
        "--weights",
        default=str(Path("weights") / "ZoeD_M12_NK.pt"),
        help="Path to checkpoint (default: weights/ZoeD_M12_NK.pt).",
    )
    p.add_argument("--variant", default="nk", choices=["n", "k", "nk"], help="Which Zoe variant to build.")
    p.add_argument("--output", default="outputs/depth", help="Output stem (without extension).")
    p.add_argument(
        "--samples",
        nargs="*",
        default=[],
        help="Optional list of normalized coords (e.g. 0.5,0.4) to log depth values.",
    )
    p.add_argument("--cpu", action="store_true", help="Force CPU even if CUDA is available.")
    return p.parse_args()


def main():
    args = parse_args()
    device = torch.device("cpu" if args.cpu or not torch.cuda.is_available() else "cuda")
    model = _build_model(args.variant, args.weights, device)

    img = Image.open(args.image).convert("RGB")
    depth = model.infer_pil(img, output_type="numpy")

    out_stem = Path(args.output)
    _save_depths(depth, out_stem)

    sample_coords = []
    for token in args.samples:
        try:
            x_str, y_str = token.split(",")
        except ValueError:
            raise SystemExit(f"Invalid --samples token '{token}'. Use normalized 'x,y' pairs.")
        sample_coords.append((float(x_str), float(y_str)))

    if sample_coords:
        samples = _sample_points(depth, sample_coords)
        with open(Path(f"{out_stem}_samples.json"), "w", encoding="utf-8") as f:
            json.dump(samples, f, indent=2)
        print("Sample depths:", samples)
    else:
        print(f"Depth stats min={depth.min():.4f} max={depth.max():.4f}")
    print(f"Wrote outputs next to {out_stem}")


if __name__ == "__main__":
    main()
