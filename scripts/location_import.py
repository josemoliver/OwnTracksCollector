#!/usr/bin/env python3
"""Import location files (Google Timeline JSON, GPX, ...) into an OwnTracks SQLite database.

Input formats live in the formats/ package; this file knows nothing about any specific format.
"""

import argparse
import json
import sqlite3
import sys
import time
from datetime import datetime, timezone
from pathlib import Path

from formats import FORMATS, ImportStats, detect_format, iso_datetime

# OwnTracks regions need a radius; Timeline/GPX places have none, so use a fixed 50 m.
WAYPOINT_RADIUS = 50

# battery, trigger and connection are device-reported OwnTracks fields that imported
# files cannot know, so they are written as NULL. created_at uses the table default.
INSERT_LOCATION = (
    "INSERT INTO locations (user, device, latitude, longitude, timestamp, accuracy, "
    "altitude, velocity, battery, tracker_id, trigger, connection, raw_payload) "
    "VALUES (?, ?, ?, ?, ?, ?, ?, ?, NULL, ?, NULL, NULL, ?)"
)
INSERT_WAYPOINT = (
    "INSERT INTO waypoints (user, device, description, latitude, longitude, radius, "
    "timestamp, raw_payload) VALUES (?, ?, ?, ?, ?, ?, ?, ?)"
)


def log(msg, quiet=False):
    """Progress messages go to stderr so stdout stays clean for the summary."""
    if not quiet:
        print(msg, file=sys.stderr, flush=True)


def non_negative_int(value):
    """argparse type: an integer >= 0."""
    try:
        n = int(value)
    except ValueError:
        raise argparse.ArgumentTypeError(f"not an integer: {value!r}")
    if n < 0:
        raise argparse.ArgumentTypeError("must be >= 0")
    return n


def positive_int(value):
    """argparse type: an integer > 0."""
    n = non_negative_int(value)
    if n == 0:
        raise argparse.ArgumentTypeError("must be > 0")
    return n


def fmt_day(ts):
    """Epoch seconds -> 'YYYY-MM-DD' in UTC (used in the summary's date span)."""
    return datetime.fromtimestamp(ts, timezone.utc).strftime("%Y-%m-%d")


def compact_json(obj):
    """Serialize without spaces and without None values (unknown fields are simply omitted)."""
    obj = {k: v for k, v in obj.items() if v is not None}
    # ensure_ascii=False keeps accented characters readable; separators remove padding spaces.
    return json.dumps(obj, ensure_ascii=False, separators=(",", ":"))


def make_payload(point, tracker_id):
    """OwnTracks-style JSON for a point; point.extra carries format-specific keys."""
    return compact_json({
        "_type": "location",  # OwnTracks message type
        "lat": point.lat,
        "lon": point.lon,
        "tst": point.ts,  # OwnTracks name for the timestamp
        "acc": point.acc,
        "alt": point.alt,
        "vel": point.vel,
        "tid": tracker_id,
        **(point.extra or {}),  # e.g. _source, _signal, _track; marks the row as imported
    })


def make_waypoint_payload(wp, ts):
    """OwnTracks-style JSON for a waypoint (region)."""
    return compact_json({"_type": "waypoint", "desc": wp.desc, "lat": wp.lat, "lon": wp.lon,
                         "rad": WAYPOINT_RADIUS, "tst": ts})


def build_parser():
    p = argparse.ArgumentParser(description="Import location files into an OwnTracks SQLite database.")
    p.add_argument("input_file", help="path to the input file (e.g. Timeline.json, track.gpx)")
    p.add_argument("db_path", help="path to owntracks.sqlite (must already exist)")
    # choices come from the registry, so a newly added format shows up here automatically.
    p.add_argument("--format", choices=sorted(FORMATS), help="input format; default: detected from the file extension")
    # No defaults for user/device: the same database can hold several people and devices,
    # and silently importing under the wrong identity would be hard to undo.
    p.add_argument("--user", required=True, help="value for the 'user' column")
    p.add_argument("--device", required=True, help="value for the 'device' column")
    p.add_argument("--tracker-id", help="tracker_id for imported rows (default depends on the format)")
    p.add_argument("--since", type=iso_datetime, help="only import points at/after this ISO-8601 time (naive = UTC)")
    p.add_argument("--until", type=iso_datetime, help="only import points at/before this ISO-8601 time (naive = UTC)")
    p.add_argument("--min-interval", type=non_negative_int, default=0, metavar="SECONDS",
                   help="keep a point only if at least SECONDS passed since the last kept point")
    p.add_argument("--waypoints", action="store_true", help="also import waypoints/places from the input")
    p.add_argument("--dry-run", action="store_true", help="report only; do not write to the database")
    p.add_argument("--batch-size", type=positive_int, default=5000, help="rows per commit (default: 5000)")
    p.add_argument("-v", "--verbose", action="store_true", help="more progress output")
    p.add_argument("-q", "--quiet", action="store_true", help="suppress progress output")
    # Each format that has its own options gets a separate section in --help.
    for name, cls in sorted(FORMATS.items()):
        if cls.option_dests:
            cls.add_arguments(p.add_argument_group(f"{name} format options"))
    return p


def check_format_options(args, format_name, parser):
    """Reject options that belong to a different format than the one in use."""
    for name, cls in FORMATS.items():
        if name == format_name:
            continue
        for dest in cls.option_dests:
            # Format options default to None, so any other value means the user typed the flag.
            if getattr(args, dest, None) is not None:
                parser.error(f"--{dest.replace('_', '-')} applies to the {name} format only")


def collect_points(fmt, data, args, existing_ts, stats):
    """Apply filters, de-duplication and thinning; return the points to insert, in time order."""
    seen = set()  # (ts, lat, lon) of every point already considered in this run
    kept = []
    for point in fmt.points(data, stats):
        # 1. User-requested time window.
        if (args.since is not None and point.ts < args.since) or (args.until is not None and point.ts > args.until):
            stats.skipped["outside --since/--until"] += 1
            continue
        # 2. Exact repeats inside the input (e.g. Timeline paths often repeat a point twice).
        #    Points sharing only a timestamp but not coordinates are different and are kept.
        key = (point.ts, point.lat, point.lon)
        if key in seen:
            stats.skipped["duplicate-in-input"] += 1
            continue
        seen.add(key)
        # 3. Filter specific to the format (e.g. Timeline path points covered by raw positions).
        reason = fmt.accept(point, stats)
        if reason:
            stats.skipped[reason] += 1
            continue
        # 4. Already in the database for this user/device: protects live OwnTracks data and
        #    makes re-running the same import a no-op.
        if point.ts in existing_ts:
            stats.skipped["already-in-db"] += 1
            continue
        kept.append(point)

    # Input order is not guaranteed (GPX tracks can be out of order), and thinning below
    # needs chronological order.
    kept.sort(key=lambda p: p.ts)
    if args.min_interval > 0:
        thinned, last = [], None
        for p in kept:
            # Keep the first point, then only points far enough from the last *kept* one.
            if last is None or p.ts - last >= args.min_interval:
                thinned.append(p)
                last = p.ts
            else:
                stats.skipped["thinned"] += 1
        kept = thinned
    return kept


def collect_waypoints(fmt, data, args, conn):
    """Return waypoint rows not already present."""
    # Existing waypoints for this user/device, keyed the same way as new ones, to avoid duplicates.
    existing = {
        (r[0], r[1], r[2])
        for r in conn.execute(
            "SELECT description, latitude, longitude FROM waypoints WHERE user=? AND device=?",
            (args.user, args.device),
        )
    }
    now = int(time.time())
    rows = []
    for wp in fmt.waypoints(data):
        key = (wp.desc, wp.lat, wp.lon)
        if key in existing:
            continue
        existing.add(key)  # also collapses duplicates within the input itself
        ts = now if wp.ts is None else wp.ts  # files without a time get the import time
        rows.append((args.user, args.device, wp.desc, wp.lat, wp.lon, WAYPOINT_RADIUS, ts,
                     make_waypoint_payload(wp, ts)))
    return rows


def open_db(path, read_only):
    """Open an existing OwnTracks database; never creates a file or changes the schema."""
    db = Path(path)
    # sqlite3.connect would silently create an empty database for a wrong path.
    if not db.is_file():
        raise FileNotFoundError(f"database not found: {path}")
    # A file: URI lets us pick the mode. "ro" guarantees --dry-run cannot modify anything;
    # "rw" (unlike the default "rwc") refuses to create the file.
    mode = "ro" if read_only else "rw"
    conn = sqlite3.connect(f"{db.resolve().as_uri()}?mode={mode}", uri=True)
    tables = {r[0] for r in conn.execute("SELECT name FROM sqlite_master WHERE type='table'")}
    if "locations" not in tables:
        conn.close()
        raise sqlite3.DatabaseError("database is missing table: locations")
    return conn


def insert_batches(conn, sql, rows, batch_size, label, quiet):
    """Insert rows in batches, one transaction per batch, so memory and journal stay small."""
    total = len(rows)
    for start in range(0, total, batch_size):
        batch = rows[start : start + batch_size]
        try:
            # "with conn" commits on success and rolls back on an exception.
            with conn:
                conn.executemany(sql, batch)
        except sqlite3.Error:
            conn.rollback()  # explicit for clarity; earlier committed batches remain
            raise
        log(f"  {label}: {min(start + batch_size, total):,}/{total:,}", quiet)


def print_summary(args, fmt, stats, points, waypoint_rows):
    """Print what was read, what was skipped (and why) and what was (or would be) inserted."""
    verb = "Would insert" if args.dry_run else "Inserted"
    span = f" ({fmt_day(points[0].ts)} -> {fmt_day(points[-1].ts)})" if points else ""
    # Fixed order mirrors the pipeline; the format's own reasons slot in after its position.
    reasons = ["outside --since/--until", "duplicate-in-input", *fmt.skip_reasons, "already-in-db", "thinned"]
    # Any reason a format used but did not declare is still reported rather than lost.
    reasons += [r for r in stats.skipped if r not in reasons]
    # Counter returns 0 for missing keys, so every listed reason prints a number.
    skipped = [f"{stats.invalid:,} invalid"] + [f"{stats.skipped[r]:,} {r}" for r in reasons]
    print("Read:      " + ", ".join(f"{n:,} {label}" for label, n in stats.read.items()))
    print("Skipped:   " + ", ".join(skipped))
    print(f"{verb + ':':<13} {len(points):,} locations{span}")
    if args.waypoints:
        print(f"{verb + ':':<13} {len(waypoint_rows):,} waypoints")


def main(argv=None):
    parser = build_parser()
    args = parser.parse_args(argv)  # exits with code 2 on bad arguments
    quiet = args.quiet

    # Choose the format: explicit --format wins, otherwise guess from the file extension.
    format_name = args.format or detect_format(args.input_file)
    if format_name is None:
        parser.error(f"cannot detect format from {args.input_file!r}; use --format {{{','.join(sorted(FORMATS))}}}")
    check_format_options(args, format_name, parser)
    fmt = FORMATS[format_name]()  # one fresh instance per run (formats may keep per-run state)
    fmt.configure(args, parser)
    tracker_id = args.tracker_id or fmt.default_tracker_id

    stats = ImportStats()
    points, waypoint_rows = [], []

    try:
        log(f"Loading {args.input_file} ({format_name}) ...", quiet)
        data = fmt.load(args.input_file)

        # --dry-run opens the database read-only, which makes "writes nothing" a hard guarantee.
        conn = open_db(args.db_path, read_only=args.dry_run)
        try:
            # Timestamps already stored for this user/device, used to skip points that exist.
            existing_ts = {
                r[0]
                for r in conn.execute(
                    "SELECT timestamp FROM locations WHERE user=? AND device=?", (args.user, args.device)
                )
            }
            log(f"Existing rows for {args.user}/{args.device}: {len(existing_ts):,} distinct timestamps", quiet)

            points = collect_points(fmt, data, args, existing_ts, stats)
            if args.waypoints:
                if conn.execute("SELECT 1 FROM sqlite_master WHERE name='waypoints'").fetchone() is None:
                    raise sqlite3.DatabaseError("database is missing table: waypoints")
                waypoint_rows = collect_waypoints(fmt, data, args, conn)

            if not args.dry_run:
                if points:
                    log("Reminder: back up the database before running against real data.", quiet)
                    # Column order here must match INSERT_LOCATION's placeholders.
                    rows = [
                        (args.user, args.device, p.lat, p.lon, p.ts, p.acc, p.alt, p.vel,
                         tracker_id, make_payload(p, tracker_id))
                        for p in points
                    ]
                    insert_batches(conn, INSERT_LOCATION, rows, args.batch_size, "locations", quiet)
                if waypoint_rows:
                    insert_batches(conn, INSERT_WAYPOINT, waypoint_rows, args.batch_size, "waypoints", quiet)
        finally:
            conn.close()  # always release the database, even after an error
    except (OSError, ValueError, sqlite3.Error) as exc:
        # Expected failures (missing/corrupt file, bad database): one clean line and exit code 1.
        print(f"error: {exc}", file=sys.stderr)
        return 1

    print_summary(args, fmt, stats, points, waypoint_rows)
    return 0


if __name__ == "__main__":
    sys.exit(main())
