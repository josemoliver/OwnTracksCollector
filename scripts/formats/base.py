"""Contract and shared helpers for input formats.

Every input format (Timeline JSON, GPX, ...) is a subclass of InputFormat that turns a
file into a stream of Point objects. The core importer (location_import.py) handles
everything after that: filtering, de-duplication, thinning and database writes.
"""

import argparse
import re
from collections import Counter
from dataclasses import dataclass, field
from datetime import datetime, timezone
from typing import Iterator, NamedTuple, Optional

# Matches Google Timeline coordinate strings such as "18.454432°, -66.085867°".
# Two signed decimal numbers separated by a comma; the degree sign is optional so
# plain "18.45, -66.08" also works. Capture group 1 is latitude, group 2 is longitude.
COORD_RE = re.compile(r"^\s*(-?\d+(?:\.\d+)?)\s*°?\s*,\s*(-?\d+(?:\.\d+)?)\s*°?\s*$")


class Point(NamedTuple):
    """One location. `ts` is UTC epoch seconds; `vel` is km/h; `extra` adds raw_payload keys."""

    ts: int  # when the position was recorded, as UTC epoch seconds (matches locations.timestamp)
    lat: float  # latitude in decimal degrees, -90..90
    lon: float  # longitude in decimal degrees, -180..180
    acc: Optional[float] = None  # horizontal accuracy in metres, None if the source has none
    alt: Optional[float] = None  # altitude in metres, None if unknown
    vel: Optional[int] = None  # speed in km/h (OwnTracks "vel"), None if unknown
    # Extra keys merged into the OwnTracks-style raw_payload JSON, typically
    # {"_source": "<format>"} so imported rows can be traced back to their origin.
    extra: Optional[dict] = None


class Waypoint(NamedTuple):
    """A named place, imported into the waypoints table when --waypoints is given."""

    desc: str  # human-readable name (waypoints.description)
    lat: float
    lon: float
    ts: Optional[int] = None  # UTC epoch seconds; None means "use the time of the import"


@dataclass
class ImportStats:
    """Counters shared between the formats and the core so the final summary can be built."""

    # label -> number of source records read, e.g. {"raw positions": 38230}.
    # Formats choose the labels; they are printed in insertion order on the "Read:" line.
    read: Counter = field(default_factory=Counter)
    # Records a format could not parse (bad coordinates, missing/invalid time, ...).
    invalid: int = 0
    # reason -> number of points dropped by the core or by a format's accept() filter.
    skipped: Counter = field(default_factory=Counter)


class InputFormat:
    """Base class for an input format. Subclass, decorate with @register, drop the file in formats/."""

    name = ""  # value of --format on the command line
    extensions = ()  # lowercase suffixes used for auto-detection, e.g. (".gpx",)
    default_tracker_id = ""  # tracker_id used when --tracker-id is not given
    # argparse dest names of the options this format adds. The core uses this list to
    # reject those options when a different format is selected, so their defaults must be None.
    option_dests = ()
    # Reasons accept() may return. They are always listed in the summary, even when zero.
    skip_reasons = ()

    @classmethod
    def sniff(cls, path):
        """True if the file's content looks like this format. Used only to choose between
        formats that share an extension (e.g. two kinds of .json); default: never."""
        return False

    @classmethod
    def add_arguments(cls, group):
        """Add format-specific options to an argparse group (no-op by default)."""

    def configure(self, args, parser):
        """Validate/store format-specific options; call parser.error() on bad values."""

    def load(self, path):
        """Read the file and return any object that points() understands. Raise OSError/ValueError on failure."""
        raise NotImplementedError

    def points(self, data, stats: ImportStats) -> Iterator[Point]:
        """Yield valid Points. Count records in stats.read[label] and unparsable ones in stats.invalid."""
        raise NotImplementedError

    def accept(self, point: Point, stats: ImportStats) -> Optional[str]:
        """Optional format-specific filter run after generic de-duplication.

        Return a reason from skip_reasons to drop the point, or None to keep it.
        """
        return None

    def waypoints(self, data) -> Iterator[Waypoint]:
        """Optionally yield Waypoints (used with --waypoints). Default: none."""
        return iter(())


# Registry of all known formats, filled by the @register decorator as the modules in
# this package are imported. Maps the format name to its class.
FORMATS = {}


def register(cls):
    """Class decorator that makes a format available under its `name`."""
    # Guard against a missing name or two modules claiming the same --format value.
    if not cls.name or cls.name in FORMATS:
        raise ValueError(f"invalid or duplicate format name: {cls.name!r}")
    FORMATS[cls.name] = cls
    return cls


def to_epoch(text):
    """ISO-8601 (with or without offset; naive is treated as UTC) -> UTC epoch seconds."""
    # Python 3.11+ parses the trailing "Z" and millisecond fractions directly.
    dt = datetime.fromisoformat(text)
    if dt.tzinfo is None:
        # No offset in the text: assume UTC rather than the machine's local zone,
        # so imports are reproducible on any computer.
        dt = dt.replace(tzinfo=timezone.utc)
    # timestamp() converts using the parsed offset; int() drops the fractional seconds.
    return int(dt.timestamp())


def iso_datetime(value):
    """argparse type for ISO-8601 date/times (used by --since / --until)."""
    try:
        return to_epoch(value)
    except ValueError:
        # argparse turns ArgumentTypeError into a clean "bad argument" message and exit code 2.
        raise argparse.ArgumentTypeError(f"not an ISO-8601 date/time: {value!r}")


def first_char(path):
    """First non-whitespace character of a UTF-8 text file ('' if empty or unreadable)."""
    try:
        with open(path, encoding="utf-8-sig") as f:
            while True:
                chunk = f.read(4096)
                if not chunk:
                    return ""
                chunk = chunk.lstrip()
                if chunk:
                    return chunk[0]
    except (OSError, UnicodeDecodeError):
        return ""


def valid_coords(lat, lon):
    """True when the pair is a possible position on Earth."""
    return -90 <= lat <= 90 and -180 <= lon <= 180


def parse_latlng(text):
    """'18.45°, -66.08°' -> (lat, lon), or None if malformed or out of range."""
    m = COORD_RE.match(text)
    if not m:
        return None
    lat, lon = float(m.group(1)), float(m.group(2))
    return (lat, lon) if valid_coords(lat, lon) else None
