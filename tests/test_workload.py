from unittest.mock import Mock
import pytest
from optimizer.test_workload import metadata_info, NAME, stop_workload
from optimizer.qbittorrent_client import ClientError


def test_metadata_rejects_other_content_and_truncation():
    raw = b"d4:infod6:lengthi123e4:name" + str(len(NAME)).encode() + b":" + NAME + b"ee"
    torrent_hash, size = metadata_info(raw)
    assert len(torrent_hash) == 40 and size == 123
    with pytest.raises(ValueError):
        metadata_info(raw.replace(NAME, b"x" * len(NAME)))
    with pytest.raises((ValueError, IndexError)):
        metadata_info(raw[:-3])


def test_stop_never_touches_unowned_torrent():
    client = Mock(host="http://local", version="v5.2.3")
    client.torrents.return_value = [{"hash": "a", "tags": "personal"}]
    with pytest.raises(ClientError, match="Owned"):
        stop_workload(client, {"host": client.host, "hash": "a", "tag": "qfrey-test"})
    client.request.assert_not_called()
    client.torrents.return_value[0]["tags"] = "personal, qfrey-test"
    stop_workload(client, {"host": client.host, "hash": "a", "tag": "qfrey-test"})
    client.request.assert_called_once_with("POST", "torrents/stop", data={"hashes": "a"})


@pytest.mark.parametrize("lost_response", [False, True])
def test_add_checks_duplicate_before_mutation(monkeypatch, tmp_path, lost_response):
    from optimizer.test_workload import add_workload
    raw = b"d4:infod6:lengthi123e4:name" + str(len(NAME)).encode() + b":" + NAME + b"ee"
    torrent_hash, _ = metadata_info(raw)
    response = Mock(status_code=200)
    response.iter_content.return_value = [raw]
    session = Mock()
    session.get.return_value = response
    context = Mock()
    context.__enter__ = Mock(return_value=session)
    context.__exit__ = Mock(return_value=False)
    monkeypatch.setattr("optimizer.test_workload.requests.Session", lambda: context)
    from dataclasses import replace
    from optimizer.workload_catalog import UBUNTU
    fixture_workload = replace(UBUNTU, size_bytes=123)
    client = Mock(host="http://local")
    client.torrents.return_value = [{"hash": torrent_hash}]
    with pytest.raises(ClientError, match="already exists"):
        add_workload(client, "E:/test", tmp_path / "identity.json", fixture_workload)
    client.request.assert_not_called()
    client.torrents.return_value = []
    def added(*args, **kwargs):
        client.torrents.return_value = [{"hash": torrent_hash, "tags": kwargs["data"]["tags"], "state": "downloading"}]
        if lost_response:
            raise ClientError("response timed out")
        return Mock(text="")
    client.request.side_effect = added
    client.request.return_value.text = "Ok."
    result = add_workload(client, "E:/test", tmp_path / "identity.json", fixture_workload)
    assert result["hash"] == torrent_hash and (tmp_path / "identity.json").exists()
    assert client.request.call_args.kwargs["data"]["savepath"] == "E:/test"
    count = client.request.call_count
    retried = add_workload(client, "E:/test", tmp_path / "identity.json", fixture_workload)
    assert retried["outcome"] == "already present"
    assert client.request.call_count == count



def test_delete_requires_endpoint_and_unique_owned_tag():
    from optimizer.test_workload import delete_workload
    client = Mock(host="http://local")
    record = {"host": client.host, "hash": "a", "tag": "qfrey-test-owned"}
    client.torrents.return_value = [{"hash": "a", "tags": "personal"}]
    with pytest.raises(ClientError, match="Owned"):
        delete_workload(client, record)
    client.request.assert_not_called()
    client.torrents.side_effect = [[{"hash": "a", "tags": record["tag"]}], []]
    delete_workload(client, record)
    client.request.assert_called_once_with("POST", "torrents/delete", data={"hashes": "a", "deleteFiles": "true"})


def test_auto_test_waits_for_sustained_traffic():
    from optimizer.test_workload import wait_for_traffic
    client = Mock(host="http://local")
    record = {"host": client.host, "hash": "a", "tag": "qfrey-test-owned"}
    client.torrents.return_value = [{"hash": "a", "tags": record["tag"], "dlspeed": 100}]
    cancel = Mock()
    cancel.is_set.return_value = False
    wait_for_traffic(client, record, cancel, Mock())
    assert client.torrents.call_count == 3
    cancel.is_set.return_value = True
    with pytest.raises(ClientError, match="cancelled"):
        wait_for_traffic(client, record, cancel, Mock())


def test_missing_record_recovers_only_verified_tagged_ubuntu(monkeypatch, tmp_path):
    from optimizer.test_workload import load_workload
    raw = b'd4:infod6:lengthi123e4:name' + str(len(NAME)).encode() + b':' + NAME + b'ee'
    torrent_hash, _ = metadata_info(raw)
    client = Mock(host='http://local')
    tag = 'qfrey-test-' + 'a' * 32
    client.torrents.return_value = [{'hash': torrent_hash, 'name': NAME.decode(), 'tags': 'personal, ' + tag, 'save_path': 'E:/test'}]
    monkeypatch.setattr('optimizer.test_workload.fetch_metadata', lambda *_: raw)
    record_path = tmp_path / 'moved-exe-state/test-workload.json'
    record = load_workload(client, record_path)
    assert record_path.exists()
    assert record['hash'] == torrent_hash and record['save_path'] == 'E:/test'
    client.request.assert_not_called()


@pytest.mark.parametrize('tags,hash_value', [('', 'a' * 40), ('qfrey-test-' + 'a' * 32, 'b' * 40)])
def test_recovery_never_adopts_personal_or_mismatched_download(tmp_path, tags, hash_value):
    from optimizer.test_workload import load_workload
    raw = b'd4:infod6:lengthi123e4:name' + str(len(NAME)).encode() + b':' + NAME + b'ee'
    client = Mock(host='http://local')
    client.torrents.return_value = [{'hash': hash_value, 'name': NAME.decode(), 'tags': tags}]
    with pytest.raises(ClientError):
        load_workload(client, tmp_path / 'missing.json', raw=raw)
    client.request.assert_not_called()


def test_after_test_removes_readds_and_waits_in_order(monkeypatch, tmp_path):
    from optimizer.test_workload import restart_workload
    record = {'hash': 'a', 'save_path': 'E:/test'}
    steps = []
    monkeypatch.setattr('optimizer.test_workload.delete_workload', lambda *args: steps.append('delete'))
    monkeypatch.setattr('optimizer.test_workload.add_workload', lambda *args: steps.append('add') or record)
    monkeypatch.setattr('optimizer.test_workload.wait_for_traffic', lambda *args: steps.append('traffic'))
    cancel = Mock()
    cancel.is_set.return_value = False
    client = Mock()
    client.torrents.return_value = [{'hash': 'a'}]
    assert restart_workload(client, record, tmp_path / 'identity.json', cancel, Mock()) == record
    assert steps == ['delete', 'add', 'traffic']
    cancel.is_set.return_value = True
    steps.clear()
    with pytest.raises(ClientError, match='cancelled'):
        restart_workload(Mock(), record, tmp_path / 'identity.json', cancel, Mock())
    assert not steps


def test_completed_test_does_not_wait_or_measure():
    from optimizer.test_workload import wait_for_traffic
    client = Mock(host='http://local')
    record = {'host': client.host, 'hash': 'a', 'tag': 'qfrey-test-owned'}
    client.torrents.return_value = [{'hash': 'a', 'tags': record['tag'], 'progress': 1, 'state': 'uploading', 'dlspeed': 0}]
    cancel = Mock()
    cancel.is_set.return_value = False
    with pytest.raises(ClientError, match='already complete'):
        wait_for_traffic(client, record, cancel, Mock())
    cancel.wait.assert_not_called()



def test_after_test_can_readd_a_previously_deleted_owned_download(monkeypatch, tmp_path):
    from optimizer.test_workload import restart_workload
    record = {'hash': 'a', 'save_path': 'E:/test'}
    client = Mock()
    client.torrents.return_value = []
    deleted = Mock()
    monkeypatch.setattr('optimizer.test_workload.delete_workload', deleted)
    monkeypatch.setattr('optimizer.test_workload.add_workload', lambda *args: record)
    monkeypatch.setattr('optimizer.test_workload.wait_for_traffic', lambda *args: None)
    cancel = Mock()
    cancel.is_set.return_value = False
    assert restart_workload(client, record, tmp_path / 'identity.json', cancel, Mock()) == record
    deleted.assert_not_called()


def test_kali_metadata_accepts_only_selected_official_filename():
    from optimizer.workload_catalog import KALI
    raw = b'd4:infod6:lengthi' + str(KALI.size_bytes).encode() + b'e4:name' + str(len(KALI.name)).encode() + b':' + KALI.name + b'ee'
    assert metadata_info(raw, KALI.name)[1] == KALI.size_bytes
    with pytest.raises(ValueError):
        metadata_info(raw, NAME)


def test_large_image_add_persists_identity_for_after_test(monkeypatch, tmp_path):
    from optimizer.test_workload import add_workload
    from optimizer.workload_catalog import KALI
    raw = b'd4:infod6:lengthi' + str(KALI.size_bytes).encode() + b'e4:name' + str(len(KALI.name)).encode() + b':' + KALI.name + b'ee'
    torrent_hash, _ = metadata_info(raw, KALI.name)
    monkeypatch.setattr('optimizer.test_workload.fetch_metadata', lambda workload: raw)
    client = Mock(host='http://local')
    client.torrents.return_value = []
    def added(*args, **kwargs):
        client.torrents.return_value = [{'hash': torrent_hash, 'tags': kwargs['data']['tags'], 'state': 'downloading'}]
    client.request.side_effect = added
    record = add_workload(client, 'E:/test', tmp_path / 'record.json', KALI)
    assert record['workload_id'] == 'kali'
    assert record['source'] == KALI.url
    assert record['bytes'] == KALI.size_bytes


def test_catalog_size_change_blocks_download_before_mutation(monkeypatch, tmp_path):
    from optimizer.test_workload import add_workload
    from optimizer.workload_catalog import UBUNTU
    raw = b'd4:infod6:lengthi123e4:name' + str(len(NAME)).encode() + b':' + NAME + b'ee'
    monkeypatch.setattr('optimizer.test_workload.fetch_metadata', lambda workload: raw)
    client = Mock()
    with pytest.raises(ClientError, match='size changed'):
        add_workload(client, 'E:/test', tmp_path / 'record.json', UBUNTU)
    client.request.assert_not_called()


def test_restart_keeps_same_large_image(monkeypatch, tmp_path):
    from optimizer.test_workload import restart_workload
    from optimizer.workload_catalog import KALI
    record = {'hash': 'a', 'save_path': 'E:/test', 'workload_id': 'kali', 'source': KALI.url}
    client = Mock()
    client.torrents.return_value = []
    added = Mock(return_value=record)
    monkeypatch.setattr('optimizer.test_workload.add_workload', added)
    monkeypatch.setattr('optimizer.test_workload.wait_for_traffic', lambda *args: None)
    cancel = Mock()
    cancel.is_set.return_value = False
    restart_workload(client, record, tmp_path / 'record.json', cancel, Mock())
    assert added.call_args.args[-1] == KALI


def test_metadata_follows_https_mirror_and_checks_pinned_hash(monkeypatch):
    from dataclasses import replace
    from optimizer.test_workload import fetch_metadata
    from optimizer.workload_catalog import KALI
    raw = b'd4:infod6:lengthi123e4:name' + str(len(KALI.name)).encode() + b':' + KALI.name + b'ee'
    fixture = replace(KALI, info_hash=metadata_info(raw, KALI.name)[0])
    redirect = Mock(status_code=302, headers={'Location': 'https://official-mirror.example/kali/test.torrent'})
    final = Mock(status_code=200)
    final.iter_content.return_value = [raw]
    session = Mock()
    session.get.side_effect = [redirect, final]
    context = Mock()
    context.__enter__ = Mock(return_value=session)
    context.__exit__ = Mock(return_value=False)
    monkeypatch.setattr('optimizer.test_workload.requests.Session', lambda: context)
    assert fetch_metadata(fixture) == raw
    assert session.get.call_count == 2
    assert session.get.call_args.args[0].startswith('https://official-mirror.example/')
    assert session.get.call_args.kwargs['allow_redirects'] is False
    redirect.close.assert_called_once()


def test_metadata_blocks_https_downgrade_before_request(monkeypatch):
    from optimizer.test_workload import fetch_metadata
    redirect = Mock(status_code=302, headers={'Location': 'http://mirror.example/test.torrent'})
    session = Mock()
    session.get.return_value = redirect
    context = Mock()
    context.__enter__ = Mock(return_value=session)
    context.__exit__ = Mock(return_value=False)
    monkeypatch.setattr('optimizer.test_workload.requests.Session', lambda: context)
    with pytest.raises(ClientError, match='HTTPS'):
        fetch_metadata()
    assert session.get.call_count == 1


def test_metadata_rejects_different_torrent_hash(monkeypatch):
    from optimizer.test_workload import fetch_metadata
    from optimizer.workload_catalog import KALI
    raw = b'd4:infod6:lengthi123e4:name' + str(len(KALI.name)).encode() + b':' + KALI.name + b'ee'
    response = Mock(status_code=200)
    response.iter_content.return_value = [raw]
    session = Mock()
    session.get.return_value = response
    context = Mock()
    context.__enter__ = Mock(return_value=session)
    context.__exit__ = Mock(return_value=False)
    monkeypatch.setattr('optimizer.test_workload.requests.Session', lambda: context)
    with pytest.raises(ClientError, match='hash does not match'):
        fetch_metadata(KALI)


def test_metadata_redirect_loop_is_bounded(monkeypatch):
    from optimizer.test_workload import fetch_metadata
    redirect = Mock(status_code=302, headers={'Location': 'https://mirror.example/loop'})
    session = Mock()
    session.get.return_value = redirect
    context = Mock()
    context.__enter__ = Mock(return_value=session)
    context.__exit__ = Mock(return_value=False)
    monkeypatch.setattr('optimizer.test_workload.requests.Session', lambda: context)
    with pytest.raises(ClientError, match='too many'):
        fetch_metadata()
    assert session.get.call_count == 6
