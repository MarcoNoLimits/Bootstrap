# UWP/HoloLens Deployment and Debug Runbook

This is the single-source operational guide for exporting, deploying, and debugging this Unity project on HoloLens 2.

## 1) Baseline environment

- Unity 2022.3 LTS project.
- UWP target architecture: ARM64.
- XR stack:
  - `com.unity.xr.arfoundation` 5.2.2
  - `com.unity.xr.openxr` 1.8.0
  - `com.microsoft.mixedreality.openxr` from `Packages/MixedReality/package`

## 2) Required scene wiring

- Use one XR camera path only:
  - `ARCameraManager` on the XR main camera.
  - `HololensPvCpuImageSource` connected to that camera manager.
  - `LocatableCameraArProjection` on the same AR camera object.
- Avoid duplicate non-XR cameras with `ARCameraManager`.
- Ensure an `ARSession` exists in scene (auto-created by projection script if missing).

## 3) Required UWP capabilities

In `Project Settings > Player > Publishing Settings`:

- `InternetClient`
- `InternetClientServer`
- `PrivateNetworkClientServer`
- `WebCam`
- `Microphone`
- `SpatialPerception`

These are already enabled in `ProjectSettings/ProjectSettings.asset`, but verify before release.

## 4) Endpoint configuration

- HoloLens must use LAN IP, not loopback:
  - Good: `http://<PC_LAN_IP>:8000`
  - Bad on device: `http://127.0.0.1:8000`
- Health contract:
  - `GET /health`
  - `POST /predict_hand` with `Content-Type: image/jpeg`

## 5) Export + build + deploy

1. In Unity, switch to **Universal Windows Platform**.
2. Build settings:
   - Target Device: HoloLens
   - Architecture: ARM64
   - Build Type: D3D Project
3. Export to `Builds/UWP`.
4. Open generated solution in Visual Studio.
5. Build configuration:
   - `Release`
   - `ARM64`
   - Deploy target: `Device` (or remote device)
6. Deploy and launch.

### Fast dev deploy profile (recommended while debugging)

- Use `Debug | ARM64` for faster incremental deploy iterations.
- In Visual Studio:
  - Disable **Just My Code** extras you do not need.
  - Keep only startup project build active.
  - Avoid full `Rebuild`; use `Build` unless Unity export changed.
- Keep Device Portal live camera preview and Mixed Reality Capture closed during deploy.
- Re-export from Unity only when package/project settings changed; otherwise reuse existing UWP export.

#### One-command helper

Use `fast_deploy.ps1` from repo root:

- Incremental debug build:
  - `powershell -ExecutionPolicy Bypass -File .\fast_deploy.ps1`
- Clean generated VS/UWP artifacts then build:
  - `powershell -ExecutionPolicy Bypass -File .\fast_deploy.ps1 -CleanArtifacts`
- Build + deploy attempt to remote device:
  - `powershell -ExecutionPolicy Bypass -File .\fast_deploy.ps1 -Deploy -RemoteMachine <DEVICE_IP>`

## 6) High-signal diagnostics

Use these runtime logs to triage quickly:

- `[HololensPvCpuImageSource] AR camera subsystem not running`
  - AR Foundation/OpenXR provider startup issue.
- `[LocatableCameraArProjection] cam ... intr=...`
  - Camera subsystem and intrinsics readiness heartbeat.
- `[SignInferenceClient][cpu-pipe:summary] ...`
  - Request success/fail rates, no-hand ratio, round-trip latency.
- `[ASR] summary attempts=... ok=... fail=...`
  - ASR upload health and empty transcript ratio.

## 7) Common failure signatures and fixes

- **No camera frames / `AR camera subsystem not running`**
  - Verify AR camera components are on XR camera only.
  - Confirm OpenXR is active for UWP and AR Foundation package is 5.1+.
- **Inference works in editor but not on device**
  - Replace `127.0.0.1` with PC LAN IP in runtime endpoint.
- **UWP build/deploy signing failure**
  - Configure local test certificate; see `UWP_CERTIFICATE_SETUP.md`.
- **Camera preempted / busy**
  - Close Mixed Reality Capture, Device Portal camera preview, and other immersive apps using PV camera.

## 8) Pre-deploy quick check

- Camera subsystem running and intrinsics available.
- Inference endpoint reachable from headset.
- ASR endpoint reachable and returning JSON.
- Caption updates visible in UI.
- No repeated request-in-flight stalls.

## 9) Runtime performance defaults for faster startup

- `SignInferenceClient` defaults tuned for less startup pressure:
  - delayed first startup capture
  - reduced JPEG width/quality for dev loop
  - debug frame saving and verbose pipeline logs off by default
- `HololensAsrManager` file logging is off by default (reduces disk I/O).
- `LocatableCameraArProjection` periodic camera diagnostics logging is off by default.
