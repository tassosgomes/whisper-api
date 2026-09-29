"""Lightweight process sampling for the local proof of concept."""

from __future__ import annotations

import math
from dataclasses import dataclass
from datetime import datetime, timezone
from collections import Counter
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
        self._duration_samples: list[float] = []

    def observe_start(self, duration_seconds: float) -> None:
        with self._lock:
            self._count += 1
            self._duration_seconds_total += duration_seconds
            self._duration_seconds_max = max(
                self._duration_seconds_max, duration_seconds
            )
            _append_duration_sample(self._duration_samples, duration_seconds)

    def snapshot(self) -> dict[str, float | int]:
        with self._lock:
            return {
                "downloadStartCount": self._count,
                "downloadStartSecondsTotal": self._duration_seconds_total,
                "downloadStartSecondsMax": self._duration_seconds_max,
                "downloadStartSecondsP95": _p95(self._duration_samples),
            }


class JobProcessingMetrics:
    """Process-local job metrics with fixed, non-sensitive dimensions."""

    def __init__(self) -> None:
        self._lock = Lock()
        self._counts: Counter[str] = Counter()
        self._durations: dict[str, dict[str, float]] = {}
        self._duration_samples: dict[str, list[float]] = {}
        self._failures: Counter[str] = Counter()
        self._rtf_total = 0.0
        self._rtf_count = 0
        self._resource_count = 0
        self._peak_cpu_percent = 0.0
        self._peak_memory_mb = 0.0
        self._peak_threads = 0
        self._notification_attempts = 0
        self._notification_outcomes: Counter[str] = Counter()
        self._notification_retry_delay_seconds_total = 0.0
        self._notification_retry_delay_seconds_max = 0.0

    def increment(self, name: str) -> None:
        with self._lock:
            self._counts[name] += 1

    def observe_duration(self, name: str, seconds: float) -> None:
        with self._lock:
            values = self._durations.setdefault(
                name, {"count": 0.0, "secondsTotal": 0.0, "secondsMax": 0.0}
            )
            values["count"] += 1
            values["secondsTotal"] += seconds
            values["secondsMax"] = max(values["secondsMax"], seconds)
            _append_duration_sample(
                self._duration_samples.setdefault(name, []), seconds
            )

    def observe_rtf(self, rtf: float | None) -> None:
        if rtf is None:
            return
        with self._lock:
            self._rtf_total += rtf
            self._rtf_count += 1

    def observe_failure(self, stage: str, code: str) -> None:
        with self._lock:
            self._failures[f"{stage}.{code}"] += 1

    def observe_resources(self, summary: dict[str, float | int]) -> None:
        with self._lock:
            self._resource_count += 1
            self._peak_cpu_percent = max(
                self._peak_cpu_percent, float(summary["peakCpuPercent"])
            )
            self._peak_memory_mb = max(
                self._peak_memory_mb, float(summary["peakMemoryMb"])
            )
            self._peak_threads = max(self._peak_threads, int(summary["peakThreads"]))

    def observe_notification_attempt(
        self, outcome: str, *, retry_delay_seconds: float | None = None
    ) -> None:
        """Aggregate delivery outcomes and delays without event or account labels."""
        with self._lock:
            self._notification_attempts += 1
            self._notification_outcomes[outcome] += 1
            if retry_delay_seconds is not None:
                delay = max(0.0, retry_delay_seconds)
                self._notification_retry_delay_seconds_total += delay
                self._notification_retry_delay_seconds_max = max(
                    self._notification_retry_delay_seconds_max, delay
                )

    def snapshot(self) -> dict:
        with self._lock:
            return {
                "counts": dict(self._counts),
                "durations": {
                    name: {
                        **values,
                        "secondsP95": _p95(self._duration_samples.get(name, [])),
                    }
                    for name, values in self._durations.items()
                },
                "failures": dict(self._failures),
                "realtimeFactorAverage": (
                    self._rtf_total / self._rtf_count if self._rtf_count else None
                ),
                "realtimeFactorCount": self._rtf_count,
                "resources": {
                    "sampledTranscriptions": self._resource_count,
                    "peakCpuPercent": round(self._peak_cpu_percent, 2),
                    "peakMemoryMb": round(self._peak_memory_mb, 2),
                    "peakThreads": self._peak_threads,
                },
                "notifications": {
                    "attempts": self._notification_attempts,
                    "outcomes": dict(self._notification_outcomes),
                    "retryDelaySecondsTotal": self._notification_retry_delay_seconds_total,
                    "retryDelaySecondsMax": self._notification_retry_delay_seconds_max,
                },
            }


MAX_DURATION_SAMPLES = 1000


def _append_duration_sample(samples: list[float], seconds: float) -> None:
    samples.append(seconds)
    if len(samples) > MAX_DURATION_SAMPLES:
        del samples[0]


def _p95(samples: list[float]) -> float:
    if not samples:
        return 0.0
    ordered = sorted(samples)
    return ordered[math.ceil(len(ordered) * 0.95) - 1]


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
