"""Build unity/Assets/FireDrill/Config/npc_cache.json (명세서 8장 NPC 반응 캐시).

Every key npc_id|player_action|smoke_level|current_action (2 NPCs × 4 × 4 × 6 = 192) gets a reaction from the
server's LLM backend and the same system prompt the server uses (server/prompts/npc_system.md).

    uv run --project server python scripts/build_npc_cache.py              # fake backend, keeps existing entries
    uv run --project server python scripts/build_npc_cache.py --mode claude --overwrite   # needs ANTHROPIC_API_KEY

A person reviews the lines and commits the file. `fallback` lines are kept as they are.
"""
from __future__ import annotations

import argparse
import asyncio
import datetime as dt
import json
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "server"))

from app.llm import ClaudeBackend, FakeBackend  # noqa: E402
from app.schemas import NpcReactRequest, NpcReaction  # noqa: E402
from app.settings import Settings  # noqa: E402

CONFIG = ROOT / "unity" / "Assets" / "FireDrill" / "Config"
PLAYER_ACTIONS = ["warn", "escort", "ignore", "none"]
NPC_ACTIONS = ["evacuate", "follow_player", "freeze", "ask_help", "go_elevator", "hide"]
SMOKE_HINT = {0: [], 1: ["연기가 조금 보임"], 2: ["연기가 짙어짐"], 3: ["연기가 매우 짙음"]}


def load(name: str) -> dict:
    return json.loads((CONFIG / name).read_text(encoding="utf-8"))


async def build(mode: str, overwrite: bool, concurrency: int) -> dict:
    settings = Settings()
    scenario = load("scenario.json")
    cache = load("npc_cache.json")
    system = (settings.prompts_dir / "npc_system.md").read_text(encoding="utf-8")
    if mode == "claude":
        if not settings.anthropic_api_key:
            raise SystemExit("ANTHROPIC_API_KEY is not set (server/.env)")
        backend = ClaudeBackend(settings)
        model = settings.npc_model
    else:
        backend = FakeBackend()
        model = "fake"

    entries: dict[str, dict] = {} if overwrite else dict(cache.get("entries", {}))
    jobs = []
    for npc in scenario["npcs"]:
        for player in PLAYER_ACTIONS:
            for smoke in range(4):
                for current in NPC_ACTIONS:
                    key = f"{npc['id']}|{player}|{smoke}|{current}"
                    if key in entries:
                        continue
                    req = NpcReactRequest(npc_id=npc["id"], persona=npc["persona"], current_action=current,
                                          zone=npc.get("home_zone"), player_action=player, smoke_level=smoke,
                                          recent_events=SMOKE_HINT[smoke])
                    jobs.append((key, req))

    sem = asyncio.Semaphore(concurrency)
    failed: list[str] = []

    async def one(key: str, req: NpcReactRequest) -> None:
        async with sem:
            user = json.dumps(req.model_dump(exclude={"schema_version"}), ensure_ascii=False)
            try:
                reply = await asyncio.wait_for(backend.npc(system, user, req), settings.npc_timeout_s * 5)
                reaction = NpcReaction.model_validate(reply.data)
                entries[key] = reaction.model_dump()
            except Exception as e:  # a missing key falls back at run time; report and keep going
                failed.append(f"{key}: {e}")

    await asyncio.gather(*(one(k, r) for k, r in jobs))
    if failed:
        print(f"{len(failed)} keys failed:\n  " + "\n  ".join(failed[:20]), file=sys.stderr)

    return {
        "schema_version": 1,
        "model": model if overwrite or not cache.get("entries") else f"{model} (+ earlier entries)",
        "generated_at": dt.date.today().isoformat(),
        "entries": dict(sorted(entries.items())),
        "fallback": cache["fallback"],
    }


def main() -> None:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--mode", choices=["fake", "claude"], default="fake")
    ap.add_argument("--overwrite", action="store_true", help="regenerate every key (default keeps existing entries)")
    ap.add_argument("--concurrency", type=int, default=4)
    args = ap.parse_args()
    data = asyncio.run(build(args.mode, args.overwrite, args.concurrency))
    out = CONFIG / "npc_cache.json"
    out.write_text(json.dumps(data, ensure_ascii=False, indent=1) + "\n", encoding="utf-8")
    print(f"wrote {out} ({len(data['entries'])} entries, model {data['model']})")


if __name__ == "__main__":
    main()
