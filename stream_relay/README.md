# HoloLens MJPEG Stream Relay

A lightweight Python proxy that sits between the Unity `HololensViewStreamer` and your browser, converting the per-frame HTTP polling into a smooth **MJPEG server-push stream**.

**Zero changes to the Unity build required.**

## Why?

The built-in viewer uses HTTP polling — every frame is a separate request/response cycle over WiFi. This causes choppy, laggy video. The relay converts it into a single persistent connection that pushes frames as they arrive (MJPEG / `multipart/x-mixed-replace`).

## Quick Start

```bash
# From the HoloLens IP (when deployed)
python relay.py --source http://172.16.6.45:8080

# From localhost (when running in Unity Editor)
python relay.py --source http://localhost:8080

# Custom port
python relay.py --source http://172.16.4.56:8080 --port 9090
```

Then open **http://localhost:9090** in your browser.

## Endpoints

| Endpoint | Description |
|---|---|
| `/` | HTML viewer page with live stats |
| `/stream` | Raw MJPEG stream (use in `<img>` or VLC) |
| `/frame` | Single JPEG frame (compatibility) |
| `/stats` | JSON stats (FPS, frame count, connection status) |

## Requirements

- Python 3.7+ (no external packages needed — stdlib only)
- The Unity `HololensViewStreamer` must be running on the source device

## Architecture

```
HoloLens (Unity)          Relay Proxy (laptop)         Browser
┌────────────────┐       ┌──────────────────┐       ┌──────────┐
│ /frame endpoint│ ←──── │ Poller thread    │       │          │
│ port 8080      │ fast  │ (60 Hz polling)  │       │  <img>   │
│                │ poll  │                  │ MJPEG │ auto-    │
│                │       │ /stream endpoint │ ────→ │ renders  │
│                │       │ port 9090        │ push  │          │
└────────────────┘       └──────────────────┘       └──────────┘
```

The poller runs at 60 Hz over LAN (fast, <1ms round-trip). The MJPEG stream pushes frames to the browser over a single persistent connection — no per-frame HTTP overhead.
