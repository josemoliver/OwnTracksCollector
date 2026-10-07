# Location import / export

Two command-line tools for moving location history in and out of an [OwnTracks](https://owntracks.org/) SQLite database (the `locations` and `waypoints` tables fed by MQTT):

| Tool | Purpose |
|---|---|
| [location_import.py](location_import.py) | Imports Google Timeline JSON, Foursquare check-ins or GPX files into the database |
| [location_export.py](location_export.py) | Exports a date range of locations (optionally filtered by user and/or device) to GPX or CSV |

Supported import formats:

| Format | `--format` | Extensions | Source |
|---|---|---|---|
| Google Timeline | `google` | `.json` | On-device Timeline export (`Timeline.json`) |
| Foursquare check-ins | `foursquare` | `.json` | Foursquare/Swarm data export (`checkins.json`); one point per check-in at the venue's position, `--waypoints` imports the venues |
| GPX | `gpx` | `.gpx` | GPX 1.0, GPX 1.1 or un-namespaced GPX tracks and waypoints |

Supported export formats: GPX 1.1 (`.gpx`) and CSV (`.csv`).

Requirements: Python 3.8 or later (developed on 3.12), standard library only. No packages to install.

## Quick start

Import:

```
python location_import.py Timeline.json owntracks.sqlite --user johndoe --device myphone --dry-run
python location_import.py Timeline.json owntracks.sqlite --user johndoe --device myphone

python location_import.py 202304_SpainTrip.gpx owntracks.sqlite --user johndoe --device myphone
```

Export:

```
python location_export.py owntracks.sqlite trip.gpx --since 2023-04-09 --until 2023-04-18
python location_export.py owntracks.sqlite april.csv --since 2026-04-01 --until 2026-04-30 --user johndoe
```

**Back up `owntracks.sqlite` before the first real import.** Always try `--dry-run` first: it opens the database read-only, so it cannot change anything. The exporter only ever reads the database.

## Importing: usage

```
python location_import.py INPUT_FILE DB_PATH --user USER --device DEVICE [options]
```

| Argument / option | Default | Description |
|---|---|---|
| `INPUT_FILE` | required | File to import |
| `DB_PATH` | required | Existing OwnTracks SQLite database (never created or altered by the tool) |
| `--user USER` | required | Value for the `user` column |
| `--device DEVICE` | required | Value for the `device` column |
| `--format {foursquare,google,gpx}` | from extension | Input format. `.json` is told apart by content (array = Foursquare, object = Timeline). Needed only if the format cannot be detected |
| `--tracker-id ID` | `gt` (google), `fsq` (foursquare), `gpx` (gpx) | `tracker_id` written to imported rows. Live OwnTracks rows typically use the device's own id, so a distinct value makes imported rows easy to find or remove |
| `--since TIME` | none | Import only points at or after this ISO-8601 time |
| `--until TIME` | none | Import only points at or before this ISO-8601 time |
| `--min-interval SECONDS` | 0 (off) | Thinning: keep a point only if at least SECONDS passed since the last kept point |
| `--waypoints` | off | Also import waypoints/places from the input |
| `--dry-run` | off | Parse, filter and report; write nothing |
| `--batch-size N` | 5000 | Rows per database commit |
| `-q`, `--quiet` | | Suppress progress output |
| `-v`, `--verbose` | | Accepted for compatibility; currently has no effect |

Times given to `--since`/`--until` follow ISO-8601, for example `2023-04-10` or `2023-04-10T08:00:00-04:00`. A time without an offset is treated as UTC.

Format-specific options:

| Option | Format | Description |
|---|---|---|
| `--sources raw,path` | json | Which Timeline sources to read: `raw` (rawSignals positions), `path` (timelinePath points). Default: both |

Using an option with a format it does not belong to (for example `--sources` with a GPX file) is an error.

### Exit codes

| Code | Meaning |
|---|---|
| 0 | Success (including a run that inserts nothing) |
| 1 | Input or database error (missing file, malformed input, database missing the `locations` table, SQLite error) |
| 2 | Bad arguments |

### Output

Progress goes to stderr and the summary to stdout:

```
Read:      38,230 raw positions, 339,607 path points
Skipped:   0 invalid, 0 outside --since/--until, 10,397 duplicate-in-input, 15,714 covered-by-raw, 367 already-in-db, 0 thinned
Inserted:     351,359 locations (2010-02-28 -> 2026-10-04)
```

With `--dry-run` the last line reads `Would insert:`. The date span is in UTC. With `--waypoints` an extra line reports waypoints.

## How it works

For every format the same pipeline runs:

1. **Parse.** The format module yields valid points. Records it cannot parse (bad coordinates, missing or invalid time, coordinates out of range) are counted as `invalid` and skipped, never fatal.
2. **Range filter.** Points outside `--since`/`--until` are dropped.
3. **Duplicates in the input.** A point with the same timestamp, latitude and longitude as an earlier one is dropped.
4. **Format-specific filter.** For Timeline JSON, `timelinePath` points that fall inside the time range covered by `rawSignals` positions are dropped (`covered-by-raw`), because raw positions are denser and carry accuracy. This only applies when both sources are enabled.
5. **Already in the database.** The tool loads the existing timestamps for the same `--user` and `--device`, and drops any point whose timestamp is already present. This protects live OwnTracks data and makes re-running the tool safe: a second run inserts nothing. Matching is on the exact second, so a point one second away from an existing row is still imported.
6. **Sort and thin.** Remaining points are sorted by time. If `--min-interval` is set, a point is kept only when at least that many seconds have passed since the last kept point (the first is always kept). Thinning is compared only against points kept in this run, not against rows already in the database.
7. **Insert.** Rows are written in batches, each batch in its own transaction; a failing batch is rolled back. Existing rows are never modified or deleted.

### Field mapping (`locations` table)

| Column | Value |
|---|---|
| `user`, `device` | `--user`, `--device` |
| `latitude`, `longitude` | Coordinates from the input |
| `timestamp` | UTC epoch seconds (offsets are converted; fractional seconds dropped) |
| `accuracy` | Accuracy in metres when the input has it, otherwise NULL |
| `altitude` | Elevation in metres when present, otherwise NULL |
| `velocity` | Speed converted to km/h and rounded (OwnTracks `vel`), otherwise NULL |
| `battery`, `trigger`, `connection` | NULL (unknown for imported data) |
| `tracker_id` | `--tracker-id` or the format default |
| `raw_payload` | OwnTracks-style JSON, see below |
| `created_at` | Database default (import time) |

`raw_payload` follows the OwnTracks `_type: "location"` layout (`lat`, `lon`, `tst`, `acc`, `alt`, `vel`, `tid`), omitting unknown values, plus format-specific keys that record where the point came from:

```json
{"_type":"location","lat":40.406883,"lon":-3.671781,"tst":1681032884,"alt":657.0,"tid":"gpx","_source":"gpx","_track":"Madrid"}
{"_type":"location","lat":18.4538991,"lon":-66.0670285,"tst":1788492085,"acc":14,"alt":-36.7,"vel":0,"tid":"gt","_source":"google-timeline","_signal":"WIFI"}
```

### Waypoints (`--waypoints`)

Each waypoint becomes a `waypoints` row with radius 50 m and an OwnTracks-style `raw_payload` (`_type: "waypoint"`). A waypoint is skipped if one with the same user, device, description and coordinates already exists. If the input gives no time, the import time is used.

## Format notes

### Google Timeline JSON (`json`)

Reads two parts of the on-device export:

- `rawSignals` entries with a `position`: high-fidelity points with accuracy, altitude, speed and a signal source (`GPS`, `WIFI`, `CELL`, ...). The signal is kept in `raw_payload` as `_signal`. In the sample file these only cover the most recent month.
- `semanticSegments[].timelinePath` points: minute-resolution coordinates with no accuracy, altitude or speed, covering the whole history. Stored with `_signal: "path"`.

Visits, activities, trip memories, Wi-Fi scans and activity records are ignored. With `--waypoints`, the entries in `userLocationProfile.frequentPlaces` become waypoints, described by their label (`HOME`, `WORK`) or their `placeId`.

Coordinates are strings such as `"18.454432°, -66.085867°"`; the file is read as UTF-8. Timestamps carry a local offset and are converted to UTC.

### GPX (`gpx`)

Reads every `<trk>` / `<trkseg>` / `<trkpt>`. Uses `lat`/`lon`, `<time>` (required; points without a valid time are counted as invalid), `<ele>` (altitude) and `<speed>` (m/s, as in GPX 1.0). The track `<name>` is kept in `raw_payload` as `_track`. GPX has no accuracy, so `accuracy` is NULL. Points need not be in time order; they are sorted before insertion. `<wpt>` elements are used with `--waypoints`; routes (`<rte>`) are not read.

## Exporting: `location_export.py`

The reverse tool: exports locations from the database to a GPX or CSV file, for a date range and optionally for specific users and/or devices. The database is opened read-only.

```
python location_export.py owntracks.sqlite trip.gpx --since 2023-04-09 --until 2023-04-18
python location_export.py owntracks.sqlite april.csv --since 2026-04-01 --until 2026-04-30 --user johndoe --device myphone
```

| Argument / option | Default | Description |
|---|---|---|
| `DB_PATH` | required | OwnTracks SQLite database (read-only) |
| `OUTPUT_FILE` | required | File to write; the format is taken from `.gpx` / `.csv` |
| `--format {gpx,csv}` | from extension | Output format, if the extension is not recognised |
| `--since TIME` | none | First moment to export. ISO-8601 date or date-time |
| `--until TIME` | none | Last moment to export. A plain date (`2023-04-18`) includes that whole day; a date-time is inclusive |
| `--user NAME` | all | Only this user. Repeat the option for several users |
| `--device NAME` | all | Only this device. Repeat the option for several devices |
| `--segment-gap SECONDS` | 3600 | GPX only: start a new track segment after a gap longer than this (0 = never split) |
| `--force` | off | Overwrite an existing output file (otherwise the tool refuses) |
| `-q`, `--quiet` | | Suppress the summary |

Times without an offset are treated as UTC, the same as the importer. For local days, give an offset, e.g. `--since 2023-04-10T00:00:00-04:00`. `--since` must be earlier than `--until`.

- **GPX output** (GPX 1.1): one `<trk>` per user/device, named `user/device`, split into `<trkseg>` segments at gaps. Each point has latitude, longitude, `<ele>` (if the altitude is known) and `<time>` in UTC. Accuracy, speed and battery are not written.
- **CSV output**: one row per location with the columns `user, device, time, timestamp, latitude, longitude, accuracy, altitude, velocity, battery, tracker_id, trigger, connection`. `time` is ISO-8601 UTC and `timestamp` is epoch seconds. Unknown values are empty.
- Rows are ordered by user, device, then time. If nothing matches, no file is written and the exit code is still 0.
- The file is written to a temporary name and renamed when complete, so a failure never leaves a truncated file or damages an existing one.
- Exit codes: 0 success (including no matches), 1 input/database/file error (including an existing output file without `--force`), 2 bad arguments.

GPX exported this way can be imported again with `location_import.py`.

To add an output format, write a function like `write_gpx` or `write_csv` in `location_export.py` and add it to the `WRITERS` table.

## Project layout

```
location_import.py        CLI, database access and the processing pipeline (format-agnostic)
location_export.py        exports a date range of locations to GPX or CSV
formats/
    __init__.py           auto-discovers every module in this folder; format detection
    base.py               InputFormat contract, Point/Waypoint types, shared helpers
    timeline_json.py      Google Timeline JSON
    foursquare.py         Foursquare check-ins JSON
    gpx.py                GPX
SPECIFICATIONS.md         original design specification (Timeline JSON)
```

## Adding a new import format

Create one file, `formats/<name>.py`. It is picked up automatically; nothing else changes, and the new format appears in `--format` and in extension detection.

```python
import csv
from .base import InputFormat, Point, register, to_epoch

@register
class CsvFormat(InputFormat):
    name = "csv"                    # value of --format
    extensions = (".csv",)          # used for auto-detection
    default_tracker_id = "csv"

    def load(self, path):
        with open(path, newline="", encoding="utf-8") as f:
            return list(csv.DictReader(f))

    def points(self, rows, stats):
        for r in rows:
            stats.read["rows"] += 1                      # shown in the "Read:" line
            yield Point(to_epoch(r["time"]), float(r["lat"]), float(r["lon"]))
```

The contract (full details in [formats/base.py](formats/base.py)):

| Member | Required | Purpose |
|---|---|---|
| `name`, `extensions`, `default_tracker_id` | yes | Identification and defaults |
| `load(path)` | yes | Read the file; return any object. Raise `OSError` or `ValueError` on failure (the tool reports it and exits with code 1) |
| `points(data, stats)` | yes | Yield `Point(ts, lat, lon, acc, alt, vel, extra)`. `ts` is UTC epoch seconds, `vel` is km/h, `extra` is a dict merged into `raw_payload` (use it to record `_source`). Count records in `stats.read[label]` and unparsable ones in `stats.invalid` |
| `waypoints(data)` | no | Yield `Waypoint(desc, lat, lon, ts)` for `--waypoints` |
| `accept(point, stats)` | no | Return a reason string to drop a point after generic de-duplication, or `None` to keep it. List the possible reasons in `skip_reasons` so they always appear in the summary |
| `add_arguments(group)`, `configure(args, parser)`, `option_dests` | no | Format-specific CLI options. Option defaults must be `None`; list their argparse dests in `option_dests` so the tool can reject them when another format is used |

Everything else (range filtering, duplicate handling, thinning, database writes, the summary) is shared and needs no changes.

## Limitations

- The whole input is loaded into memory (the 5-million-line sample JSON loads comfortably).
- Duplicate detection against the database is by exact timestamp, per user and device. Existing rows are never changed, and no database indexes or constraints are added.
- Timeline visits and activities are not converted to locations.
- All formats share one `--tracker-id`/`--user`/`--device` per run; import files separately to use different values.
- The importer takes one file per run. To import several files, run it in a loop, for example in PowerShell: `Get-ChildItem *.gpx | ForEach-Object { python location_import.py $_.FullName owntracks.sqlite --user johndoe --device myphone -q }`. Points repeated across files are skipped automatically.
- The exporter writes only locations (not waypoints), and GPX output omits accuracy, speed and battery. CSV output keeps every location column.
