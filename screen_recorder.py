"""
Screen Recorder - Python / Tkinter / FFmpeg
Requirements:
    pip install mss numpy opencv-python sounddevice soundfile Pillow pyaudiowpatch
    FFmpeg must be installed and on your PATH:
        Windows : https://ffmpeg.org/download.html
        Linux   : sudo apt install ffmpeg
        macOS   : brew install ffmpeg
"""

import tkinter as tk
from tkinter import ttk, filedialog, messagebox
import threading
import queue
import time
import os
import sys
import shutil
import subprocess
import tempfile


def _try_relaunch_in_local_venv():
    """Relaunch using a local .venv interpreter if available."""
    if os.environ.get("SCREEN_RECORDER_VENV_RELAUNCHED") == "1":
        return False

    script_path = os.path.abspath(__file__)
    script_dir = os.path.dirname(script_path)
    candidates = [
        os.path.join(script_dir, ".venv", "Scripts", "python.exe"),
        os.path.join(script_dir, ".venv", "bin", "python"),
    ]

    current_python = os.path.abspath(sys.executable)
    for python_path in candidates:
        if os.path.exists(python_path) and os.path.abspath(python_path) != current_python:
            os.environ["SCREEN_RECORDER_VENV_RELAUNCHED"] = "1"
            os.execv(python_path, [python_path, script_path, *sys.argv[1:]])
    return False


try:
    import numpy as np
except ModuleNotFoundError:
    _try_relaunch_in_local_venv()
    raise

# Optional imports — gracefully degrade if missing
try:
    import mss
    MSS_AVAILABLE = True
except ImportError:
    MSS_AVAILABLE = False

try:
    import sounddevice as sd
    import soundfile as sf
    AUDIO_AVAILABLE = True
except ImportError:
    AUDIO_AVAILABLE = False

try:
    import cv2
    CV2_AVAILABLE = True
except ImportError:
    CV2_AVAILABLE = False


# ─────────────────────────────────────────────
#  RECORDER CORE
# ─────────────────────────────────────────────

class ScreenRecorder:
    """
    Handles all recording logic independently of the GUI.
    Communicates back to the GUI via a status_callback.
    """

    def __init__(self, status_callback=None):
        self.status_callback   = status_callback or (lambda msg: None)
        self.frame_queue       = queue.Queue(maxsize=60)   # buffer up to 60 frames
        self.is_recording      = False
        self.capture_thread    = None
        self.encoder_thread    = None
        self.audio_thread      = None
        self.audio_data        = []
        self.audio_samplerate  = 44100
        self.temp_video        = None
        self.temp_audio        = None
        self.frame_count       = 0
        self.start_time        = None

    # ── Public API ────────────────────────────

    def start(self, output_path, fps, region, record_audio):
        if not self._check_dependencies(record_audio):
            return False

        self.output_path  = output_path
        self.fps          = fps
        self.region       = region       # dict: top, left, width, height  (or None = full screen)
        self.record_audio = record_audio and AUDIO_AVAILABLE
        self.is_recording = True
        self.frame_count  = 0
        self.audio_data   = []
        self.start_time   = time.time()

        # Temp files to hold raw video/audio before mux
        self.temp_video = tempfile.NamedTemporaryFile(suffix=".mp4", delete=False).name
        self.temp_audio = tempfile.NamedTemporaryFile(suffix=".wav", delete=False).name

        self._start_ffmpeg_process()

        self.capture_thread = threading.Thread(target=self._capture_loop, daemon=True)
        self.encoder_thread = threading.Thread(target=self._encoder_loop, daemon=True)
        self.capture_thread.start()
        self.encoder_thread.start()

        if self.record_audio:
            self.audio_thread = threading.Thread(target=self._audio_loop, daemon=True)
            self.audio_thread.start()

        self.status_callback("recording")
        return True

    def stop(self):
        t0 = time.time()
        self.is_recording = False

        # Wait for threads to finish
        if self.capture_thread:
            self.capture_thread.join(timeout=5)
        print(f"[stop] capture_thread.join phase done at t={time.time() - t0:.2f}s", flush=True)
        if self.encoder_thread:
            self.encoder_thread.join(timeout=10)
        print(f"[stop] encoder_thread.join phase done at t={time.time() - t0:.2f}s", flush=True)
        if self.audio_thread:
            self.audio_thread.join(timeout=5)
        print(f"[stop] audio_thread.join phase done at t={time.time() - t0:.2f}s", flush=True)

        # Close FFmpeg stdin → signals it to finish encoding
        try:
            self.ffmpeg_proc.stdin.close()
            try:
                self.ffmpeg_proc.wait(timeout=5)
            except subprocess.TimeoutExpired:
                print("[stop] ffmpeg_proc.wait timeout; killing ffmpeg process", flush=True)
                self.ffmpeg_proc.kill()
                self.ffmpeg_proc.wait()
            print(f"[stop] ffmpeg_proc.wait phase done at t={time.time() - t0:.2f}s", flush=True)
        except (OSError, ValueError, subprocess.SubprocessError) as e:
            self.status_callback(f"FFmpeg close error: {e}")

        # Save audio
        if self.record_audio and self.audio_data:
            self._save_audio()
            if os.path.exists(self.temp_audio) and os.path.getsize(self.temp_audio) > 0:
                self._mux_audio_video()
                print(f"[stop] mux phase done at t={time.time() - t0:.2f}s", flush=True)
            else:
                print("[stop] audio file empty or missing — saving video only", flush=True)
                os.replace(self.temp_video, self.output_path)
        else:
            # No audio — just rename temp video to final output
            os.replace(self.temp_video, self.output_path)

        self._cleanup_temp_files()
        elapsed = time.time() - self.start_time
        self.status_callback(f"saved:{self.frame_count}:{elapsed:.1f}")

    @property
    def elapsed(self):
        if self.start_time:
            return time.time() - self.start_time
        return 0

    # ── Internal: Capture ─────────────────────

    def _capture_loop(self):
        """Grabs frames as fast as possible, puts them on the queue."""
        interval = 1.0 / self.fps
        with mss.MSS() as sct:
            monitor = self.region if self.region else sct.monitors[1]
            next_capture = time.perf_counter()
            while self.is_recording:
                now = time.perf_counter()
                if now >= next_capture:
                    frame = np.array(sct.grab(monitor))        # BGRA uint8
                    frame = frame[:, :, :3]                    # drop alpha → BGR
                    try:
                        self.frame_queue.put_nowait(frame)
                    except queue.Full:
                        pass  # drop frame rather than block
                    next_capture += interval
                else:
                    time.sleep(max(0, next_capture - now - 0.001))

    # ── Internal: Encoder ─────────────────────

    def _start_ffmpeg_process(self):
        """Opens an FFmpeg subprocess that reads raw BGR24 frames from stdin."""
        region = self.region
        if region:
            w, h = region["width"], region["height"]
        else:
            # Detect screen size via mss
            with mss.MSS() as sct:
                m = sct.monitors[1]
                w, h = m["width"], m["height"]

        self.frame_width  = w
        self.frame_height = h

        cmd = [
            "ffmpeg",
            "-y",                              # overwrite
            "-f",       "rawvideo",
            "-vcodec",  "rawvideo",
            "-s",       f"{w}x{h}",
            "-pix_fmt", "bgr24",
            "-r",       str(self.fps),
            "-i",       "pipe:0",              # stdin
            "-vcodec",  "libx264",
            "-preset",  "ultrafast",           # fast encode; change to 'medium' for smaller file
            "-crf",     "23",                  # quality (18=great, 28=smaller)
            "-pix_fmt", "yuv420p",             # broad player compatibility
            self.temp_video
        ]

        self.ffmpeg_proc = subprocess.Popen(
            cmd,
            stdin=subprocess.PIPE,
            stdout=subprocess.DEVNULL,
            stderr=subprocess.DEVNULL
        )

    def _encoder_loop(self):
        """Pulls frames from the queue and writes raw bytes to FFmpeg stdin."""
        drain_deadline = None
        while True:
            if not self.is_recording:
                if drain_deadline is None:
                    drain_deadline = time.perf_counter() + 2.0
                remaining = drain_deadline - time.perf_counter()
                if remaining <= 0:
                    break
                timeout = min(0.2, remaining)
            else:
                timeout = 0.5

            try:
                frame = self.frame_queue.get(timeout=timeout)
                self.ffmpeg_proc.stdin.write(frame.tobytes())
                self.frame_count += 1
            except queue.Empty:
                if not self.is_recording:
                    continue
            except BrokenPipeError:
                break

    # ── Internal: Audio ───────────────────────

    def _audio_loop(self):
        """Records system audio chunks, preferring WASAPI loopback."""
        def callback(indata, frames, time_info, status):
            if status:
                print(f"[audio] status: {status}", flush=True)
            if self.is_recording:
                self.audio_data.append(indata.copy())

        def _capture_with_pyaudiowpatch():
            try:
                import pyaudiowpatch as pyaudio
            except ImportError:
                print("[audio] pyaudiowpatch not installed; skipping WASAPI loopback path", flush=True)
                return False

            pa = pyaudio.PyAudio()
            stream = None
            try:
                loopback_device = None

                if hasattr(pa, "get_default_wasapi_loopback"):
                    loopback_device = pa.get_default_wasapi_loopback()
                else:
                    default_output = pa.get_default_output_device_info()
                    if default_output.get("isLoopbackDevice", False):
                        loopback_device = default_output
                    elif hasattr(pa, "get_loopback_device_info_generator"):
                        default_name = default_output.get("name", "")
                        for dev in pa.get_loopback_device_info_generator():
                            if default_name and default_name in dev.get("name", ""):
                                loopback_device = dev
                                break
                        if loopback_device is None:
                            for dev in pa.get_loopback_device_info_generator():
                                loopback_device = dev
                                break
                    else:
                        loopback_device = default_output

                if loopback_device is None:
                    raise RuntimeError("No WASAPI loopback device found")

                capture_channels = int(loopback_device.get("maxInputChannels", 0))
                if capture_channels <= 0:
                    capture_channels = int(loopback_device.get("maxOutputChannels", 0))
                capture_channels = max(1, capture_channels)
                output_channels = min(2, capture_channels)
                self.audio_samplerate = int(loopback_device.get("defaultSampleRate", self.audio_samplerate))

                open_kwargs = dict(
                    format=pyaudio.paFloat32,
                    channels=capture_channels,
                    rate=self.audio_samplerate,
                    input=True,
                    frames_per_buffer=1024,
                    input_device_index=int(loopback_device["index"]),
                )
                if not loopback_device.get("isLoopbackDevice", False):
                    open_kwargs["as_loopback"] = True

                print(
                    f"[audio] using WASAPI loopback via pyaudiowpatch: "
                    f"{loopback_device.get('name', 'unknown')} "
                    f"({capture_channels}->{output_channels}ch @ {self.audio_samplerate}Hz)",
                    flush=True,
                )
                stream = pa.open(**open_kwargs)

                while self.is_recording:
                    data = stream.read(1024, exception_on_overflow=False)
                    chunk = np.frombuffer(data, dtype=np.float32)
                    if chunk.size == 0:
                        continue
                    frame_count = chunk.size // capture_channels
                    if frame_count <= 0:
                        continue
                    chunk = chunk[:frame_count * capture_channels].reshape(frame_count, capture_channels)
                    if capture_channels > output_channels:
                        chunk = chunk[:, :output_channels]
                    self.audio_data.append(chunk.copy())

                return True
            except Exception as e:
                print(f"[audio] pyaudiowpatch loopback failed: {e}", flush=True)
                return False
            finally:
                if stream is not None:
                    try:
                        stream.stop_stream()
                    except Exception:
                        pass
                    try:
                        stream.close()
                    except Exception:
                        pass
                pa.terminate()

        def _capture_with_sounddevice_wasapi_inputs():
            hostapis = sd.query_hostapis()
            wasapi_ids = {
                idx for idx, hostapi in enumerate(hostapis)
                if "WASAPI" in str(hostapi.get("name", "")).upper()
            }
            devices = sd.query_devices()
            candidates = []
            for idx, info in enumerate(devices):
                if int(info["hostapi"]) not in wasapi_ids:
                    continue
                if int(info["max_input_channels"]) <= 0:
                    continue
                candidates.append((idx, info))

            candidates.sort(
                key=lambda item: (
                    "loopback" not in str(item[1].get("name", "")).lower(),
                    item[0],
                )
            )

            for device_id, info in candidates:
                try:
                    channels = max(1, min(2, int(info["max_input_channels"])))
                    self.audio_samplerate = int(info.get("default_samplerate", self.audio_samplerate))
                    print(
                        f"[audio] using WASAPI input device {device_id}: "
                        f"{info['name']} ({channels}ch @ {self.audio_samplerate}Hz)",
                        flush=True,
                    )
                    with sd.InputStream(
                        device=device_id,
                        samplerate=self.audio_samplerate,
                        channels=channels,
                        dtype="float32",
                        latency="low",
                        blocksize=1024,
                        callback=callback,
                    ):
                        while self.is_recording:
                            time.sleep(0.1)
                    return True
                except Exception as e:
                    print(f"[audio] WASAPI device {device_id} failed: {e}", flush=True)
            return False

        def _capture_with_legacy_inputs():
            # Final fallback: legacy input devices (Stereo Mix / mic / line in)
            preferred_devices = [17, 13, 16, 12]
            for device_id in preferred_devices:
                try:
                    info = sd.query_devices(device_id)
                    channels = min(2, int(info["max_input_channels"]))
                    if channels == 0:
                        continue
                    self.audio_samplerate = int(info.get("default_samplerate", self.audio_samplerate))
                    print(
                        f"[audio] using legacy input device {device_id}: "
                        f"{info['name']} ({channels}ch @ {self.audio_samplerate}Hz)",
                        flush=True,
                    )
                    with sd.InputStream(
                        device=device_id,
                        samplerate=self.audio_samplerate,
                        channels=channels,
                        dtype="float32",
                        latency="low",
                        blocksize=1024,
                        callback=callback,
                    ):
                        while self.is_recording:
                            time.sleep(0.1)
                    return True
                except Exception as e:
                    print(f"[audio] legacy device {device_id} failed: {e}", flush=True)
            return False

        print(
            "[audio] WDM-KS Stereo Mix can open but deliver empty callbacks on some PortAudio builds; "
            "preferring WASAPI loopback capture",
            flush=True,
        )
        opened = (
            _capture_with_pyaudiowpatch()
            or _capture_with_sounddevice_wasapi_inputs()
            or _capture_with_legacy_inputs()
        )
        if not opened:
            self.status_callback("Audio: no working capture device found")

    def _save_audio(self):
        if not self.audio_data:
            print("[audio] no audio data captured", flush=True)
            return
        audio_array = np.concatenate(self.audio_data, axis=0)
        peak = np.abs(audio_array).max()
        print(f"[audio] {len(self.audio_data)} chunks, peak level: {peak:.4f}", flush=True)
        if peak < 1e-4:
            print("[audio] warning: captured audio is near silence", flush=True)
        # Boost quiet captures (Stereo Mix is often low)
        if peak > 0 and peak < 0.1:
            gain = min(0.9 / peak, 10.0)
            audio_array = np.clip(audio_array * gain, -1.0, 1.0)
            print(f"[audio] boosted by {gain:.1f}x", flush=True)
        sf.write(self.temp_audio, audio_array, self.audio_samplerate, subtype="PCM_16")
        print(f"[audio] saved to {self.temp_audio}", flush=True)

    def _mux_audio_video(self):
        """Combine separate video and audio files into one via FFmpeg."""
        mux_t0 = time.time()
        cmd = [
            "ffmpeg", "-y",
            "-i", self.temp_video,
            "-i", self.temp_audio,
            "-c:v", "copy",
            "-c:a", "aac",
            "-b:a", "192k",
            "-shortest",
            self.output_path
        ]
        result = subprocess.run(cmd, capture_output=True, text=True, timeout=30)
        if result.returncode != 0:
            print(f"[mux] FFmpeg error:\n{result.stderr}", flush=True)
        else:
            print(f"[mux] done in {time.time() - mux_t0:.2f}s", flush=True)

    # ── Helpers ───────────────────────────────

    def _check_dependencies(self, record_audio):
        missing = []
        if not MSS_AVAILABLE:
            missing.append("mss")

        if record_audio and not AUDIO_AVAILABLE:
            missing.append("sounddevice / soundfile")
        if shutil.which("ffmpeg") is None:
            missing.append("ffmpeg")
        if missing:
            self.status_callback(f"missing:{','.join(missing)}")
            return False
        return True

    def _cleanup_temp_files(self):
        for f in [self.temp_video, self.temp_audio]:
            try:
                if f and os.path.exists(f):
                    os.remove(f)
            except OSError:
                pass


# ─────────────────────────────────────────────
#  REGION SELECTOR  (transparent overlay window)
# ─────────────────────────────────────────────

class RegionSelector(tk.Toplevel):
    """
    Full-screen transparent overlay. Click and drag to select a region.
    Returns (top, left, width, height) or None if cancelled.
    """

    def __init__(self, parent):
        super().__init__(parent)
        self.result = None
        self.start_x = self.start_y = 0
        self.rect_id = None

        # Fullscreen transparent window
        self.attributes("-fullscreen", True)
        self.attributes("-alpha", 0.3)
        self.configure(bg="black", cursor="crosshair")
        self.overrideredirect(True)

        self.canvas = tk.Canvas(self, bg="black", highlightthickness=0)
        self.canvas.pack(fill=tk.BOTH, expand=True)

        label = tk.Label(
            self.canvas, text="Click and drag to select region  •  ESC to cancel",
            bg="black", fg="white", font=("Segoe UI", 14)
        )
        label.place(relx=0.5, rely=0.05, anchor="center")

        self.canvas.bind("<ButtonPress-1>",   self._on_press)
        self.canvas.bind("<B1-Motion>",        self._on_drag)
        self.canvas.bind("<ButtonRelease-1>", self._on_release)
        self.bind("<Escape>", lambda e: self.destroy())
        self.lift()
        self.focus_force()

    def _on_press(self, event):
        self.start_x, self.start_y = event.x, event.y
        if self.rect_id:
            self.canvas.delete(self.rect_id)

    def _on_drag(self, event):
        if self.rect_id:
            self.canvas.delete(self.rect_id)
        self.rect_id = self.canvas.create_rectangle(
            self.start_x, self.start_y, event.x, event.y,
            outline="#00FF88", width=2, dash=(6, 3)
        )

    def _on_release(self, event):
        x1, y1 = min(self.start_x, event.x), min(self.start_y, event.y)
        x2, y2 = max(self.start_x, event.x), max(self.start_y, event.y)
        w, h = x2 - x1, y2 - y1
        if w > 10 and h > 10:
            self.result = {"top": y1, "left": x1, "width": w, "height": h}
        self.destroy()


# ─────────────────────────────────────────────
#  MAIN GUI
# ─────────────────────────────────────────────

class RecorderApp(tk.Tk):

    DARK_BG    = "#1a1a2e"
    PANEL_BG   = "#16213e"
    ACCENT     = "#00FF88"
    TEXT       = "#e0e0e0"
    TEXT_DIM   = "#888888"
    DANGER     = "#ff4757"
    BTN_START  = "#00c96e"
    BTN_STOP   = "#ff4757"

    def __init__(self):
        super().__init__()
        self.title("Screen Recorder")
        self.resizable(False, False)
        self.configure(bg=self.DARK_BG)

        self.recorder  = ScreenRecorder(status_callback=self._on_status)
        self.region    = None          # None = full screen
        self.timer_job = None          # after() handle for live timer

        self._build_ui()
        self._center_window()

    # ── UI Construction ───────────────────────

    def _build_ui(self):
        pad = {"padx": 20, "pady": 10}

        # ── Header ──
        hdr = tk.Frame(self, bg=self.DARK_BG)
        hdr.pack(fill=tk.X, **pad)
        tk.Label(hdr, text="⏺  Screen Recorder",
                 font=("Segoe UI", 18, "bold"),
                 bg=self.DARK_BG, fg=self.ACCENT).pack(side=tk.LEFT)

        # ── Settings panel ──
        panel = tk.Frame(self, bg=self.PANEL_BG, bd=0)
        panel.pack(fill=tk.X, padx=20, pady=(0, 10))

        self._setting_row(panel, "Output file:",    self._make_file_row(panel))
        self._setting_row(panel, "FPS:",             self._make_fps_row(panel))
        self._setting_row(panel, "Capture region:", self._make_region_row(panel))
        self._setting_row(panel, "Record audio:",   self._make_audio_row(panel))

        # ── Status bar ──
        status_frame = tk.Frame(self, bg=self.DARK_BG)
        status_frame.pack(fill=tk.X, padx=20)

        self.timer_label = tk.Label(
            status_frame, text="00:00", font=("Courier New", 28, "bold"),
            bg=self.DARK_BG, fg=self.ACCENT
        )
        self.timer_label.pack()

        self.status_label = tk.Label(
            status_frame, text="Ready", font=("Segoe UI", 10),
            bg=self.DARK_BG, fg=self.TEXT_DIM
        )
        self.status_label.pack()

        # ── Controls ──
        ctrl = tk.Frame(self, bg=self.DARK_BG)
        ctrl.pack(pady=16)

        self.start_btn = tk.Button(
            ctrl, text="▶  Start Recording",
            font=("Segoe UI", 11, "bold"),
            bg=self.BTN_START, fg="black",
            relief=tk.FLAT, padx=18, pady=10,
            cursor="hand2", command=self._start_recording
        )
        self.start_btn.pack(side=tk.LEFT, padx=8)

        self.stop_btn = tk.Button(
            ctrl, text="■  Stop",
            font=("Segoe UI", 11, "bold"),
            bg=self.BTN_STOP, fg="white",
            relief=tk.FLAT, padx=18, pady=10,
            cursor="hand2", command=self._stop_recording,
            state=tk.DISABLED
        )
        self.stop_btn.pack(side=tk.LEFT, padx=8)

        # ── Footer ──
        tk.Label(
            self, text="Encoded via FFmpeg  •  mss capture",
            font=("Segoe UI", 8), bg=self.DARK_BG, fg=self.TEXT_DIM
        ).pack(pady=(0, 12))

    def _setting_row(self, parent, label_text, widget):
        row = tk.Frame(parent, bg=self.PANEL_BG)
        row.pack(fill=tk.X, padx=12, pady=6)
        tk.Label(row, text=label_text, width=16, anchor="w",
                 font=("Segoe UI", 9), bg=self.PANEL_BG, fg=self.TEXT_DIM
                 ).pack(side=tk.LEFT)
        widget.pack(side=tk.LEFT, fill=tk.X, expand=True)

    def _make_file_row(self, parent):
        f = tk.Frame(parent, bg=self.PANEL_BG)
        self.file_var = tk.StringVar(value=os.path.join(os.path.expanduser("~"), "recording.mp4"))
        entry = tk.Entry(f, textvariable=self.file_var, width=34,
                         font=("Segoe UI", 9), bg="#0f3460", fg=self.TEXT,
                         relief=tk.FLAT, insertbackground=self.TEXT)
        entry.pack(side=tk.LEFT, ipady=4, padx=(0, 6))
        tk.Button(f, text="Browse", font=("Segoe UI", 8),
                  bg="#0f3460", fg=self.TEXT, relief=tk.FLAT,
                  padx=8, cursor="hand2",
                  command=self._browse_file).pack(side=tk.LEFT)
        return f

    def _make_fps_row(self, parent):
        f = tk.Frame(parent, bg=self.PANEL_BG)
        self.fps_var = tk.IntVar(value=20)
        for val in (10, 15, 20, 30):
            tk.Radiobutton(f, text=str(val), variable=self.fps_var, value=val,
                           font=("Segoe UI", 9), bg=self.PANEL_BG, fg=self.TEXT,
                           selectcolor="#0f3460", activebackground=self.PANEL_BG
                           ).pack(side=tk.LEFT, padx=6)
        return f

    def _make_region_row(self, parent):
        f = tk.Frame(parent, bg=self.PANEL_BG)
        self.region_label = tk.Label(f, text="Full screen",
                                     font=("Segoe UI", 9), bg=self.PANEL_BG, fg=self.TEXT)
        self.region_label.pack(side=tk.LEFT, padx=(0, 10))
        tk.Button(f, text="Select region", font=("Segoe UI", 8),
                  bg="#0f3460", fg=self.TEXT, relief=tk.FLAT,
                  padx=8, cursor="hand2",
                  command=self._select_region).pack(side=tk.LEFT, padx=4)
        tk.Button(f, text="Reset", font=("Segoe UI", 8),
                  bg="#0f3460", fg=self.TEXT_DIM, relief=tk.FLAT,
                  padx=8, cursor="hand2",
                  command=self._reset_region).pack(side=tk.LEFT)
        return f

    def _make_audio_row(self, parent):
        f = tk.Frame(parent, bg=self.PANEL_BG)
        self.audio_var = tk.BooleanVar(value=AUDIO_AVAILABLE)
        cb = tk.Checkbutton(f, variable=self.audio_var,
                             text="Record microphone/system audio",
                             font=("Segoe UI", 9), bg=self.PANEL_BG, fg=self.TEXT,
                             selectcolor="#0f3460", activebackground=self.PANEL_BG,
                             state=tk.NORMAL if AUDIO_AVAILABLE else tk.DISABLED)
        cb.pack(side=tk.LEFT)
        if not AUDIO_AVAILABLE:
            tk.Label(f, text="(sounddevice not installed)",
                     font=("Segoe UI", 8), bg=self.PANEL_BG, fg=self.DANGER
                     ).pack(side=tk.LEFT, padx=6)
        return f

    # ── Actions ───────────────────────────────

    def _browse_file(self):
        path = filedialog.asksaveasfilename(
            defaultextension=".mp4",
            filetypes=[("MP4 video", "*.mp4"), ("All files", "*.*")]
        )
        if path:
            self.file_var.set(path)

    def _select_region(self):
        self.withdraw()
        self.after(200, self._open_region_selector)   # slight delay so window hides first

    def _open_region_selector(self):
        sel = RegionSelector(self)
        self.wait_window(sel)
        self.deiconify()
        if sel.result:
            self.region = sel.result
            r = sel.result
            self.region_label.config(
                text=f"{r['width']}×{r['height']}  @ ({r['left']}, {r['top']})"
            )
        else:
            self.region_label.config(text="Full screen" if not self.region else self.region_label.cget("text"))

    def _reset_region(self):
        self.region = None
        self.region_label.config(text="Full screen")

    def _start_recording(self):
        output = self.file_var.get().strip()
        if not output:
            messagebox.showerror("Error", "Please choose an output file.")
            return

        self.start_btn.config(state=tk.DISABLED)
        self.stop_btn.config(state=tk.NORMAL)
        self._set_status("Starting…", self.ACCENT)

        threading.Thread(
            target=self.recorder.start,
            args=(output, self.fps_var.get(), self.region, self.audio_var.get()),
            daemon=True
        ).start()

        self._tick_timer()

    def _stop_recording(self):
        self.stop_btn.config(state=tk.DISABLED)
        self._set_status("Stopping — encoding…", self.TEXT_DIM)
        if self.timer_job:
            self.after_cancel(self.timer_job)

        threading.Thread(target=self.recorder.stop, daemon=True).start()

    # ── Status & Timer ────────────────────────

    def _tick_timer(self):
        elapsed = int(self.recorder.elapsed)
        m, s = divmod(elapsed, 60)
        self.timer_label.config(text=f"{m:02d}:{s:02d}")
        self.timer_job = self.after(500, self._tick_timer)

    def _on_status(self, msg):
        """Called from recorder thread → marshal back to main thread."""
        self.after(0, self._handle_status, msg)

    def _handle_status(self, msg):
        if msg == "recording":
            self._set_status("● Recording", self.DANGER)
        elif msg.startswith("saved:"):
            _, frames, elapsed = msg.split(":")
            actual_fps = int(frames) / float(elapsed) if float(elapsed) > 0 else 0
            self._set_status(
                f"Saved  •  {frames} frames  •  {actual_fps:.1f} fps avg", self.ACCENT
            )
            self.timer_label.config(text="00:00")
            self.start_btn.config(state=tk.NORMAL)
            messagebox.showinfo("Done", f"Recording saved to:\n{self.file_var.get()}")
        elif msg.startswith("missing:"):
            libs = msg.split(":")[1]
            missing_items = [x.strip() for x in libs.split(",") if x.strip()]
            pip_items = [x for x in missing_items if x != "ffmpeg"]
            lines = []
            if pip_items:
                lines.append("Install Python packages:")
                lines.append(f"pip install {' '.join(pip_items)}")
            if "ffmpeg" in missing_items:
                if lines:
                    lines.append("")
                lines.append("Install FFmpeg and add it to PATH:")
                lines.append("https://ffmpeg.org/download.html")
            messagebox.showerror("Missing dependencies", "\n".join(lines) if lines else "Missing dependencies detected.")
            self.start_btn.config(state=tk.NORMAL)
            self.stop_btn.config(state=tk.DISABLED)
        else:
            self._set_status(msg, self.TEXT_DIM)

    def _set_status(self, text, colour=None):
        self.status_label.config(text=text, fg=colour or self.TEXT_DIM)

    # ── Utilities ─────────────────────────────

    def _center_window(self):
        self.update_idletasks()
        w, h = self.winfo_reqwidth(), self.winfo_reqheight()
        sw, sh = self.winfo_screenwidth(), self.winfo_screenheight()
        self.geometry(f"{w}x{h}+{(sw - w) // 2}+{(sh - h) // 2}")


# ─────────────────────────────────────────────
#  ENTRY POINT
# ─────────────────────────────────────────────

if __name__ == "__main__":
    app = RecorderApp()
    app.mainloop()
