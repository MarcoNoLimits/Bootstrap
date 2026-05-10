# ASR (speech-to-text)

This work supports **automatic speech recognition** for everyday use. The system is **fine-tuned for people who are deaf or hard of hearing**, so that spoken language can be presented as text in a way that better reflects how this community speaks. The aim is to make communication more accessible in education, assistive technology, and inclusive settings.

---

The sections below summarise where the model is hosted and how developers can run the software locally.

## Model on Hugging Face

- **Weights & tokenizer:** [Thedeezat/ASR-Hearing-Impaired](https://huggingface.co/Thedeezat/ASR-Hearing-Impaired)
- **Hosted HTTP API (Space):** [Thedeezat/ASR-Hearing-Impaired-API](https://huggingface.co/spaces/Thedeezat/ASR-Hearing-Impaired-API)  
  - Base URL: `https://thedeezat-asr-hearing-impaired-api.hf.space`

## HTTP API contract

Use this section when configuring Cursor, apps, or scripts to call the Space.

### Endpoints

- `GET /` → browser test UI (mic capture)
- `GET /info` → service metadata (model ids, contract)
- `GET /health` → health check
- `POST /audio` → speech-to-text inference
- `POST /client_log` → optional HoloLens pipeline telemetry (compact `[client] …` lines on stdout); set **`DEBUG_CLIENT_LOG_VERBOSE=1`** on the Space for full JSON. Register via `client_log_route.register_client_log_route` — see `ASR-API-Space/README_CLIENT_LOG.md`

### `POST /audio` input

- **Body:** raw little-endian `float32` PCM, mono (no WAV header)
- **Required header:** `X-Sample-Rate` (example: `16000`)
- **Optional header:** `X-Forced-Language`
  - `english` → fastest for English speech
  - `italian` → fastest for Italian speech
  - `auto` (default) → best language arbitration, slower

### `POST /audio` output

- Success:
  - `{"text":"..."}`
- Error:
  - `{"text":"","error":"..."}`

The API returns an empty text (`{"text":""}`) for silence / low-confidence chunks by design.

### cURL example

```bash
curl -X POST "https://thedeezat-asr-hearing-impaired-api.hf.space/audio" \
  -H "Content-Type: application/octet-stream" \
  -H "X-Sample-Rate: 16000" \
  -H "X-Forced-Language: english" \
  --data-binary @chunk.f32
```

Where `chunk.f32` is raw float32 mono PCM.

### HoloLens/client audio requirements (important)

For stable output and low hallucination rate, your client (HoloLens, Unity, mobile, browser) should send:

- mono stream only
- `float32` PCM values in `[-1.0, 1.0]`
- chunk lengths around `1.2s - 2.5s` (avoid very tiny chunks)
- correct `X-Sample-Rate` header that matches real audio sample rate
- no WAV/RIFF/container header bytes in the body

If any of the above is wrong, symptoms are often: random letters/words, language flips, very high latency, or unstable transcripts.

### Language selection strategy

- Use `X-Forced-Language: english` when speaker is English
- Use `X-Forced-Language: italian` when speaker is Italian
- Use `auto` only when language can change dynamically

Explicit language mode is fastest and usually most stable.

### Recommended runtime variables (free CPU Space, quality-preserving)

- `ASR_CPU_THREADS=2`
- `ASR_CPU_INTEROP_THREADS=1`
- `ASR_DUAL_DECODE=0`
- `ASR_BACKEND=faster-whisper`
- `ASR_FAST_COMPUTE_TYPE=int8`
- `ASR_FAST_MODEL_EN=distil-large-v3`
- `ASR_FAST_MODEL_IT=distil-large-v3`
- `ASR_FAST_REALTIME_MODEL_EN=medium.en`
- `ASR_FAST_REALTIME_MODEL_IT=medium`
- `ASR_FAST_USE_REALTIME_FOR_FORCED=1`

For lowest latency, choose `english` or `italian` explicitly instead of `auto`.

### Current balanced realtime profile (no UI mode switching)

The API is tuned to a single balanced profile for free CPU Spaces:

- preserve most words (avoid aggressive dropping)
- reduce runaway lag over time
- suppress low-energy hallucination fragments

Main defaults currently used by the API:

- `ASR_WHISPER_MAX_NEW_TOKENS=224`
- `ASR_DROP_WHEN_BUSY=0`
- `ASR_RMS_SKIP_FORCED=0.0074`
- `ASR_RMS_SKIP_AUTO=0.0072`
- browser sender overlap tail: ~`0.45s` (to recover cut boundary words)

If your HoloLens stream is still unstable, keep these values first and tune only one variable at a time.

### Hallucination/instability controls

These are already supported by the API and can be set as Space Variables:

- `ASR_NO_SPEECH_THRESHOLD` (default around `0.6`)
  - higher = more aggressive silence rejection (fewer random tokens)
- `ASR_COMPRESSION_RATIO_THRESHOLD` (default around `2.2`)
  - lower = stricter repetition/gibberish rejection
- `ASR_LOGPROB_THRESHOLD` (default around `-1.0`)
  - higher (less negative) = stricter confidence filtering
- `ASR_MIN_RMS` (default around `0.007`)
  - higher = ignore quieter chunks (less hallucination, may miss quiet speech)

Start with defaults. Tune one variable at a time.

### Latency vs quality tuning

If latency is still too high on free CPU, try these in order:

1. Keep language forced (`english` or `italian`) instead of `auto`
2. Keep `ASR_DUAL_DECODE=0`
3. Use client chunk size near `1.8s - 2.2s`
4. Keep `ASR_WHISPER_NUM_BEAMS=2` (quality-first baseline)
5. Only if needed: set `ASR_WHISPER_MAX_NEW_TOKENS=192` (faster, small risk on very long utterances)

Avoid lowering beams to `1` if your priority is quality.

### HoloLens weird output troubleshooting

If HoloLens still hallucinates or acts weird, verify in this order:

1. **Payload format**
   - confirm body is raw float32 PCM, not 16-bit PCM, not WAV bytes
2. **Sample rate correctness**
   - confirm header equals actual capture sample rate
3. **Chunk duration**
   - avoid ultra-short chunks (`< 0.8s`) unless you also increase smoothing
4. **Silence/noise**
   - if random short tokens appear during silence, increase `ASR_MIN_RMS` slightly (`0.008` or `0.009`)
5. **Language mode**
   - set forced language instead of `auto` during single-language sessions
6. **Server logs**
   - check startup logs for applied env vars and thread settings

### HoloLens update checklist (recommended)

Use this exact checklist before changing models:

1. **Force language from client**
   - send `X-Forced-Language: english` for English sessions
   - send `X-Forced-Language: italian` for Italian sessions
   - avoid `auto` unless language truly switches mid-session
2. **Audio format**
   - mono, raw `float32` PCM, no WAV header
   - normalized range near `[-1, 1]`
3. **Chunking**
   - target chunk duration around `1.6s - 2.6s`
   - avoid too many tiny chunks (`< 0.8s`)
4. **Silence handling**
   - if random words appear while silent, raise `ASR_MIN_RMS` slightly (e.g. `0.008`)
   - if words are dropped too often, lower `ASR_MIN_RMS` or `ASR_RMS_SKIP_FORCED` slightly
5. **Redeploy after variable changes**
   - Space variables only apply after restart/rebuild

### How to read HF timing logs

The API prints one timing line per `/audio` call:

- `asr_ms`: model inference time (main latency driver)
- `preprocess_ms`: audio prep time (normally small)
- `chars`: output size (`0` often means silence/low-confidence chunk)
- `dropped=silence|low_rms|busy`: chunk intentionally ignored by policy

If you see many `chars=0` on `chunk_s > 2s`, your stream likely has low-energy/noisy segments and needs client-side gain/VAD tuning.

### Quick validation checklist

Before integrating with production client, run:

- `GET /health` returns `ok: true`
- `GET /info` returns expected model ids
- manual `/` browser test page transcribes correctly
- same speech sample tested in:
  - `english` mode
  - `italian` mode
  - `auto` mode
- confirm latency and stability with real microphone conditions (quiet and noisy)

## Layout

| Path | Role |
|------|------|
| `src/` | Python: `web_realtime`, `web_realtime_remote`, `whisper_asr`, `train_whisper`, `dataset`, … |
| `models/` | Local checkpoints (e.g. `whisper_finetuned/`), optional `lexicon.txt` |
| `scripts/` | Evaluation / dataset helpers |
| `ASR-API-Space/` | Active Docker Space source for the HF API (`/audio`, `/health`, `/info`, UI at `/`) |

## Quick start (local)

From the **repository root** (parent of `ASR/`):

```bash
# Remote API only — no GPU model load; proxies to the Space by default
python3 -m ASR.src.web_realtime_remote
```

Open `http://127.0.0.1:5000` (use `PORT=5050` if port 5000 is busy). Override the upstream URL with `ASR_REMOTE_AUDIO_URL`.

```bash
# Local Whisper (needs PyTorch + checkpoint under ASR/models/…)
python3 -m ASR.src.web_realtime
```

Training and evaluation are documented in `TRAINING.md` and `WHISPER_TRAINING_PLAN.md`. Device / HoloLens HTTP details: `HOLOLENS_API.md`.