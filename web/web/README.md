# Web Version

This folder contains an isolated web version of your UI and interactions.

## Run

From the project root:

```bash
python3 -m http.server 8000
```

Then open:

- `http://localhost:8000/web/`

This web build uses:

- Current design from `UI-Preview.html`
- ASR button + API streaming behavior
- Sign Language and Translation mode UI flow
- Hand-drawn volume bar animation

It does not modify or depend on HoloLens scene wiring.

## Translation API (secure)

Do not put NMT API keys in this HTML. Serve the page from your app and add a POST route such as `/api/translate` that attaches the key server-side. The page defaults to `NMT_URL = '/api/translate'` and optional `HOLOASSIST_NMT_API_KEY` only if you inject it from a trusted server (not recommended for production).

Optional overrides on `window` before this script runs: `HOLOASSIST_ASR_URL`, `HOLOASSIST_NMT_URL`, `HOLOASSIST_NMT_API_KEY`, `HOLOASSIST_NMT_TARGET_LANG` (default Italian `it`). Set `HOLOASSIST_TRANSLATE_LATEST_ONLY` to translate only the latest ASR phrase instead of the full caption.

The page includes a **Clear text** control (bottom-left) to reset the caption without stopping ASR or sign mode.
