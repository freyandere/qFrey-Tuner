"""Ideal-rate workload sizing and image boundary validation."""
import math
import pytest
from optimizer.workload_catalog import UBUNTU, KALI, REQUIRED_SECONDS, workload_recommendation


@pytest.mark.parametrize('speed,key', [(100, 'ubuntu'), (400, 'ubuntu'), (500, 'kali'), (1000, 'kali'), (2500, None)])
def test_smallest_sufficient_official_image(speed, key):
    choice = workload_recommendation(speed)
    assert (choice['workload'].key if choice['workload'] else None) == key
    assert choice['required_bytes'] == math.ceil(speed * 1e6 / 8 * 90)
    if choice['workload']:
        assert choice['workload'].size_bytes >= choice['required_bytes']


@pytest.mark.parametrize('image,next_key', [(UBUNTU, 'kali'), (KALI, None)])
def test_size_switch_boundary(image, next_key):
    boundary = image.size_bytes * 8 / 1e6 / REQUIRED_SECONDS
    below = workload_recommendation(boundary - .000001)
    above = workload_recommendation(boundary + .000001)
    assert below['workload'] == image
    assert (above['workload'].key if above['workload'] else None) == next_key


@pytest.mark.parametrize('speed', [0, -1, float('nan'), float('inf')])
def test_invalid_speed_does_not_choose_a_download(speed):
    with pytest.raises(ValueError):
        workload_recommendation(speed)


def test_mbps_is_decimal_bits_not_megabytes():
    choice = workload_recommendation(1000)
    assert choice['required_bytes'] == 11_250_000_000
    assert choice['ubuntu_seconds'] == pytest.approx(38.101663744)
