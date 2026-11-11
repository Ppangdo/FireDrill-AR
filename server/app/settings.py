"""Server settings (명세서 9장). Values come from environment variables or server/.env.

Every number the server uses lives here, not in code.
"""
from __future__ import annotations

from functools import cached_property
from pathlib import Path
from typing import Literal

from pydantic_settings import BaseSettings, SettingsConfigDict

SERVER_DIR = Path(__file__).resolve().parents[1]
REPO_DIR = SERVER_DIR.parent


class Settings(BaseSettings):
    model_config = SettingsConfigDict(
        env_file=SERVER_DIR / ".env", env_file_encoding="utf-8", extra="ignore",
    )

    anthropic_api_key: str = ""
    llm_mode: Literal["claude", "fake", "off"] = "fake"
    # "device:token,device:token". A token authorises REST calls; the WebSocket also
    # checks that the token belongs to the device in the path.
    device_tokens: str = "pc-01:dev-token-change-me"
    # Relative sqlite paths resolve against server/, not the current directory.
    database_url: str = "sqlite:///./firedrill.db"

    npc_model: str = "claude-haiku-5-5"
    npc_effort: Literal["low", "medium", "high"] = "low"
    npc_max_tokens: int = 2048
    npc_timeout_s: float = 3.0

    report_model: str = "claude-sonnet-5-5"
    report_effort: Literal["low", "medium", "high"] = "medium"
    report_max_tokens: int = 8000
    report_timeout_s: float = 10.0

    ws_ping_interval_s: float = 2.0

    config_dir: Path = REPO_DIR / "unity" / "Assets" / "FireDrill" / "Config"
    prompts_dir: Path = SERVER_DIR / "prompts"
    static_dir: Path = SERVER_DIR / "app" / "static"

    @cached_property
    def tokens(self) -> dict[str, str]:
        """token -> device_id"""
        out: dict[str, str] = {}
        for pair in self.device_tokens.split(","):
            pair = pair.strip()
            if not pair:
                continue
            device, sep, token = pair.partition(":")
            if not sep or not device.strip() or not token.strip():
                raise ValueError(f"DEVICE_TOKENS entry must be device:token, got {pair!r}")
            out[token.strip()] = device.strip()
        return out

    @property
    def effective_llm_mode(self) -> Literal["claude", "fake", "off"]:
        """claude without a key behaves as off (fallback responses only)."""
        if self.llm_mode == "claude" and not self.anthropic_api_key:
            return "off"
        return self.llm_mode

    @property
    def resolved_database_url(self) -> str:
        prefix = "sqlite:///"
        url = self.database_url
        if url.startswith(prefix) and not url.startswith(prefix + "/") and url != "sqlite://":
            rel = url[len(prefix):]
            if rel and rel != ":memory:" and not Path(rel).is_absolute():
                return prefix + (SERVER_DIR / rel).resolve().as_posix()
        return url
