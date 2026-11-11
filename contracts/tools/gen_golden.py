"""Generate the golden behaviour logs in contracts/golden/ (명세서 15.1).

The smoke values here are a deterministic stand-in, not the fire engine: golden logs
test the Evaluator, which only reads the log. Positions follow the demo_3f layout so
ZoneGraph.ExitDistance gives meaningful values.

Also writes fire_demo.trace.json (contracts/tools/ref_fire.py on the demo config).

Run:  uv run --project server python contracts/tools/gen_golden.py
Stdlib only.
"""
from __future__ import annotations

import json
import math
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
OUT = ROOT / "contracts" / "golden"

STANDING = 1.65
CROUCH_RATIO = 0.9 / STANDING
CEILING = 2.6
MIN_LAYER = 0.3
FIRE_AT = 20.0
DT = 0.1

# demo_3f zones as axis-aligned rectangles (x0, z0, x1, z1), checked in this order.
ZONES = [
    ("z_corr_s", 0, 0, 2, 8),
    ("z_corr_m", 0, 8, 2, 16),
    ("z_corr_n", 0, 16, 2, 24),
    ("z_exit_stair_b", 0, -3, 2, 0),
    ("z_exit_stair_a", 0, 24, 2, 27),
    ("z_elev_lobby", -4, 10, 0, 14),
    ("z_room_301", 2, 2, 10, 8),
    ("z_lab_302", 2, 10, 10, 16),
    ("z_storage_303", 2, 18, 10, 22),
]
EXITS = {"z_exit_stair_b", "z_exit_stair_a"}


def clamp(v: float, lo: float, hi: float) -> float:
    return max(lo, min(hi, v))


def zone_at(x: float, z: float) -> str | None:
    for zid, x0, z0, x1, z1 in ZONES:
        if x0 <= x < x1 and z0 <= z < z1:
            return zid
    return None


def zone_d(zone: str | None, t: float) -> float:
    tf = t - FIRE_AT
    if zone is None or tf <= 0:
        return 0.0
    return {
        "z_storage_303": clamp(tf / 40, 0, 1.0),
        "z_corr_n": clamp((tf - 5) / 60, 0, 0.9),
        "z_corr_m": clamp((tf - 5) / 40, 0, 0.8),
        "z_elev_lobby": 0.8 * clamp((tf - 5) / 40, 0, 0.8),
        "z_corr_s": clamp((tf - 20) / 60, 0, 0.6),
        "z_lab_302": clamp((tf - 40) / 150, 0, 0.4),
        "z_room_301": clamp((tf - 60) / 150, 0, 0.3),
    }.get(zone, 0.0)


class Failed(Exception):
    pass


class Run:
    def __init__(self, run_id: str, start: tuple[float, float], attempt: int):
        self.run_id = run_id
        self.events: list[dict] = []
        self.t = 0.0
        self.x, self.z = start
        self.ratio = 1.0
        self.fed = 0.0
        self.stage = 0
        self.towel_on = False
        self.towel_wet = False
        self.zone = zone_at(self.x, self.z)
        self.fire_started = False
        self.cues_done: set[str] = set()
        self.next_sample = 1.0
        self.ended = False
        self.emit("RUN_START", {"scenario_id": "s_demo_fire", "attempt": attempt})
        self.emit("STATE_CHANGE", {"from": "Calibration", "to": "PreFire"})

    # ---- snapshot -------------------------------------------------------
    def snap(self) -> dict:
        d = zone_d(self.zone, self.t)
        layer = CEILING - d * (CEILING - MIN_LAYER)
        head_y = STANDING * self.ratio
        c = d if head_y > layer else 0.2 * d
        return {
            "zone": self.zone,
            "pos": [round(self.x, 3), round(head_y, 3), round(self.z, 3)],
            "head_ratio": round(self.ratio, 3),
            "c_head": round(c, 4),
            "fed": round(self.fed, 4),
            "zone_d": round(d, 4),
            "layer_y": round(layer, 3),
            "_c": c,
        }

    def emit(self, etype: str, data: dict | None = None) -> None:
        s = self.snap()
        s.pop("_c")
        self.events.append({
            "run_id": self.run_id,
            "seq": len(self.events) + 1,
            "t": round(self.t, 2),
            "type": etype,
            **s,
            "data": data or {},
        })

    def cue(self, name: str) -> None:
        if name not in self.cues_done:
            self.cues_done.add(name)
            self.emit("CUE_EXPOSED", {"cue": name})

    # ---- time -----------------------------------------------------------
    def tick(self) -> None:
        if self.ended:
            raise Failed()
        self.t = round(self.t + DT, 2)
        if not self.fire_started and self.t >= FIRE_AT:
            self.fire_started = True
            self.emit("STATE_CHANGE", {"from": "PreFire", "to": "Fire"})
            self.cue("sound")
        if self.fire_started:
            tf = self.t - FIRE_AT
            if tf >= 5:
                self.cue("smell")
            if tf >= 60:
                self.cue("alarm")
            if zone_d(self.zone, self.t) > 0.05:
                self.cue("smoke")
        s = self.snap()
        mult = 1.0
        if self.towel_on:
            mult = 0.6 if self.towel_wet else 0.8
        self.fed += s["_c"] * mult / 60.0 * DT
        for i, th in enumerate((0.30, 0.60, 0.85), start=1):
            if self.stage < i and self.fed >= th:
                self.stage = i
                self.emit("BODY_STAGE", {"stage": i})
        if self.t + 1e-6 >= self.next_sample:
            self.next_sample += 1.0
            self.emit("SAMPLE")
        if self.fed >= 1.0:
            self.emit("STATE_CHANGE", {"from": "Fire", "to": "Ending"})
            self.emit("RUN_END", {"outcome": "failed"})
            self.ended = True
            raise Failed()

    def wait(self, seconds: float) -> None:
        for _ in range(int(round(seconds / DT))):
            self.tick()

    def wait_until(self, t: float) -> None:
        while self.t + 1e-6 < t:
            self.tick()

    def walk_to(self, x: float, z: float) -> None:
        while True:
            dx, dz = x - self.x, z - self.z
            dist = math.hypot(dx, dz)
            step = (0.7 if self.ratio < 0.7 else 1.4) * DT
            if dist <= step:
                self.x, self.z = x, z
            else:
                self.x += dx / dist * step
                self.z += dz / dist * step
            new_zone = zone_at(self.x, self.z)
            if new_zone != self.zone:
                old, self.zone = self.zone, new_zone
                self.emit("ZONE_ENTER", {"from": old, "to": new_zone})
                if new_zone in EXITS:
                    self.emit("STATE_CHANGE", {"from": "Fire", "to": "Ending"})
                    self.emit("RUN_END", {"outcome": "escaped"})
                    self.ended = True
                    return
            self.tick()
            if dist <= step:
                return

    # ---- actions --------------------------------------------------------
    def crouch(self, on: bool) -> None:
        self.ratio = CROUCH_RATIO if on else 1.0
        self.emit("POSTURE", {"low": on})

    def pickup(self, obj: str, item: str, essential: bool) -> None:
        self.wait(1.0)
        self.emit("PICKUP", {"item": item, "object": obj, "essential": essential})

    def handle_check(self, edge: str, hot: bool = False) -> None:
        self.wait(0.5)
        self.emit("HANDLE_CHECK", {"edge": edge, "hot": hot})

    def open_door(self, edge: str, checked: bool, hot: bool = False) -> None:
        self.emit("DOOR_OPEN", {"edge": edge, "hot": hot, "checked_before": checked})
        self.wait(0.5)


def model_run() -> Run:
    r = Run("golden-model", (6.0, 13.0), attempt=2)
    r.wait_until(FIRE_AT + 1.5)
    r.walk_to(4.0, 11.4)
    r.pickup("o_towel_302", "towel", True)
    r.walk_to(9.2, 11.0)
    r.wait(2.0)
    r.towel_wet = True
    r.emit("TOWEL_WET")
    r.towel_on = True
    r.emit("TOWEL_EQUIP", {"on": True})
    r.walk_to(7.5, 14.5)
    r.emit("NPC_INTERACT", {"npc": "npc_minji", "action": "escort"})
    r.wait(1.0)
    r.walk_to(2.4, 13.35)
    r.handle_check("e_302_door")
    r.open_door("e_302_door", checked=True)
    r.crouch(True)
    r.walk_to(1.0, 13.0)
    r.walk_to(0.5, 12.0)
    r.emit("NPC_INTERACT", {"npc": "npc_junho", "action": "warn"})
    r.emit("SHOUT", {"method": "choice"})
    r.walk_to(1.5, 9.3)
    r.wait(0.5)
    r.emit("ALARM_PRESSED", {"object": "o_alarm_corr_m"})
    r.cue("alarm")
    r.walk_to(1.0, 0.6)
    r.handle_check("e_stair_b")
    r.open_door("e_stair_b", checked=True)
    r.walk_to(1.0, -1.0)
    return r


def fail_run() -> Run:
    r = Run("golden-fail", (6.0, 13.0), attempt=1)
    try:
        r.wait_until(FIRE_AT + 2.0)
        r.walk_to(5.0, 14.6)
        r.pickup("o_bag_302", "bag", False)
        r.emit("NPC_INTERACT", {"npc": "npc_minji", "action": "ignore"})
        r.wait(4.0)
        r.walk_to(2.4, 13.0)
        r.open_door("e_302_door", checked=False)
        r.walk_to(1.0, 13.0)
        r.walk_to(-2.5, 12.3)
        r.emit("NPC_INTERACT", {"npc": "npc_junho", "action": "ignore"})
        r.walk_to(-3.5, 12.0)
        while True:
            r.wait(0.5)
            r.emit("ELEVATOR_CALL")
            r.wait(9.5)
    except Failed:
        pass
    return r


def na_run() -> Run:
    r = Run("golden-na", (1.0, 4.0), attempt=1)
    r.wait_until(FIRE_AT + 2.0)
    r.walk_to(1.0, 0.6)
    r.handle_check("e_stair_b")
    r.open_door("e_stair_b", checked=True)
    r.walk_to(1.0, -1.0)
    return r


EXPECTED = {
    "model_run": {
        "description": "모범 플레이: 수건을 적셔 착용, 민지 동행, 준호에게 경고, 경보, 낮은 자세로 남쪽 계단 탈출",
        "outcome": "escaped", "total_pct": 100,
        "grades": {"alert": "good", "evac_delay": "good", "no_elevator": "good", "door_check": "good",
                   "nose_mouth": "good", "low_posture": "good", "extinguish": "na", "blocked_response": "na",
                   "belongings": "good", "warn_others": "good"},
    },
    "fail_run": {
        "description": "실패 플레이: 가방 챙기기, 확인 없이 문 열기, NPC 무시, 엘리베이터 앞에서 기다리다 쓰러짐",
        "outcome": "failed", "total_pct": 6,
        "grades": {"alert": "poor", "evac_delay": "poor", "no_elevator": "poor", "door_check": "poor",
                   "nose_mouth": "poor", "low_posture": "poor", "extinguish": "na", "blocked_response": "na",
                   "belongings": "normal", "warn_others": "poor"},
    },
    "na_run": {
        "description": "해당 없음 플레이: 남쪽 복도에서 단서 직후 바로 탈출. 연기 구간을 지나지 않음",
        "outcome": "escaped", "total_pct": 69,
        "grades": {"alert": "poor", "evac_delay": "good", "no_elevator": "good", "door_check": "good",
                   "nose_mouth": "na", "low_posture": "na", "extinguish": "na", "blocked_response": "na",
                   "belongings": "good", "warn_others": "poor"},
    },
}


def main() -> None:
    OUT.mkdir(parents=True, exist_ok=True)
    for name, fn in (("model_run", model_run), ("fail_run", fail_run), ("na_run", na_run)):
        run = fn()
        path = OUT / f"{name}.jsonl"
        with path.open("w", encoding="utf-8", newline="\n") as f:
            for e in run.events:
                f.write(json.dumps(e, ensure_ascii=False) + "\n")
        exp = {"schema_version": 1, "log": f"{name}.jsonl", **EXPECTED[name]}
        (OUT / f"{name}.expected.json").write_text(
            json.dumps(exp, ensure_ascii=False, indent=2) + "\n", encoding="utf-8", newline="\n")
        print(f"{name}: {len(run.events)} events, ends t={run.t:.1f}, outcome={run.events[-1]['data']}")

    # Fire reference trace for the C# FireEngine test (Stage 1). Depends on the config, not the logs.
    import ref_fire
    data = ref_fire.trace(*ref_fire.load_defaults())
    (OUT / "fire_demo.trace.json").write_text(ref_fire.trace_json(data), encoding="utf-8", newline="\n")
    print(f"fire_demo.trace: {len(data['rows'])} rows x {len(data['zones'])} zones")


if __name__ == "__main__":
    main()
