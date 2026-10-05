"""Observed torrent ramp-up; never infer unobserved discovery or launch times."""
import statistics


def trace_point(torrents, elapsed, phase, hashes=None):
    selected = [t for t in torrents if hashes is None or t.get('hash') in hashes]
    def count(key):
        return sum(t[key] for t in selected) if selected and all(isinstance(t.get(key), (int, float)) for t in selected) else None
    seeds, leechs = count('num_seeds'), count('num_leechs')
    return {'elapsed': float(elapsed), 'download': sum(t.get('dlspeed', 0) for t in selected),
            'upload': sum(t.get('upspeed', 0) for t in selected), 'seeds': seeds,
            'peers': seeds + leechs if seeds is not None and leechs is not None else None,
            'phase': phase}


def ramp_metrics(trace):
    points = trace.get('samples', [])
    measured = [p['download'] for p in points if p['phase'] == 'measurement']
    reference = statistics.median(measured[len(measured)//2:]) if measured else 0
    def first(predicate):
        return next((p['elapsed'] for p in points if predicate(p)), None)
    def sustained(fraction):
        if reference <= 0:
            return None
        for index in range(len(points)-2):
            group = points[index:index+3]
            if all(p['download'] >= reference*fraction for p in group) and 1.5 <= group[-1]['elapsed'] - group[0]['elapsed'] <= 3.5:
                return group[0]['elapsed']
        return None
    return {'first_traffic_seconds': first(lambda p: p['download'] > 0),
            'first_seed_seconds': first(lambda p: p.get('seeds') is not None and p['seeds'] > 0),
            'first_peer_seconds': first(lambda p: p.get('peers') is not None and p['peers'] > 0),
            'time_to_50_seconds': sustained(.5), 'time_to_90_seconds': sustained(.9),
            'reference_download_mib_s': reference / 1024**2,
            'peak_connected_seeds': max((p['seeds'] for p in points if p.get('seeds') is not None), default=None),
            'peak_connected_peers': max((p['peers'] for p in points if p.get('peers') is not None), default=None),
            'fresh_start': trace.get('fresh_start', False)}
