"""Reference evaluator in Python (stdlib only).

Purpose: prove that the golden expectations in contracts/golden/*.expected.json follow
from the rules written in evaluation.json `measure` strings. The product evaluator is
the C# one in FireDrill.Core (명세서 7장, 14.2); both must give the same grades.
"""
from __future__ import annotations

import heapq
import json
import math
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
CONFIG = ROOT / "unity" / "Assets" / "FireDrill" / "Config"


def load_json(p: Path):
    return json.loads(p.read_text(encoding="utf-8"))


def load_log(p: Path) -> list[dict]:
    return [json.loads(line) for line in p.read_text(encoding="utf-8").splitlines() if line.strip()]


class ZoneGraph:
    def __init__(self, data: dict, blocked: set[str] | None = None):
        self.zones = {z["id"]: z for z in data["zones"]}
        blocked = blocked or set()
        self.edges = [e for e in data["edges"] if e["id"] not in blocked]
        self.exit_zones = {zid for zid, z in self.zones.items() if z["type"] == "exit"}
        self._dist = self._exit_dist_per_edge()

    def edges_of(self, zid: str):
        return [e for e in self.edges if zid in (e["from"], e["to"])]

    def _exit_dist_per_edge(self) -> dict[str, float]:
        dist = {e["id"]: math.inf for e in self.edges}
        heap = []
        for e in self.edges:
            if e["from"] in self.exit_zones or e["to"] in self.exit_zones:
                dist[e["id"]] = 0.0
                heapq.heappush(heap, (0.0, e["id"]))
        by_id = {e["id"]: e for e in self.edges}
        while heap:
            d, eid = heapq.heappop(heap)
            if d > dist[eid]:
                continue
            e = by_id[eid]
            for zid in (e["from"], e["to"]):
                if zid in self.exit_zones:
                    continue
                for n in self.edges_of(zid):
                    if n["id"] == eid:
                        continue
                    nd = d + math.dist(e["center"], n["center"])
                    if nd < dist[n["id"]]:
                        dist[n["id"]] = nd
                        heapq.heappush(heap, (nd, n["id"]))
        return dist

    def exit_distance(self, zone: str | None, pos: list[float]) -> float | None:
        if zone is None:
            return None
        if zone in self.exit_zones:
            return 0.0
        p = (pos[0], pos[2])
        best = math.inf
        for e in self.edges_of(zone):
            best = min(best, math.dist(p, e["center"]) + self._dist[e["id"]])
        return best


def round_half_up(x: float) -> int:
    return int(math.floor(x + 0.5))


def evaluate(events: list[dict], graph: ZoneGraph, scenario: dict, cfg: dict) -> dict:
    items_cfg = {i["id"]: i for i in cfg["items"]}
    cues = [e for e in events if e["type"] == "CUE_EXPOSED"]
    t0 = cues[0]["t"] if cues else None
    after = [e for e in events if t0 is not None and e["t"] >= t0]
    samples = [e for e in after if e["type"] == "SAMPLE"]
    grades: dict[str, str] = {}

    def by_time(value, th, good_key, normal_key):
        if value is None:
            return "poor"
        if value <= th[good_key]:
            return "good"
        if value <= th[normal_key]:
            return "normal"
        return "poor"

    # alert
    th = items_cfg["alert"]["thresholds"]
    if t0 is None:
        grades["alert"] = "na"
    else:
        hits = [e["t"] for e in after if e["type"] in ("ALARM_PRESSED", "SHOUT")]
        grades["alert"] = by_time(hits[0] - t0 if hits else None, th, "good_max_s", "normal_max_s")

    # evac_delay
    th = items_cfg["evac_delay"]["thresholds"]
    if t0 is None:
        grades["evac_delay"] = "na"
    else:
        pts = []
        for e in after:
            if e["type"] == "SAMPLE":
                pts.append((e["t"], graph.exit_distance(e["zone"], e["pos"]), False))
            elif e["type"] == "ZONE_ENTER" and e["data"]["to"] in graph.exit_zones:
                pts.append((e["t"], 0.0, True))
        start = None
        for i in range(len(pts) - 1):
            t_s, e_s, _ = pts[i]
            if e_s is None or pts[i + 1][1] is None or not (pts[i + 1][1] < e_s - th["first_step_drop_m"]):
                continue
            ok, prev, reached, last = True, e_s, False, e_s
            for t_j, e_j, is_exit in pts[i + 1:]:
                if t_j > t_s + th["window_s"] + 1e-6:
                    break
                if e_j is None or e_j - prev > th["max_rise_m"]:
                    ok = False
                    break
                prev, last = e_j, e_j
                if is_exit:
                    reached = True
                    break
            if ok and (reached or e_s - last >= th["min_drop_m"]):
                start = t_s
                break
        grades["evac_delay"] = by_time(start - t0 if start is not None else None, th, "good_max_s", "normal_max_s")

    # no_elevator
    calls = sum(1 for e in events if e["type"] == "ELEVATOR_CALL")
    grades["no_elevator"] = "good" if calls <= items_cfg["no_elevator"]["thresholds"]["good_max_count"] else "poor"

    # door_check
    th = items_cfg["door_check"]["thresholds"]
    opens = [e for e in after if e["type"] == "DOOR_OPEN"]
    if not opens:
        grades["door_check"] = "na"
    elif any(e["data"]["hot"] for e in opens):
        grades["door_check"] = "poor"
    else:
        ratio = sum(1 for e in opens if e["data"]["checked_before"]) / len(opens)
        grades["door_check"] = "good" if ratio >= th["good_min_ratio"] else "normal" if ratio >= th["normal_min_ratio"] else "poor"

    # nose_mouth
    th = items_cfg["nose_mouth"]["thresholds"]
    first = next((s for s in samples if s["zone_d"] >= th["smoke_d_min"]), None)
    if first is None:
        grades["nose_mouth"] = "na"
    else:
        before = [e for e in events if e["seq"] < first["seq"]]
        equips = [e for e in before if e["type"] == "TOWEL_EQUIP"]
        on = bool(equips) and equips[-1]["data"]["on"]
        wet = any(e["type"] == "TOWEL_WET" for e in before)
        grades["nose_mouth"] = "good" if on and wet else "normal" if on else "poor"

    # low_posture
    th = items_cfg["low_posture"]["thresholds"]
    smoky = [s for s in samples if s["layer_y"] < cfg["standing_head_m"]]
    if not smoky:
        grades["low_posture"] = "na"
    else:
        ratio = sum(1 for s in smoky if s["head_ratio"] <= cfg["posture_threshold"]) / len(smoky)
        grades["low_posture"] = "good" if ratio >= th["good_min_ratio"] else "normal" if ratio >= th["normal_min_ratio"] else "poor"

    # non-MVP in this phase
    grades["extinguish"] = "na"
    grades["blocked_response"] = "na"

    # belongings
    th = items_cfg["belongings"]["thresholds"]
    n = sum(1 for e in after if e["type"] == "PICKUP" and not e["data"]["essential"])
    grades["belongings"] = "good" if n <= th["good_max_count"] else "normal" if n <= th["normal_max_count"] else "poor"

    # warn_others
    npcs = [n["id"] for n in scenario["npcs"]]
    if not npcs:
        grades["warn_others"] = "na"
    else:
        helped = {e["data"]["npc"] for e in events
                  if e["type"] == "NPC_INTERACT" and e["data"]["action"] in ("warn", "escort")}
        ratio = len(helped & set(npcs)) / len(npcs)
        grades["warn_others"] = "good" if ratio >= items_cfg["warn_others"]["thresholds"]["good_min_ratio"] else "normal" if ratio > 0 else "poor"

    if cfg["mvp_only"]:
        for iid, it in items_cfg.items():
            if not it["mvp"]:
                grades[iid] = "na"

    pts_sum = max_sum = 0.0
    for iid, g in grades.items():
        if g == "na":
            continue
        w = items_cfg[iid]["weight"]
        max_sum += w
        pts_sum += w * cfg["grade_ratio"][g]
    total = round_half_up(100 * pts_sum / max_sum) if max_sum else 0
    end = next((e for e in reversed(events) if e["type"] == "RUN_END"), None)
    return {"outcome": end["data"]["outcome"] if end else None, "total_pct": total, "grades": grades}


def default_inputs():
    return (ZoneGraph(load_json(CONFIG / "zone_graph.json")),
            load_json(CONFIG / "scenario.json"),
            load_json(CONFIG / "evaluation.json"))


if __name__ == "__main__":
    g, s, c = default_inputs()
    for name in ("model_run", "fail_run", "na_run"):
        res = evaluate(load_log(ROOT / "contracts" / "golden" / f"{name}.jsonl"), g, s, c)
        print(name, res)
