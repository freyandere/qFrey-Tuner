"""A guarded baseline -> preview -> verified apply -> comparison -> rollback cycle."""

from dataclasses import asdict
from datetime import datetime, timezone
from enum import Enum
import json
import math
import os
from pathlib import Path
import re
import statistics
import tempfile
import time
import uuid

from .models import EncryptionMode, ProtocolMode
from .qbittorrent_client import ClientError
from .workload_catalog import WARMUP_SECONDS, SAMPLE_SECONDS


def json_value(value):
    if isinstance(value, Enum):
        return value.name
    if isinstance(value, dict):
        return {k: json_value(v) for k, v in value.items()}
    if isinstance(value, (list, tuple)):
        return [json_value(v) for v in value]
    return value


def save_json(path, data):
    path = Path(path)
    path.parent.mkdir(parents=True, exist_ok=True)
    fd, name = tempfile.mkstemp(dir=path.parent, prefix=".cycle-", suffix=".tmp")
    try:
        with os.fdopen(fd, "w", encoding="utf-8") as stream:
            json.dump(json_value(data), stream, indent=2)
            stream.flush()
            os.fsync(stream.fileno())
        os.replace(name, path)
    finally:
        if os.path.exists(name):
            os.unlink(name)


def recommended_preferences(settings, current, libtorrent):
    """Only send documented keys returned by the target's live schema.

    API speed limits are bytes/s (verified against appcontroller.cpp).
    libtorrent 2 has no legacy disk cache/coalescing settings.
    """
    candidates = {
        "up_limit": settings.global_upload_limit_kib_s * 1024,
        "dl_limit": settings.global_download_limit_kib_s * 1024,
        "max_connec": settings.max_connections_global,
        "max_connec_per_torrent": settings.max_connections_per_torrent,
        "max_uploads": settings.upload_slots_global,
        "max_uploads_per_torrent": settings.upload_slots_per_torrent,
        "limit_utp_rate": True,
        "queueing_enabled": True,
        "max_active_downloads": settings.max_active_downloads,
        "max_active_uploads": settings.max_active_uploads,
        "max_active_torrents": settings.max_active_torrents,
        "preallocate_all": settings.pre_allocate_disk,
        "async_io_threads": settings.async_io_threads,
        "bittorrent_protocol": {ProtocolMode.UTP_TCP: 0, ProtocolMode.TCP_ONLY: 1, ProtocolMode.UTP_ONLY: 2}[settings.protocol_mode],
        "send_buffer_watermark": settings.send_buffer_watermark_kb,
        "send_buffer_low_watermark": settings.send_buffer_low_watermark_kb,
        "send_buffer_watermark_factor": settings.send_buffer_factor,
        "socket_backlog_size": settings.socket_backlog_size,
        "connection_speed": settings.outgoing_connections_per_second,
        "encryption": {EncryptionMode.PREFER: 0, EncryptionMode.REQUIRE: 1, EncryptionMode.DISABLED: 2}[settings.encryption_mode],
        "anonymous_mode": settings.anonymous_mode,
        "dht": settings.enable_dht,
        "pex": settings.enable_pex,
        "lsd": settings.enable_lsd,
    }
    omitted = ["Super seeding is per torrent; not changed by this application."]
    if libtorrent.startswith("1."):
        candidates.update(disk_cache=settings.disk_cache_mb, enable_os_cache=settings.enable_os_cache,
                          enable_coalesce_read_write=settings.coalesce_reads_writes)
    else:
        omitted.append("Legacy disk cache, OS cache and coalescing rules skipped for libtorrent 2.")
    if settings.network_interface:
        candidates["current_network_interface"] = settings.network_interface
    else:
        omitted.append("Existing network interface binding preserved; no interface was requested.")
    if "Random" in settings.listening_port:
        candidates["listen_port"] = int(re.search(r"\d+", settings.listening_port).group())
        candidates["random_port"] = False
    supported = {}
    for key, value in candidates.items():
        if key not in current or type(current[key]) is not type(value):
            omitted.append(f"{key}: unsupported or incompatible with the target schema.")
        else:
            supported[key] = value
    required = {"up_limit", "dl_limit", "max_connec", "max_connec_per_torrent", "dht", "pex", "lsd", "encryption"}
    missing = required - supported.keys()
    if missing:
        raise ClientError(f"Required settings unsupported: {', '.join(sorted(missing))}")
    if settings.network_interface and "current_network_interface" not in supported:
        raise ClientError("Requested VPN binding is unsupported; refusing a partial privacy change.")
    return supported, omitted


class OptimizationCycle:
    def __init__(self, client, directory):
        self.client = client
        self.path = Path(directory) / f"cycle-{uuid.uuid4().hex}.json"
        self.baseline = self.optimized = None
        self.plan = None
        self.original = None
        self.applied = None
        self.verified = False
        self.failure = ""
        self.test_workload = None

    def persist(self):
        save_json(self.path, {
            "created_or_updated_utc": datetime.now(timezone.utc).isoformat(),
            "host": self.client.host, "qBittorrent": self.client.version,
            "api": self.client.api_version, "libtorrent": self.client.libtorrent,
            "baseline": self.baseline, "optimized": self.optimized,
            "plan": self.plan, "original": self.original, "applied": self.applied,
            "verified": self.verified, "failure": self.failure, "test_workload": {k: v for k, v in self.test_workload.items() if not k.startswith("_")} if self.test_workload else None,
        })

    def measure(self, mode, warmup=WARMUP_SECONDS, samples=SAMPLE_SECONDS, interval=1, progress=None, cancelled=None):
        if not self.client.connected:
            raise ClientError("Connect and validate the target first.")
        if mode not in ("baseline", "optimized"):
            raise ValueError("Unknown measurement mode")
        if self.original is not None and mode == "baseline":
            raise ClientError("Roll back before recording another baseline.")
        if mode == "optimized" and not self.verified:
            raise ClientError("Apply and verify changes before the optimized measurement.")
        if samples < 30 or interval <= 0 or warmup < 0:
            raise ValueError("Use at least 30 samples and a positive interval.")
        prefs_before = self.client.preferences()
        traffic_before = self.client.transfer()
        if traffic_before.get("scheduler_enabled") or prefs_before.get("scheduler_enabled") or traffic_before.get("use_alt_speed_limits"):
            raise ClientError("Disable scheduled/alternative speed limits before a controlled measurement.")
        if mode == "optimized" and any(prefs_before.get(k) != v for k, v in self.applied.items()):
            self.verified = False
            raise ClientError("Applied settings changed; readback no longer matches.")
        if traffic_before.get('dl_info_speed', 0) + traffic_before.get('up_info_speed', 0) <= 0:
            raise ClientError('No active download or upload traffic. Start a fresh test download or resume your torrents before measuring. No result was saved.')
        torrents_before = self.client.torrents()
        active = sorted(t["hash"] for t in torrents_before
                        if t.get("state") in {"downloading", "uploading", "forcedDL", "forcedUP", "stalledDL", "stalledUP"})
        if not active:
            raise ClientError("No active torrent workload. Start your chosen torrents in qBittorrent first.")
        if mode == "optimized" and active != self.baseline["workload"]:
            raise ClientError("Active torrent set differs from the baseline. Restore the same workload.")
        downloading = {t['hash'] for t in torrents_before
                       if t.get('state') in ('downloading', 'forcedDL') and t.get('progress', 0) < 1}
        if mode == 'optimized':
            before_downloads = {t['hash'] for t in self.baseline['torrent_context']
                                if t.get('state') in ('downloading', 'forcedDL') and (t.get('progress') or 0) < 1}
            if not before_downloads.issubset(downloading):
                raise ClientError('The before test used a download that is now complete or stopped. '
                                  'Start a fresh download for the after test or restore the same downloading workload. No speed result was saved.')
        def check_downloads(torrents):
            finished = [t for t in torrents if t.get('hash') in downloading and t.get('progress', 0) >= 1]
            if finished:
                raise ClientError('A download completed during the speed test. No result was saved. '
                                  'The test image is too small for a full test at this speed; use a larger active workload. '
                                  'Applied settings are kept; Undo is available in Results.')
        check_downloads(torrents_before)
        def wait(seconds):
            end = time.monotonic() + seconds
            while time.monotonic() < end:
                if cancelled and cancelled.is_set():
                    raise ClientError("Measurement cancelled; no result saved.")
                time.sleep(min(0.1, max(0, end - time.monotonic())))
        from .ramp_metrics import trace_point, ramp_metrics
        record = self.test_workload or {}
        fresh_start = '_startup_origin' in record
        trace_origin = record.get('_startup_origin', time.monotonic())
        trace_samples = list(record.get('_startup_points', []))
        trace_hashes = {record['hash']} if fresh_start else set(active)
        if progress:
            progress(0, "Recording warm-up and peer connections")
        warm_start = time.monotonic()
        for warm_index in range(math.ceil(warmup)):
            wait(max(0, warm_start + warm_index - time.monotonic()))
            warm_torrents = self.client.torrents()
            check_downloads(warm_torrents)
            trace_samples.append(trace_point(warm_torrents, time.monotonic()-trace_origin, 'warmup', trace_hashes))
        wait(max(0, warm_start+warmup-time.monotonic()))
        history = []
        start = time.monotonic()
        for index in range(samples):
            wait(max(0, start + index * interval - time.monotonic()))
            current_torrents = self.client.torrents()
            check_downloads(current_torrents)
            data = self.client.transfer()  # Errors abort; never substitute zeros.
            if data.get("use_alt_speed_limits"):
                raise ClientError("Alternative speed limits activated during measurement; run invalidated.")
            if time.monotonic() - (start + index * interval) > max(2 * interval, .1):
                raise ClientError("Statistics requests fell behind the sampling schedule; run invalidated.")
            trace_samples.append(trace_point(current_torrents, time.monotonic()-trace_origin, 'measurement', trace_hashes))
            history.append({"elapsed": time.monotonic() - start,
                            "download": data["dl_info_speed"], "upload": data["up_info_speed"],
                            "dht_nodes": data.get("dht_nodes", 0)})
            if progress:
                progress((index + 1) / samples, f"{mode.title()}: {index + 1}/{samples}")
        torrents_after = self.client.torrents()
        active_after = sorted(t["hash"] for t in torrents_after
                              if t.get("state") in {"downloading", "uploading", "forcedDL", "forcedUP", "stalledDL", "stalledUP"})
        check_downloads(torrents_after)
        if active_after != active:
            raise ClientError('Active torrents changed during the speed test. Keep the same torrents running and retry. No result was saved.')
        if self._safe_preferences(self.client.preferences()) != self._safe_preferences(prefs_before):
            raise ClientError('Torrent speed or connection settings changed during the test. Keep settings unchanged and retry. No result was saved.')
        dl = [s["download"] for s in history]
        ul = [s["upload"] for s in history]
        if max(dl + ul) <= 0:
            raise ClientError("No transfer activity; this measurement cannot establish performance.")
        result = {"workload": active, "samples": history, "preferences": self._safe_preferences(prefs_before),
                  "mean_download_mib_s": statistics.mean(dl) / 1024**2,
                  "mean_upload_mib_s": statistics.mean(ul) / 1024**2,
                  "download_stddev": statistics.pstdev(dl), "upload_stddev": statistics.pstdev(ul),
                  "warmup_seconds": warmup, "interval_seconds": interval,
                  "torrent_context": [{k: t.get(k) for k in ("hash", "progress", "state", "num_seeds", "num_leechs")} for t in torrents_before]}
        result['ramp_trace'] = {'fresh_start': fresh_start,
                                'scope': 'test torrent' if fresh_start else 'active torrents',
                                'samples': trace_samples}
        result['ramp_metrics'] = ramp_metrics(result['ramp_trace'])
        if mode == "baseline":
            self.baseline = result
            self.optimized = self.plan = None
        else:
            self.optimized = result
        self.persist()
        return result

    @staticmethod
    def _safe_preferences(prefs):
        # A curated performance/privacy snapshot. Never persist Web UI credentials.
        keys = {"max_connec", "max_connec_per_torrent", "max_uploads", "max_uploads_per_torrent",
                "up_limit", "dl_limit", "queueing_enabled", "max_active_downloads", "max_active_uploads",
                "max_active_torrents", "preallocate_all", "async_io_threads", "bittorrent_protocol",
                "send_buffer_watermark", "send_buffer_low_watermark", "send_buffer_watermark_factor",
                "socket_backlog_size", "connection_speed", "encryption", "anonymous_mode", "dht", "pex", "lsd",
                "current_network_interface", "listen_port", "random_port", "disk_cache", "enable_os_cache",
                "enable_coalesce_read_write", "limit_utp_rate", "alt_up_limit", "alt_dl_limit",
                "scheduler_enabled", "upload_slots_behavior"}
        return {k: v for k, v in prefs.items() if k in keys}

    def preview(self, settings, inputs):
        if self.original is not None:
            raise ClientError("Roll back existing changes before calculating a new plan.")
        current = self.client.preferences()
        if self.baseline and self._safe_preferences(current) != self.baseline["preferences"]:
            raise ClientError("Settings changed since the baseline. Record a new baseline.")
        requested, omitted = recommended_preferences(settings, current, self.client.libtorrent)
        if settings.network_interface:
            interfaces = self.client.interfaces()
            matches = {i.get("value", i.get("name")) for i in interfaces
                       if settings.network_interface in (i.get("value"), i.get("name"))}
            if len(matches) != 1:
                raise ClientError("Requested VPN interface does not exist on this qBittorrent host.")
            requested["current_network_interface"] = matches.pop()
        changes = {k: v for k, v in requested.items() if current[k] != v}
        if not changes:
            raise ClientError("No effective changes; current settings already match the supported recommendations.")
        self.plan = {"before": {k: current[k] for k in changes}, "after": changes,
                     "inputs": json_value(inputs), "omitted": omitted,
                     "explanations": settings.explanations, "warnings": settings.warnings}
        self.persist()
        return self.plan

    def apply(self, inputs):
        if not self.baseline:
            raise ClientError("Recommendations are available, but record an active-torrent baseline before applying a measured optimization cycle.")
        if not self.plan or json_value(inputs) != self.plan["inputs"]:
            raise ClientError("Inputs changed or no preview exists. Calculate a fresh preview.")
        current = self.client.preferences()
        if self._safe_preferences(current) != self.baseline["preferences"]:
            raise ClientError("Target settings changed since baseline; start a new cycle.")
        self.original = dict(self.plan["before"])
        self.applied = dict(self.plan["after"])
        self.verified = False
        try:
            self.persist()  # Durable rollback values BEFORE any mutation.
        except OSError:
            self.original = self.applied = None  # No API write has been attempted.
            raise
        try:
            self.client.set_preferences(self.applied)
            for _ in range(10):
                actual = self.client.preferences()
                if all(actual.get(k) == v for k, v in self.applied.items()):
                    self.verified = True
                    break
                time.sleep(0.2)
            if not self.verified:
                raise ClientError("Readback differs from the preview. Changes are not verified; use rollback.")
        except Exception as exc:
            self.failure = str(exc)
            self.persist()
            raise
        self.persist()

    def rollback(self):
        if self.original is None:
            raise ClientError("No backup is available for this cycle.")
        self.verified = False
        self.persist()  # A failed or partial restore must not retain verified status.
        current = self.client.preferences()
        if any(current.get(k) not in (v, (self.applied or {}).get(k)) for k, v in self.original.items()):
            raise ClientError("Settings were edited outside this cycle. Rollback blocked to avoid overwriting unrelated changes; the backup is preserved.")
        self.client.set_preferences(self.original)
        for _ in range(10):
            actual = self.client.preferences()
            if all(actual.get(k) == v for k, v in self.original.items()):
                break
            time.sleep(0.2)
        else:
            raise ClientError("Rollback could not be verified. Backup has been preserved.")
        self.original = None
        self.applied = None
        self.plan = None
        self.persist()

    def restore_backup(self, path):
        data = json.loads(Path(path).read_text(encoding="utf-8"))
        if data.get("host") != self.client.host or data.get("qBittorrent") != self.client.version:
            raise ClientError("Backup belongs to a different endpoint or qBittorrent version.")
        original = data.get("original")
        current = self.client.preferences()
        if not isinstance(original, dict) or not original or any(k not in self._safe_preferences(current) or type(v) is not type(current[k]) for k, v in original.items()):
            raise ClientError("Backup contains unsupported settings or no rollback values.")
        self.original = original
        self.applied = data.get("applied") or {}
        self.path = Path(path)
        self.rollback()

    def comparison(self):
        if not self.baseline or not self.optimized:
            raise ClientError("Both valid measurements are required.")
        lines = ["Observed before / after (MiB/s):"]
        for direction in ("download", "upload"):
            key = f"mean_{direction}_mib_s"
            before, after = self.baseline[key], self.optimized[key]
            if before <= 0:
                verdict = "inconclusive: no baseline traffic"
            else:
                delta = (after / before - 1) * 100
                n, m = len(self.baseline["samples"]), len(self.optimized["samples"])
                noise = 2 * math.sqrt(self.baseline[f"{direction}_stddev"]**2 / n + self.optimized[f"{direction}_stddev"]**2 / m) / 1024**2
                verdict = "inconclusive" if abs(after-before) <= max(noise, before * .05) else ("observed improvement" if after > before else "observed regression")
                verdict += f" ({delta:+.1f}%)"
            lines.append(f"{direction.title()}: {before:.2f} -> {after:.2f}: {verdict}")
        lines.append("Peer availability and cache state can change. Repeat trials; this is not proof of causation.")
        return "\n".join(lines)
