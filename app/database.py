"""Database configuration and shared SQLAlchemy metadata."""

from __future__ import annotations

import os
from pathlib import Path

from alembic import command
from alembic.config import Config
from sqlalchemy import URL, create_engine
from sqlalchemy.engine import Engine
from sqlalchemy.orm import DeclarativeBase


class Base(DeclarativeBase):
    """Base for schema objects managed by Alembic."""


def database_url_from_env() -> str:
    configured_url = os.getenv("DATABASE_URL")
    if configured_url:
        return configured_url

    return URL.create(
        "postgresql+psycopg",
        username=os.getenv("DATABASE_USER", "whisper"),
        password=os.getenv("DATABASE_PASSWORD", "local-placeholder-only"),
        host=os.getenv("DATABASE_HOST", "localhost"),
        port=int(os.getenv("DATABASE_PORT", "5432")),
        database=os.getenv("DATABASE_NAME", "whisper"),
    ).render_as_string(hide_password=False)


def create_database_engine(database_url: str) -> Engine:
    return create_engine(database_url, pool_pre_ping=True)


def upgrade_database(engine: Engine) -> None:
    """Apply the checked-in Alembic migrations before the app uses the schema."""
    root = Path(__file__).resolve().parent.parent
    config = Config(str(root / "alembic.ini"))
    config.set_main_option("script_location", str(root / "migrations"))
    with engine.begin() as connection:
        config.attributes["connection"] = connection
        command.upgrade(config, "head")
