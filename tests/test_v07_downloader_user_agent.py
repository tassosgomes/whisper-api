"""V-07: User-Agent identificável no GET da origem."""

from __future__ import annotations

from app.transcription.source import SOURCE_USER_AGENT, SourceConnector


def test_downloader_user_agent(controlled_https_origin, tmp_path):
    origin = controlled_https_origin
    destination = tmp_path / "sample.wav"

    downloaded = SourceConnector().download_to_path(
        origin.url_for("/sample.wav"), destination
    )

    assert downloaded == len(origin.body)
    assert destination.read_bytes() == origin.body
    assert origin.user_agents == [SOURCE_USER_AGENT]
    assert "Whisper" in SOURCE_USER_AGENT
    assert "Mozilla/" not in SOURCE_USER_AGENT
