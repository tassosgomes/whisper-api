"""Lightweight process sampling for the local proof of concept."""

from __future__ import annotations

from dataclasses import dataclass
from datetime import datetime, timezone
from threading import Event, Thread
from time import monotonic
from threading import Lock

import psutil


@dataclass(frozen=True)
class ResourceSample:
    timestamp: str
    cpu_percent: float
    memory_mb: float
    threads: int


class DownloadStartMetrics:
    """Process-local aggregate for time from API admission to source connection."""

    def __init__(self) -> None:
        self._lock = Lock()
        self._count = 0
        self._duration_seconds_total = 0.0
        self._duration_seconds_max = 0.0

    def observe_start(self, duration_seconds: float) -> None:
        with self._lock:
            self._count += 1
            self._duration_seconds_total += duration_seconds
            self._duration_seconds_max = max(
                self._duration_seconds_max, duration_seconds
            )

    def snapshot(self) -> dict[str, float | int]:
        with self._lock:
            return {
                "downloadStartCount": self._count,
                "downloadStartSecondsTotal": self._duration_seconds_total,
                "downloadStartSecondsMax": self._duration_seconds_max,
            }


class ResourceSampler:
    """Sample this process once a second while a transcription is running.

    psutil's process CPU convention allows values over 100% when multiple
    logical CPUs are busy. The first call primes its interval measurement.
    """

    def __init__(self, interval_seconds: float = 1.0) -> None:
        self.interval_seconds = interval_seconds
        self.process = psutil.Process()
        self.samples: list[ResourceSample] = []
        self.baseline_memory_mb = self.process.memory_info().rss / 1_000_000
        self._stop = Event()
        self._thread: Thread | None = None
        self.started_at: float | None = None

    def _sample(self) -> None:
        self.samples.append(
            ResourceSample(
                timestamp=datetime.now(timezone.utc).isoformat(),
                cpu_percent=self.process.cpu_percent(interval=None),
                memory_mb=self.process.memory_info().rss / 1_000_000,
                threads=self.process.num_threads(),
            )
        )

    def _run(self) -> None:
        while not self._stop.wait(self.interval_seconds):
            self._sample()

    def start(self) -> None:
        if self._thread is not None:
            raise RuntimeError("ResourceSampler já foi iniciado")
        self.baseline_memory_mb = self.process.memory_info().rss / 1_000_000
        self.process.cpu_percent(interval=None)
        self.started_at = monotonic()
        self._thread = Thread(target=self._run, name="resource-sampler", daemon=True)
        self._thread.start()

    def stop(self) -> float:
        if self._thread is None or self.started_at is None:
            raise RuntimeError("ResourceSampler não foi iniciado")
        elapsed = monotonic() - self.started_at
        self._stop.set()
        self._thread.join()
        self._sample()
        return elapsed

    def summary(self) -> dict[str, float | int]:
        cpu = [sample.cpu_percent for sample in self.samples]
        memory = [
            self.baseline_memory_mb,
            *(sample.memory_mb for sample in self.samples),
        ]
        return {
            "baselineMemoryMb": round(self.baseline_memory_mb, 2),
            "peakMemoryMb": round(max(memory), 2),
            "averageCpuPercent": round(sum(cpu) / len(cpu), 2) if cpu else 0.0,
            "peakCpuPercent": round(max(cpu), 2) if cpu else 0.0,
            "peakThreads": max(
                (sample.threads for sample in self.samples),
                default=self.process.num_threads(),
            ),
            "sampleCount": len(self.samples),
            "sampleIntervalSeconds": self.interval_seconds,
        }
