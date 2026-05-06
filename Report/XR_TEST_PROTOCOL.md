# XR Test Protocol (Unity HoloLens2 vs WebXR)

## Goal
Collect statistically valid performance data so platform selection is evidence-based.

## Metrics
- `avg_fps` (higher is better)
- `p95_frame_ms` (lower is better)
- `avg_frame_ms`
- `updates_per_sec`
- `duration_sec`

## Required Repetitions
- Minimum: 10 runs per platform
- Recommended: 20 runs per platform
- Keep run duration fixed (for example 60s), same scene, same workload, similar thermals.

## Procedure
1. Restart app/session before each run.
2. Wait 30s idle stabilization.
3. Run workload for fixed duration.
4. In WebXR (`web/index.html`), use **Performance Capture** panel:
   - `Start Run` before testing features
   - `Stop Run` after test window
   - fill `Observed load delay (min)` and `Notes` (for lag/failure context)
   - `Download All Runs CSV` (or `Copy Last CSV Row`)
5. Export one row per run into `Report/xr_perf_runs.csv`.

## CSV Format
Use this header:

`platform,run_id,avg_fps,p95_frame_ms,avg_frame_ms,updates_per_sec,duration_sec`

Platforms must be exactly:
- `Unity_HoloLens2`
- `WebXR`

Notes:
- The web capture panel exports extra columns (`startup_delay_min`, `lag_spikes_over_100ms`, `notes`).
- Keep them for documentation, but for `Report/xr_perf_runs.csv` ensure at least the required columns above are present.

## Analysis Command
From repo root:

`MPLBACKEND=Agg MPLCONFIGDIR="$(pwd)/.mplconfig" .venv/bin/python Report/xr_stats_and_plots.py`

## Outputs
- `Report/xr_statistical_table.csv` (numerical table)
- `Report/xr_statistical_results.md` (written interpretation)
- `Report/xr_fps_comparison.png` (separate FPS figure, white background, larger fonts)
- `Report/xr_p95_comparison.png` (separate P95 figure, white background, larger fonts)
