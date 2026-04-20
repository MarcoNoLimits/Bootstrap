# UWP/HoloLens Release Gate Checklist

Use this before every deployment candidate.

## A) Configuration gate

- [ ] Build target is UWP, ARM64, Release.
- [ ] OpenXR is enabled for UWP.
- [ ] UWP capabilities include camera, microphone, networking, spatial perception.
- [ ] Endpoint settings use reachable LAN/production host (no device loopback).
- [ ] Signing certificate is configured locally and build signs successfully.

## B) Runtime health gate

- [ ] `ARCameraManager.subsystem.running == true` on device.
- [ ] `LocatableCameraArProjection` reaches intrinsics-ready state.
- [ ] `SignInferenceClient` receives successful `/predict_hand` responses.
- [ ] `HololensAsrManager` uploads and parses transcripts without sustained failure.
- [ ] No uncaught exceptions in dispatcher or network coroutines.

## C) Behavior gate

- [ ] Sign mode: letter/confidence captions update in real time.
- [ ] No-hand behavior is stable and user-visible.
- [ ] ASR mode: listening, fallback, and translation pathways all function.
- [ ] Mode switching (ASR <-> Sign <-> None) works without lockups.

## D) Performance/soak gate (30 minutes minimum)

- [ ] No repeated request deadlocks (`inFlight` flags always clear).
- [ ] No sustained frame-rate collapse from capture/inference loop.
- [ ] No repeated network error storms without recovery.
- [ ] Memory usage remains stable (no runaway growth).

## E) Pass/fail criteria

- **Pass**: All A/B/C gates pass and D has no critical instability.
- **Fail**: Any blocker in camera startup, inference, ASR pipeline, or deploy signing.
- **Conditional pass**: Non-blocking warnings only, with documented mitigation and owner.
