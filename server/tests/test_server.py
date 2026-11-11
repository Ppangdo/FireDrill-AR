"""Stage 2 gate (명세서 15장): every endpoint, token rejection, duplicate seq ignored,
LLM timeout/error/schema mismatch -> fallback, WebSocket abort delivery. No API key needed."""
from __future__ import annotations

import asyncio
import csv
import io
import json
import re
from pathlib import Path
from types import SimpleNamespace

import pytest
from fastapi.testclient import TestClient
from sqlmodel import Session, select
from starlette.websockets import WebSocketDisconnect

from app import schemas as api
from app.db import EvaluationRow, LlmCall, ReportRow
from app.factory import create_app
from app.llm import ClaudeBackend, FakeBackend, LlmBackendError, LlmReply
from app.settings import SERVER_DIR, Settings
from app.templates import template_report

ROOT = Path(__file__).resolve().parents[2]
CONFIG = ROOT / "unity" / "Assets" / "FireDrill" / "Config"
GOLDEN = ROOT / "contracts" / "golden"
EVAL_CFG = json.loads((CONFIG / "evaluation.json").read_text(encoding="utf-8"))
NPC_CACHE = json.loads((CONFIG / "npc_cache.json").read_text(encoding="utf-8"))

TOKEN = "tok-pc"
H = {"X-Device-Token": TOKEN}


def make_settings(tmp_path: Path, **kw) -> Settings:
    base = dict(_env_file=None, database_url=f"sqlite:///{(tmp_path / 't.db').as_posix()}", llm_mode="fake",
                device_tokens=f"pc-01:{TOKEN},staff:tok-staff", anthropic_api_key="",
                npc_timeout_s=0.3, report_timeout_s=0.3, ws_ping_interval_s=0.05)
    base.update(kw)
    return Settings(**base)


@pytest.fixture
def client(tmp_path):
    with TestClient(create_app(make_settings(tmp_path))) as c:
        yield c


def client_with(tmp_path, backend, **kw) -> TestClient:
    return TestClient(create_app(make_settings(tmp_path, **kw), llm_backend=backend))


def db_rows(c: TestClient, model):
    with Session(c.app.state.engine) as s:
        return s.exec(select(model)).all()


def assert_error(r, status: int, code: str):
    assert r.status_code == status, r.text
    body = r.json()
    api.ErrorResponse.model_validate(body)
    assert body["error"]["code"] == code


def new_run(c: TestClient) -> str:
    r = c.post("/sessions", headers=H, json={"schema_version": 1, "building_id": "demo_3f",
                                             "participant_code": "P01", "device_id": "pc-01"})
    assert r.status_code == 200, r.text
    sid = api.CreateSessionResponse.model_validate(r.json()).session_id
    r = c.post(f"/sessions/{sid}/runs", headers=H, json={"schema_version": 1, "scenario_id": "s_demo_fire",
                                                          "attempt": 1})
    assert r.status_code == 200, r.text
    return str(api.CreateRunResponse.model_validate(r.json()).run_id)


def golden_events(name: str, rid: str) -> list[dict]:
    lines = (GOLDEN / f"{name}.jsonl").read_text(encoding="utf-8").splitlines()
    return [{**json.loads(l), "run_id": rid} for l in lines if l.strip()]


def evaluation(name: str, rid: str) -> dict:
    """An EvaluationResult with the golden grades; evidence fills every template placeholder."""
    exp = json.loads((GOLDEN / f"{name}.expected.json").read_text(encoding="utf-8"))
    items = []
    for it in EVAL_CFG["items"]:
        g = exp["grades"][it["id"]]
        keys = set(re.findall(r"\{([a-z_]+)\}", json.dumps(it["text"], ensure_ascii=False)))
        items.append({"id": it["id"], "grade": g,
                      "points": 0 if g == "na" else it["weight"] * EVAL_CFG["grade_ratio"][g],
                      "max": it["weight"], "evidence": {k: 12.5 for k in keys}, "rule_ref": it["rule_ref"]})
    return {"schema_version": 1, "run_id": rid, "outcome": exp["outcome"], "total_pct": exp["total_pct"],
            "elapsed_s": 100.0, "items": items, "timeline": [{"t": 0, "c_head": 0, "zone": "r303"}],
            "weak_zones": []}


def npc_req(**kw) -> dict:
    base = {"schema_version": 1, "npc_id": "npc_minji", "persona": "겁 많은 후배", "current_action": "freeze",
            "zone": "r302", "player_action": "warn", "smoke_level": 1, "recent_events": ["경보가 울림"]}
    base.update(kw)
    return base


# ---- health, tokens, errors -------------------------------------------------

def test_health(client):
    r = client.get("/health", headers=H)
    assert r.status_code == 200
    body = api.HealthResponse.model_validate(r.json())
    assert body.ok and body.llm == "fake"


ALL_ENDPOINTS = [
    ("get", "/health"), ("get", "/buildings/demo_3f/zone-graph"), ("post", "/sessions"),
    ("post", "/sessions/x/runs"), ("post", "/runs/x/events"), ("post", "/runs/x/finish"),
    ("post", "/runs/x/report"), ("post", "/npc/react"), ("get", "/runs/x/export"),
    ("get", "/staff"), ("get", "/staff/devices"), ("post", "/staff/abort/pc-01"),
]


@pytest.mark.parametrize("method,path", ALL_ENDPOINTS)
@pytest.mark.parametrize("headers", [{}, {"X-Device-Token": "wrong"}], ids=["missing", "wrong"])
def test_token_rejected(client, method, path, headers):
    assert_error(getattr(client, method)(path, headers=headers), 401, "bad_token")


def test_any_listed_token_works(client):
    assert client.get("/health", headers={"X-Device-Token": "tok-staff"}).status_code == 200


def test_unknown_route_uses_error_format(client):
    assert_error(client.get("/nope", headers=H), 404, "not_found")


def test_schema_version_is_checked(client):
    r = client.post("/sessions", headers=H, json={"schema_version": 2, "building_id": "demo_3f",
                                                  "participant_code": "P", "device_id": "pc-01"})
    assert_error(r, 422, "invalid")


# ---- buildings, sessions, runs ----------------------------------------------

def test_zone_graph(client):
    r = client.get("/buildings/demo_3f/zone-graph", headers=H)
    assert r.status_code == 200
    assert r.json() == json.loads((CONFIG / "zone_graph.json").read_text(encoding="utf-8"))
    assert_error(client.get("/buildings/nope/zone-graph", headers=H), 404, "not_found")


def test_session_and_run(client):
    rid = new_run(client)
    assert len(rid) == 36
    r = client.post("/sessions", headers=H, json={"schema_version": 1, "building_id": "nope",
                                                  "participant_code": "P01", "device_id": "pc-01"})
    assert_error(r, 404, "not_found")
    r = client.post("/sessions", headers=H, json={"schema_version": 1, "building_id": "demo_3f",
                                                  "participant_code": "X" * 17, "device_id": "pc-01"})
    assert_error(r, 422, "invalid")
    r = client.post("/sessions/nope/runs", headers=H, json={"schema_version": 1, "scenario_id": "s",
                                                            "attempt": 1})
    assert_error(r, 404, "not_found")
    r = client.post("/sessions", headers=H, json={"schema_version": 1, "building_id": "demo_3f",
                                                  "participant_code": "P", "device_id": "pc-01", "name": "x"})
    assert_error(r, 422, "invalid")  # no extra fields (participants are codes only)


# ---- events -----------------------------------------------------------------

def post_events(c, rid, events):
    return c.post(f"/runs/{rid}/events", headers=H, json={"schema_version": 1, "events": events})


def test_events_and_duplicate_seq_ignored(client):
    rid = new_run(client)
    events = golden_events("model_run", rid)
    n = len(events)
    first, rest = events[: n // 2], events[n // 2:]

    r = post_events(client, rid, first)
    assert r.json() == {"schema_version": 1, "accepted": len(first), "last_seq": first[-1]["seq"]}
    # resend everything (offline queue replay): only the new half is stored
    r = post_events(client, rid, events)
    assert r.json() == {"schema_version": 1, "accepted": len(rest), "last_seq": events[-1]["seq"]}
    # exact repeat: nothing new
    r = post_events(client, rid, events[:10])
    assert r.json()["accepted"] == 0 and r.json()["last_seq"] == events[-1]["seq"]


def test_duplicate_seq_inside_one_batch(client):
    rid = new_run(client)
    e = golden_events("model_run", rid)[:3]
    r = post_events(client, rid, [e[0], e[1], {**e[1], "t": 99.0}, e[2]])
    assert r.json()["accepted"] == 3


def test_events_rejections(client):
    rid = new_run(client)
    e = golden_events("model_run", rid)
    assert_error(post_events(client, "nope", [{**e[0], "run_id": "nope"}]), 404, "not_found")
    assert_error(post_events(client, rid, [{**e[0], "run_id": "other"}]), 422, "invalid")
    assert_error(post_events(client, rid, [{**e[0], "seq": i + 1} for i in range(501)]), 422, "invalid")
    assert_error(post_events(client, rid, [{**e[0], "type": "DANCE"}]), 422, "invalid")
    assert post_events(client, rid, []).json() == {"schema_version": 1, "accepted": 0, "last_seq": 0}


def test_export_csv(client):
    rid = new_run(client)
    events = golden_events("fail_run", rid)
    for i in range(0, len(events), 500):
        post_events(client, rid, events[i:i + 500])
    r = client.get(f"/runs/{rid}/export", headers=H)
    assert r.status_code == 200 and r.headers["content-type"].startswith("text/csv")
    text = r.content.decode("utf-8")
    assert text.startswith("﻿")
    rows = list(csv.DictReader(io.StringIO(text[1:])))
    assert len(rows) == len(events)
    assert [int(x["seq"]) for x in rows] == sorted(e["seq"] for e in events)
    assert rows[0]["type"] == events[0]["type"]
    assert json.loads(rows[0]["data"]) == events[0]["data"]
    assert_error(client.get("/runs/nope/export", headers=H), 404, "not_found")


# ---- finish -----------------------------------------------------------------

def test_finish(client):
    rid = new_run(client)
    ev = evaluation("model_run", rid)
    r = client.post(f"/runs/{rid}/finish", headers=H, json={"schema_version": 1, "outcome": "escaped",
                                                             "evaluation": ev})
    assert r.json() == {"schema_version": 1, "ok": True}
    row = db_rows(client, EvaluationRow)[0]
    assert row.run_id == rid and row.total_pct == 100 and len(row.items) == len(EVAL_CFG["items"])
    # finishing again overwrites (client retry)
    assert client.post(f"/runs/{rid}/finish", headers=H,
                       json={"schema_version": 1, "outcome": "escaped", "evaluation": ev}).status_code == 200
    assert len(db_rows(client, EvaluationRow)) == 1

    assert_error(client.post(f"/runs/{rid}/finish", headers=H,
                             json={"schema_version": 1, "outcome": "failed", "evaluation": ev}), 422, "invalid")
    assert_error(client.post(f"/runs/{rid}/finish", headers=H,
                             json={"schema_version": 1, "outcome": "escaped",
                                   "evaluation": {**ev, "run_id": "other"}}), 422, "invalid")
    assert_error(client.post("/runs/nope/finish", headers=H,
                             json={"schema_version": 1, "outcome": "escaped",
                                   "evaluation": {**ev, "run_id": "nope"}}), 404, "not_found")


# ---- report -----------------------------------------------------------------

def post_report(c, rid, name="model_run"):
    return c.post(f"/runs/{rid}/report", headers=H,
                  json={"schema_version": 1, "evaluation": evaluation(name, rid)})


def graded(name: str) -> list[str]:
    exp = json.loads((GOLDEN / f"{name}.expected.json").read_text(encoding="utf-8"))
    return [it["id"] for it in EVAL_CFG["items"] if exp["grades"][it["id"]] != "na"]


@pytest.mark.parametrize("name", ["model_run", "fail_run", "na_run"])
def test_report_fake(client, name):
    rid = new_run(client)
    r = post_report(client, rid, name)
    assert r.status_code == 200, r.text
    body = api.ReportResponse.model_validate(r.json())
    assert body.fallback_used is False and body.model == "fake"
    assert [i.id for i in body.report.items] == graded(name)
    row = db_rows(client, ReportRow)[0]
    assert row.run_id == rid and row.model == "fake" and row.text["summary"] == body.report.summary
    calls = db_rows(client, LlmCall)
    assert len(calls) == 1 and calls[0].kind == "report" and calls[0].ok


def test_report_rejections(client):
    rid = new_run(client)
    assert_error(post_report(client, "nope"), 404, "not_found")
    r = client.post(f"/runs/{rid}/report", headers=H,
                    json={"schema_version": 1, "evaluation": evaluation("model_run", "other")})
    assert_error(r, 422, "invalid")


class SlowBackend(FakeBackend):
    async def npc(self, *a):
        await asyncio.sleep(5)

    async def report(self, *a):
        await asyncio.sleep(5)


class BrokenBackend(FakeBackend):
    async def npc(self, *a):
        raise LlmBackendError("refusal (cyber)")

    async def report(self, *a):
        raise RuntimeError("connection reset")


class WrongShapeBackend(FakeBackend):
    model = "wrong"

    async def npc(self, system, user, req):
        return LlmReply({"action": "dance", "line": "x", "emotion": "calm"}, self.model, 10, 5)

    async def report(self, system, user, ev, cfg):
        good = template_report(ev, cfg).model_dump()
        good["items"] = good["items"][:-1]  # one item missing -> id set differs
        return LlmReply(good, self.model, 10, 5)


class ExtraItemBackend(FakeBackend):
    async def report(self, system, user, ev, cfg):
        good = template_report(ev, cfg).model_dump()
        good["items"].append({"id": "extinguish", "good": "", "improve": "x", "guideline": "y"})
        return LlmReply(good, self.model, 10, 5)


@pytest.mark.parametrize("backend,err", [(SlowBackend(), "timeout"), (BrokenBackend(), "RuntimeError"),
                                         (WrongShapeBackend(), "schema"), (ExtraItemBackend(), "schema")])
def test_report_fallback(tmp_path, backend, err):
    with client_with(tmp_path, backend) as c:
        rid = new_run(c)
        r = post_report(c, rid, "fail_run")
        assert r.status_code == 200, r.text
        body = api.ReportResponse.model_validate(r.json())
        assert body.fallback_used is True and body.model == "template"
        ev = api.EvaluationResult.model_validate(evaluation("fail_run", rid))
        assert body.report == template_report(ev, EVAL_CFG)
        assert body.latency_ms < 2000
        call = db_rows(c, LlmCall)[0]
        assert call.kind == "report" and call.ok is False and err in call.error


def test_report_off_mode(tmp_path):
    with client_with(tmp_path, None) as c:
        assert c.get("/health", headers=H).json()["llm"] == "off"
        body = post_report(c, new_run(c)).json()
        assert body["fallback_used"] is True and body["model"] == "template"
        assert db_rows(c, LlmCall) == []


def test_claude_without_key_runs_as_off(tmp_path):
    with TestClient(create_app(make_settings(tmp_path, llm_mode="claude", anthropic_api_key=""))) as c:
        assert c.get("/health", headers=H).json()["llm"] == "off"
        assert c.post("/npc/react", headers=H, json=npc_req()).json()["source"] == "fallback"


# ---- template report (same rules as C# Evaluator.TemplateReport) ------------

def test_template_report_matches_csharp_summary():
    m = template_report(api.EvaluationResult.model_validate(evaluation("model_run", "r")), EVAL_CFG)
    assert m.summary == "결과 탈출 성공, 총점 100%. 가장 잘한 점: 대피 개시 지연. 가장 먼저 고칠 점: 없음."
    f = template_report(api.EvaluationResult.model_validate(evaluation("fail_run", "r")), EVAL_CFG)
    assert f.summary == "결과 실패, 총점 6%. 가장 잘한 점: 소지품 수집 지연. 가장 먼저 고칠 점: 대피 개시 지연."


@pytest.mark.parametrize("name", ["model_run", "fail_run", "na_run"])
def test_template_report_fills_placeholders(name):
    ev = api.EvaluationResult.model_validate(evaluation(name, "r"))
    rep = template_report(ev, EVAL_CFG)
    assert [i.id for i in rep.items] == graded(name)
    for t in rep.items:
        g = next(i.grade for i in ev.items if i.id == t.id)
        assert (g == "good") == bool(t.good) and (g != "good") == bool(t.improve) and t.guideline
        for s in (t.good, t.improve, t.guideline, rep.summary):
            assert not re.search(r"\{[a-z_]+\}", s)


def test_template_formatting():
    ev = api.EvaluationResult.model_validate(evaluation("model_run", "r"))
    for it in ev.items:
        it.evidence = {"low_ratio": 0.845, "towel": "wet", "delay_s": 7.25, "zones": [], "count": 2}
    cfg = {**EVAL_CFG, "items": [{**i, "text": {"good": "{low_ratio}|{towel}|{delay_s}|{zones}|{count}|{missing}",
                                                "improve": "", "guideline": "g"}} for i in EVAL_CFG["items"]]}
    good = template_report(ev, cfg).items[0].good
    assert good == "84%|젖은 수건|7.3|-|2|{missing}"


# ---- NPC --------------------------------------------------------------------

def test_npc_fake(client):
    r = client.post("/npc/react", headers=H, json=npc_req())
    body = api.NpcReactResponse.model_validate(r.json())
    assert body.source == "llm" and body.action == "evacuate"
    call = db_rows(client, LlmCall)[0]
    assert call.kind == "npc" and call.ok and call.model == "fake"


@pytest.mark.parametrize("backend,err", [(SlowBackend(), "timeout"), (BrokenBackend(), "refusal"),
                                         (WrongShapeBackend(), "schema")])
def test_npc_fallback(tmp_path, backend, err):
    with client_with(tmp_path, backend) as c:
        r = c.post("/npc/react", headers=H, json=npc_req(smoke_level=1))
        body = api.NpcReactResponse.model_validate(r.json())
        assert body.source == "fallback"
        assert body.line == NPC_CACHE["fallback"]["npc_minji"][1]
        assert body.action == "freeze"  # keeps the current action
        call = db_rows(c, LlmCall)[0]
        assert call.kind == "npc" and call.ok is False and err in call.error and call.latency_ms < 2000


def test_npc_fallback_unknown_npc(tmp_path):
    with client_with(tmp_path, SlowBackend()) as c:
        body = c.post("/npc/react", headers=H, json=npc_req(npc_id="npc_x")).json()
        assert body["source"] == "fallback" and body["line"]


def test_npc_request_validation(client):
    assert_error(client.post("/npc/react", headers=H, json=npc_req(smoke_level=4)), 422, "invalid")
    assert_error(client.post("/npc/react", headers=H, json=npc_req(player_action="hug")), 422, "invalid")


# ---- Claude backend response handling (no network) --------------------------

def fake_message(stop_reason="end_turn", text='{"action":"evacuate","line":"가요","emotion":"calm"}'):
    content = [] if text is None else [SimpleNamespace(type="text", text=text)]
    return SimpleNamespace(stop_reason=stop_reason, content=content, model="claude-haiku-5-5",
                           stop_details=SimpleNamespace(category="cyber"),
                           usage=SimpleNamespace(input_tokens=100, output_tokens=20, cache_read_input_tokens=50,
                                                 cache_creation_input_tokens=0))


def test_claude_parse():
    reply = ClaudeBackend._parse(fake_message())
    assert reply.data["action"] == "evacuate" and reply.input_tokens == 150 and reply.output_tokens == 20
    for msg in (fake_message("refusal"), fake_message("max_tokens"), fake_message(text=None),
                fake_message(text="not json")):
        with pytest.raises(LlmBackendError):
            ClaudeBackend._parse(msg)


def test_claude_backend_request_shape(tmp_path, monkeypatch):
    """The relay sends structured-output requests with the configured models (client mocked)."""
    s = make_settings(tmp_path, llm_mode="claude", anthropic_api_key="sk-test")
    backend = ClaudeBackend(s)
    seen = {}

    async def npc_create(**kw):
        seen["npc"] = kw
        return fake_message()

    async def report_create(**kw):
        seen["report"] = kw
        return fake_message(text=json.dumps({"summary": "s", "items": []}))

    monkeypatch.setattr(backend.client.messages, "create", npc_create)
    monkeypatch.setattr(backend.client.beta.messages, "create", report_create)
    with client_with(tmp_path, backend, llm_mode="claude", anthropic_api_key="sk-test") as c:
        assert c.post("/npc/react", headers=H, json=npc_req()).json()["source"] == "llm"
        body = post_report(c, new_run(c)).json()
        assert body["fallback_used"] is True  # empty item list != graded ids
    assert seen["npc"]["model"] == "claude-haiku-5-5"
    assert seen["npc"]["output_config"]["effort"] == "low"
    assert seen["npc"]["output_config"]["format"]["type"] == "json_schema"
    assert seen["npc"]["system"][0]["cache_control"] == {"type": "ephemeral"}
    assert seen["report"]["model"] == "claude-sonnet-5-5"
    assert seen["report"]["output_config"]["effort"] == "medium"
    assert seen["report"]["fallbacks"] == "default"
    user = json.loads(seen["report"]["messages"][0]["content"])
    assert user["required_item_ids"] == graded("model_run")


# ---- staff + WebSocket ------------------------------------------------------

def status_msg(**kw) -> dict:
    base = {"type": "status", "run_id": "r1", "state": "Running", "t": 12.0, "zone": "corr_mid",
            "fed": 0.42, "stage": 1}
    base.update(kw)
    return base


def test_staff_page(client):
    r = client.get(f"/staff?token={TOKEN}")
    assert r.status_code == 200 and r.headers["content-type"].startswith("text/html")
    assert "비상 중단" in r.text and "/staff/abort/" in r.text


def test_ws_rejects_bad_token(client):
    for url in ("/ws/device/pc-01", "/ws/device/pc-01?token=wrong", "/ws/device/pc-01?token=tok-staff"):
        with pytest.raises(WebSocketDisconnect) as ei:
            with client.websocket_connect(url) as ws:
                ws.receive_json()
        assert ei.value.code == 1008


def receive_until(ws, kind: str, limit: int = 200) -> dict:
    for _ in range(limit):
        msg = ws.receive_json()
        if msg["type"] == kind:
            return msg
    raise AssertionError(f"no {kind} message")


def test_ws_status_ping_and_abort(client):
    assert_error(client.post("/staff/abort/pc-01", headers=H), 404, "not_found")  # not connected yet
    with client.websocket_connect(f"/ws/device/pc-01?token={TOKEN}") as ws:
        api.WsPing.model_validate(receive_until(ws, "ping"))
        ws.send_json(status_msg())
        ws.send_text("not json")  # ignored, connection stays up
        ws.send_json({"type": "status", "fed": "high"})  # invalid, ignored
        receive_until(ws, "ping")  # round trip so the status above has been processed

        devices = client.get("/staff/devices", headers=H).json()["devices"]
        pc = next(d for d in devices if d["device_id"] == "pc-01")
        assert pc["connected"] and pc["status"]["fed"] == 0.42 and pc["status"]["zone"] == "corr_mid"
        assert next(d for d in devices if d["device_id"] == "staff")["connected"] is False

        r = client.post("/staff/abort/pc-01", headers={"X-Device-Token": "tok-staff"})
        assert r.json() == {"schema_version": 1, "ok": True}
        assert receive_until(ws, "abort") == {"type": "abort", "by": "staff"}

    devices = client.get("/staff/devices", headers=H).json()["devices"]
    pc = next(d for d in devices if d["device_id"] == "pc-01")
    assert pc["connected"] is False and pc["status"]["fed"] == 0.42  # last status kept
    assert_error(client.post("/staff/abort/pc-01", headers=H), 404, "not_found")


def test_ws_ping_interval_from_settings(tmp_path):
    with TestClient(create_app(make_settings(tmp_path))) as c:
        assert c.app.state.settings.ws_ping_interval_s == 0.05
    assert Settings(_env_file=None).ws_ping_interval_s == 2.0


# ---- settings ---------------------------------------------------------------

def test_settings_defaults_and_parsing():
    s = Settings(_env_file=None, device_tokens=" pc-01:a , phone:b ,")
    assert s.tokens == {"a": "pc-01", "b": "phone"}
    assert (s.npc_timeout_s, s.report_timeout_s) == (3.0, 10.0)
    assert (s.npc_model, s.report_model) == ("claude-haiku-5-5", "claude-sonnet-5-5")
    assert s.resolved_database_url == "sqlite:///" + (SERVER_DIR / "firedrill.db").resolve().as_posix()
    with pytest.raises(ValueError):
        _ = Settings(_env_file=None, device_tokens="no-colon").tokens
    assert Settings(_env_file=None, llm_mode="claude", anthropic_api_key="k").effective_llm_mode == "claude"
    assert Settings(_env_file=None, llm_mode="claude").effective_llm_mode == "off"
