"""LLM relay (명세서 8장). The LLM never grades: it only writes NPC lines and report sentences.

Every call is time-limited and validated. On timeout, API error, refusal or schema mismatch the
relay answers with a fallback (NPC: npc_cache.json `fallback`, report: evaluation.json template)
and records the attempt in `llm_calls`.
"""
from __future__ import annotations

import asyncio
import json
import logging
import time
from dataclasses import dataclass
from typing import Any, Protocol

from pydantic import ValidationError
from sqlmodel import Session

from .db import LlmCall
from .schemas import (EvaluationResult, NpcReactRequest, NpcReactResponse, NpcReaction, ReportResponse,
                      ReportText)
from .settings import Settings
from .templates import graded_ids, npc_fallback, template_report

log = logging.getLogger("firedrill.llm")

NPC_SCHEMA: dict[str, Any] = {
    "type": "object",
    "properties": {
        "action": {"type": "string",
                   "enum": ["evacuate", "follow_player", "freeze", "ask_help", "go_elevator", "hide"]},
        "line": {"type": "string"},
        "emotion": {"type": "string", "enum": ["calm", "scared", "panicked", "relieved", "stubborn"]},
    },
    "required": ["action", "line", "emotion"],
    "additionalProperties": False,
}

REPORT_SCHEMA: dict[str, Any] = {
    "type": "object",
    "properties": {
        "summary": {"type": "string"},
        "items": {
            "type": "array",
            "items": {
                "type": "object",
                "properties": {
                    "id": {"type": "string"},
                    "good": {"type": "string"},
                    "improve": {"type": "string"},
                    "guideline": {"type": "string"},
                },
                "required": ["id", "good", "improve", "guideline"],
                "additionalProperties": False,
            },
        },
    },
    "required": ["summary", "items"],
    "additionalProperties": False,
}


@dataclass
class LlmReply:
    data: Any
    model: str
    input_tokens: int | None = None
    output_tokens: int | None = None


class LlmBackendError(Exception):
    """The model answered but not usefully (refusal, truncated, not JSON)."""


class LlmBackend(Protocol):
    mode: str

    async def npc(self, system: str, user: str, req: NpcReactRequest) -> LlmReply: ...

    async def report(self, system: str, user: str, ev: EvaluationResult, cfg: dict) -> LlmReply: ...


# ---- backends --------------------------------------------------------------

class ClaudeBackend:
    mode = "claude"

    def __init__(self, settings: Settings):
        import anthropic

        self._anthropic = anthropic
        self.s = settings
        # The relay enforces the overall deadline; no SDK retries so a slow call cannot run past it.
        self.client = anthropic.AsyncAnthropic(api_key=settings.anthropic_api_key, max_retries=0,
                                               timeout=max(settings.npc_timeout_s, settings.report_timeout_s))

    @staticmethod
    def _system(text: str) -> list[dict]:
        # Fixed system prompt + cache breakpoint; everything that changes goes in the user message.
        return [{"type": "text", "text": text, "cache_control": {"type": "ephemeral"}}]

    @staticmethod
    def _parse(resp) -> LlmReply:
        if resp.stop_reason == "refusal":
            cat = getattr(getattr(resp, "stop_details", None), "category", None)
            raise LlmBackendError(f"refusal ({cat})")
        if resp.stop_reason == "max_tokens":
            raise LlmBackendError("max_tokens")
        text = next((b.text for b in resp.content if b.type == "text"), None)
        if text is None:
            raise LlmBackendError("no text block")
        try:
            data = json.loads(text)
        except json.JSONDecodeError as e:
            raise LlmBackendError(f"not JSON: {e}") from e
        u = resp.usage
        inp = (u.input_tokens or 0) + (getattr(u, "cache_read_input_tokens", 0) or 0) \
            + (getattr(u, "cache_creation_input_tokens", 0) or 0)
        return LlmReply(data, resp.model, inp, u.output_tokens)

    async def npc(self, system: str, user: str, req: NpcReactRequest) -> LlmReply:
        resp = await self.client.messages.create(
            model=self.s.npc_model,
            max_tokens=self.s.npc_max_tokens,
            system=self._system(system),
            messages=[{"role": "user", "content": user}],
            output_config={"effort": self.s.npc_effort, "format": {"type": "json_schema", "schema": NPC_SCHEMA}},
        )
        return self._parse(resp)

    async def report(self, system: str, user: str, ev: EvaluationResult, cfg: dict) -> LlmReply:
        # 명세서 8장: on refusal the server-side fallback model ("default") answers instead.
        resp = await self.client.beta.messages.create(
            model=self.s.report_model,
            max_tokens=self.s.report_max_tokens,
            betas=["server-side-fallback-2026-07-01"],
            fallbacks="default",
            system=self._system(system),
            messages=[{"role": "user", "content": user}],
            output_config={"effort": self.s.report_effort,
                           "format": {"type": "json_schema", "schema": REPORT_SCHEMA}},
        )
        return self._parse(resp)


class FakeBackend:
    """Fixed answers for tests and development without an API key (LLM_MODE=fake)."""
    mode = "fake"
    model = "fake"

    async def npc(self, system: str, user: str, req: NpcReactRequest) -> LlmReply:
        helped = req.player_action in ("warn", "escort")
        return LlmReply({
            "action": ("follow_player" if req.player_action == "escort" else "evacuate") if helped
            else req.current_action,
            "line": "네, 같이 나가요!" if helped else "어떡하지…",
            "emotion": "relieved" if helped else "scared",
        }, self.model, 0, 0)

    async def report(self, system: str, user: str, ev: EvaluationResult, cfg: dict) -> LlmReply:
        return LlmReply(template_report(ev, cfg).model_dump(), self.model, 0, 0)


# ---- relay -----------------------------------------------------------------

def _ms(start: float) -> int:
    return int(round((time.perf_counter() - start) * 1000))


class LlmRelay:
    def __init__(self, settings: Settings, engine, backend: LlmBackend | None,
                 eval_cfg: dict, npc_cache: dict):
        self.s = settings
        self.engine = engine
        self.backend = backend
        self.eval_cfg = eval_cfg
        self.npc_cache = npc_cache
        self.npc_system = (settings.prompts_dir / "npc_system.md").read_text(encoding="utf-8")
        self.report_system = (settings.prompts_dir / "report_system.md").read_text(encoding="utf-8")

    @classmethod
    def for_mode(cls, settings: Settings, engine, eval_cfg: dict, npc_cache: dict) -> "LlmRelay":
        mode = settings.effective_llm_mode
        backend: LlmBackend | None = (ClaudeBackend(settings) if mode == "claude"
                                      else FakeBackend() if mode == "fake" else None)
        return cls(settings, engine, backend, eval_cfg, npc_cache)

    @property
    def mode(self) -> str:
        return self.backend.mode if self.backend else "off"

    def _record(self, kind: str, model: str, latency_ms: int, reply: LlmReply | None, error: str | None) -> None:
        with Session(self.engine) as s:
            s.add(LlmCall(kind=kind, model=model, latency_ms=latency_ms,
                          input_tokens=reply.input_tokens if reply else None,
                          output_tokens=reply.output_tokens if reply else None,
                          ok=error is None, error=error))
            s.commit()

    @staticmethod
    async def _call(coro, timeout: float) -> tuple[LlmReply | None, str | None]:
        try:
            return await asyncio.wait_for(coro, timeout), None
        except (asyncio.TimeoutError, TimeoutError):
            return None, f"timeout after {timeout}s"
        except LlmBackendError as e:
            return None, str(e)
        except Exception as e:  # API/network errors: never let them reach the client
            return None, f"{type(e).__name__}: {e}"[:500]

    # -- NPC -----------------------------------------------------------------

    def npc_user_message(self, req: NpcReactRequest) -> str:
        return json.dumps(req.model_dump(exclude={"schema_version"}), ensure_ascii=False)

    async def npc_react(self, req: NpcReactRequest) -> NpcReactResponse:
        if self.backend is None:
            return NpcReactResponse(**npc_fallback(req, self.npc_cache).model_dump(), source="fallback")
        model = self.s.npc_model if self.backend.mode == "claude" else self.backend.mode
        start = time.perf_counter()
        reply, err = await self._call(self.backend.npc(self.npc_system, self.npc_user_message(req), req),
                                      self.s.npc_timeout_s)
        reaction: NpcReaction | None = None
        if reply is not None:
            try:
                reaction = NpcReaction.model_validate(reply.data)
            except ValidationError as e:
                err = f"schema: {e.error_count()} errors"
        self._record("npc", reply.model if reply else model, _ms(start), reply, err)
        if reaction is None:
            log.warning("npc fallback for %s: %s", req.npc_id, err)
            return NpcReactResponse(**npc_fallback(req, self.npc_cache).model_dump(), source="fallback")
        return NpcReactResponse(**reaction.model_dump(), source="llm")

    # -- report --------------------------------------------------------------

    def report_user_message(self, ev: EvaluationResult) -> str:
        items_cfg = {it["id"]: it for it in self.eval_cfg["items"]}
        rules = self.eval_cfg.get("rules", {})
        items = []
        for r in ev.items:
            it = items_cfg.get(r.id, {})
            rule = rules.get(r.rule_ref, {})
            items.append({
                "id": r.id, "name": it.get("name", r.id), "grade": r.grade,
                "points": r.points, "max": r.max, "evidence": r.evidence,
                "template": it.get("text", {}),
                "rule": {"ref": r.rule_ref, "source": rule.get("source"), "quote": rule.get("quote")},
            })
        return json.dumps({
            "outcome": ev.outcome, "total_pct": ev.total_pct, "elapsed_s": ev.elapsed_s,
            "weak_zones": ev.weak_zones, "items": items,
            "required_item_ids": graded_ids(ev, self.eval_cfg),
        }, ensure_ascii=False)

    def check_report(self, data: Any, ev: EvaluationResult) -> ReportText:
        """Validate shape and id set; ids must be exactly the graded (non-na) items, each once."""
        text = ReportText.model_validate(data)
        want = graded_ids(ev, self.eval_cfg)
        got = [i.id for i in text.items]
        if sorted(got) != sorted(want):
            raise ValueError(f"item ids {sorted(got)} != {sorted(want)}")
        order = {iid: n for n, iid in enumerate(want)}
        return ReportText(summary=text.summary, items=sorted(text.items, key=lambda i: order[i.id]))

    async def report(self, ev: EvaluationResult) -> ReportResponse:
        start = time.perf_counter()
        if self.backend is None:
            return ReportResponse(report=template_report(ev, self.eval_cfg), model="template",
                                  fallback_used=True, latency_ms=_ms(start))
        model = self.s.report_model if self.backend.mode == "claude" else self.backend.mode
        reply, err = await self._call(self.backend.report(self.report_system, self.report_user_message(ev),
                                                          ev, self.eval_cfg),
                                      self.s.report_timeout_s)
        text: ReportText | None = None
        if reply is not None:
            try:
                text = self.check_report(reply.data, ev)
            except (ValidationError, ValueError) as e:
                err = f"schema: {e}"[:500]
        self._record("report", reply.model if reply else model, _ms(start), reply, err)
        if text is None:
            log.warning("report fallback for %s: %s", ev.run_id, err)
            return ReportResponse(report=template_report(ev, self.eval_cfg), model="template",
                                  fallback_used=True, latency_ms=_ms(start))
        return ReportResponse(report=text, model=reply.model, fallback_used=False, latency_ms=_ms(start))
