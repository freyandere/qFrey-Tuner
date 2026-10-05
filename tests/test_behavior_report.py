from optimizer.behavior_report import behavior_report
from optimizer.ramp_metrics import trace_point


def test_report_uses_measured_samples_and_preserves_unknowns():
    def run(speeds, states):
        return {'samples': [dict(download=s * 1024**2, upload=1024**2) for s in speeds],
                'ramp_trace': {'scope': 'test torrent', 'samples': [
                    trace_point([dict(hash='a', dlspeed=0, state=state, num_seeds=2, num_leechs=1)],
                                i, 'measurement') for i, state in enumerate(states)]}}
    before = run([0, 2, 4], ['stalledDL', 'downloading', 'downloading'])
    after = run([2, 2, 2], ['downloading'] * 3)
    report = behavior_report(before, after)
    assert 'Download median: 2.00 → 2.00' in report
    assert '81.6% → 0.0%' in report
    assert 'Download zero-speed readings: 33.3% → 0.0%' in report
    assert 'Readings with stalled downloads: 33.3% → 0.0%' in report
    assert 'Mean connected peers: 3.0 → 3.0' in report
    assert 'not proof' in report
    before['ramp_trace']['samples'][0]['seeds'] = None
    assert 'Mean connected seeds: not recorded' in behavior_report(before, after)
    after['ramp_trace']['scope'] = 'active torrents'
    assert 'different workload scopes' in behavior_report(before, after)
    assert 'not recorded' in behavior_report({'samples': []}, {'samples': []})
    assert 'Complete both tests' in behavior_report(None, after)
    before['samples'] = [dict(download=0, upload=0)]
    assert 'no traffic in at least one test' in behavior_report(before, after)
    point = trace_point([{'hash': 'a'}], 0, 'measurement')
    assert point['stalled_downloads'] is None and point['errors'] is None
    point = trace_point([{'hash': 'a', 'state': 'missingFiles'}], 0, 'measurement')
    assert point['errors'] == 1


def test_outcome_report_includes_changed_settings_and_old_results():
    from types import SimpleNamespace
    from unittest.mock import Mock
    from ui.tabs.results_tab import OutcomeTab
    tab = SimpleNamespace(summary=Mock(), saved=Mock(), behavior_summary=Mock(), draw=Mock(), draw_ramp=Mock())
    data = {'samples': []}
    cycle = SimpleNamespace(baseline=data, optimized=data, original={'up_limit': 0}, path='test.json',
                            comparison=lambda: 'Comparison', plan={'before': {'up_limit': 0},
                            'after': {'up_limit': 1048576}, 'omitted': ['Super seeding not applied.']})
    OutcomeTab.show_cycle(tab, cycle)
    report = tab.behavior_summary.configure.call_args.kwargs['text']
    assert 'Upload speed limit: Unlimited → 1.00 MiB/s' in report
    assert 'Super seeding not applied' in report
    assert 'no recorded speed history' in report
    cycle.optimized = None
    OutcomeTab.show_cycle(tab, cycle)
    assert 'Complete both tests' in tab.behavior_summary.configure.call_args.kwargs['text']
