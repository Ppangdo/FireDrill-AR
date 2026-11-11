"""DB tables (명세서 9장). SQLModel + SQLite."""
from __future__ import annotations

import json
from datetime import datetime, timezone
from typing import Any

from sqlalchemy import JSON, Column
from sqlalchemy.pool import StaticPool
from sqlmodel import Field, Session, SQLModel, create_engine

from .settings import Settings


def utcnow() -> datetime:
    return datetime.now(timezone.utc)


def json_col() -> Any:
    return Field(default=None, sa_column=Column(JSON))


class Building(SQLModel, table=True):
    __tablename__ = "buildings"
    id: str = Field(primary_key=True)
    name: str
    zone_graph: Any = json_col()
    version: int


class TrainingSession(SQLModel, table=True):
    __tablename__ = "sessions"
    id: str = Field(primary_key=True)
    building_id: str = Field(foreign_key="buildings.id")
    participant_code: str
    device: str
    started_at: datetime = Field(default_factory=utcnow)


class Run(SQLModel, table=True):
    __tablename__ = "runs"
    id: str = Field(primary_key=True)
    session_id: str = Field(foreign_key="sessions.id", index=True)
    scenario_id: str
    attempt: int
    outcome: str | None = None
    started_at: datetime = Field(default_factory=utcnow)
    ended_at: datetime | None = None


class EventRow(SQLModel, table=True):
    __tablename__ = "events"
    run_id: str = Field(primary_key=True, foreign_key="runs.id")
    seq: int = Field(primary_key=True)
    t: float
    type: str
    zone: str | None = None
    pos: Any = json_col()
    head_ratio: float
    c_head: float
    fed: float
    zone_d: float
    layer_y: float
    data: Any = json_col()


class EvaluationRow(SQLModel, table=True):
    __tablename__ = "evaluations"
    run_id: str = Field(primary_key=True, foreign_key="runs.id")
    outcome: str
    total_pct: int
    elapsed_s: float
    items: Any = json_col()
    timeline: Any = json_col()
    weak_zones: Any = json_col()


class ReportRow(SQLModel, table=True):
    __tablename__ = "reports"
    run_id: str = Field(primary_key=True, foreign_key="runs.id")
    model: str
    text: Any = json_col()
    latency_ms: int
    fallback_used: bool
    created_at: datetime = Field(default_factory=utcnow)


class LlmCall(SQLModel, table=True):
    __tablename__ = "llm_calls"
    id: int | None = Field(default=None, primary_key=True)
    kind: str  # npc | report
    model: str
    latency_ms: int
    input_tokens: int | None = None
    output_tokens: int | None = None
    ok: bool
    error: str | None = None
    created_at: datetime = Field(default_factory=utcnow)


def make_engine(settings: Settings):
    url = settings.resolved_database_url
    if url.startswith("sqlite"):
        kwargs: dict[str, Any] = {"connect_args": {"check_same_thread": False}}
        if url in ("sqlite://", "sqlite:///:memory:"):
            kwargs["poolclass"] = StaticPool
        return create_engine(url, **kwargs)
    return create_engine(url)


def init_db(engine, settings: Settings) -> None:
    SQLModel.metadata.create_all(engine)
    seed_buildings(engine, settings)


def seed_buildings(engine, settings: Settings) -> None:
    """Load Config/zone_graph.json into buildings (insert or update to the newest version)."""
    path = settings.config_dir / "zone_graph.json"
    if not path.exists():
        return
    zg = json.loads(path.read_text(encoding="utf-8"))
    with Session(engine) as s:
        row = s.get(Building, zg["building_id"])
        if row is None:
            row = Building(id=zg["building_id"], name=zg.get("name", zg["building_id"]),
                           zone_graph=zg, version=zg["version"])
        else:
            row.zone_graph, row.version = zg, zg["version"]
        s.add(row)
        s.commit()
