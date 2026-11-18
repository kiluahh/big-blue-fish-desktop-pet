#!/usr/bin/env python3
"""F5-TTS 离线中文合成（零样本音色克隆）。

用法:
    .venv-f5\\Scripts\\python.exe f5_tts_run.py "你好呀，我是小晓。" -o out/f5_hello.wav
    .venv-f5\\Scripts\\python.exe f5_tts_run.py --file tests/sample_zh.txt --ref refs/kids/kid_005614.wav -o out/f5_long.wav
"""

from __future__ import annotations

import argparse
import os
import sys
import time
from pathlib import Path

ROOT = Path(__file__).resolve().parent
os.environ.setdefault("HF_HOME", str(ROOT / "models" / "hf"))
os.environ.setdefault("MPLCONFIGDIR", str(ROOT / ".cache" / "mpl"))
os.environ.setdefault("HF_HUB_DISABLE_XET", "1")

DEFAULT_REF_DIR = ROOT / "refs" / "kids"
DEFAULT_TEXT = "你好呀，今天过得怎么样？我想跟你说一件小事。"

_MODEL = {}


def local_checkpoints(models_dir: Path | None):
    """Complete local weights win over the HuggingFace cache, so a downloaded pack works offline."""
    if models_dir is None:
        return "", "", None
    ckpt = models_dir / "model_1250000.safetensors"
    vocab = models_dir / "vocab.txt"
    vocos = models_dir / "vocos"
    if ckpt.exists() and vocab.exists() and (vocos / "config.yaml").exists() and (vocos / "pytorch_model.bin").exists():
        return str(ckpt), str(vocab), str(vocos)
    return "", "", None


def _patch_torchaudio() -> None:
    """torchaudio 2.11 把 load/save 转交给 torchcodec，而 torchcodec 在 Windows 上装不了。
    这里用 soundfile 顶上，行为对 wav 完全够用。"""
    import numpy as np
    import soundfile as sf
    import torch
    import torchaudio

    def load(path, **kwargs):
        data, sr = sf.read(str(path), dtype="float32", always_2d=True)
        return torch.from_numpy(np.ascontiguousarray(data.T)), sr

    def save(path, src, sample_rate=16000, **kwargs):
        arr = src.detach().cpu().numpy() if hasattr(src, "detach") else np.asarray(src)
        if arr.ndim == 1:
            arr = arr[None, :]
        sf.write(str(path), arr.T, int(sample_rate))

    torchaudio.load = load
    torchaudio.save = save


_patch_torchaudio()


def get_model(model_name: str, device: str, models_dir: Path | None = None):
    key = f"{model_name}:{device}:{models_dir}"
    if key not in _MODEL:
        from f5_tts.api import F5TTS

        ckpt, vocab, vocos = local_checkpoints(models_dir)
        if ckpt:
            print(f"[f5] 使用本地权重 {ckpt}", file=sys.stderr)
            _MODEL[key] = F5TTS(model=model_name, device=device, ckpt_file=ckpt, vocab_file=vocab, vocoder_local_path=vocos)
        else:
            _MODEL[key] = F5TTS(model=model_name, device=device)
    return _MODEL[key]


def resolve_ref(ref: str | None) -> tuple[Path, str]:
    """参考音频与它的文本：优先用同名的 .txt，其次 .lab。"""
    if ref:
        path = Path(ref)
    else:
        candidates = sorted(DEFAULT_REF_DIR.glob("*.wav"))
        if not candidates:
            raise SystemExit(
                f"没有找到参考音频，先运行 scripts/fetch_kids_ref.py 生成，或手动放一个 wav 到 {DEFAULT_REF_DIR}"
            )
        path = candidates[0]

    for ext in (".txt", ".lab"):
        side = path.with_suffix(ext)
        if side.exists():
            return path, side.read_text(encoding="utf-8").strip()
    raise SystemExit(f"参考音频缺少文本标注：{path.with_suffix('.txt')}")


def main(argv: list[str] | None = None) -> int:
    ap = argparse.ArgumentParser(description="F5-TTS 离线中文合成")
    ap.add_argument("text", nargs="?", help="要朗读的文本")
    ap.add_argument("--file", type=Path, help="从文件读取文本")
    ap.add_argument("--ref", help="参考音频 wav（默认用 refs/kids 下第一个）")
    ap.add_argument("--ref-text", help="参考音频对应的文本（默认读同名 .txt）")
    ap.add_argument("-o", "--out", type=Path, help="输出 wav")
    ap.add_argument("-m", "--model", default="F5TTS_v1_Base")
    ap.add_argument("--device", default="auto", help="auto / cuda / cpu")
    ap.add_argument("--models-dir", type=Path, default=None, help="本地权重目录；齐全时不访问 HuggingFace")
    ap.add_argument("--nfe", type=int, default=32, help="采样步数，越大越稳越慢")
    ap.add_argument("--cfg", type=float, default=2.0, help="与参考的贴合强度")
    ap.add_argument("--speed", type=float, default=1.0)
    ap.add_argument("--seed", type=int, default=None)

    args = ap.parse_args(argv)

    if args.file:
        text = args.file.read_text(encoding="utf-8")
    elif args.text:
        text = args.text
    else:
        ap.print_help()
        return 2

    text = text.strip()
    if not text:
        print("文本为空", file=sys.stderr)
        return 2

    ref_path, ref_text = resolve_ref(args.ref)
    if args.ref_text:
        ref_text = args.ref_text

    print(f"参考音频: {ref_path.name}  ({ref_text[:30]}…)", file=sys.stderr)
    device = args.device
    if device == "auto":
        import torch

        device = "cuda" if torch.cuda.is_available() else "cpu"
    print(f"设备: {device}", file=sys.stderr)
    tts = get_model(args.model, device, args.models_dir)

    out = args.out or ROOT / "out" / f"语音_{time.strftime('%Y%m%d_%H%M%S')}.wav"
    out = Path(out)
    out.parent.mkdir(parents=True, exist_ok=True)

    started = time.perf_counter()
    wav, sr, _spec = tts.infer(
        ref_file=str(ref_path),
        ref_text=ref_text,
        gen_text=text,
        nfe_step=args.nfe,
        cfg_strength=args.cfg,
        speed=args.speed,
        seed=args.seed,
        show_info=lambda *a, **k: None,
    )
    elapsed = time.perf_counter() - started

    import numpy as np
    import soundfile as sf

    wav = np.asarray(wav, dtype="float32").reshape(-1)
    peak = float(np.abs(wav).max()) or 1.0
    if peak > 0.99 or peak < 0.5:  # F5 输出经常过冲，统一拉到 0.95
        wav = wav * (0.95 / peak)
    sf.write(str(out), np.clip(wav, -1.0, 1.0), sr, subtype="PCM_16")

    duration = len(wav) / sr
    print(f"\n输出: {out}")
    print(f"时长: {duration:.2f}s  耗时: {elapsed:.2f}s  实时率: {elapsed / max(duration, 1e-6):.2f}x  峰值: {peak:.2f}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
