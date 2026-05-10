"""Standalone ``POST /client_log`` registration for the HF ASR Space FastAPI app."""

from __future__ import annotations

import json
import os

from pydantic import BaseModel, Field


class ClientLogPayload(BaseModel):
    device: str = Field(default="hololens")
    session_id: str = Field(default="")
    chunk_id: int = Field(default=0)
    event: str = Field(default="")
    line: str = Field(default="")


def register_client_log_route(app) -> None:
    verbose = os.environ.get("DEBUG_CLIENT_LOG_VERBOSE", "0").strip() == "1"
    verbose_ids = os.environ.get("DEBUG_VERBOSE_IDS", "0").strip() == "1"

    @app.post("/client_log")
    async def client_log(payload: ClientLogPayload) -> dict:
        if verbose:
            data = payload.model_dump() if hasattr(payload, "model_dump") else payload.dict()
            print(json.dumps(data, ensure_ascii=False))
        else:
            text = (payload.line or "").strip()
            if text:
                if verbose_ids and payload.session_id:
                    print(f"{text} | session_id={payload.session_id} chunk_id={payload.chunk_id}")
                else:
                    print(text)
        return {"ok": True}
