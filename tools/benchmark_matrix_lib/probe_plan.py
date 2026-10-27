"""What a start launches KSP with (the probe's arguments) and how long it is expected to take."""
from .constants import (PROBE_FLIGHT_SECONDS, PROBE_SET_SECONDS, PROFILE_START_SECONDS, SHOT,
                        START_OVERHEAD_SECONDS)


def ini_sets_argument(phases):
    """-redefinitionProbeShotIni's value: the sets in order, each with every key the
    matrix varies, so that no set inherits a value from the one before it."""
    return ";".join(",".join("{}={}".format(k, v) for k, v in p["keys"].items()) for p in phases)


def probe_arguments(start):
    """KSP_x64.exe's arguments for one start: the probe, the scene's, and every set of keys."""
    return (["-redefinitionProbe", "-redefinitionProbeStay", "-redefinitionProbeClouds"] + list(start.get("probe_args") or [])
            + ["-redefinitionProbeShot", SHOT, "-redefinitionProbeShotVariant", "none",
               "-redefinitionProbeShotIni", ini_sets_argument(start["phases"])])


def command_length(start):
    """The characters of the command line a start launches KSP with (Windows takes 32767)."""
    return len(" ".join(["KSP_x64.exe"] + probe_arguments(start)))


def start_seconds(start):
    """The seconds a start is expected to take: the launch and quit, the flight's wait and each phase's set."""
    return START_OVERHEAD_SECONDS + PROBE_FLIGHT_SECONDS + PROBE_SET_SECONDS * len(start["phases"])


def estimate_seconds(starts, with_profile=True):
    """The seconds the starts are expected to take, with the profile's own start before them where asked."""
    return (PROFILE_START_SECONDS if with_profile else 0.0) + sum(start_seconds(s) for s in starts)
