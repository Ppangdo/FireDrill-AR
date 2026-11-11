"""FireDrill-AR server (명세서 9장, 14.6): storage, LLM relay, emergency abort. Nothing else.

`create_app` builds an app from Settings; `app.main` holds the instance uvicorn serves.
"""
from __future__ import annotations

import asyncio
import contextlib
import csv
import io
import json
import logging
import uuid
from datetime import datetime, timezone
from typing import Any

from fastapi import Depends, FastAPI, Request, WebSocket, WebSocketDisconnect
from fastapi.exceptions import RequestValidationError
from fastapi.responses import FileResponse, JSONResponse, Response
from pydantic import ValidationError
from sqlmodel import Session, func, select
from starlette.exceptions import HTTPException as StarletteHTTPException

from . import schemas as api
from .db import Building, EvaluationRow, EventRow, ReportRow, Run, TrainingSession, init_db, make_engine, utcnow
from .devices import DeviceHub
from .llm import LlmBackend, LlmRelay
from .settings import Settings

log = logging.getLogger("firedrill")

_AUTO: Any = object()

STATUS_CODES = {401: "bad_token", 404: "not_found", 422: "invalid", 502: "llm_error", 504: "llm_timeout"}


class ApiError(Exception):
    def __init__(self, status: int, message: str):
        self.status, self.code, self.message = status, STATUS_CODES.get(status, "invalid"), message


def error_response(status: int, code: str, message: str) -> JSONResponse:
    body = api.ErrorResponse(error=api.ErrorBody(code=code, message=message))
    return JSONResponse(body.model_dump(), status_code=status)


def _load_json(path) -> dict:
    return json.loads(path.read_text(encoding="utf-8"))


def create_app(settings: Settings | None = None, llm_backend: LlmBackend | None = _AUTO) -> FastAPI:
    settings = settings or Settings()
    engine = make_engine(settings)
    init_db(engine, settings)
    eval_cfg = _load_json(settings.config_dir / "evaluation.json")
    npc_cache = _load_json(settings.config_dir / "npc_cache.json")
    relay = (LlmRelay.for_mode(settings, engine, eval_cfg, npc_cache) if llm_backend is _AUTO
             else LlmRelay(settings, engine, llm_backend, eval_cfg, npc_cache))
    hub = DeviceHub()
    if settings.llm_mode == "claude" and relay.mode == "off":
        log.warning("LLM_MODE=claude but ANTHROPIC_API_KEY is empty: using fallback responses only")

    app = FastAPI(title="FireDrill-AR server", version="2")
    app.state.settings, app.state.engine, app.state.relay, app.state.hub = settings, engine, relay, hub

    # ---- errors: always {"error": {"code", "message"}} ----------------------

    @app.exception_handler(ApiError)
    async def _api_error(_: Request, e: ApiError):
        return error_response(e.status, e.code, e.message)

    @app.exception_handler(StarletteHTTPException)
    async def _http_error(_: Request, e: StarletteHTTPException):
        return error_response(e.status_code, STATUS_CODES.get(e.status_code, "invalid"), str(e.detail))

    @app.exception_handler(RequestValidationError)
    async def _validation_error(_: Request, e: RequestValidationError):
        first = e.errors()[0] if e.errors() else {}
        where = ".".join(str(p) for p in first.get("loc", ()))
        return error_response(422, "invalid", f"{where}: {first.get('msg', 'invalid request')}")

    # ---- auth ---------------------------------------------------------------

    def device_token(request: Request) -> str:
        """X-Device-Token header (or ?token= for the staff page opened in a phone browser)."""
        token = request.headers.get("x-device-token") or request.query_params.get("token")
        if not token or token not in settings.tokens:
            raise ApiError(401, "missing or unknown X-Device-Token")
        return settings.tokens[token]

    auth = [Depends(device_token)]

    def get_run(s: Session, rid: str) -> Run:
        run = s.get(Run, rid)
        if run is None:
            raise ApiError(404, f"run {rid} not found")
        return run

    def same_run(rid: str, other: str, what: str) -> None:
        if other != rid:
            raise ApiError(422, f"{what} run_id {other} does not match path run {rid}")

    # ---- REST (14.6) --------------------------------------------------------

    @app.get("/health", response_model=api.HealthResponse, dependencies=auth)
    def health():
        return api.HealthResponse(ok=True, llm=relay.mode,
                                  server_time=datetime.now(timezone.utc).isoformat(timespec="seconds"))

    @app.get("/buildings/{building_id}/zone-graph", dependencies=auth)
    def zone_graph(building_id: str):
        with Session(engine) as s:
            b = s.get(Building, building_id)
            if b is None:
                raise ApiError(404, f"building {building_id} not found")
            return JSONResponse(b.zone_graph)

    @app.post("/sessions", response_model=api.CreateSessionResponse, dependencies=auth)
    def create_session(body: api.CreateSessionRequest):
        with Session(engine) as s:
            if s.get(Building, body.building_id) is None:
                raise ApiError(404, f"building {body.building_id} not found")
            sid = uuid.uuid4()
            s.add(TrainingSession(id=str(sid), building_id=body.building_id,
                                  participant_code=body.participant_code, device=body.device_id))
            s.commit()
        return api.CreateSessionResponse(session_id=sid)

    @app.post("/sessions/{sid}/runs", response_model=api.CreateRunResponse, dependencies=auth)
    def create_run(sid: str, body: api.CreateRunRequest):
        with Session(engine) as s:
            if s.get(TrainingSession, sid) is None:
                raise ApiError(404, f"session {sid} not found")
            rid = uuid.uuid4()
            s.add(Run(id=str(rid), session_id=sid, scenario_id=body.scenario_id, attempt=body.attempt))
            s.commit()
        return api.CreateRunResponse(run_id=rid)

    @app.post("/runs/{rid}/events", response_model=api.PostEventsResponse, dependencies=auth)
    def post_events(rid: str, body: api.PostEventsRequest):
        with Session(engine) as s:
            get_run(s, rid)
            for e in body.events:
                same_run(rid, e.run_id, f"event seq {e.seq}")
            seqs = [e.seq for e in body.events]
            existing = set(s.exec(select(EventRow.seq).where(EventRow.run_id == rid,
                                                             EventRow.seq.in_(seqs))).all()) if seqs else set()
            accepted = 0
            for e in body.events:  # (run_id, seq) already stored or repeated in this batch -> ignored
                if e.seq in existing:
                    continue
                existing.add(e.seq)
                s.add(EventRow(run_id=rid, seq=e.seq, t=e.t, type=e.type, zone=e.zone, pos=list(e.pos),
                               head_ratio=e.head_ratio, c_head=e.c_head, fed=e.fed, zone_d=e.zone_d,
                               layer_y=e.layer_y, data=e.data))
                accepted += 1
            s.commit()
            last = s.exec(select(func.max(EventRow.seq)).where(EventRow.run_id == rid)).one()
        return api.PostEventsResponse(accepted=accepted, last_seq=last or 0)

    @app.post("/runs/{rid}/finish", response_model=api.OkResponse, dependencies=auth)
    def finish_run(rid: str, body: api.FinishRunRequest):
        ev = body.evaluation
        same_run(rid, ev.run_id, "evaluation")
        if ev.outcome != body.outcome:
            raise ApiError(422, f"outcome {body.outcome} does not match evaluation.outcome {ev.outcome}")
        with Session(engine) as s:
            run = get_run(s, rid)
            run.outcome, run.ended_at = body.outcome, utcnow()
            row = s.get(EvaluationRow, rid) or EvaluationRow(run_id=rid, outcome=ev.outcome, total_pct=0,
                                                             elapsed_s=0)
            row.outcome, row.total_pct, row.elapsed_s = ev.outcome, ev.total_pct, ev.elapsed_s
            row.items = [i.model_dump() for i in ev.items]
            row.timeline = [p.model_dump() for p in ev.timeline]
            row.weak_zones = list(ev.weak_zones)
            s.add(run)
            s.add(row)
            s.commit()
        return api.OkResponse()

    @app.post("/runs/{rid}/report", response_model=api.ReportResponse, dependencies=auth)
    async def report(rid: str, body: api.ReportRequest):
        same_run(rid, body.evaluation.run_id, "evaluation")
        with Session(engine) as s:
            get_run(s, rid)
        res = await relay.report(body.evaluation)
        with Session(engine) as s:
            row = s.get(ReportRow, rid) or ReportRow(run_id=rid, model="", latency_ms=0, fallback_used=True)
            row.model, row.text, row.latency_ms, row.fallback_used = (res.model, res.report.model_dump(),
                                                                      res.latency_ms, res.fallback_used)
            row.created_at = utcnow()
            s.add(row)
            s.commit()
        return res

    @app.post("/npc/react", response_model=api.NpcReactResponse, dependencies=auth)
    async def npc_react(body: api.NpcReactRequest):
        return await relay.npc_react(body)

    @app.get("/runs/{rid}/export", dependencies=auth)
    def export_run(rid: str):
        with Session(engine) as s:
            get_run(s, rid)
            rows = s.exec(select(EventRow).where(EventRow.run_id == rid).order_by(EventRow.seq)).all()
        buf = io.StringIO()
        w = csv.writer(buf, lineterminator="\n")
        w.writerow(["run_id", "seq", "t", "type", "zone", "x", "y", "z", "head_ratio", "c_head", "fed",
                    "zone_d", "layer_y", "data"])
        for r in rows:
            x, y, z = r.pos
            w.writerow([r.run_id, r.seq, r.t, r.type, r.zone or "", x, y, z, r.head_ratio, r.c_head, r.fed,
                        r.zone_d, r.layer_y, json.dumps(r.data or {}, ensure_ascii=False, sort_keys=True)])
        # BOM so Excel opens the Korean text correctly
        return Response(("﻿" + buf.getvalue()).encode("utf-8"), media_type="text/csv; charset=utf-8",
                        headers={"Content-Disposition": f'attachment; filename="run_{rid}.csv"'})

    # ---- staff (요원 화면) ---------------------------------------------------

    @app.get("/staff", dependencies=auth)
    def staff_page():
        return FileResponse(settings.static_dir / "staff.html", media_type="text/html; charset=utf-8")

    @app.get("/staff/devices", dependencies=auth)
    def staff_devices():
        return {"schema_version": 1, "devices": hub.snapshot(sorted(set(settings.tokens.values())))}

    @app.post("/staff/abort/{device_id}", response_model=api.OkResponse, dependencies=auth)
    async def staff_abort(device_id: str):
        if not await hub.abort(device_id, by="staff"):
            raise ApiError(404, f"device {device_id} is not connected")
        log.warning("abort sent to %s", device_id)
        return api.OkResponse()

    # ---- WebSocket ----------------------------------------------------------

    @app.websocket("/ws/device/{device_id}")
    async def device_ws(ws: WebSocket, device_id: str):
        token = ws.query_params.get("token") or ws.headers.get("x-device-token")
        if not token or settings.tokens.get(token) != device_id:
            await ws.close(code=1008, reason="bad_token")
            return
        await ws.accept()
        conn = hub.attach(device_id, ws)
        pinger = asyncio.create_task(hub.ping_loop(conn, settings.ws_ping_interval_s))
        try:
            while True:
                raw = await ws.receive_text()
                try:
                    msg = json.loads(raw)
                    if isinstance(msg, dict) and msg.get("type") == "status":
                        hub.update(device_id, api.WsStatus.model_validate(msg))
                except (json.JSONDecodeError, ValidationError) as e:
                    log.info("ignored bad ws message from %s: %s", device_id, str(e)[:200])
        except WebSocketDisconnect:
            pass
        finally:
            pinger.cancel()
            with contextlib.suppress(asyncio.CancelledError, Exception):
                await pinger
            hub.detach(device_id, conn)

    return app

