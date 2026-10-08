"""Offline regressions for actual C# payload input to the upstream checker."""
import json
from pathlib import Path

import pytest

from scripts.check_qbittorrent_schema import check_source, load_payloads


def exported_payloads():
    fixture = Path(__file__).resolve().parents[1] / 'tests-contract/fixtures/legacy/settings-payloads.json'
    return json.loads(fixture.read_text(encoding='utf-8'))


def controller_source():
    values = exported_payloads()['1.2.19']['payload']
    read = '\n'.join(f'data[u"{key}"_s] = value;' for key in values)
    write = '\n'.join(f'if (hasKey(u"{key}"_s)) value.{conversion}();'
                      for key, value in values.items()
                      for conversion in ['toBool' if type(value) is bool else 'toInt' if type(value) is int else 'toString'])
    return read + '\nvoid AppController::setPreferencesAction()\n' + write


def test_actual_export_and_default_python_payloads_use_same_source_check(tmp_path):
    path = tmp_path / 'actual.json'
    data = exported_payloads()
    # Actual output values, rather than regenerated Python values, are checked against the controller.
    data['1.2.19']['payload']['up_limit'] = 123 * 1024
    path.write_text(json.dumps(data), encoding='utf-8')
    actual = load_payloads(path)
    check_source(controller_source(), 'test', actual)
    check_source(controller_source(), 'test')
    broken = controller_source().replace('value.toInt();', 'value.toBool();')
    with pytest.raises(ValueError, match='does not accept int'):
        check_source(broken, 'test', actual)


@pytest.mark.parametrize('content', [
    '{', '[]', '{}', '{"1.2.19":{"payload":{"up_limit":1}}}',
    '{"1.2.19":{"payload":{}},"2.0.11":{"payload":{}}}',
    '{"1.2.19":null,"2.0.11":{"payload":{"up_limit":1}}}',
    '{"1.2.19":{"payload":{"up_limit":1.5}},"2.0.11":{"payload":{"up_limit":1}}}',
    '{"1.2.19":{"payload":{"up_limit":2147483648}},"2.0.11":{"payload":{"up_limit":1}}}',
    '{"1.2.19":{"payload":{"up_limit":null}},"2.0.11":{"payload":{"up_limit":1}}}',
    '{"1.2.19":{"payload":{"up_limit":[]}},"2.0.11":{"payload":{"up_limit":1}}}',
    '{"1.2.19":{"payload":{"up_limit":1,"up_limit":2}},"2.0.11":{"payload":{"up_limit":1}}}',
])
def test_malformed_or_missing_branches_fail_before_upstream_fetch(tmp_path, content):
    path = tmp_path / 'actual.json'
    path.write_text(content, encoding='utf-8')
    with pytest.raises(ValueError):
        load_payloads(path)


@pytest.mark.parametrize('branch,key,value', [
    ('1.2.19', 'up_limit', True), ('2.0.11', 'dht', 1),
    ('1.2.19', 'current_network_interface', 0), ('2.0.11', 'listen_port', '55000'),
])
def test_valid_json_with_wrong_preference_type_is_rejected(tmp_path, branch, key, value):
    data = exported_payloads()
    data[branch]['payload'][key] = value
    path = tmp_path / 'actual.json'
    path.write_text(json.dumps(data), encoding='utf-8')
    with pytest.raises(ValueError, match='incompatible payload type'):
        check_source(controller_source(), 'test', load_payloads(path))


@pytest.mark.parametrize('branch,key', [
    ('1.2.19', 'listen_port'), ('2.0.11', 'current_network_interface'),
    ('1.2.19', 'disk_cache'), ('2.0.11', 'random_port'),
])
def test_missing_optional_or_legacy_key_is_not_hidden_by_filtering(tmp_path, branch, key):
    data = exported_payloads()
    del data[branch]['payload'][key]
    path = tmp_path / 'actual.json'
    path.write_text(json.dumps(data), encoding='utf-8')
    with pytest.raises(ValueError, match='incomplete or unexpected payload keys'):
        check_source(controller_source(), 'test', load_payloads(path))


def test_libtorrent_two_export_cannot_include_legacy_cache_keys(tmp_path):
    data = exported_payloads()
    data['2.0.11']['payload']['disk_cache'] = -1
    path = tmp_path / 'actual.json'
    path.write_text(json.dumps(data), encoding='utf-8')
    with pytest.raises(ValueError, match='incomplete or unexpected payload keys'):
        check_source(controller_source(), 'test', load_payloads(path))
