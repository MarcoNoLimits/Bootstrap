# XR Statistical Analysis (Unity HoloLens2 vs WebXR)

## Input Data Sufficiency
- Unity_HoloLens2: n=1 runs
- WebXR: n=1 runs
- Requirement for std + significance: at least 2 runs per platform (prefer 10+)

## Numerical Results

### Average FPS (higher is better)
- Unity_HoloLens2: mean=49.470, std=N/A, 95% bootstrap CI=N/A
- WebXR: mean=31.000, std=N/A, 95% bootstrap CI=N/A
- Permutation test p-value (difference in means): N/A

### Frame-time P95 in ms (lower is better)
- Unity_HoloLens2: mean=17.600, std=N/A, 95% bootstrap CI=N/A
- WebXR: mean=33.000, std=N/A, 95% bootstrap CI=N/A
- Permutation test p-value (difference in means): N/A

### Additional Metrics (from statistical table)
- Unity_HoloLens2 avg_frame_ms: 20.270; updates_per_sec: 8.470; duration_sec: 15.000
- WebXR avg_frame_ms: 32.260; updates_per_sec: 7.120; duration_sec: 15.000

## Decision
- Inferential conclusion is NOT valid yet because repeated runs are insufficient to estimate variance (std) robustly.
