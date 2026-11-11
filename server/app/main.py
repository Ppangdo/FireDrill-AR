"""ASGI entry point.

Run (repo root):
    uv run --project server python -m uvicorn app.main:app --app-dir server --host 0.0.0.0 --port 8000 --ws wsproto
"""
import logging

from .factory import create_app

logging.basicConfig(level=logging.INFO, format="%(asctime)s %(levelname)s %(name)s: %(message)s")

app = create_app()
