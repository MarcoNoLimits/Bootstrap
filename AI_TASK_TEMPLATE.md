# AI Task Template (HoloLens ASL)

Copy/paste this template when asking an AI agent to implement work in the HoloLens repo.

---

## Context

You are implementing HoloLens ASL fingerspelling integration.

Read and follow this contract first:

- `ASL_PIPELINE_CONTRACT.md` (provided in prompt or repo)

Assume this is a cross-repo task (you do NOT have full context unless explicitly provided).

## Goal

Implement: `<ONE concrete task only>`

Examples:

- "Add hand ROI extraction from OpenXR joints and draw debug bbox overlay."
- "Send hand crop to HF Space `/predict` and show letter+confidence."
- "Add client-side debounce history with commit/space/backspace/clear."

## Must-follow constraints

1. Do not refactor unrelated systems.
2. Keep constants configurable in one place.
3. Use the exact API contract:
   - Space: `mederbekaiana/Sign-Language`
   - api_name: `/predict`
   - output: `predicted_letter`, `confidence`
4. Use one in-flight request at a time.
5. Do not assume server-side text buffer exists.

## Implementation requirements

- Add clear logging for:
  - request rate/FPS
  - confidence
  - dropped/invalid frames
- Include debug visuals for ROI.
- Preserve existing app stability (no blocking main loop).

## Deliverables

1. List of changed files
2. Summary of logic added
3. Runtime instructions to test
4. Known limitations

## Acceptance tests

- [ ] Hand off-center still works.
- [ ] Invalid/no-hand frames are gated.
- [ ] Letter/confidence update live.
- [ ] History updates only after debounce.
- [ ] No request floods (one in-flight).

## Output format

Provide:

1. "What changed"
2. "How to run"
3. "How I validated"
4. "Follow-up improvements (optional)"

---

## Example task request to AI

"Using the contract, implement only Stage 1: OpenXR hand joints -> 2D ROI bbox overlay.
No API calls yet. Add logs and a toggle for debug overlay.
Return changed files and test steps."

