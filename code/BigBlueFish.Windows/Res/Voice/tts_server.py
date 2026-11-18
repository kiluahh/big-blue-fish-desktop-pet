#!/usr/bin/env python3
"""常驻 F5-TTS 服务，给桌宠（BigBlueFish）调用。

启动即加载模型一次，之后按请求合成。协议尽量简单：

    GET  /health              -> {"ready":true,"device":"cuda","voices":[...]}
    POST /speak               -> body {"text":"...","voice":"kid_005614","speed":1.0,"nfe":32}
                                 返回 audio/wav，响应头 X-Audio-Seconds 是时长

用法:
    .venv-f5\\Scripts\\python.exe tts_server.py --port 8765

设计取舍：
- 只用标准库 http.server，不额外装 web 框架；桌宠只在同一台机器上调用。
- 推理用一把锁串行化，GPU 显存只够一次。
- 服务常驻，避免每条命令 12 秒的模型加载。
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
sys.path.insert(0, str(ROOT))

import f5_tts_run as f5  # noqa: E402  复用 torchaudio 补丁与模型缓存

REF_ROOTS = (ROOT / "refs" / "kids",)
DEFAULT_VOICE = "kid_005614"


def list_voices() -> list[dict]:
    """列出所有带文本标注的参考音色。"""
    voices = []
    for root in REF_ROOTS:
        if not root.exists():
            continue
        for wav in sorted(root.glob("*.wav")):
            side = wav.with_suffix(".txt")
            if not side.exists():
                continue
            voices.append({"id": wav.stem, "path": str(wav), "group": root.parent.name, "text": side.read_text(encoding="utf-8").strip()[:60]})
    return voices


def find_voice(voice_id: str) -> Path:
    for root in REF_ROOTS:
        candidate = root / f"{voice_id}.wav"
        if candidate.exists() and candidate.with_suffix(".txt").exists():
            return candidate
    raise KeyError(f"未知音色：{voice_id}")


def cuda_ready() -> bool:
    """GPU 只在真的可用时用；第一次调用会缓存结果。"""
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
    def __init__(self, model_name: str, device: str, models_dir: Path | None = None):
        self.lock = threading.Lock()
        self.model_name = model_name
        self.models_dir = models_dir
        self.device = None
        self.tts = None
        self.load_seconds = 0.0
        self.ensure_device(device)

    def ensure_device(self, device: str) -> None:
        """Reload on the requested device; the previous copy is dropped so VRAM is not doubled."""
        if device == self.device and self.tts is not None:
            return
        f5._MODEL.clear()
        started = time.perf_counter()
        self.tts = f5.get_model(self.model_name, device, self.models_dir)
        self.device = device
        self.load_seconds = time.perf_counter() - started
        print(f"[tts] 模型已加载 {self.model_name} on {device}，耗时 {self.load_seconds:.1f}s", flush=True)

    def speak(self, text: str, voice: str, speed: float, nfe: int, cfg: float, seed: int | None, pitch: float = 0.0, device: str = "cuda") -> tuple[bytes, float]:
        import numpy as np
        import soundfile as sf

        ref_path = find_voice(voice)
        ref_text = ref_path.with_suffix(".txt").read_text(encoding="utf-8").strip()
        with self.lock:
            self.ensure_device(device)
            wav, sr, _ = self.tts.infer(
                ref_file=str(ref_path),
                ref_text=ref_text,
                gen_text=text,
                nfe_step=nfe,
                cfg_strength=cfg,
                speed=speed,
                seed=seed,
                show_info=lambda *a, **k: None,
            )
        wav = np.asarray(wav, dtype="float32").reshape(-1)
        peak = float(np.abs(wav).max()) or 1.0
        if peak > 0.99 or peak < 0.5:
            wav = wav * (0.95 / peak)
        # Pitch is applied after synthesis: the F5 model itself has no pitch control.
        if abs(pitch) >= 0.05:
            import librosa

            wav = librosa.effects.pitch_shift(wav, sr=sr, n_steps=float(np.clip(pitch, -12.0, 12.0)))
            peak = float(np.abs(wav).max()) or 1.0
            if peak > 0.99:
                wav = wav * (0.95 / peak)
        buffer = io.BytesIO()
        sf.write(buffer, np.clip(wav, -1.0, 1.0), sr, format="WAV", subtype="PCM_16")
        return buffer.getvalue(), len(wav) / sr


class Handler(BaseHTTPRequestHandler):
    synthesizer: Synthesizer

    def log_message(self, fmt: str, *args) -> None:  # 静音访问日志，桌宠会频繁探活
        return

    def _send_json(self, code: int, payload: dict) -> None:
        body = json.dumps(payload, ensure_ascii=False).encode("utf-8")
        self.send_response(code)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def do_GET(self) -> None:
        if self.path.split("?")[0] != "/health":
            self._send_json(404, {"error": "not found"})
            return
        self._send_json(200, {
            "ready": True,
            "device": self.synthesizer.device,
            "model": self.synthesizer.model_name,
            "loadSeconds": round(self.synthesizer.load_seconds, 1),
            "voices": list_voices(),
        })

    def do_POST(self) -> None:
        if self.path.split("?")[0] != "/speak":
            self._send_json(404, {"error": "not found"})
            return
        try:
            length = int(self.headers.get("Content-Length") or 0)
            request = json.loads(self.rfile.read(length) or b"{}")
            text = str(request.get("text") or "").strip()
            if not text:
                self._send_json(400, {"error": "text 为空"})
                return
            started = time.perf_counter()
            request_device = "cuda" if (request.get("gpu", True) and cuda_ready()) else "cpu"
            audio, seconds = self.synthesizer.speak(
                text=text[:1000],
                voice=str(request.get("voice") or DEFAULT_VOICE),
                speed=float(request.get("speed") or 1.0),
                nfe=int(request.get("nfe") or 32),
                cfg=float(request.get("cfg") or 2.0),
                seed=request.get("seed"),
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
                # The pet cancels stale lines by closing the connection; that is normal, not a failure.
                print("[tts] 客户端已取消，丢弃本次音频", flush=True)
                return
            print(f"[tts] {seconds:5.2f}s 音频 / {elapsed:5.2f}s 合成 / {len(text)} 字", flush=True)
        except KeyError as error:
            self._send_json(404, {"error": str(error)})
        except Exception as error:  # 合成失败要让桌宠能降级，而不是断开连接
            traceback.print_exc()
            try:
                self._send_json(500, {"error": f"{type(error).__name__}: {error}"})
            except (ConnectionAbortedError, ConnectionResetError, BrokenPipeError):
                pass


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="F5-TTS 常驻服务")
    parser.add_argument("--port", type=int, default=8765)
    parser.add_argument("--host", default="127.0.0.1")
    parser.add_argument("--model", default="F5TTS_v1_Base")
    parser.add_argument("--device", default="auto", help="auto / cuda / cpu")
    parser.add_argument("--models-dir", type=Path, default=None, help="本地权重目录；齐全时不访问 HuggingFace")
    args = parser.parse_args(argv)

    device = args.device
    if device == "auto":
        import torch

        device = "cuda" if torch.cuda.is_available() else "cpu"

    # The pet downloads the weights into models/f5; use them when present so no network is needed.
    models_dir = args.models_dir
    if models_dir is None and (ROOT / "models" / "f5").exists():
        models_dir = ROOT / "models" / "f5"

    Handler.synthesizer = Synthesizer(args.model, device, models_dir)
    server = ThreadingHTTPServer((args.host, args.port), Handler)
    print(f"[tts] 监听 http://{args.host}:{args.port}  音色 {len(list_voices())} 个", flush=True)
    try:
        server.serve_forever()
    except KeyboardInterrupt:
        print("[tts] 退出", flush=True)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
