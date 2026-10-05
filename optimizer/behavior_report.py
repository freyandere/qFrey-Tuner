"""Describe observed behavior without attributing it to individual settings."""
import statistics


def behavior_report(before, after):
    if not before or not after:
        return 'Complete both tests to see how qBittorrent behaved before and after.'
    lines = ['Observed behavior • before → after',
             'Transfer readings cover the whole session; peer/state readings cover the traced torrents.']
    histories = [run.get('samples', []) for run in (before, after)]
    for direction in ('download', 'upload'):
        speeds = [[p[direction] for p in points if isinstance(p.get(direction), (int, float))]
                  for points in histories]
        if not all(speeds):
            lines.append(f'{direction.title()}: no recorded speed history in one or both tests.')
            continue
        means = [statistics.mean(values) for values in speeds]
        if not all(value > 0 for value in means):
            lines.append(f'{direction.title()}: no traffic in at least one test; performance comparison unavailable.')
            continue
        medians = [statistics.median(values) / 1024**2 for values in speeds]
        variability = [statistics.pstdev(values) / mean * 100 for values, mean in zip(speeds, means)]
        zero = [sum(value == 0 for value in values) / len(values) * 100 for values in speeds]
        lines.extend([
            f'{direction.title()} median: {medians[0]:.2f} → {medians[1]:.2f} MiB/s.',
            f'{direction.title()} variability (standard deviation / mean): {variability[0]:.1f}% → {variability[1]:.1f}% (lower means steadier readings).',
            f'{direction.title()} zero-speed readings: {zero[0]:.1f}% → {zero[1]:.1f}% (not a diagnosis of disk or network stalls).'])
    traces = [run.get('ramp_trace', {}) for run in (before, after)]
    points = [[p for p in trace.get('samples', []) if p.get('phase') == 'measurement'] for trace in traces]
    if traces[0].get('scope') != traces[1].get('scope'):
        lines.append('Peer/state comparison unavailable: the tests traced different workload scopes.')
    else:
        for key, title, percent in (
            ('seeds', 'Mean connected seeds', False),
            ('peers', 'Mean connected peers', False),
            ('stalled_downloads', 'Readings with stalled downloads', True),
            ('stalled_uploads', 'Readings with stalled uploads', True),
            ('errors', 'Readings with torrent errors', True)):
            values = [[p.get(key) for p in group] for group in points]
            if not all(group and all(isinstance(v, (int, float)) for v in group) for group in values):
                lines.append(f'{title}: not recorded in one or both tests.')
                continue
            measured = [sum(v > 0 for v in group) / len(group) * 100 if percent else statistics.mean(group)
                        for group in values]
            unit = '%' if percent else ''
            lines.append(f'{title}: {measured[0]:.1f}{unit} → {measured[1]:.1f}{unit}.')
        lines.append('More connected peers does not by itself mean better performance. Stalled means no current transfer, not necessarily a fault.')
    lines.extend([
        'Not measured: CPU/RAM use, disk latency/queue, network latency under load, incoming port reachability and VPN leak protection.',
        'A single download does not validate torrent queue limits or seeding capacity. Upload results depend on demand from downloading peers.',
        'These are observations, not proof that a particular setting caused a change. Repeat comparable tests; peer availability and cache state can change.'])
    return '\n\n'.join(lines)
