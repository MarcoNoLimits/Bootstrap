# HoloAssist — Progress Report
**Date:** March 25, 2026

---

## Overview

The team is building HoloAssist, a Mixed Reality application that runs on the HoloLens 2 and acts as a real-time communication companion. The idea is that a user wearing the headset can speak naturally, have their words recognized, translated, and displayed directly in their field of view — all without taking the device off or looking at a phone or screen.

The project is built in Unity using the OpenXR standard, which means it can target HoloLens 2 and other XR headsets with minimal changes.

---

## What Has Been Built

- **Designed and Built the Core UI System.** The team created a world-space user interface that floats in front of the user at a comfortable distance and gently follows their head movement. Rather than relying on Unity's inspector to set things up, everything is wired in code — the panel, its texture, its collider, and its interaction handlers — making the system robust and scene-independent.

[SCREENSHOT NEEDED: File: App.cs, Lines: 70–177, Context: The InitializeUI method that builds the floating world-space UI panel from scratch at runtime.]

- **Implemented Hand Interaction with the UI.** One of the harder problems in XR development is getting hand-ray controllers to actually "click" buttons rendered inside a floating panel. A custom bridge script was written that translates the XR ray's hit position on the panel into a proper pointer event that Unity's UI system can understand. This means users can point at a button with their hand ray and press it naturally.

[SCREENSHOT NEEDED: File: WorldUIInputBridge.cs, Lines: 96–134, Context: The ClickUI method showing how XR ray hit coordinates are converted into UI pointer events.]

- **Added Live Voice Recognition.** The application listens continuously to the user using Windows' built-in speech recognition engine. When the user finishes speaking, the recognized sentence is automatically sent to the translation server. If the recognizer stops for any reason, it restarts itself — so the user never has to manually trigger listening.

[SCREENSHOT NEEDED: File: WizardOfOzClient.cs, Lines: 236–274, Context: The VoiceManager class showing the DictationRecognizer setup and auto-restart logic.]

- **Connected to a Remote Translation Server.** Once the speech is recognized, the text is sent over the network to an external server (running separately on a PC) that handles the actual translation. The translated result comes back and appears on the user's display panel in real time. This is the "Wizard of Oz" setup that lets the team test the full experience before the AI model is fully embedded on-device.

[SCREENSHOT NEEDED: File: WizardOfOzClient.cs, Lines: 153–167, Context: The WireEvents method showing the voice → network → UI display chain.]

- **Built a Live Camera Feed Viewer.** To help observers in the room (or remote collaborators) see exactly what the HoloLens user is seeing, a small HTTP server runs inside Unity. Anyone on the same network can open a browser and see a live video feed from the headset at up to 30 frames per second. This has been useful for demonstrations and remote debugging.

[SCREENSHOT NEEDED: File: HololensViewStreamer.cs, Lines: 182–232, Context: The camera capture and JPEG streaming pipeline that powers the live browser feed.]

---

## Challenges

- WebXR was evaluated early but dropped — UI panels drifted in world space and performance was inconsistent across headsets
- WebXR lacks fine-grained hand-joint access; Unity's XR Interaction Toolkit provided the per-frame joint data needed for reliable gesture handling
- No native WebXR support for persistent world-locked UI; workarounds broke on headset sleep/resume
- Hand-ray "clicking" on floating UI panels required a custom `WorldUIInputBridge` — not supported out-of-the-box in Unity's UI Toolkit
- Input hit-testing had to be carefully layered to prevent input leaking between world-space objects and UI colliders
- Subtitle panels needed iterative layout tuning to stay readable without blocking the user's hand view

---

## Current State & Next Steps

The core infrastructure is solid. The UI displays correctly in the headset, hand interaction works, voice recognition is running continuously, and the full pipeline from speech to translated text is functional.

What remains is connecting the navigation buttons in the settings sidebar to actual screens, and building out the dedicated views for ASR configuration, sign language display, and the other planned features. The UI shells for these are already in place — they just need their logic filled in.

---

*All information in this report is based directly on source files in the `Assets/` directory as of March 25, 2026.*
