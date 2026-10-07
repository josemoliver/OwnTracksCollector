"""Foursquare / Swarm check-in history export (checkins.json): one point per check-in at its venue."""

import json

from .base import InputFormat, Point, Waypoint, first_char, register, valid_coords

# Value stored in raw_payload["_source"] for every point from this format.
SOURCE = "foursquare"


@register
class FoursquareFormat(InputFormat):
    name = "foursquare"
    # Shares ".json" with Timeline; detect_format() tells them apart with sniff().
    extensions = (".json",)
    default_tracker_id = "fsq"

    @classmethod
    def sniff(cls, path):
        # The check-in list is a JSON array (Timeline.json is an object).
        return first_char(path) == "["

    def load(self, path):
        with open(path, encoding="utf-8") as f:
            data = json.load(f)
        # Some exports wrap the list as {"items": [...]}; accept both shapes.
        if isinstance(data, dict):
            data = data.get("items")
        if not isinstance(data, list):
            raise ValueError("unexpected Foursquare export structure (expected a list of check-ins)")
        return data

    @staticmethod
    def _position(checkin):
        """Return (ts, lat, lon, venue) for a check-in, or None if it lacks a usable time or venue position."""
        try:
            venue = checkin["venue"]
            loc = venue["location"]
            ts, lat, lon = int(checkin["createdAt"]), float(loc["lat"]), float(loc["lng"])
        except (KeyError, ValueError, TypeError):
            return None
        return (ts, lat, lon, venue) if valid_coords(lat, lon) else None

    def points(self, data, stats):
        stats.read.update({"check-ins": 0})
        for checkin in data:
            stats.read["check-ins"] += 1
            pos = self._position(checkin) if isinstance(checkin, dict) else None
            if pos is None:
                stats.invalid += 1
                continue
            ts, lat, lon, venue = pos
            # Venue coordinates are not a GPS fix, so no accuracy/altitude/speed is recorded.
            yield Point(ts, lat, lon, extra={"_source": SOURCE, "_venue": venue.get("name")})

    def waypoints(self, data):
        # Each venue becomes a place; the core collapses repeats of the same name and coordinates.
        for checkin in data:
            pos = self._position(checkin) if isinstance(checkin, dict) else None
            if pos is not None and pos[3].get("name"):
                yield Waypoint(pos[3]["name"], pos[1], pos[2], pos[0])
