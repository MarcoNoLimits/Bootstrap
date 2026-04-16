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

### A) HoloLens PV pipeline — Health (local / LAN)

The Unity client (`SignInferenceClient` + `HololensPvCpuImageSource`) sends **raw JPEG** frames to a **Health** service:

- **GET** `http://127.0.0.1:8000/health` — sanity check  
- **POST** `http://127.0.0.1:8000/predict_hand` — **Content-Type: `image/jpeg`**, body = JPEG bytes  

PowerShell examples:

```powershell
Invoke-RestMethod http://127.0.0.1:8000/health
$bytes = [System.IO.File]::ReadAllBytes("C:\path\to\your.jpg")
Invoke-RestMethod -Uri http://127.0.0.1:8000/predict_hand -Method Post -ContentType "image/jpeg" -Body $bytes
```

On HoloLens, set **`baseUrl`** to `http://<PC_LAN_IP>:8000` (not `127.0.0.1`). Inspector: **`inferEndpointPath`** = `/predict_hand` (default).

### Unity HoloLens — correct camera stack (common failure point)

Use **AR Foundation PV** only:

| Use | Do not use |
|-----|------------|
| `ARCameraManager` + `TryAcquireLatestCpuImage` → `XRCpuImage` | `WebCamTexture` |
| Same path as this repo: `HololensPvCpuImageSource.cs` | Raw WinRT / MediaCapture-only paths for this pipeline |

**Scene (XR Main Camera):**

- `Camera`
- `ARCameraManager` (required) -done
- `ARCameraBackground` (optional; typical for passthrough)
- `HololensPvCpuImageSource` (assign the same `ARCameraManager` or leave empty for `FindObjectOfType`)-done

**UWP capabilities** (Project Settings → Player → Publishing Settings — this repo’s `ProjectSettings.asset` already enables):

- **InternetClient** — HTTP to your PC -done
- **WebCam** — needed for PV / camera access in the UWP manifest -done
- **SpatialPerception** — MR / tracking (recommended on HoloLens) -done

**Server URL:** `http://<PC_LAN_IP>:8000` with path **`/predict_hand`** for the Health contract (not `localhost` / `127.0.0.1` on device). Same Wi‑Fi as the PC. --done (but baseurl it,s http://192.168.1.42:8000)

**Orientation:** if classification looks wrong, toggle **`mirrorY`** on `HololensPvCpuImageSource` (maps to `XRCpuImage.Transformation.MirrorY` vs `None`). -done

### AR Foundation version + OpenXR (if you see “AR camera subsystem not running”)

Unity’s **OpenXR** loader can run on HoloLens and Mixed Reality OpenXR will log `LocatableCameraProvider_Registered`, but **`ARCameraManager`** still needs a registered **`XRCameraSubsystem`** from AR Foundation’s platform provider.

- **AR Foundation 4.x** (e.g. 4.1) documented HoloLens camera against the legacy **Windows XR Plugin**, not OpenXR-only.
- **AR Foundation 5.1+** documents the **OpenXR Plug-in** as the HoloLens provider, including **camera** / `TryAcquireLatestCpuImage`.

If `HololensPvCpuImageSource` reports **`AR camera subsystem not running`** (or `SubsystemManager` lists **no** `XRCameraSubsystem` descriptors on device), **upgrade `com.unity.xr.arfoundation`** in Package Manager to **5.1 or newer** (match your Unity 2022.3 LTS), keep **XR Plug-in Management → OpenXR** for UWP, then fix any API breakages the upgrader reports. After upgrade, rebuild UWP and deploy.

**Ignore most of this in the VS Output window:** repeated `80070005` on `Windows.Networking.Connectivity` and similar lines are common when debugging UWP; they are not the root cause of the AR camera subsystem. **`0xC00DABE0` / “No capture devices are available”** often comes from a **Media Foundation / generic webcam** path—your PV path should be AR Foundation + `XRCpuImage`, not `WebCamTexture`.

Expected JSON fields (client parses these):

- `predicted_letter` (string)
- `confidence` (float, 0..1)
- `no_hand` (bool, optional)

### B) Legacy: Hugging Face Space

- Space ID: `mederbekaiana/Sign-Language`
- Runtime: `https://mederbekaiana-sign-language.hf.space`
- API name: `/predict` (Gradio client path in Unity is separate)

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

