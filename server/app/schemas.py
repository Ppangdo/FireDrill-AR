"""Server contract (명세서 14.6). Field names match the C# DTOs in FireDrill.Core.Contracts.

Every request/response body carries schema_version = 1.
"""
from __future__ import annotations

from typing import Any, Literal
from uuid import UUID

from pydantic import BaseModel, ConfigDict, Field

SchemaVersion = Literal[1]
Outcome = Literal["escaped", "rescued", "failed", "timeout", "aborted"]
Grade = Literal["good", "normal", "poor", "na"]
NpcAction = Literal["evacuate", "follow_player", "freeze", "ask_help", "go_elevator", "hide"]
PlayerAction = Literal["warn", "escort", "ignore", "none"]
Emotion = Literal["calm", "scared", "panicked", "relieved", "stubborn"]
EventType = Literal[
    "RUN_START", "RUN_END", "STATE_CHANGE", "CUE_EXPOSED", "ZONE_ENTER", "POSTURE", "SAMPLE",
    "HANDLE_CHECK", "DOOR_OPEN", "DOOR_CLOSE", "ALARM_PRESSED", "SHOUT", "PICKUP", "USE",
    "TOWEL_WET", "TOWEL_EQUIP", "EXTINGUISH", "ELEVATOR_CALL", "WINDOW_SIGNAL",
    "NPC_INTERACT", "CROWD_FOLLOW", "BODY_STAGE", "ABORT",
]


class Strict(BaseModel):
    model_config = ConfigDict(extra="forbid")


# ---- shared payloads -------------------------------------------------------

class LogEvent(Strict):
    run_id: str
    seq: int = Field(ge=1)
    t: float = Field(ge=0)
    type: EventType
    zone: str | None
    pos: tuple[float, float, float]
    head_ratio: float = Field(ge=0)
    c_head: float = Field(ge=0, le=1)
    fed: float = Field(ge=0)
    zone_d: float = Field(ge=0, le=1)
    layer_y: float
    data: dict[str, Any] = Field(default_factory=dict)


class ItemResult(Strict):
    id: str
    grade: Grade
    points: float = Field(ge=0)
    max: float = Field(ge=0)
    evidence: dict[str, Any] = Field(default_factory=dict)
    rule_ref: str


class TimelinePoint(Strict):
    t: float
    c_head: float
    zone: str | None


class EvaluationResult(Strict):
    schema_version: SchemaVersion = 1
    run_id: str
    outcome: Outcome
    total_pct: int = Field(ge=0, le=100)
    elapsed_s: float = Field(ge=0)
    items: list[ItemResult]
    timeline: list[TimelinePoint]
    weak_zones: list[str] = Field(max_length=2)


class ReportItemText(Strict):
    id: str
    good: str
    improve: str
    guideline: str


class ReportText(Strict):
    summary: str
    items: list[ReportItemText]


class NpcReactRequest(Strict):
    schema_version: SchemaVersion = 1
    npc_id: str
    persona: str
    current_action: NpcAction
    zone: str | None
    player_action: PlayerAction
    smoke_level: int = Field(ge=0, le=3)
    recent_events: list[str] = Field(default_factory=list, max_length=10)


class NpcReaction(Strict):
    action: NpcAction
    line: str = Field(max_length=60)
    emotion: Emotion


# ---- REST bodies -----------------------------------------------------------

class HealthResponse(Strict):
    schema_version: SchemaVersion = 1
    ok: bool
    llm: Literal["claude", "fake", "off"]
    server_time: str


class CreateSessionRequest(Strict):
    schema_version: SchemaVersion = 1
    building_id: str
    participant_code: str = Field(max_length=16)
    device_id: str


class CreateSessionResponse(Strict):
    schema_version: SchemaVersion = 1
    session_id: UUID


class CreateRunRequest(Strict):
    schema_version: SchemaVersion = 1
    scenario_id: str
    attempt: int = Field(ge=1)


class CreateRunResponse(Strict):
    schema_version: SchemaVersion = 1
    run_id: UUID


class PostEventsRequest(Strict):
    schema_version: SchemaVersion = 1
    events: list[LogEvent] = Field(max_length=500)


class PostEventsResponse(Strict):
    schema_version: SchemaVersion = 1
    accepted: int
    last_seq: int


class FinishRunRequest(Strict):
    schema_version: SchemaVersion = 1
    outcome: Outcome
    evaluation: EvaluationResult


class OkResponse(Strict):
    schema_version: SchemaVersion = 1
    ok: bool = True


class ReportRequest(Strict):
    schema_version: SchemaVersion = 1
    evaluation: EvaluationResult


class ReportResponse(Strict):
    schema_version: SchemaVersion = 1
    report: ReportText
    model: str
    fallback_used: bool
    latency_ms: int


class NpcReactResponse(NpcReaction):
    schema_version: SchemaVersion = 1
    source: Literal["llm", "fallback"]


class ErrorBody(Strict):
    code: Literal["bad_token", "not_found", "invalid", "llm_timeout", "llm_error"]
    message: str


class ErrorResponse(Strict):
    error: ErrorBody


# ---- WebSocket /ws/device/{device_id}?token=... ----------------------------

class WsStatus(Strict):
    type: Literal["status"] = "status"
    run_id: str | None
    state: str
    t: float
    zone: str | None
    fed: float
    stage: int = Field(ge=0, le=4)


class WsAbort(Strict):
    type: Literal["abort"] = "abort"
    by: Literal["staff", "system"]


class WsPing(Strict):
    type: Literal["ping"] = "ping"
