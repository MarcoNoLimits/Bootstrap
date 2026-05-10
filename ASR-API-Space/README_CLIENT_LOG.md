# `/client_log` — HoloLens pipeline telemetry on HF Space

Unity POSTs JSON to **`POST https://<space-host>/client_log`** (fire-and-forget). Logs appear in the Space container stdout:

```text
[client_log] device=hololens session=<session_id> chunk_id=<n> event=ASR_MERGE line=[ASR MERGE] old='...' ...
```

## Add to your Space FastAPI app

In the HF repo **`main.py`** (or wherever `app = FastAPI()` lives):

```python
from client_log_route import register_client_log_route

register_client_log_route(app)
```

Redeploy the Space after merging.

## Log format

- Default (**`DEBUG_CLIENT_LOG_VERBOSE` unset or `0`**): Space stdout prints only the JSON **`line`** field (compact `[client] …` rows). No session/device dump.
- Verbose (**`DEBUG_CLIENT_LOG_VERBOSE=1`**): prints the full JSON body for deep debugging.

Payload schema:

```json
{
  "device": "hololens",
  "session_id": "...",
  "chunk_id": 123,
  "event": "ASR_MERGE",
  "line": "[ASR MERGE] old='...' new='...' merged='...'"
}
```

Response: `{"ok": true}`
