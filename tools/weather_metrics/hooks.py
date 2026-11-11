"""Figures of package C24 (the depth hook and the history reset), for the wall test and the forced-cut test once they run.

Pure functions; no series is registered here because the scenes that would feed them do not exist yet (the wall test needs the
rig's after-upscaling depth hook, which the weather player does not run).

  hidden_pixels_expected(wall, band)    the pixels of a band rectangle that lie behind a wall rectangle: the geometric expectation
  relative_error(measured, expected)    |measured - expected| / expected, the figure of C24.wall_hidden_pixels_error (limit 0.02)
  frames_to_clear(errors, floor)        frames after a cut until the temporal error is at or under floor (limit 3)

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


if __name__ == "__main__":
    assert hidden_pixels_expected((100, 0, 200, 100), (50, 40, 150, 60)) == 50 * 20
    assert hidden_pixels_expected((0, 0, 10, 10), (20, 20, 30, 30)) == 0
    assert relative_error(1020, 1000) == 0.02
    assert relative_error(0, 0) == 0.0
    assert frames_to_clear([0.4, 0.2, 0.05, 0.01], 0.05) == 2
    assert frames_to_clear([0.4, 0.3], 0.05) is None
    print("hooks.py: self-test passed")
