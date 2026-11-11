"""Figures of package C24 (the depth hook and the history reset), for the wall test and the forced-cut test once they run.

Pure functions; no series is registered here because the scenes that would feed them do not exist yet (the wall test needs the
rig's after-upscaling depth hook, which the weather player does not run).

  hidden_pixels_expected(wall, band)    the pixels of a band rectangle that lie behind a wall rectangle: the geometric expectation
  relative_error(measured, expected)    |measured - expected| / expected, the figure of C24.wall_hidden_pixels_error (limit 0.02)
  frames_to_clear(errors, floor)        frames after a cut until the temporal error is at or under floor (limit 3)
  near_is_larger(near, far)             1 where the near ball's depth value is the larger (reversed Z), else 0: C24.depth_polarity_near_large
  segment_share(segment_ms, plain_ms)   a phase's raytracing segment as a share of the plain phase's: the rt_blizzard_*_segment_share rows
  mean_abs_difference(a, b)             the mean absolute difference of two equally long value lists: C24.rt_blizzard_image_difference

Rectangles are (x0, y0, x1, y1) in pixels, x1 and y1 exclusive.
"""


def _overlap(a0, a1, b0, b1):
    return max(0, min(a1, b1) - max(a0, b0))


def hidden_pixels_expected(wall, band):
    """Pixels of `band` inside `wall`, both (x0, y0, x1, y1): what a depth-tested band must lose behind a wall in front of it."""
    return _overlap(wall[0], wall[2], band[0], band[2]) * _overlap(wall[1], wall[3], band[1], band[3])


def relative_error(measured, expected):
    """|measured - expected| / expected; infinity where nothing was expected but something was measured, 0 where both are 0."""
    if expected == 0:
        return 0.0 if measured == 0 else float("inf")
    return abs(measured - expected) / float(expected)


def frames_to_clear(errors, floor):
    """Frames after the cut (errors[0] is the cut frame's error) until the error is at or below floor; None where it never is."""
    for index, value in enumerate(errors):
        if value <= floor:
            return index
    return None


def near_is_larger(near, far):
    """1 where the depth read back at the near object is larger than at the far one (reversed Z, the text of the API), else 0."""
    return 1 if near > far else 0


def segment_share(segment_ms, plain_ms):
    """A phase's raytracing segment over the plain phase's; None where the plain one is not a positive number."""
    if plain_ms is None or not plain_ms > 0:
        return None
    return segment_ms / float(plain_ms)


def mean_abs_difference(a, b):
    """The mean of |a[i] - b[i]|; None for lists of unequal or no length."""
    if len(a) != len(b) or not a:
        return None
    return sum(abs(x - y) for x, y in zip(a, b)) / float(len(a))


if __name__ == "__main__":
    assert hidden_pixels_expected((100, 0, 200, 100), (50, 40, 150, 60)) == 50 * 20
    assert hidden_pixels_expected((0, 0, 10, 10), (20, 20, 30, 30)) == 0
    assert relative_error(1020, 1000) == 0.02
    assert relative_error(0, 0) == 0.0
    assert frames_to_clear([0.4, 0.2, 0.05, 0.01], 0.05) == 2
    assert frames_to_clear([0.4, 0.3], 0.05) is None
    assert near_is_larger(0.07, 0.002) == 1 and near_is_larger(0.2, 0.9) == 0
    assert segment_share(3.0, 6.0) == 0.5 and segment_share(1.0, 0.0) is None
    assert mean_abs_difference([1, 2, 3], [1, 4, 0]) == 5 / 3.0 and mean_abs_difference([], []) is None
    print("hooks.py: self-test passed")
