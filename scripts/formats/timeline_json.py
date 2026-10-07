"""Google Timeline on-device export (Timeline.json): rawSignals positions + timelinePath points."""

import json

from .base import InputFormat, Point, Waypoint, first_char, parse_latlng, register, to_epoch

# Value stored in raw_payload["_source"] for every point from this format.
SOURCE = "google-timeline"


@register
class TimelineJsonFormat(InputFormat):
    name = "google"
    extensions = (".json",)
    default_tracker_id = "gt"
    option_dests = ("sources",)  # lets the core reject --sources for other formats
    skip_reasons = ("covered-by-raw",)  # the one reason accept() can return

    @classmethod
    def sniff(cls, path):
        return first_char(path) == "{"

    def __init__(self):
        # Which parts of the file to read; replaced by configure() if --sources is given.
        self.sources = {"raw", "path"}
        # (earliest, latest) timestamp of the raw positions, set while reading. accept() uses it
        # to discard path points that raw positions already cover.
        self.raw_range = None

    @classmethod
    def add_arguments(cls, group):
        # No default on purpose: None means "not given", which the core relies on to
        # detect this option being used with a different format.
        group.add_argument(
            "--sources",
            help="comma list of: raw (rawSignals positions), path (timelinePath points); default raw,path",
        )

    def configure(self, args, parser):
        if args.sources is not None:
            # Accept "raw", "path" or "raw,path" (spaces tolerated, order irrelevant).
            sources = {s.strip() for s in args.sources.split(",") if s.strip()}
            if not sources or not sources <= {"raw", "path"}:
                parser.error("--sources must be a comma list of: raw, path")
            self.sources = sources

    def load(self, path):
        # The export is UTF-8; the degree sign in coordinate strings must not be mangled.
        with open(path, encoding="utf-8") as f:
            data = json.load(f)
        if not isinstance(data, dict):
            raise ValueError("unexpected Timeline.json structure (top level is not an object)")
        return data

    def _raw(self, data, stats):
        """Yield Points from rawSignals[].position entries (dense, with accuracy/altitude/speed)."""
        for entry in data.get("rawSignals", []):
            # rawSignals mixes position, wifiScan and activityRecord entries; only positions matter.
            pos = entry.get("position")
            if pos is None:
                continue
            stats.read["raw positions"] += 1
            try:
                # Note the key is "LatLng" (capital L) here but "latLng" in semanticSegments.
                ll = parse_latlng(pos["LatLng"])
                ts = to_epoch(pos["timestamp"])
            except (KeyError, ValueError, TypeError):
                ll = None
            if ll is None:
                stats.invalid += 1
                continue
            # Speed and altitude are missing from some entries, hence the type check.
            speed = pos.get("speedMetersPerSecond")
            vel = round(speed * 3.6) if isinstance(speed, (int, float)) else None
            # "_signal" records how Google obtained the fix: GPS, WIFI, CELL, ...
            extra = {"_source": SOURCE, "_signal": pos.get("source") or "UNKNOWN"}
            yield Point(ts, ll[0], ll[1], pos.get("accuracyMeters"), pos.get("altitudeMeters"), vel, extra)

    def _path(self, data, stats):
        """Yield Points from semanticSegments[].timelinePath (coarse, minute resolution, no accuracy)."""
        # Same extra dict for every path point; "path" distinguishes them from raw signals.
        extra = {"_source": SOURCE, "_signal": "path"}
        for seg in data.get("semanticSegments", []):
            # Only some segments carry a timelinePath; others are visits, activities or trip memories.
            for pt in seg.get("timelinePath", ()):
                stats.read["path points"] += 1
                try:
                    ll = parse_latlng(pt["point"])
                    ts = to_epoch(pt["time"])
                except (KeyError, ValueError, TypeError):
                    ll = None
                if ll is None:
                    stats.invalid += 1
                    continue
                yield Point(ts, ll[0], ll[1], extra=extra)

    def points(self, data, stats):
        # Register both labels first so the summary always shows both, even when --sources
        # disables one of them (it then reads "0 raw positions").
        stats.read.update({"raw positions": 0, "path points": 0})
        # Raw positions are read completely first because accept() needs their time range
        # before the first path point is judged.
        raw_points = list(self._raw(data, stats)) if "raw" in self.sources else []
        if raw_points:
            stamps = [p.ts for p in raw_points]
            self.raw_range = (min(stamps), max(stamps))
        yield from raw_points
        if "path" in self.sources:
            yield from self._path(data, stats)

    def accept(self, point, stats):
        # Raw positions are denser and carry accuracy, so path points inside their range are redundant.
        if point.extra["_signal"] == "path" and self.raw_range and self.raw_range[0] <= point.ts <= self.raw_range[1]:
            return "covered-by-raw"
        return None

    def waypoints(self, data):
        # frequentPlaces lists the user's recurring places; HOME and WORK carry a label,
        # the others only a Google placeId.
        for place in (data.get("userLocationProfile") or {}).get("frequentPlaces") or []:
            ll = parse_latlng(place.get("placeLocation", ""))
            desc = place.get("label") or place.get("placeId")
            if ll is not None and desc:
                yield Waypoint(desc, ll[0], ll[1])  # no time in the file: the core uses the import time
