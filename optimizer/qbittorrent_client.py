"""Version-aware qBittorrent Web API access; never guess an offline INI schema."""

import json
import time
from urllib.parse import urlsplit

import requests
from packaging.version import Version, InvalidVersion
from .diagnostics import logger


class ClientError(RuntimeError):
    pass


class QBittorrentClient:
    def __init__(self, host="http://127.0.0.1:8080"):
        self.host = host.rstrip("/")
        self.session = requests.Session()
        self.session.trust_env = False
        self.connected = False
        self.version = self.api_version = self.libtorrent = ""
        self.auth_mode = ""

    def request(self, method, endpoint, allowed_status=(), **kwargs):
        started = time.monotonic()
        try:
            response = self.session.request(method, f"{self.host}/api/v2/{endpoint}",
                                            timeout=(3, 8), allow_redirects=False, **kwargs)
            logger.info("API %s %s status=%s elapsed=%.3fs", method, endpoint, response.status_code, time.monotonic()-started)
            if response.status_code not in allowed_status:
                response.raise_for_status()
                if 300 <= response.status_code < 400:
                    raise ClientError("The address redirects to another location. Enter the direct qBittorrent Web UI URL.")
            return response
        except requests.exceptions.SSLError as exc:
            self.connected = False
            logger.error("API %s %s TLS validation failed", method, endpoint)
            raise ClientError("HTTPS certificate validation failed. Check the URL and certificate; use HTTP only if HTTPS is disabled in qBittorrent.") from exc
        except requests.exceptions.Timeout as exc:
            self.connected = False
            logger.error("API %s %s timed out", method, endpoint)
            raise ClientError(f"qBittorrent did not respond at {self.host}. Check the Web UI address and firewall.") from exc
        except requests.exceptions.ConnectionError as exc:
            self.connected = False
            logger.error("API %s %s connection failed", method, endpoint)
            raise ClientError(f"Cannot reach qBittorrent at {self.host}. Check that it is running, Web UI is enabled, and the port matches Options > Web UI.") from exc
        except requests.exceptions.HTTPError as exc:
            self.connected = False
            status = exc.response.status_code
            if status == 403:
                message = "qBittorrent denied access (HTTP 403). Check authentication, localhost bypass and the Web UI ban settings. Stop retrying if this address is temporarily banned."
            elif status == 401:
                message = "Authentication is required (HTTP 401). Enter the Web UI password or API key."
            else:
                message = f"qBittorrent returned HTTP {status} for {endpoint}. Check the address and API compatibility."
            raise ClientError(message) from exc
        except requests.RequestException as exc:
            self.connected = False
            logger.error("API %s %s failed: %s", method, endpoint, type(exc).__name__)
            raise ClientError(f"qBittorrent request failed for {endpoint}. See the diagnostic log.") from exc

    def connect(self, username="", password="", api_key=""):
        self.connected = False
        parsed = urlsplit(self.host)
        if parsed.scheme not in ("http", "https") or not parsed.hostname or parsed.username or parsed.password or parsed.query or parsed.fragment:
            raise ClientError("Enter an http(s) Web UI URL without embedded credentials.")
        self.session.close()
        self.session = requests.Session()
        self.session.trust_env = False
        self.session.headers.update({"Referer": self.host})
        if api_key:
            self.session.headers["Authorization"] = f"Bearer {api_key.strip()}"
        # Probe a protected endpoint first. This also supports localhost bypass
        # without submitting empty credentials and accumulating failed logins.
        response = self.request("GET", "app/version", allowed_status=(401, 403))
        if response.status_code in (401, 403):
            if api_key:
                raise ClientError("API key was rejected or this address is banned. Check the key in Options > Web UI; do not use it as a password.")
            if not username or not password:
                raise ClientError("This endpoint requires authentication. Enter the Web UI username and password, or an API key. With localhost bypass enabled, use http://127.0.0.1:<Web UI port>.")
            login = self.request("POST", "auth/login", data={"username": username, "password": password})
            if login.text.strip() != "Ok.":
                logger.warning("Password authentication rejected (no credentials logged)")
                raise ClientError("Login failed: qBittorrent rejected the username/password. Enter the actual Web UI password, not the 'Change current password' placeholder or an API key. Avoid repeated attempts because qBittorrent can temporarily ban this address.")
            response = self.request("GET", "app/version")
            self.auth_mode = "username/password"
        else:
            self.auth_mode = "API key" if api_key else "existing access / authentication bypass"
        self.version = response.text.strip()
        self.api_version = self.request("GET", "app/webapiVersion").text.strip()
        self.libtorrent = self.request("GET", "app/buildInfo").json().get("libtorrent", "")
        try:
            version = Version(self.version.lstrip("v"))
            api = Version(self.api_version)
            libtorrent = Version(self.libtorrent)
        except (InvalidVersion, TypeError) as exc:
            raise ClientError("Cannot validate the reported qBittorrent/API/libtorrent version.") from exc
        # Expand only after checking the tagged upstream contract (see docs/version-compatibility.md).
        reviewed = (Version("4.6") <= version < Version("4.7") or Version("5.0") <= version < Version("5.3"))
        if not (reviewed and Version("2.8") <= api < Version("3")) or any(
                v.is_prerelease or v.is_devrelease for v in (version, api, libtorrent)):
            raise ClientError(f"Unsupported version: qBittorrent {self.version}, API {self.api_version}, libtorrent {self.libtorrent}. Supported: stable qBittorrent 4.6.x/5.0–5.2.x, API 2.8+ in v2. See docs/version-compatibility.md before expanding support.")
        if len(libtorrent.release) < 2 or libtorrent.major not in (1, 2):
            raise ClientError("Unknown libtorrent version; settings compatibility cannot be established.")
        self.preferences()  # Read permission and live schema must both work.
        self.connected = True
        logger.info("Target validated host=%s qBittorrent=%s API=%s libtorrent=%s authentication=%s", self.host, self.version, self.api_version, self.libtorrent, self.auth_mode)

    def preferences(self):
        prefs = self.request("GET", "app/preferences").json()
        if not isinstance(prefs, dict) or "max_connec" not in prefs:
            raise ClientError("The endpoint did not return a recognized preferences schema.")
        return prefs

    def set_preferences(self, values):
        self.request("POST", "app/setPreferences", data={"json": json.dumps(values)})

    def torrents(self):
        return self.request("GET", "torrents/info").json()

    def transfer(self):
        data = self.request("GET", "transfer/info").json()
        if not all(k in data for k in ("dl_info_speed", "up_info_speed")):
            raise ClientError("Invalid transfer statistics; measurement aborted.")
        return data

    def interfaces(self):
        return self.request("GET", "app/networkInterfaceList").json()

    def disconnect(self):
        self.connected = False
        self.session.close()

    def shutdown(self):
        self.request("POST", "app/shutdown")
        self.connected = False
