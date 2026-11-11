"""Fallback text when the LLM is off, slow, wrong or refuses (명세서 8장).

`template_report` follows the same rules as C# `Evaluator.TemplateReport`
(contracts/README.md "1단계 구현 메모 — 문장 틀"), so the client and the server
show the same sentences when the LLM is not used.
"""
from __future__ import annotations

import json
import re
from decimal import ROUND_HALF_UP, Decimal
from typing import Any

from .schemas import EvaluationResult, NpcReactRequest, NpcReaction, ReportItemText, ReportText

OUTCOME_KO = {"escaped": "탈출 성공", "rescued": "구조됨", "failed": "실패", "timeout": "시간 초과", "aborted": "중단"}
TOWEL_KO = {"wet": "젖은 수건", "dry": "마른 수건", "none": "없음"}
RANK = {"good": 2, "normal": 1, "poor": 0}
PLACEHOLDER = re.compile(r"\{([a-z_]+)\}")


def _format(key: str, v: Any) -> str:
    if v is None:
        return "-"
    if isinstance(v, list):
        return ", ".join(x if isinstance(x, str) else _format("", x) for x in v) if v else "-"
    if isinstance(v, bool):
        return "True" if v else "False"
    if isinstance(v, (int, float)):
        if key.endswith("_ratio"):
            return str(int(round(float(v) * 100))) + "%"  # banker's rounding, same as Math.Round
        s = str(Decimal(float(v)).quantize(Decimal("0.1"), rounding=ROUND_HALF_UP))
        return s[:-2] if s.endswith(".0") else s
    if isinstance(v, str):
        return TOWEL_KO.get(v, v) if key == "towel" else v
    return json.dumps(v, ensure_ascii=False)


def _fill(template: str | None, evidence: dict[str, Any]) -> str:
    if not template:
        return ""
    return PLACEHOLDER.sub(lambda m: _format(m.group(1), evidence[m.group(1)]) if m.group(1) in evidence else m.group(0),
                           template)


def graded_ids(result: EvaluationResult, cfg: dict) -> list[str]:
    """Item ids a report must cover: every item that is not na and exists in evaluation.json."""
    known = {it["id"] for it in cfg["items"]}
    return [r.id for r in result.items if r.grade != "na" and r.id in known]


def template_report(result: EvaluationResult, cfg: dict) -> ReportText:
    items_cfg = {it["id"]: it for it in cfg["items"]}
    texts: list[ReportItemText] = []
    graded: list[tuple[Any, dict, int]] = []
    for order, r in enumerate(result.items):
        it = items_cfg.get(r.id)
        if r.grade == "na" or it is None:
            continue
        graded.append((r, it, order))
        text = it.get("text") or {}
        texts.append(ReportItemText(
            id=r.id,
            good=_fill(text.get("good"), r.evidence) if r.grade == "good" else "",
            improve=_fill(text.get("improve"), r.evidence) if r.grade != "good" else "",
            guideline=text.get("guideline") or "",
        ))

    best_c = sorted(graded, key=lambda x: (-RANK[x[0].grade], -x[1]["weight"], x[2]))
    worst_c = sorted(graded, key=lambda x: (RANK[x[0].grade], -x[1]["weight"], x[2]))
    best = next((x for x in best_c if x[0].grade != "poor"), None)
    worst = next((x for x in worst_c if x[0].grade != "good"), None)

    values = {
        "outcome_ko": OUTCOME_KO[result.outcome],
        "outcome": OUTCOME_KO[result.outcome],
        "total_pct": str(result.total_pct),
        "best": best[1]["name"] if best else "없음",
        "worst": worst[1]["name"] if worst else "없음",
    }
    summary = PLACEHOLDER.sub(lambda m: values.get(m.group(1), m.group(0)), cfg.get("summary_template") or "")
    return ReportText(summary=summary, items=texts)


def npc_fallback(req: NpcReactRequest, cache: dict) -> NpcReaction:
    """Spare line from npc_cache.json `fallback`; the NPC keeps doing what it was doing."""
    lines = cache.get("fallback", {}).get(req.npc_id) or ["…"]
    line = lines[req.smoke_level % len(lines)]
    return NpcReaction(action=req.current_action, line=line[:60], emotion="scared")
