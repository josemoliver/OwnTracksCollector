#!/usr/bin/env python3
"""Export locations from an OwnTracks SQLite database to a GPX or CSV file.

Select a date range and optionally filter by user and/or device. The database is opened
read-only and is never modified. To add an output format, write a function with the
signature of write_gpx/write_csv and add it to WRITERS.
"""

import argparse
import csv
import itertools
import os
import sqlite3
import sys
from datetime import datetime, timedelta, timezone
from pathlib import Path
from xml.sax.saxutils import escape

GENERATOR = "location_export"

# Columns exported to CSV, in order. "time" is the ISO-8601 UTC rendering of "timestamp".
CSV_COLUMNS = [
    "user", "device", "time", "timestamp", "latitude", "longitude", "accuracy",
    "altitude", "velocity", "battery", "tracker_id", "trigger", "connection",
]

# Everything the writers need from the locations table. Ordering by user, device and
# time lets the GPX writer build one track per user/device by simply watching for changes.
SELECT_COLUMNS = (
    "user, device, timestamp, latitude, longitude, accuracy, altitude, velocity, "
    "battery, tracker_id, trigger, connection"
)


def iso_utc(ts):
    """Epoch seconds -> '2023-04-17T10:08:48Z'."""
    return datetime.fromtimestamp(ts, timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")


def parse_bound(value, is_until):
    """Parse a --since/--until value into epoch seconds.

    Accepts ISO-8601 dates ('2023-04-10') or date-times ('2023-04-10T08:00:00-04:00').
    A value without an offset is treated as UTC, matching location_import.py.
    Returns an *inclusive* lower bound for --since and an *exclusive* upper bound for
    --until, so a date-only --until includes that entire day.
    """
    # fromisoformat accepts a bare date (midnight) as well as full date-times.
    dt = datetime.fromisoformat(value)
    if dt.tzinfo is None:
        dt = dt.replace(tzinfo=timezone.utc)
    ts = int(dt.timestamp())
    if not is_until:
        return ts
    # "--until 2023-04-10" is naturally read as "through the end of April 10th".
    # A date-only value has no 'T' or space separating a time part.
    date_only = "T" not in value and " " not in value
    return ts + (86400 if date_only else 1)  # +1 s makes an explicit time inclusive


def date_arg(is_until):
    """Build an argparse type function for --since (is_until=False) or --until (True)."""
    def convert(value):
        try:
            return parse_bound(value, is_until)
        except ValueError:
            raise argparse.ArgumentTypeError(f"not an ISO-8601 date/time: {value!r}")
    return convert


def non_negative_int(value):
    """argparse type: an integer >= 0."""
    try:
        n = int(value)
    except ValueError:
        raise argparse.ArgumentTypeError(f"not an integer: {value!r}")
    if n < 0:
        raise argparse.ArgumentTypeError("must be >= 0")
    return n


# ----------------------------------------------------------------------------- writers
# A writer receives an iterator of sqlite3.Row objects (already filtered and ordered),
# an open text file and the parsed arguments. It returns (points_written, tracks_written).

def write_csv(rows, out, args):
    writer = csv.writer(out)  # file is opened with newline="" so csv controls line endings
    writer.writerow(CSV_COLUMNS)
    count = 0
    for r in rows:
        writer.writerow([
            r["user"], r["device"], iso_utc(r["timestamp"]), r["timestamp"],
            r["latitude"], r["longitude"], r["accuracy"], r["altitude"], r["velocity"],
            r["battery"], r["tracker_id"], r["trigger"], r["connection"],
        ])  # csv writes None as an empty cell
        count += 1
    return count, 0  # CSV has no notion of tracks


def write_gpx(rows, out, args):
    out.write('<?xml version="1.0" encoding="UTF-8"?>\n')
    out.write(
        f'<gpx version="1.1" creator="{GENERATOR}" xmlns="http://www.topografix.com/GPX/1/1">\n'
    )
    count = tracks = 0
    # One <trk> per (user, device). groupby needs sorted input, which the query guarantees.
    for (user, device), group in itertools.groupby(rows, key=lambda r: (r["user"], r["device"])):
        tracks += 1
        out.write(f"  <trk>\n    <name>{escape(f'{user}/{device}')}</name>\n")
        prev_ts = None
        segment_open = False
        for r in group:
            # Start a new <trkseg> at the first point, and after any gap longer than
            # --segment-gap (0 disables splitting). Segments tell GPX viewers not to draw a
            # straight line across periods where nothing was recorded.
            if prev_ts is None or (args.segment_gap and r["timestamp"] - prev_ts > args.segment_gap):
                if segment_open:
                    out.write("    </trkseg>\n")
                out.write("    <trkseg>\n")
                segment_open = True
            out.write(f'      <trkpt lat="{r["latitude"]}" lon="{r["longitude"]}">\n')
            if r["altitude"] is not None:
                out.write(f"        <ele>{r['altitude']}</ele>\n")  # <ele> must precede <time> in GPX
            out.write(f"        <time>{iso_utc(r['timestamp'])}</time>\n")
            out.write("      </trkpt>\n")
            prev_ts = r["timestamp"]
            count += 1
        out.write("    </trkseg>\n  </trk>\n")
    out.write("</gpx>\n")
    return count, tracks


# format name -> (file extension, writer function, newline setting for open()).
# Add a new output format here.
WRITERS = {
    "gpx": (".gpx", write_gpx, "\n"),
    "csv": (".csv", write_csv, ""),
}


def detect_format(path):
    """Pick the output format from the file extension, or None if it is not recognised."""
    suffix = Path(path).suffix.lower()
    for name, (ext, _, _) in WRITERS.items():
        if suffix == ext:
            return name
    return None


# --------------------------------------------------------------------------------- core

def build_parser():
    p = argparse.ArgumentParser(
        description="Export locations from an OwnTracks SQLite database to GPX or CSV."
    )
    p.add_argument("db_path", help="path to owntracks.sqlite (opened read-only)")
    p.add_argument("output_file", help="file to write (.gpx or .csv)")
    p.add_argument("--format", choices=sorted(WRITERS), help="output format; default: detected from the extension")
    p.add_argument("--since", type=date_arg(False),
                   help="first moment to export: ISO-8601 date or date-time (naive = UTC)")
    p.add_argument("--until", type=date_arg(True),
                   help="last moment to export: a date includes that whole day; a date-time is inclusive (naive = UTC)")
    # append lets the option be repeated (--user a --user b); None means "no filter".
    p.add_argument("--user", action="append", help="only this user (repeatable)")
    p.add_argument("--device", action="append", help="only this device (repeatable)")
    p.add_argument("--segment-gap", type=non_negative_int, default=3600, metavar="SECONDS",
                   help="gpx only: start a new track segment after a gap longer than SECONDS "
                        "(default 3600, 0 = never split)")
    p.add_argument("--force", action="store_true", help="overwrite the output file if it exists")
    p.add_argument("-q", "--quiet", action="store_true", help="suppress the summary")
    return p


def open_db_readonly(path):
    """Open the database read-only; never creates a file and cannot change data."""
    db = Path(path)
    # sqlite3.connect would otherwise happily create an empty database for a wrong path.
    if not db.is_file():
        raise FileNotFoundError(f"database not found: {path}")
    conn = sqlite3.connect(f"{db.resolve().as_uri()}?mode=ro", uri=True)
    conn.row_factory = sqlite3.Row  # access columns by name in the writers
    tables = {r[0] for r in conn.execute("SELECT name FROM sqlite_master WHERE type='table'")}
    if "locations" not in tables:
        conn.close()
        raise sqlite3.DatabaseError("database is missing table: locations")
    return conn


def build_query(args):
    """Return (sql, params) for the requested range and filters."""
    where, params = [], []
    if args.since is not None:
        where.append("timestamp >= ?")
        params.append(args.since)
    if args.until is not None:
        where.append("timestamp < ?")  # exclusive: parse_bound already adjusted the bound
        params.append(args.until)
    # Each repeatable filter becomes "col IN (?, ?, ...)". Values are always bound as
    # parameters, never formatted into the SQL, so user input cannot inject SQL.
    for column, values in (("user", args.user), ("device", args.device)):
        if values:
            where.append(f"{column} IN ({', '.join('?' * len(values))})")
            params.extend(values)
    sql = f"SELECT {SELECT_COLUMNS} FROM locations"
    if where:
        sql += " WHERE " + " AND ".join(where)
    # id is a final tie-breaker so rows sharing a timestamp come out in a stable order.
    sql += " ORDER BY user, device, timestamp, id"
    return sql, params


def main(argv=None):
    parser = build_parser()
    args = parser.parse_args(argv)  # exits with code 2 on bad arguments

    # Validate argument combinations before touching any files.
    format_name = args.format or detect_format(args.output_file)
    if format_name is None:
        parser.error(f"cannot detect format from {args.output_file!r}; use --format {{{','.join(sorted(WRITERS))}}}")
    if args.since is not None and args.until is not None and args.since >= args.until:
        parser.error("--since must be earlier than --until")
    _, writer, newline = WRITERS[format_name]

    out_path = Path(args.output_file)
    if out_path.exists() and not args.force:
        print(f"error: {out_path} already exists (use --force to overwrite)", file=sys.stderr)
        return 1
    # Write to a temporary sibling and rename at the end, so a failure part-way never
    # leaves a truncated file at the real path (or destroys an existing one).
    tmp_path = out_path.with_name(out_path.name + ".tmp")

    conn = None
    try:
        conn = open_db_readonly(args.db_path)
        sql, params = build_query(args)
        cursor = conn.execute(sql, params)  # rows are streamed, not loaded into memory

        # Peek at the first row so an empty result is reported without creating a file.
        first = cursor.fetchone()
        if first is None:
            if not args.quiet:
                print("No matching locations; nothing written.")
            return 0

        # encoding utf-8 so non-ASCII user/device names survive; newline per format.
        with open(tmp_path, "w", encoding="utf-8", newline=newline) as out:
            count, tracks = writer(itertools.chain([first], cursor), out, args)
        os.replace(tmp_path, out_path)  # atomic on the same volume; replaces an existing file
    except (OSError, ValueError, sqlite3.Error) as exc:
        # Expected failures (missing database, unwritable path, ...): one line, exit code 1.
        print(f"error: {exc}", file=sys.stderr)
        tmp_path.unlink(missing_ok=True)  # do not leave the half-written temp file behind
        return 1
    finally:
        if conn is not None:
            conn.close()

    if not args.quiet:
        detail = f" in {tracks} track(s)" if format_name == "gpx" else ""
        print(f"Exported {count:,} locations{detail} to {out_path} ({format_name})")
    return 0


if __name__ == "__main__":
    sys.exit(main())
