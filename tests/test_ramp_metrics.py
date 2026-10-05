"""Ramp-up metrics use sustained observations and preserve missing data."""
from optimizer.ramp_metrics import trace_point, ramp_metrics


def test_known_ramp_and_first_connected_source():
    speeds = [0, 10, 20, 55, 70, 100, 100, 100, 100, 100, 100]
    trace = {'fresh_start': True, 'samples': [
        {'elapsed': i, 'download': value*1024**2, 'upload': 0,
         'seeds': 0 if i < 2 else 3, 'peers': 0 if i < 1 else 5,
         'phase': 'measurement' if i >= 5 else 'warmup'}
        for i, value in enumerate(speeds)]}
    metrics = ramp_metrics(trace)
    assert metrics['first_traffic_seconds'] == 1
    assert metrics['first_seed_seconds'] == 2
    assert metrics['first_peer_seconds'] == 1
    assert metrics['time_to_50_seconds'] == 3
    assert metrics['time_to_90_seconds'] == 5
    assert metrics['reference_download_mib_s'] == 100
    assert metrics['peak_connected_seeds'] == 3


def test_one_sample_spike_is_not_sustained_ramp():
    values = [0, 100, 0, 0, 0, 100, 100, 100, 100]
    trace = {'samples': [dict(elapsed=i, download=value, phase='measurement') for i, value in enumerate(values)]}
    assert ramp_metrics(trace)['time_to_90_seconds'] == 5


def test_immediate_duplicate_points_do_not_count_as_three_seconds():
    trace = {'samples': [dict(elapsed=t, download=100, phase='measurement') for t in (0, .1, .2)]}
    assert ramp_metrics(trace)['time_to_90_seconds'] is None


def test_missing_peer_counts_remain_unknown():
    point = trace_point([{'hash': 'a', 'dlspeed': 10}], 2, 'measurement')
    assert point['peers'] is None and point['seeds'] is None
    assert ramp_metrics({'samples': [point]})['first_seed_seconds'] is None


def test_test_torrent_trace_does_not_include_unrelated_traffic():
    point = trace_point([{'hash': 'a', 'dlspeed': 10, 'num_seeds': 2, 'num_leechs': 3},
                         {'hash': 'b', 'dlspeed': 9000, 'num_seeds': 100, 'num_leechs': 100}], 0, 'warmup', {'a'})
    assert point['download'] == 10 and point['seeds'] == 2 and point['peers'] == 5


def test_zero_reference_does_not_invent_ramp_thresholds():
    trace = {'samples': [dict(elapsed=i, download=0, phase='measurement') for i in range(4)]}
    assert ramp_metrics(trace)['time_to_50_seconds'] is None
    assert ramp_metrics(trace)['time_to_90_seconds'] is None
