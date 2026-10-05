"""Opt-in official test image workload; never run or install downloaded content."""
import hashlib
import uuid
import json
import time
from pathlib import Path
import requests
from .qbittorrent_client import ClientError
from .optimization_cycle import save_json
from .workload_catalog import UBUNTU, WORKLOADS, record_workload

URL = "https://releases.ubuntu.com/22.04.5/ubuntu-22.04.5-desktop-amd64.iso.torrent"
NAME = b"ubuntu-22.04.5-desktop-amd64.iso"


def metadata_info(raw, name=NAME):
    """Extract the exact bencoded info slice for the v1 torrent hash."""
    position = 0
    info_slice = None
    def read(depth=0):
        nonlocal position, info_slice
        if depth > 16:
            raise ValueError("Metadata nesting")
        token = raw[position:position+1]
        if token == b"i":
            end = raw.index(b"e", position)
            value = int(raw[position+1:end]); position = end+1
            return value
        if token in (b"d", b"l"):
            position += 1
            value = {} if token == b"d" else []
            while raw[position:position+1] != b"e":
                key = read(depth+1)
                if token == b"l":
                    value.append(key)
                else:
                    start = position
                    value[key] = read(depth+1)
                    if depth == 0 and key == b"info":
                        info_slice = raw[start:position]
            position += 1
            return value
        colon = raw.index(b":", position)
        size = int(raw[position:colon]); position = colon+1
        if size < 0 or position+size > len(raw):
            raise ValueError("Metadata size")
        value = raw[position:position+size]; position += size
        return value
    data = read()
    if position != len(raw) or not info_slice or data[b"info"][b"name"] != name or b"files" in data[b"info"]:
        raise ValueError("Unexpected torrent contents")
    return hashlib.sha1(info_slice).hexdigest(), data[b"info"][b"length"]


def fetch_metadata(workload=UBUNTU):
    from urllib.parse import urljoin, urlsplit
    try:
        with requests.Session() as session:
            # This session carries no qBittorrent credentials or ambient proxy auth.
            session.trust_env = False
            url = workload.url
            for hop in range(6):
                parsed = urlsplit(url)
                if parsed.scheme != "https" or not parsed.hostname or parsed.username or parsed.password:
                    raise ValueError("Metadata redirect must use HTTPS without credentials")
                response = session.get(url, timeout=(5, 20), allow_redirects=False, stream=True)
                response.raise_for_status()
                if response.status_code in (301, 302, 303, 307, 308):
                    location = response.headers.get("Location")
                    response.close()
                    if not location or hop == 5:
                        raise ValueError("Missing redirect address or too many metadata redirects")
                    url = urljoin(url, location)
                    continue
                if response.status_code != 200:
                    raise ValueError(f"Unexpected metadata response: HTTP {response.status_code}")
                raw = bytearray()
                for chunk in response.iter_content(65536):
                    raw.extend(chunk)
                    if len(raw) > 2_000_000:
                        raise ValueError("Oversized metadata")
                break
        torrent_hash, _ = metadata_info(bytes(raw), workload.name)
        if workload.info_hash and torrent_hash != workload.info_hash:
            raise ValueError("Torrent hash does not match the verified official image")
        return bytes(raw)
    except requests.RequestException as exc:
        status = getattr(getattr(exc, "response", None), "status_code", None)
        reason = f"HTTP {status}" if status else type(exc).__name__
        raise ClientError(f"Could not download {workload.label} torrent metadata ({reason}). "
                          "Check your internet connection and retry. No torrent was added.") from exc
    except (ValueError, KeyError, IndexError, TypeError) as exc:
        raise ClientError(f"{workload.label} torrent metadata was rejected: {exc}. No torrent was added.") from exc


def add_workload(client, save_path, record_path, workload=UBUNTU):
    if not save_path.strip():
        raise ClientError("Enter a download folder on the qBittorrent host.")
    raw = fetch_metadata(workload)
    torrent_hash, size = metadata_info(raw, workload.name)
    if size != workload.size_bytes:
        raise ClientError("Official image size changed. No torrent was added; update the workload catalog before testing.")
    existing = next((t for t in client.torrents() if t.get("hash") == torrent_hash), None)
    try:
        saved = json.loads(Path(record_path).read_text())
        if not isinstance(saved, dict):
            saved = {}
    except (OSError, ValueError):
        saved = {}
    if existing:
        if not saved and any(tag.strip().startswith("qfrey-test-") for tag in existing.get("tags", "").split(",")):
            saved = load_workload(client, record_path, raw=raw)
        if saved.get("host") == client.host and saved.get("hash") == torrent_hash and saved.get("tag") in [v.strip() for v in existing.get("tags", "").split(",")]:
            return dict(saved, outcome="already present", torrent_state=existing.get("state", "unknown"))
        raise ClientError("This test image torrent already exists independently of the saved test. Use it in qBittorrent; no duplicate was added or ownership changed.")
    same_pending = saved.get("host") == client.host and saved.get("hash") == torrent_hash and isinstance(saved.get("tag"), str) and saved["tag"].startswith("qfrey-test-")
    tag = saved["tag"] if same_pending else "qfrey-test-" + uuid.uuid4().hex
    record = {"host": client.host, "hash": torrent_hash, "tag": tag, "bytes": size, "save_path": save_path, "source": workload.url, "workload_id": workload.key}
    save_json(record_path, record)  # Preserve identity even if the add response is lost.
    request_error = None
    startup_origin = time.monotonic()
    try:
        client.request("POST", "torrents/add", files={"torrents": (workload.name.decode() + ".torrent", bytes(raw), "application/x-bittorrent")},
            data={"savepath": save_path, "tags": tag, "autoTMM": "false", "paused": "false", "stopped": "false"})
    except ClientError as exc:
        request_error = exc  # A lost response does not prove the mutation failed.
    for _ in range(20):
        try:
            found = next((t for t in client.torrents() if t.get("hash") == torrent_hash), None)
        except ClientError:
            break
        if found:
            if tag not in [v.strip() for v in found.get("tags", "").split(",")]:
                raise ClientError("Torrent is present, but the test tag is missing. Inspect it in qBittorrent; ownership was not verified.")
            from .ramp_metrics import trace_point
            return dict(record, outcome="verified present", torrent_state=found.get("state", "unknown"),
                        _startup_origin=startup_origin,
                        _startup_points=[trace_point([found], time.monotonic()-startup_origin, 'connecting')])
        time.sleep(.25)
    raise ClientError("The add request could not be verified in the torrent list. The saved identity is retained; reconnect and retry to check it before adding again." + (f" {request_error}" if request_error else ""))


def stop_workload(client, record):
    if record.get("host") != client.host:
        raise ClientError("Test torrent belongs to another endpoint.")
    matches = [t for t in client.torrents() if t.get("hash") == record.get("hash") and record.get("tag") in [v.strip() for v in t.get("tags", "").split(",")]]
    if len(matches) != 1:
        raise ClientError("Owned test torrent was not found. Inspect it in qBittorrent; no other torrent was touched.")
    endpoint = "torrents/stop" if client.version.lstrip("v").split(".")[0] == "5" else "torrents/pause"
    client.request("POST", endpoint, data={"hashes": record["hash"]})


def delete_workload(client, record):
    """Delete only the exact torrent whose ownership tag and endpoint match."""
    if record.get('host') != client.host or not record.get('tag', '').startswith('qfrey-test-'):
        raise ClientError('Test torrent belongs to another endpoint or has no valid ownership tag.')
    matches = [t for t in client.torrents() if t.get('hash') == record.get('hash')
               and record['tag'] in [v.strip() for v in t.get('tags', '').split(',')]]
    if len(matches) != 1:
        raise ClientError('Owned test torrent was not found. No files were deleted.')
    client.request('POST', 'torrents/delete', data={'hashes': record['hash'], 'deleteFiles': 'true'})
    for _ in range(20):
        if not any(t.get('hash') == record['hash'] for t in client.torrents()):
            return
        time.sleep(.25)
    raise ClientError('Deletion is still pending. Check qBittorrent before starting another test.')


def wait_for_traffic(client, record, cancelled, progress, timeout=120):
    """Wait for three consecutive samples of real traffic from the owned download."""
    deadline = time.monotonic() + timeout
    consecutive = 0
    while time.monotonic() < deadline:
        if cancelled.is_set():
            raise ClientError('Test cancelled. test image remains in qBittorrent; stop or delete it when finished.')
        torrent = next((t for t in client.torrents() if t.get('hash') == record.get('hash')
                        and record.get('tag') in [v.strip() for v in t.get('tags', '').split(',')]), None)
        if record.get('host') != client.host or torrent is None:
            raise ClientError('Owned image test was not found on this connection.')
        if torrent.get('progress', 0) >= 1:
            raise ClientError('test image is already complete. Delete the test download and start a fresh test.')
        if torrent.get('state') in ('pausedDL', 'stoppedDL'):
            raise ClientError('test image is stopped. Resume it in qBittorrent or delete it to start fresh.')
        if '_startup_origin' in record:
            from .ramp_metrics import trace_point
            record['_startup_points'].append(trace_point([torrent], time.monotonic()-record['_startup_origin'], 'connecting'))
        consecutive = consecutive + 1 if torrent.get('dlspeed', 0) > 0 else 0
        progress(0, 'Waiting for test image download traffic… Cancel stops the test, not the download.')
        if consecutive >= 3:
            return
        cancelled.wait(1)
    raise ClientError('No sustained test image traffic after two minutes. Check peers in qBittorrent, then retry the starting speed test.')



def load_workload(client, record_path, raw=None):
    """Recover a moved EXE's test identity from verified metadata and ownership tag."""
    path = Path(record_path)
    try:
        record = json.loads(path.read_text(encoding='utf-8'))
    except (OSError, ValueError):
        record = None
    if isinstance(record, dict) and record.get('host') == client.host:
        torrent = next((t for t in client.torrents() if t.get('hash') == record.get('hash')
                        and record.get('tag') in [tag.strip() for tag in t.get('tags', '').split(',')]), None)
        if torrent is not None:
            return record
    # Never adopt an untagged personal test download.
    import re
    candidates = [(t, tag.strip()) for t in client.torrents()
                  for tag in t.get('tags', '').split(',')
                  if re.fullmatch(r'qfrey-test-[0-9a-f]{32}', tag.strip())
                  and t.get('name') in [item.name.decode() for item in WORKLOADS.values()]]
    if len(candidates) != 1:
        raise ClientError('No unique tuner-owned image test was found on this server. '
                          'Start a test download here, or remove your own torrent in qBittorrent. No files were touched.')
    torrent, tag = candidates[0]
    workload = next(item for item in WORKLOADS.values() if item.name.decode() == torrent['name'])
    expected_hash, size = metadata_info(raw if raw is not None else fetch_metadata(workload), workload.name)
    if torrent.get('hash') != expected_hash:
        raise ClientError('The tagged torrent does not match the verified image test. No files were touched.')
    record = {'host': client.host, 'hash': expected_hash, 'tag': tag, 'bytes': size,
              'save_path': torrent.get('save_path', ''), 'source': workload.url, 'workload_id': workload.key}
    save_json(path, record)
    return record


def restart_workload(client, record, record_path, cancelled, progress):
    """Called only after approval to delete the owned test and download it again."""
    if cancelled.is_set():
        raise ClientError('Test cancelled before deleting the test download.')
    if not record.get('save_path'):
        raise ClientError('Test download folder is missing. Start a new image test from Speed test.')
    if any(t.get('hash') == record.get('hash') for t in client.torrents()):
        progress(0, 'Removing the previous image test download…')
        delete_workload(client, record)
    if cancelled.is_set():
        raise ClientError('Test cancelled. Previous image test was deleted; no new download was added.')
    progress(0, 'Adding a fresh test image download…')
    fresh = add_workload(client, record['save_path'], record_path, record_workload(record))
    if fresh['hash'] != record['hash']:
        raise ClientError('image test identity changed. Record a new before test for this workload.')
    wait_for_traffic(client, fresh, cancelled, progress)
    return fresh
