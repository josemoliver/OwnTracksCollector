"""GPX tracks (<trk>/<trkseg>/<trkpt>) and waypoints (<wpt>); GPX 1.0, 1.1 or no namespace."""

import xml.etree.ElementTree as ET

from .base import InputFormat, Point, Waypoint, register, to_epoch, valid_coords


def _local(tag):
    """Element name without its XML namespace.

    ElementTree reports namespaced tags as '{http://www.topografix.com/GPX/1/0}trkpt'.
    Stripping the '{...}' prefix lets one code path handle GPX 1.0, GPX 1.1 and files
    that declare no namespace at all.
    """
    return tag.rsplit("}", 1)[-1]


def _child_text(el, name):
    """Stripped text of the first direct child called `name`, or None if there is none."""
    for c in el:
        if _local(c.tag) == name:
            return (c.text or "").strip()  # text is None for an empty element like <ele/>
    return None


def _float(text):
    """Parse a number, returning None for missing or malformed values instead of raising."""
    try:
        return float(text) if text not in (None, "") else None
    except ValueError:
        return None


def _coords(el):
    """(lat, lon) from the lat/lon attributes of a <trkpt>/<wpt>, or None if absent/invalid."""
    try:
        lat, lon = float(el.get("lat")), float(el.get("lon"))
    except (TypeError, ValueError):
        # TypeError: attribute missing (get returned None); ValueError: not a number.
        return None
    return (lat, lon) if valid_coords(lat, lon) else None


@register
class GpxFormat(InputFormat):
    name = "gpx"
    extensions = (".gpx",)
    default_tracker_id = "gpx"

    def load(self, path):
        try:
            root = ET.parse(path).getroot()
        except ET.ParseError as exc:
            # The core only catches OSError/ValueError, so report malformed XML as a ValueError.
            raise ValueError(str(exc)) from exc
        # Catch the common mistake of pointing the tool at some other XML file.
        if _local(root.tag) != "gpx":
            raise ValueError("not a GPX file (root element is not <gpx>)")
        return root

    def points(self, root, stats):
        # Create the label up front so the "Read:" line shows "0 track points" for an empty file.
        stats.read["track points"] += 0
        # root.iter() walks the whole tree; matching on the local name keeps this namespace-agnostic.
        for trk in root.iter():
            if _local(trk.tag) != "trk":
                continue
            # The track name (e.g. "Madrid") is kept in raw_payload so points can be traced to a track.
            name = _child_text(trk, "name")
            extra = {"_source": "gpx", "_track": name} if name else {"_source": "gpx"}
            # A track holds one or more segments; a new segment starts after a signal gap.
            for seg in trk:
                if _local(seg.tag) != "trkseg":
                    continue
                for pt in seg:
                    if _local(pt.tag) != "trkpt":
                        continue
                    stats.read["track points"] += 1
                    ll = _coords(pt)
                    try:
                        # <time> is required: without a timestamp the point cannot be stored.
                        ts = to_epoch(_child_text(pt, "time"))
                    except (ValueError, TypeError):
                        # ValueError: unparsable time; TypeError: <time> element missing (None).
                        ts = None
                    if ll is None or ts is None:
                        stats.invalid += 1
                        continue
                    speed = _float(_child_text(pt, "speed"))  # GPX 1.0 speed is m/s
                    # OwnTracks stores velocity in km/h as an integer.
                    vel = round(speed * 3.6) if speed is not None else None
                    # GPX has no accuracy field, so acc stays None. <ele> becomes altitude.
                    yield Point(ts, ll[0], ll[1], alt=_float(_child_text(pt, "ele")), vel=vel, extra=extra)

    def waypoints(self, root):
        for el in root.iter():
            if _local(el.tag) != "wpt":
                continue
            ll = _coords(el)
            desc = _child_text(el, "name")
            # A waypoint needs valid coordinates and a name to be useful as a region.
            if ll is None or not desc:
                continue
            try:
                ts = to_epoch(_child_text(el, "time"))
            except (ValueError, TypeError):
                ts = None  # no time in the file: the core substitutes the import time
            yield Waypoint(desc, ll[0], ll[1], ts)
