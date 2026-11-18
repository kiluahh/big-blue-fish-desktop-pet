#!/usr/bin/env python3
"""GPT-SoVITS v2 常驻服务（MIT 权重，约 1.03GB）。

    python tts_gsv_server.py --port 8767

权重由桌宠下载到 models/gsv；还可以直接使用源码仓库 GPT_SoVITS/pretrained_models 里的副本。
零样本克隆：音色取自 refs/kids 里的参考音频。协议与 tts_server.py 一致。
"""

from __future__ import annotations

import argparse
import io
import json
import os
import sys
import threading
import time
import traceback
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path

ROOT = Path(__file__).resolve().parent
REPO = ROOT / "vendor" / "GPT-SoVITS"
MODELS = ROOT / "models" / "gsv"
sys.path.insert(0, str(REPO))
sys.path.insert(0, str(REPO / "GPT_SoVITS"))
os.environ.setdefault("HF_HOME", str(ROOT / "models" / "hf"))
os.environ.setdefault("MPLCONFIGDIR", str(ROOT / ".cache" / "mpl"))
REF_DIR = ROOT / "refs" / "kids"
DEFAULT_VOICE = "kid_005614"


def weights(*parts: str) -> str:
    """The downloaded copy wins; the vendored repo's own pretrained_models is the fallback."""
    local = MODELS.joinpath(*parts)
    if local.exists():
        return str(local)
    return str(Path("GPT_SoVITS") / "pretrained_models" / Path(*parts))


def list_voices() -> list[dict]:
    voices = []
    for wav in sorted(REF_DIR.glob("*.wav")):
        if wav.with_suffix(".txt").exists():
            voices.append({"id": wav.stem})
    return voices


def patch_torchaudio() -> None:
    """torchaudio 2.11 的 load/save 交给 torchcodec（Windows 装不了），用 soundfile 顶替。"""
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


def force_float32(tts) -> None:
    """HuBERT 权重是 fp16，CPU 上会报 dtype 不匹配，统一转回 fp32。"""
    for attr in ("cnhuhbert_model", "bert_model", "vits_model", "t2s_model", "vocoder"):
        module = getattr(tts, attr, None)
        if module is not None and hasattr(module, "float"):
            module.float()


def cuda_ready() -> bool:
    """有可用显卡就用显卡；结果缓存。"""
    global _CUDA
    if _CUDA is None:
        try:
            import torch

            _CUDA = bool(torch.cuda.is_available())
        except Exception:
            _CUDA = False
    return _CUDA


_CUDA = None


class Synthesizer:
    def __init__(self, device: str):
        os.chdir(REPO)  # 配置里的相对路径以仓库根为准
        patch_torchaudio()
        self.lock = threading.Lock()
        self.device = None
        self.tts = None
        self.load_seconds = 0.0
        self.ensure_device(device)

    def ensure_device(self, device: str) -> None:
        """重载到指定设备；旧模型直接丢掉，避免占着显存。"""
        if device == self.device and self.tts is not None:
            return
        from GPT_SoVITS.TTS_infer_pack.TTS import TTS, TTS_Config

        started = time.perf_counter()
        config = TTS_Config(
            {
                "device": device,
                "is_half": False,
                "version": "v2",
                "t2s_weights_path": weights("gsv-v2final-pretrained", "s1bert25hz-5kh-longer-epoch=12-step=369668.ckpt"),
                "vits_weights_path": weights("gsv-v2final-pretrained", "s2G2333k.pth"),
                "cnhuhbert_base_path": weights("chinese-hubert-base"),
                "bert_base_path": weights("chinese-roberta-wwm-ext-large"),
            }
        )
        self.tts = TTS(config)
        force_float32(self.tts)
        self.device = device
        self.load_seconds = time.perf_counter() - started
        print(f"[gsv] 就绪 {device}，{len(list_voices())} 个参考音色，加载 {self.load_seconds:.1f}s", flush=True)

    def speak(self, text: str, voice: str, speed: float, pitch: float, device: str = "cuda") -> tuple[bytes, float]:
        import numpy as np
        import soundfile as sf

        ref = REF_DIR / f"{voice}.wav"
        if not ref.exists():
            ref = REF_DIR / f"{DEFAULT_VOICE}.wav"
        prompt = ref.with_suffix(".txt").read_text(encoding="utf-8").strip()
        with self.lock:
            self.ensure_device(device)
            audio = None
            sr = 32000
            for sample_rate, chunk in self.tts.run(
                {
                    "text": text,
                    "text_lang": "zh",
                    "ref_audio_path": str(ref),
                    "prompt_text": prompt,
                    "prompt_lang": "zh",
                    "top_k": 15,
                    "top_p": 1.0,
                    "temperature": 1.0,
                    "text_split_method": "cut5",
                    "batch_size": 1,
                    "speed_factor": speed,
                    "seed": 42,
                    "parallel_infer": True,
                }
            ):
                sr = sample_rate
                audio = chunk if audio is None else np.concatenate([audio, chunk])
        if audio is None:
            raise ValueError("空音频")
        wav = np.asarray(audio, dtype="float32").reshape(-1)
        peak = float(np.abs(wav).max()) or 1.0
        wav = wav * (0.95 / peak)
        if abs(pitch) >= 0.05:
            import librosa

            wav = librosa.effects.pitch_shift(wav, sr=sr, n_steps=float(np.clip(pitch, -12.0, 12.0)))
        buffer = io.BytesIO()
        sf.write(buffer, np.clip(wav, -1.0, 1.0), sr, format="WAV", subtype="PCM_16")
        return buffer.getvalue(), len(wav) / sr


class Handler(BaseHTTPRequestHandler):
    synthesizer: Synthesizer

    def log_message(self, fmt: str, *args) -> None:
        return

    def _json(self, code: int, payload: dict) -> None:
        body = json.dumps(payload, ensure_ascii=False).encode("utf-8")
        self.send_response(code)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def do_GET(self) -> None:
        if self.path.split("?")[0] != "/health":
            self._json(404, {"error": "not found"})
            return
        self._json(200, {"ready": True, "engine": "gsv", "device": self.synthesizer.device,
                         "loadSeconds": round(self.synthesizer.load_seconds, 1),
                         "local": MODELS.exists(), "voices": list_voices()})

    def do_POST(self) -> None:
        if self.path.split("?")[0] != "/speak":
            self._json(404, {"error": "not found"})
            return
        try:
            length = int(self.headers.get("Content-Length") or 0)
            request = json.loads(self.rfile.read(length) or b"{}")
            text = str(request.get("text") or "").strip()
            if not text:
                self._json(400, {"error": "text 为空"})
                return
            started = time.perf_counter()
            request_device = "cuda" if (request.get("gpu", True) and cuda_ready()) else "cpu"
            audio, seconds = self.synthesizer.speak(
                text=text[:1000],
                voice=str(request.get("voice") or DEFAULT_VOICE),
                speed=float(request.get("speed") or 1.0),
                pitch=float(request.get("pitch") or 0.0),
                device=request_device,
            )
            elapsed = time.perf_counter() - started
            self.send_response(200)
            self.send_header("Content-Type", "audio/wav")
            self.send_header("Content-Length", str(len(audio)))
            self.send_header("X-Audio-Seconds", f"{seconds:.3f}")
            self.send_header("X-Synthesis-Seconds", f"{elapsed:.3f}")
            self.end_headers()
            try:
                self.wfile.write(audio)
            except (ConnectionAbortedError, ConnectionResetError, BrokenPipeError):
                print("[gsv] 客户端已取消", flush=True)
                return
            print(f"[gsv] {seconds:5.2f}s 音频 / {elapsed:5.2f}s 合成 / {len(text)} 字", flush=True)
        except Exception as error:
            traceback.print_exc()
            try:
                self._json(500, {"error": f"{type(error).__name__}: {error}"})
            except (ConnectionAbortedError, ConnectionResetError, BrokenPipeError):
                pass


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="GPT-SoVITS v2 服务")
    parser.add_argument("--port", type=int, default=8767)
    parser.add_argument("--host", default="127.0.0.1")
    parser.add_argument("--device", default="auto", help="auto / cuda / cpu")
    args = parser.parse_args(argv)
    device = args.device
    if device == "auto":
        import torch

        device = "cuda" if torch.cuda.is_available() else "cpu"
    Handler.synthesizer = Synthesizer(device)
    server = ThreadingHTTPServer((args.host, args.port), Handler)
    print(f"[gsv] 监听 http://{args.host}:{args.port}", flush=True)
    try:
        server.serve_forever()
    except KeyboardInterrupt:
        pass
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
