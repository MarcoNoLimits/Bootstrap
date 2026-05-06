# HoloLens vs WebXR Comparison Guide

This guide explains how to run repeatable performance comparisons between Unity on HoloLens and WebXR on HoloLens, collect metrics, and generate statistical outputs (including standard deviation when repeated runs are available).

## 1) Serve `web/index.html` for HoloLens browser

From the repository root:

```bash
python3 -m http.server 8080
```

Find your laptop LAN IP (same Wi-Fi as HoloLens):

```bash
ipconfig getifaddr en0
```

Open on HoloLens browser:

```text
http://<YOUR_LAPTOP_IP>:8080/web/index.html
```

## 2) Capture WebXR run metrics on HoloLens

Use the **Performance Capture** panel in `web/index.html`:

- Set `Platform` to `WebXR`
- Set `Run ID` (1, 2, 3, ...)
- Set `Observed load delay (min)` when needed
- Add notes (for example: `lag blocked feature testing`)
- Click `Start Run`
- Test features
- Click `Stop Run`
- Repeat for multiple runs
- Click `Download All Runs CSV` (or `Copy Last CSV Row`)

Captured metrics include:

- `avg_fps`
- `p95_frame_ms`
- `avg_frame_ms`
- `updates_per_sec`
- `duration_sec`
- `startup_delay_min`
- `lag_spikes_over_100ms`
- `notes`

## 3) Add Unity run metrics

Add Unity HoloLens run rows in `Report/xr_perf_runs.csv` with:

```csv
platform,run_id,avg_fps,p95_frame_ms,avg_frame_ms,updates_per_sec,duration_sec
Unity_HoloLens2,1,49.47,17.60,20.27,8.47,15.00
WebXR,1,31.00,33.00,32.26,7.12,15.00
```

Use exact platform names:

- `Unity_HoloLens2`
- `WebXR`

## 4) Generate statistical outputs

If virtualenv is not ready:

```bash
python3 -m venv .venv
.venv/bin/python -m pip install matplotlib pandas numpy
```

Run analysis:

```bash
MPLBACKEND=Agg MPLCONFIGDIR="$(pwd)/.mplconfig" .venv/bin/python Report/xr_stats_and_plots.py
```

Generated files:

- `Report/xr_statistical_table.csv`
- `Report/xr_statistical_results.md`
- `Report/xr_fps_comparison.png`
- `Report/xr_p95_comparison.png`
- `Report/xr_unity_metrics.png`
- `Report/xr_webxr_metrics.png`

## 5) Statistical interpretation notes

- With only one run per platform (`n=1`), `std`, confidence intervals, and p-values are not valid.
- To claim statistical significance, run repeated trials (minimum 10 per platform recommended).
- If WebXR has severe startup delay or lag that blocks feature testing, document this in `startup_delay_min` and `notes`; treat it as operational evidence in addition to numeric metrics.
