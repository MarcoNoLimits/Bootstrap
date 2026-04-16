# ASL Pipeline Contract (Laptop/Space -> HoloLens)

Use this document as the source-of-truth when implementing HoloLens integration in another repo.

## 1) Objective

Match the current ASL fingerspelling behavior from this repo:

- Hand ROI detection/cropping logic
- Inference API contract
- Confidence handling and history behavior

## 2) Canonical references (this repo)

- `hand_pipeline.py` (bbox + crop conventions)
- `webcam_inference.py` (runtime flow, spell behavior)
- `hf_upload/Sign-Language/app.py` (HF Space model I/O)
- `web/index.html` (browser-side gating/throttling/history)

## 3) Inference backend

Current backend: Hugging Face Space.

- Space ID: `mederbekaiana/Sign-Language`
- Runtime: `https://mederbekaiana-sign-language.hf.space`
- API name: `/predict`

Expected response fields:

- `predicted_letter` (single letter A-Z, string)
- `confidence` (float, 0..1)

## 4) Input contract to model

Model expects hand ROI image with preprocessing equivalent to:

- RGB image
- Resize to `224x224`
- Normalize mean/std:
  - mean `[0.485, 0.456, 0.406]`
  - std `[0.229, 0.224, 0.225]`

If HoloLens path sends image directly to HF Space, Space app applies this preprocessing.

## 5) Hand ROI contract

Required steps:

1. Detect hand landmarks/joints.
2. Build bounding box from landmarks.
3. Apply padding around bbox.
4. Clamp bbox to frame bounds.
5. Crop from raw camera frame (not transformed preview).

Baseline padding from Python path:

- `pad = 40` pixels

## 6) Runtime gating defaults (from web flow)

Start with these defaults:

- `MIN_API_MS = 220` (about 4-5 FPS)
- `MIN_HAND_AREA_FRAC = 0.07`
- `MIN_HANDEDNESS_SCORE = 0.72`
- One request in-flight at a time
- JPEG quality around `0.95`

## 7) Text/history behavior (client-side)

Because HF Space returns per-frame letter only, text buffering is client-side.

Use baseline behavior:

- Accept predicted letter only if confidence >= `0.55`
- Commit to history after stable repeats:
  - `STABLE_FRAMES_FOR_COMMIT = 3`
- Support manual controls:
  - commit, space, backspace, clear

## 8) Non-negotiable correctness checks

Before tuning anything else, verify:

- Projection/alignment: overlay bbox matches real hand
- No mirror/flip mismatch between preview and crop
- Channel order is RGB as expected
- Crop comes from raw frame region, not warped UI texture

## 9) Acceptance criteria

Implementation is acceptable only if:

1. Hand does not need to be centered for detection.
2. No prediction call when hand ROI is invalid.
3. Returned letter/confidence visible in UI.
4. Recognized text history updates with debounce.
5. Confidence and request cadence are logged for debugging.

