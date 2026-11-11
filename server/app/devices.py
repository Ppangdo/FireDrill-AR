"""Connected devices (명세서 14.6 WebSocket): latest status per device, abort delivery, ping."""
from __future__ import annotations

import asyncio
import time
from dataclasses import dataclass, field

from fastapi import WebSocket

from .schemas import WsAbort, WsPing, WsStatus


@dataclass
class DeviceConn:
    ws: WebSocket
    lock: asyncio.Lock = field(default_factory=asyncio.Lock)

    async def send(self, payload: dict) -> None:
        async with self.lock:
            await self.ws.send_json(payload)


@dataclass
class DeviceState:
    status: WsStatus | None = None
    status_at: float | None = None  # monotonic
    connected_at: float | None = None
    disconnected_at: float | None = None


class DeviceHub:
    def __init__(self) -> None:
        self.conns: dict[str, DeviceConn] = {}
        self.states: dict[str, DeviceState] = {}

    def attach(self, device_id: str, ws: WebSocket) -> DeviceConn:
        conn = DeviceConn(ws)
        self.conns[device_id] = conn  # a reconnect replaces the old socket
        st = self.states.setdefault(device_id, DeviceState())
        st.connected_at, st.disconnected_at = time.monotonic(), None
        return conn

    def detach(self, device_id: str, conn: DeviceConn) -> None:
        if self.conns.get(device_id) is conn:
            del self.conns[device_id]
            self.states[device_id].disconnected_at = time.monotonic()

    def update(self, device_id: str, status: WsStatus) -> None:
        st = self.states.setdefault(device_id, DeviceState())
        st.status, st.status_at = status, time.monotonic()

    async def abort(self, device_id: str, by: str = "staff") -> bool:
        conn = self.conns.get(device_id)
        if conn is None:
            return False
        await conn.send(WsAbort(by=by).model_dump())
        return True

    async def ping_loop(self, conn: DeviceConn, interval_s: float) -> None:
        payload = WsPing().model_dump()
        while True:
            await asyncio.sleep(interval_s)
            await conn.send(payload)

    def snapshot(self, known_devices: list[str]) -> list[dict]:
        now = time.monotonic()
        out = []
        for device_id in sorted(set(known_devices) | set(self.states)):
            st = self.states.get(device_id, DeviceState())
            out.append({
                "device_id": device_id,
                "connected": device_id in self.conns,
                "status": st.status.model_dump() if st.status else None,
                "status_age_s": round(now - st.status_at, 1) if st.status_at is not None else None,
            })
        return out
