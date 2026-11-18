#!/usr/bin/env python3
"""Kokoro v1.1-zh 常驻服务（Apache-2.0，约 316MB，CPU 就能实时）。

    python tts_kokoro_server.py --port 8766

权重由桌宠下载到 models/kokoro，服务只读本地文件，不会临时去 HuggingFace 取权重。
协议与 tts_server.py 一致：GET /health、POST /speak 返回 wav。

设计取舍：
- 只用标准库 http.server，不额外装 web 框架；桌宠只在同一台机器上调用。
- 推理用一把锁串行化，GPU 显存只够一次。
- 服务常驻，避免每条命令几秒的模型加载。
"""

from __future__ import annotations

import argparse
import io
import json
import os
import threading
import time
import traceback
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path

ROOT = Path(__file__).resolve().parent
MODELS = ROOT / "models" / "kokoro"
CONFIG = MODELS / "config.json"
WEIGHTS = MODELS / "kokoro-v1_1-zh.pth"
VOICE_DIR = MODELS / "voices"
REPO_ID = "hexgrad/Kokoro-82M-v1.1-zh"
DEFAULT_VOICE = "zf_038"
# 扫描过的中文女声，按基频从高到低；基频越高越稚嫩。
KNOWN_VOICES = ["zf_038", "zf_072", "zf_074", "zf_092", "zf_017", "zf_073", "zf_076", "zf_085"]


def local_model() -> bool:
    return CONFIG.exists() and WEIGHTS.exists()


def list_voices() -> list[dict]:
    """Only list voices that are actually on disk, so the picker never promises an unavailable one."""
    if VOICE_DIR.is_dir():
        found = {path.stem for path in VOICE_DIR.glob("*.pt")}
        if found:
            ordered = sorted(found, key=lambda v: KNOWN_VOICES.index(v) if v in KNOWN_VOICES else 999)
            return [{"id": voice} for voice in ordered]
    return [{"id": voice} for voice in KNOWN_VOICES]


def cuda_ready() -> bool:
    """有可用显卡就用显卡；结果缓存，避免每次请求都探测。"""
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
    def __init__(self):
        self.lock = threading.Lock()
        self.device = None
        self.pipe = None
        self.load_seconds = 0.0
        self.ensure_device("cuda" if cuda_ready() else "cpu")

    def ensure_device(self, device: str) -> None:
        if device == self.device and self.pipe is not None:
            return
        from kokoro import KModel, KPipeline

        started = time.perf_counter()
        if local_model():
            # The downloaded weights replace the HuggingFace repo, so a fresh install works offline.
            model = KModel(repo_id=REPO_ID, config=str(CONFIG), model=str(WEIGHTS)).to(device).eval()
            self.pipe = KPipeline(lang_code="z", repo_id=REPO_ID, model=model, device=device)
        else:
            self.pipe = KPipeline(lang_code="z", repo_id=REPO_ID, device=device)
        self.device = device
        self.load_seconds = time.perf_counter() - started
        print(f"[kokoro] 就绪 {device}，{len(list_voices())} 个中文女声，加载 {self.load_seconds:.1f}s", flush=True)

    def voice_arg(self, voice: str) -> str:
        """KPipeline accepts a .pt path directly; only fall back to the repo id when it is absent."""
        path = VOICE_DIR / f"{voice}.pt"
        return str(path) if path.exists() else voice

    def speak(self, text: str, voice: str, speed: float, pitch: float, device: str = "cpu") -> tuple[bytes, float]:
        import numpy as np
        import soundfile as sf

        with self.lock:
            self.ensure_device(device)
            pieces = []
            for result in self.pipe(text, voice=self.voice_arg(voice), speed=speed):
                audio = result.output.audio
                pieces.append(audio.detach().cpu().numpy() if hasattr(audio, "detach") else np.asarray(audio))
        if not pieces:
            raise ValueError("空音频")
        wav = np.concatenate(pieces).astype("float32").reshape(-1)
        sr = 24000
        peak = float(np.abs(wav).max()) or 1.0
        if peak > 0.99 or peak < 0.5:
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
        self._json(200, {"ready": True, "engine": "kokoro", "device": self.synthesizer.device,
                         "loadSeconds": round(self.synthesizer.load_seconds, 1),
                         "local": local_model(), "voices": list_voices()})

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
                print("[kokoro] 客户端已取消", flush=True)
                return
            print(f"[kokoro] {seconds:5.2f}s 音频 / {elapsed:5.2f}s 合成 / {len(text)} 字", flush=True)
        except Exception as error:
            traceback.print_exc()
            try:
                self._json(500, {"error": f"{type(error).__name__}: {error}"})
            except (ConnectionAbortedError, ConnectionResetError, BrokenPipeError):
                pass


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="Kokoro v1.1-zh 服务")
    parser.add_argument("--port", type=int, default=8766)
    parser.add_argument("--host", default="127.0.0.1")
    args = parser.parse_args(argv)
    Handler.synthesizer = Synthesizer()
    server = ThreadingHTTPServer((args.host, args.port), Handler)
    print(f"[kokoro] 监听 http://{args.host}:{args.port}", flush=True)
    try:
        server.serve_forever()
    except KeyboardInterrupt:
        pass
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
