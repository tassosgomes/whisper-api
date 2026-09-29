"""Alembic environment for the application schema."""

from __future__ import annotations

from alembic import context

from app.access import models as _access_models  # noqa: F401
from app.database import Base, create_database_engine, database_url_from_env
from app.jobs import models as _job_models  # noqa: F401


config = context.config
target_metadata = Base.metadata


def run_migrations_offline() -> None:
    context.configure(
        url=database_url_from_env(),
        target_metadata=target_metadata,
        literal_binds=True,
        dialect_opts={"paramstyle": "named"},
    )
    with context.begin_transaction():
        context.run_migrations()


def _run_with_connection(connection) -> None:
    context.configure(connection=connection, target_metadata=target_metadata)
    with context.begin_transaction():
        context.run_migrations()


def run_migrations_online() -> None:
    connection = config.attributes.get("connection")
    if connection is not None:
        _run_with_connection(connection)
        return

    engine = create_database_engine(database_url_from_env())
    try:
        with engine.connect() as connection:
            _run_with_connection(connection)
    finally:
        engine.dispose()


if context.is_offline_mode():
    run_migrations_offline()
else:
    run_migrations_online()
