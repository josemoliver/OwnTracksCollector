"""Input formats. Every module in this package is imported automatically and registers itself.

To add a format: create formats/<name>.py containing an InputFormat subclass decorated with
@register (see base.py for the contract and gpx.py for a small example). No other file changes.
"""

import importlib
import pkgutil
from pathlib import Path

from .base import FORMATS, ImportStats, InputFormat, Point, Waypoint, iso_datetime, register

# Import every sibling module so its @register decorator runs and fills FORMATS.
# Sorting keeps the registration order (and therefore extension detection) deterministic.
# base.py is skipped because it is the shared contract, not a format.
for _mod in sorted(m.name for m in pkgutil.iter_modules(__path__)):
    if _mod != "base":
        importlib.import_module(f"{__name__}.{_mod}")


def detect_format(path):
    """Return the name of the format whose extension matches `path`, or None if unknown."""
    suffix = Path(path).suffix.lower()  # lowercase so "TRACK.GPX" matches ".gpx"
    candidates = [(name, cls) for name, cls in FORMATS.items() if suffix in cls.extensions]
    if len(candidates) > 1:
        # Several formats share this extension (.json): let the file's content decide.
        for name, cls in candidates:
            if cls.sniff(path):
                return name
    return candidates[0][0] if candidates else None


# Names the core importer is allowed to import from this package.
__all__ = ["FORMATS", "ImportStats", "InputFormat", "Point", "Waypoint", "detect_format", "iso_datetime", "register"]
