#!/usr/bin/env python3
"""
HoloLens View Stream – MJPEG Relay Proxy
=========================================
Polls the existing Unity HololensViewStreamer /frame endpoint at high speed
and re-serves the frames as a continuous MJPEG (multipart/x-mixed-replace)
stream.  This eliminates per-frame HTTP round-trip overhead for the browser.

Usage:
    python relay.py                              # defaults
    python relay.py --source http://172.16.4.56:8080 --port 9090

Then open http://localhost:9090 in your browser.

Zero changes to the Unity build are required.
"""

import argparse
import io
import os
import sys
import threading
import time
from http.server import HTTPServer, BaseHTTPRequestHandler
from urllib.request import urlopen, Request
from urllib.error import URLError

# ── Configuration ──────────────────────────────────────────────────────────

DEFAULT_SOURCE = "http://localhost:8080"
DEFAULT_PORT = 9090
BOUNDARY = b"frameboundary7f2e1a"
POLL_INTERVAL = 1.0 / 60          # poll at 60 Hz (will be clamped by source speed)
CONNECT_RETRY_INTERVAL = 2.0      # seconds between reconnection attempts
REQUEST_TIMEOUT = 2.0             # seconds before a single /frame fetch times out

# ── Shared state ───────────────────────────────────────────────────────────

_latest_frame: bytes = b""
_frame_lock = threading.Lock()
_frame_event = threading.Event()   # signaled when a new frame arrives
_stats = {
    "frames_fetched": 0,
    "fetch_errors": 0,
    "fps": 0.0,
    "connected": False,
    "last_error": "",
}
_stats_lock = threading.Lock()

# ── Poller thread ──────────────────────────────────────────────────────────

def poller_thread(source_base_url: str):
    """Continuously fetches /frame from the Unity server and updates _latest_frame."""
    global _latest_frame

    frame_url = source_base_url.rstrip("/") + "/frame"
    fps_window: list[float] = []

    while True:
        try:
            req = Request(frame_url, headers={"Cache-Control": "no-cache"})
            with urlopen(req, timeout=REQUEST_TIMEOUT) as resp:
                data = resp.read()

            if data and len(data) > 100:  # sanity: a valid JPEG is > 100 bytes
                now = time.monotonic()
                with _frame_lock:
                    _latest_frame = data
                _frame_event.set()

                # FPS tracking (rolling 1-second window)
                fps_window.append(now)
                cutoff = now - 1.0
                fps_window = [t for t in fps_window if t > cutoff]

                with _stats_lock:
                    _stats["frames_fetched"] += 1
                    _stats["fps"] = len(fps_window)
                    _stats["connected"] = True
                    _stats["last_error"] = ""

            time.sleep(POLL_INTERVAL)

        except (URLError, OSError, Exception) as e:
            with _stats_lock:
                _stats["fetch_errors"] += 1
                _stats["connected"] = False
                _stats["last_error"] = str(e)[:200]

            time.sleep(CONNECT_RETRY_INTERVAL)


# ── HTML Viewer ────────────────────────────────────────────────────────────

def build_viewer_html(port: int) -> str:
    return f"""<!DOCTYPE html>
<html lang="en">
<head>
  <meta charset="UTF-8">
  <meta name="viewport" content="width=device-width, initial-scale=1">
  <title>HoloLens Live View — Relay</title>
  <link rel="preconnect" href="https://fonts.googleapis.com">
  <link rel="preconnect" href="https://fonts.gstatic.com" crossorigin>
  <link href="https://fonts.googleapis.com/css2?family=Inter:wght@400;500;600;700&display=swap" rel="stylesheet">
  <style>
    * {{ box-sizing: border-box; margin: 0; padding: 0; }}

    body {{
      font-family: 'Inter', -apple-system, BlinkMacSystemFont, sans-serif;
      background: #0a0e17;
      color: rgba(255,255,255,0.95);
      min-height: 100vh;
      display: flex;
      flex-direction: column;
      align-items: center;
      padding: 28px 16px;
      overflow-x: hidden;
    }}

    .container {{
      width: 100%;
      max-width: 1200px;
      background: linear-gradient(135deg, rgba(15,25,50,0.85), rgba(10,18,35,0.92));
      border: 1px solid rgba(100,160,255,0.15);
      border-radius: 20px;
      padding: 24px 28px 20px;
      box-shadow:
        0 0 0 1px rgba(100,160,255,0.06),
        0 20px 60px rgba(0,0,0,0.4),
        0 0 80px rgba(50,100,200,0.06);
      backdrop-filter: blur(20px);
    }}

    header {{
      display: flex;
      align-items: center;
      justify-content: space-between;
      margin-bottom: 16px;
      flex-wrap: wrap;
      gap: 10px;
    }}

    .title-group {{
      display: flex;
      align-items: center;
      gap: 14px;
    }}

    h1 {{
      font-size: 1.35rem;
      font-weight: 700;
      letter-spacing: -0.01em;
      background: linear-gradient(135deg, #e0eaff, #8ab4ff);
      -webkit-background-clip: text;
      -webkit-text-fill-color: transparent;
    }}

    .badge {{
      background: linear-gradient(135deg, rgba(50,120,220,0.7), rgba(80,60,200,0.5));
      color: #fff;
      padding: 5px 12px;
      border-radius: 8px;
      font-size: 0.72rem;
      font-weight: 700;
      letter-spacing: 0.08em;
      text-transform: uppercase;
    }}

    .stats {{
      display: flex;
      gap: 16px;
      align-items: center;
      flex-wrap: wrap;
    }}

    .stat {{
      font-size: 0.78rem;
      font-weight: 500;
      color: rgba(180,200,230,0.8);
      display: flex;
      align-items: center;
      gap: 5px;
    }}

    .stat .value {{
      font-weight: 700;
      color: rgba(140,200,255,0.95);
      font-variant-numeric: tabular-nums;
    }}

    .dot {{
      width: 8px;
      height: 8px;
      border-radius: 50%;
      background: #444;
      transition: background 0.3s;
    }}
    .dot.live {{
      background: #4ade80;
      box-shadow: 0 0 8px rgba(74,222,128,0.5);
      animation: pulse-dot 2s ease-in-out infinite;
    }}
    .dot.error {{
      background: #f87171;
      box-shadow: 0 0 8px rgba(248,113,113,0.5);
    }}

    @keyframes pulse-dot {{
      0%, 100% {{ opacity: 1; }}
      50% {{ opacity: 0.5; }}
    }}

    .viewer {{
      position: relative;
      width: 100%;
      background: rgba(0,0,0,0.4);
      border-radius: 14px;
      overflow: hidden;
      border: 1px solid rgba(100,160,255,0.1);
      aspect-ratio: 16 / 9;
    }}

    .viewer img {{
      width: 100%;
      height: 100%;
      object-fit: contain;
      display: block;
    }}

    .placeholder {{
      position: absolute;
      inset: 0;
      display: flex;
      align-items: center;
      justify-content: center;
      flex-direction: column;
      gap: 14px;
      color: rgba(180,200,230,0.6);
      font-size: 0.9rem;
      font-weight: 500;
      transition: opacity 0.3s;
    }}
    .placeholder.hidden {{ opacity: 0; pointer-events: none; }}

    .spinner {{
      width: 32px;
      height: 32px;
      border: 3px solid rgba(100,160,255,0.15);
      border-top-color: rgba(100,160,255,0.7);
      border-radius: 50%;
      animation: spin 0.8s linear infinite;
    }}

    @keyframes spin {{
      to {{ transform: rotate(360deg); }}
    }}

    .footer {{
      margin-top: 14px;
      display: flex;
      justify-content: space-between;
      align-items: center;
      flex-wrap: wrap;
      gap: 8px;
    }}

    .status-text {{
      font-size: 0.78rem;
      font-weight: 500;
      color: rgba(180,200,230,0.7);
      transition: color 0.3s;
    }}
    .status-text.live {{ color: rgba(74,222,128,0.9); }}
    .status-text.error {{ color: rgba(248,113,113,0.85); }}

    .controls {{
      display: flex;
      gap: 8px;
    }}

    .controls button {{
      background: rgba(100,160,255,0.1);
      border: 1px solid rgba(100,160,255,0.2);
      color: rgba(200,220,255,0.9);
      padding: 6px 14px;
      border-radius: 8px;
      font-size: 0.75rem;
      font-weight: 600;
      cursor: pointer;
      transition: all 0.2s;
      font-family: inherit;
    }}
    .controls button:hover {{
      background: rgba(100,160,255,0.2);
      border-color: rgba(100,160,255,0.35);
    }}
  </style>
</head>
<body>
  <div class="container">
    <header>
      <div class="title-group">
        <h1>HoloLens Live View</h1>
        <span class="badge">MJPEG Relay</span>
      </div>
      <div class="stats">
        <div class="stat"><span class="dot" id="dot"></span></div>
        <div class="stat">FPS <span class="value" id="fps">—</span></div>
        <div class="stat">Frames <span class="value" id="frames">0</span></div>
      </div>
    </header>

    <div class="viewer">
      <img id="feed" alt="Live feed">
      <div class="placeholder" id="placeholder">
        <div class="spinner"></div>
        Waiting for MJPEG stream…
      </div>
    </div>

    <div class="footer">
      <span class="status-text" id="status">Connecting…</span>
      <div class="controls">
        <button onclick="reconnect()">Reconnect</button>
        <button onclick="toggleFullscreen()">Fullscreen</button>
      </div>
    </div>
  </div>

  <script>
    const img      = document.getElementById('feed');
    const ph       = document.getElementById('placeholder');
    const status   = document.getElementById('status');
    const dot      = document.getElementById('dot');
    const fpsEl    = document.getElementById('fps');
    const framesEl = document.getElementById('frames');
    const viewer   = document.querySelector('.viewer');

    let frameCount  = 0;
    let lastSize    = '';
    let statsTimer  = null;

    function connect() {{
      // MJPEG: just set the img src to the stream endpoint
      img.src = '/stream?t=' + Date.now();
    }}

    img.onload = function() {{
      ph.classList.add('hidden');
      status.textContent = 'Live — MJPEG stream';
      status.className = 'status-text live';
      dot.className = 'dot live';

      let nw = img.naturalWidth, nh = img.naturalHeight;
      if (nw > 0 && nh > 0) {{
        let key = nw + 'x' + nh;
        if (key !== lastSize) {{
          lastSize = key;
          viewer.style.aspectRatio = nw + ' / ' + nh;
        }}
      }}
    }};

    img.onerror = function() {{
      status.textContent = 'Stream disconnected — retrying in 2s…';
      status.className = 'status-text error';
      dot.className = 'dot error';
      ph.classList.remove('hidden');
      setTimeout(connect, 2000);
    }};

    function reconnect() {{
      img.src = '';
      ph.classList.remove('hidden');
      status.textContent = 'Reconnecting…';
      status.className = 'status-text';
      dot.className = 'dot';
      setTimeout(connect, 300);
    }}

    function toggleFullscreen() {{
      if (!document.fullscreenElement) {{
        viewer.requestFullscreen().catch(() => {{}});
      }} else {{
        document.exitFullscreen();
      }}
    }}

    // Poll stats endpoint
    function updateStats() {{
      fetch('/stats')
        .then(r => r.json())
        .then(d => {{
          fpsEl.textContent   = d.fps.toFixed(0);
          framesEl.textContent = d.frames_fetched.toLocaleString();
        }})
        .catch(() => {{}});
    }}

    statsTimer = setInterval(updateStats, 1000);
    connect();
  </script>
</body>
</html>"""


# ── HTTP Handler ───────────────────────────────────────────────────────────

class RelayHandler(BaseHTTPRequestHandler):
    """Serves the MJPEG stream, HTML viewer, and stats."""

    # Suppress per-request console logging (too noisy with MJPEG)
    def log_message(self, format, *args):
        pass

    def do_GET(self):
        path = self.path.split("?")[0].strip("/")

        if path == "stream":
            self._serve_mjpeg_stream()
        elif path == "stats":
            self._serve_stats()
        elif path == "frame":
            self._serve_single_frame()
        else:
            self._serve_viewer()

    def _serve_viewer(self):
        html = build_viewer_html(self.server.server_address[1]).encode("utf-8")
        self.send_response(200)
        self.send_header("Content-Type", "text/html; charset=utf-8")
        self.send_header("Content-Length", str(len(html)))
        self.end_headers()
        self.wfile.write(html)

    def _serve_stats(self):
        import json
        with _stats_lock:
            data = dict(_stats)
        body = json.dumps(data).encode("utf-8")
        self.send_response(200)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body)))
        self.send_header("Access-Control-Allow-Origin", "*")
        self.end_headers()
        self.wfile.write(body)

    def _serve_single_frame(self):
        """Compatibility: serve a single frame like the original Unity server."""
        with _frame_lock:
            frame = _latest_frame
        if frame:
            self.send_response(200)
            self.send_header("Content-Type", "image/jpeg")
            self.send_header("Content-Length", str(len(frame)))
            self.send_header("Cache-Control", "no-store")
            self.end_headers()
            self.wfile.write(frame)
        else:
            self.send_response(204)
            self.end_headers()

    def _serve_mjpeg_stream(self):
        """The main MJPEG stream: keeps the connection open and pushes frames."""
        self.send_response(200)
        self.send_header("Content-Type", f"multipart/x-mixed-replace; boundary={BOUNDARY.decode()}")
        self.send_header("Cache-Control", "no-store, no-cache, must-revalidate, max-age=0")
        self.send_header("Pragma", "no-cache")
        self.send_header("Connection", "keep-alive")
        self.send_header("Access-Control-Allow-Origin", "*")
        self.end_headers()

        last_frame_id = None
        try:
            while True:
                # Wait for a new frame (with timeout so we can detect disconnection)
                _frame_event.wait(timeout=2.0)
                _frame_event.clear()

                with _frame_lock:
                    frame = _latest_frame
                    frame_id = id(frame)

                if not frame:
                    continue

                # Only push if we have a genuinely new frame
                if frame_id == last_frame_id:
                    continue
                last_frame_id = frame_id

                # Write MJPEG multipart boundary + headers + data
                header = (
                    b"--" + BOUNDARY + b"\r\n"
                    b"Content-Type: image/jpeg\r\n"
                    b"Content-Length: " + str(len(frame)).encode() + b"\r\n"
                    b"\r\n"
                )
                self.wfile.write(header)
                self.wfile.write(frame)
                self.wfile.write(b"\r\n")
                self.wfile.flush()

        except (BrokenPipeError, ConnectionResetError, ConnectionAbortedError, OSError):
            # Client disconnected — this is normal for MJPEG streams
            pass


# ── Main ───────────────────────────────────────────────────────────────────

def main():
    parser = argparse.ArgumentParser(
        description="MJPEG relay proxy for HoloLens View Streamer",
        formatter_class=argparse.RawDescriptionHelpFormatter,
        epilog="""
Examples:
  python relay.py
  python relay.py --source http://172.16.4.56:8080 --port 9090
  python relay.py --source http://localhost:8080 --port 9090
        """
    )
    parser.add_argument(
        "--source", "-s",
        default=DEFAULT_SOURCE,
        help=f"Base URL of the Unity HololensViewStreamer (default: {DEFAULT_SOURCE})"
    )
    parser.add_argument(
        "--port", "-p",
        type=int,
        default=DEFAULT_PORT,
        help=f"Port to serve the MJPEG relay on (default: {DEFAULT_PORT})"
    )
    args = parser.parse_args()

    source = args.source.rstrip("/")
    port = args.port

    # Start the poller thread
    t = threading.Thread(target=poller_thread, args=(source,), daemon=True)
    t.start()

    # Start the HTTP server
    server = HTTPServer(("0.0.0.0", port), RelayHandler)
    server.request_queue_size = 16

    print("=" * 60)
    print(f"  HoloLens MJPEG Relay Proxy")
    print(f"  Source:  {source}/frame")
    print(f"  Viewer:  http://localhost:{port}/")
    print(f"  Stream:  http://localhost:{port}/stream")
    print(f"  Stats:   http://localhost:{port}/stats")
    print("=" * 60)
    print(f"  Open http://localhost:{port} in your browser.")
    print(f"  Press Ctrl+C to stop.\n")

    try:
        server.serve_forever()
    except KeyboardInterrupt:
        print("\nShutting down…")
        server.shutdown()


if __name__ == "__main__":
    main()
