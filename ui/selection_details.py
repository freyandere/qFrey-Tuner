"""Descriptions of actual calculator branches, not promises of performance."""
from optimizer.models import EnvironmentProfile, ConnectionType

PROFILE_DETAILS = {
    EnvironmentProfile.SYSTEM: ('Installed on this computer', 'Finds the standard user profile as well as nearby profiles. Uses normal desktop tuning rules based on your drive, memory and speeds.'),
    EnvironmentProfile.PORTABLE: ('Portable folder on Windows', 'Searches beside qBittorrent and its portable profile; skips the standard user config. Tuning calculations are the same as System desktop. This choice does not move your files.'),
    EnvironmentProfile.TRUENAS: ('Server with ZFS storage', 'Skips local config discovery. Recommends no early file-space reservation. Legacy cache rules favour ZFS/system caching; those cache controls are skipped on libtorrent 2. Enter the server hardware yourself.'),
    EnvironmentProfile.NAS: ('Network storage server', 'Skips local config discovery. Recommends early file-space reservation; legacy cache rules use a 512 MB buffer without system caching. Those cache controls are skipped on libtorrent 2. Enter the NAS hardware yourself.'),
    EnvironmentProfile.DOCKER: ('Container using a VPN', 'Skips local config discovery. Recommends TCP, smaller outgoing buffers and VPN adapter binding (defaults to tun0 if no adapter was entered). The adapter must exist on the qBittorrent host. This does not create a VPN.'),
    EnvironmentProfile.SEEDBOX: ('Dedicated torrent server', 'Skips local config discovery. Recommends higher connection and upload allowances, larger outgoing buffers and TCP. Private-tracker rules can override the allowances. Enter the server hardware and speeds yourself.'),
}

CONNECTION_DETAILS = {
    ConnectionType.FIBER: ('Fiber internet', 'A fixed internet connection delivered by optical cable. The current calculator recommends TCP for this selection. Enter download and upload speeds separately; this choice does not measure them.'),
    ConnectionType.CABLE_DSL: ('Cable or telephone-line internet', 'Cable uses a coaxial cable; DSL uses a telephone line. The current calculator uses both TCP and µTP for this selection. Enter the speeds actually available to qBittorrent.'),
    ConnectionType.MOBILE_4G: ('Mobile or satellite internet', '4G uses the mobile network; Starlink is satellite internet. The current calculator treats both as a non-fiber connection and uses TCP plus µTP. It does not measure latency or set a data allowance.'),
}
