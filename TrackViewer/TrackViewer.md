# TrackViewer — Implementation Specification

## Overview

TrackViewer is a responsive Blazor Server web application that provides an interactive map-based interface for exploring location tracks and waypoints stored in the OwnTracksCollector SQLite database. It is inspired by the [OwnTracks Frontend](https://github.com/owntracks/frontend) and targets .NET 10.

---

## Goals

- Visualise device tracks (polylines) and waypoints (geofence circles) on an interactive map.
- Allow filtering by user, device, date range, and map extent.
- Provide an at-a-glance statistics panel.
- Auto-refresh the map at a configurable interval (default 20 s) so live fixes appear without a page reload.
- Segment tracks at configurable time-gap thresholds to avoid "teleport" lines across stops.
- Require authentication — the app may be exposed to the internet.
- Export selected tracks and waypoints as a GPX file.
- Present a modern, polished UI — dark-themed, with clean typography, smooth transitions, and a professional feel comparable to a commercial mapping product.
- Run as a standalone Blazor Server app that reads the same `owntracks.db` as the collector.
- Be fully responsive — usable on desktop, tablet, and mobile.

---

## Project Layout

The application lives in a new solution folder alongside the collector:

```
OwnTracksCollector.sln
OwnTracksCollector/          ← existing collector
TrackViewer/
├── TrackViewer.csproj
├── Program.cs
├── appsettings.json
├── Components/
│   ├── App.razor
│   ├── Routes.razor
│   └── Layout/
│       ├── MainLayout.razor
│       └── MainLayout.razor.css
├── Pages/
│   ├── MapView.razor            ← primary page
│   ├── MapView.razor.css
│   ├── Login.razor              ← cookie-based login form
│   └── Export.razor             ← GPX export page
├── Services/
│   ├── LocationQueryService.cs  ← SQLite read queries
│   ├── WaypointQueryService.cs
│   └── GpxExportService.cs      ← GPX serialisation
├── Models/
│   ├── LocationRecord.cs
│   ├── WaypointRecord.cs
│   ├── DeviceIdentifier.cs
│   ├── TrackFilter.cs
│   └── ExportRequest.cs         ← GPX export parameters
└── wwwroot/
    ├── css/
    │   └── app.css
    └── js/
        └── mapInterop.js        ← JS interop for Leaflet
```

---

## Technology Stack

| Concern | Choice | Rationale |
|---|---|---|
| Framework | .NET 10 Blazor Server | Shared process with DB, minimal JS, full C# |
| Map library | [Leaflet.js](https://leafletjs.com/) via JS interop | Lightweight, well-documented, same library used by OwnTracks Frontend |
| Tile provider | OpenStreetMap (default) — switchable to Esri/CartoDB in config | No API key required for OSM |
| Database | Microsoft.Data.Sqlite (same as collector) | Re-uses existing schema; no ORM overhead |
| Styling | Bootstrap 5 (bundled with Blazor template) | Responsive grid, dark mode support |
| Theme | Dark colour scheme (`#0d1117` background, `#161b22` panels, accent `#58a6ff`) | Consistent with tools like GitHub, Grafana — practical for map viewing in low light |
| Icons | [Bootstrap Icons](https://icons.getbootstrap.com/) via CDN | Crisp SVG icons; matches Bootstrap 5 without extra dependencies |
| Date pickers | [Flatpickr](https://flatpickr.js.org/) via CDN | Lightweight, no Blazor component dep |
| Authentication | ASP.NET Core Cookie Authentication | Built-in, stateless sessions, no external IdP required |
| GPX export | Custom `GpxExportService` using `System.Xml` | No extra NuGet dep; full control over schema |

---

## Data Models

### `LocationRecord`

```csharp
public class LocationRecord
{
    public long   Id         { get; set; }
    public string User       { get; set; } = string.Empty;
    public string Device     { get; set; } = string.Empty;
    public double Latitude   { get; set; }
    public double Longitude  { get; set; }
    public long   Timestamp  { get; set; }
    public double? Accuracy  { get; set; }
    public double? Altitude  { get; set; }
    public int?   Velocity   { get; set; }
    public int?   Battery    { get; set; }
    public string? TrackerId { get; set; }
    public string? Trigger   { get; set; }
    public string? Connection{ get; set; }
}
```

### `WaypointRecord`

```csharp
public class WaypointRecord
{
    public long   Id          { get; set; }
    public string User        { get; set; } = string.Empty;
    public string Device      { get; set; } = string.Empty;
    public string? Description{ get; set; }
    public double Latitude    { get; set; }
    public double Longitude   { get; set; }
    public int?   Radius      { get; set; }
    public long   Timestamp   { get; set; }
}
```

### `DeviceIdentifier`

```csharp
public record DeviceIdentifier(string User, string Device);
```

### `TrackFilter`

```csharp
public class TrackFilter
{
    // Selected users/devices; empty = all
    public HashSet<DeviceIdentifier> SelectedDevices { get; set; } = [];

    public DateTime? From { get; set; }
    public DateTime? To   { get; set; }

    // Map bounding box for spatial pre-filter (optional — disabled when null)
    public double? MinLat { get; set; }
    public double? MaxLat { get; set; }
    public double? MinLon { get; set; }
    public double? MaxLon { get; set; }

    // Layer visibility toggles
    public bool ShowTracks    { get; set; } = true;
    public bool ShowPoints    { get; set; } = false;
    public bool ShowWaypoints { get; set; } = true;
    public bool ShowHeatmap   { get; set; } = false;
}
```

### `ExportRequest`

```csharp
public class ExportRequest
{
    // Track selection — reuses DeviceIdentifier + date range
    public HashSet<DeviceIdentifier> Devices { get; set; } = [];
    public DateTime? From { get; set; }
    public DateTime? To   { get; set; }

    // Waypoints to include — subset of IDs returned by WaypointQueryService
    public HashSet<long> WaypointIds { get; set; } = [];

    // GPX metadata
    public string Creator { get; set; } = "TrackViewer";
}
```

---

## Services

### `LocationQueryService`

Reads from the `locations` table using plain `SqliteCommand` with fully parameterised SQL. No ORM.

```
GetDevicesAsync()              → IReadOnlyList<DeviceIdentifier>
GetLocationsAsync(TrackFilter) → IReadOnlyList<LocationRecord>
GetStatisticsAsync(TrackFilter)→ TrackStatistics
```

**WAL mode**: `DbInitialiser` (called from `Program.cs` before the host is built) runs `PRAGMA journal_mode=WAL;` once on startup so TrackViewer reads safely while OwnTracksCollector is writing concurrently.

**`GetLocationsAsync` query pattern:**

Because `Microsoft.Data.Sqlite` does not support array-valued parameters, the `IN` clause is constructed dynamically with individually numbered parameters (`$d0`, `$d1`, …). No string interpolation of user data occurs — each value is bound via `cmd.Parameters.AddWithValue`.

```csharp
// Pseudocode — actual implementation in LocationQueryService.cs
var inClause = string.Join(",", devices.Select((_, i) => $"$d{i}"));
var sql = $"""
    SELECT id, user, device, latitude, longitude, timestamp,
           accuracy, altitude, velocity, battery, tracker_id, trigger, connection
    FROM   locations
    WHERE  ({(devices.Any() ? $"(user || '/' || device) IN ({inClause})" : "1=1")})
      AND  ($noFrom   OR timestamp >= $from)
      AND  ($noTo     OR timestamp <= $to)
      AND  ($noBBox   OR (latitude  BETWEEN $minLat AND $maxLat
                      AND longitude BETWEEN $minLon AND $maxLon))
    ORDER  BY user, device, timestamp
    LIMIT  $limit
    """;
```

Cap result at **50,000 rows** by default (configurable via `Query:MaxLocationRows`). When the cap is hit, a warning banner is shown.

### `WaypointQueryService`

```
GetWaypointsAsync(IEnumerable<DeviceIdentifier>?) → IReadOnlyList<WaypointRecord>
```

Loads all matching waypoints; no row cap needed. Uses the same dynamic `IN` clause pattern.

### `GpxExportService`

Generates a GPX 1.1 document from a set of `LocationRecord` track segments and optionally selected `WaypointRecord` entries.

```
ExportAsync(ExportRequest, IReadOnlyList<LocationRecord>, IReadOnlyList<WaypointRecord>)
    → Stream   (UTF-8 XML, application/gpx+xml)
```

**GPX structure produced:**

```xml
<?xml version="1.0" encoding="UTF-8"?>
<gpx version="1.1" creator="TrackViewer"
     xmlns="http://www.topografix.com/GPX/1/1"
     xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance"
     xsi:schemaLocation="http://www.topografix.com/GPX/1/1
                         http://www.topografix.com/GPX/1/1/gpx.xsd">
  <metadata>
    <name>OwnTracks Export</name>
    <time>2026-03-20T12:00:00Z</time>
  </metadata>

  <!-- One <wpt> per selected waypoint -->
  <wpt lat="51.5" lon="-0.1">
    <name>Home</name>
    <desc>alice/phone — radius 100 m</desc>
  </wpt>

  <!-- One <trk> per (user, device) pair; segments split at gap threshold -->
  <trk>
    <name>alice / phone</name>
    <trkseg>
      <trkpt lat="51.505" lon="-0.09">
        <ele>12.3</ele>
        <time>2026-03-20T08:00:00Z</time>
        <extensions>
          <speed>0</speed>
          <battery>87</battery>
        </extensions>
      </trkpt>
      ...
    </trkseg>
    <!-- Additional <trkseg> elements when gap exceeds threshold -->
  </trk>
</gpx>
```

The export is triggered from a dedicated `Export.razor` page and streamed to the browser via a minimal API endpoint (`/api/export/gpx`) to avoid loading the entire document into Blazor component state.

### `TrackStatistics`

Computed server-side from the filtered result set before serialising to the client:

```csharp
public class TrackStatistics
{
    public int    TotalPoints   { get; set; }
    public int    TotalDevices  { get; set; }
    public double TotalDistance { get; set; }  // km, Haversine
    public DateTime? EarliestFix { get; set; }
    public DateTime? LatestFix   { get; set; }
}
```

---

## Page: `MapView.razor`

The single primary page, accessible at the root path `/`.

### Layout (responsive)

```
┌──────────────────────────────────────────────────────────────┐
│ Navbar: "TrackViewer"                              [☰ menu]  │
├────────────┬─────────────────────────────────────────────────┤
│            │                                                   │
│  SIDEBAR   │              MAP (Leaflet)                        │
│  (filter   │                                                   │
│   panel)   │                                                   │
│            │                                                   │
│            ├─────────────────────────────────────────────────┤
│            │  STATS BAR (points · devices · distance · range) │
└────────────┴─────────────────────────────────────────────────┘
```

- On **mobile** (< 768 px): sidebar collapses into a slide-up bottom sheet; stats bar stacks below. Map occupies full viewport height minus navbar.
- On **tablet/desktop** (≥ 768 px): sidebar is a fixed left panel (280 px wide); map fills remaining width.

---

## Sidebar — Filter Panel

### Device List

- Displays each `user/device` pair as a checkbox row.
- Grouped by user (expandable `<details>` element).
- "Select all / None" quick-toggle per user group.
- Each row shows the device name and the last-seen timestamp.

### Date Range

- Two `<input type="date">` fields (enhanced by Flatpickr for a calendar picker).
- "Quick ranges": Today, Yesterday, Last 7 days, Last 30 days, All time.

### Layer Toggles

| Toggle | Default | Description |
|---|---|---|
| Tracks | ON | Polylines connecting points in time order per device |
| Points | OFF | Individual fix markers (only shown when < 5,000 points to preserve performance) |
| Waypoints | ON | Circle for each waypoint geofence |
| Heatmap | OFF | Leaflet.heat density layer (replaces tracks/points when active) |

### Apply / Reset Buttons

- **Apply**: triggers `GetLocationsAsync` + `GetWaypointsAsync`, serialises GeoJSON to JS interop, re-renders map layers.
- **Reset**: restores default filter state and refreshes.

---

## Map Component (`mapInterop.js`)

All map rendering is delegated to Leaflet via `IJSRuntime` JS interop. Blazor calls JS functions; JS never calls back into Blazor (fire-and-forget pattern).

### Exposed JS functions

```js
MapInterop.init(elementId, options)
MapInterop.setTracks(geoJsonFeatureCollection)
MapInterop.setPoints(geoJsonFeatureCollection)
MapInterop.setWaypoints(waypointArray)   // [{lat, lon, radius, label}]
MapInterop.setHeatmap(latLonArray)       // [[lat, lon, intensity]]
MapInterop.clearAll()
MapInterop.fitBounds()                   // zoom to data extent
MapInterop.getBounds()                   // returns {minLat,maxLat,minLon,maxLon}
```

### Trip Segmentation

Before serialising to GeoJSON, the `LocationRecord` list for each `(User, Device)` is split into **segments** wherever the gap between consecutive timestamps exceeds `Query:TripSegmentThresholdMinutes` (default 30 minutes). Each segment becomes a separate `LineString` Feature. This prevents straight "teleport" lines across overnight stops or device power-off periods.

```csharp
// Pseudocode — same logic reused by GpxExportService for <trkseg> splitting
var segments = new List<List<LocationRecord>>();
var current  = new List<LocationRecord> { records[0] };
for (var i = 1; i < records.Count; i++)
{
    var gapMinutes = (records[i].Timestamp - records[i-1].Timestamp) / 60.0;
    if (gapMinutes > thresholdMinutes)
    {
        segments.Add(current);
        current = new List<LocationRecord>();
    }
    current.Add(records[i]);
}
segments.Add(current);
```

### GeoJSON Serialisation (server-side)

`LocationRecord` rows are grouped by `(User, Device)`, segmented, and converted to `LineString` Features:

```json
{
  "type": "FeatureCollection",
  "features": [
    {
      "type": "Feature",
      "geometry": { "type": "LineString", "coordinates": [[lon, lat], ...] },
      "properties": { "user": "alice", "device": "phone" }
    }
  ]
}
```

Each device gets a **deterministic colour** derived from `$"{user}/{device}".GetHashCode()` mapped to an HSL palette (same technique used by OwnTracks Frontend).

### Waypoint rendering

Each waypoint is rendered as:
- A `L.circle` with radius from the `radius` field (default 50 m if null).
- A `L.marker` at the centre using a pin icon.
- A tooltip showing `description`, `user/device`, and formatted timestamp.

### Point markers (when Points layer active)

- `L.circleMarker` (8 px radius).
- Colour matches the track colour for the same device.
- Popup showing: timestamp (local), speed, battery, accuracy, trigger.

---

## Statistics Bar

Displayed below the map (or in a collapsible panel on mobile):

```
📍 12,450 points   👤 3 devices   📏 2,341 km   🕒 2026-01-01 → 2026-03-20
```

Updates whenever the filter is applied or an auto-refresh tick fires. Computed server-side in `TrackStatistics`.

---

## Auto-Refresh

A `PeriodicTimer` in `MapView.razor` fires every `Map:AutoRefreshSeconds` seconds (default 20). On each tick:

1. Re-query `GetLocationsAsync` and `GetWaypointsAsync` with the current filter.
2. Compare the returned `TrackStatistics.LatestFix` against the previously rendered value.
3. If changed, call `MapInterop.setTracks` / `MapInterop.setPoints` / `MapInterop.setWaypoints` to update only the modified layers.
4. Update the statistics bar.

The auto-refresh can be paused/resumed with a toggle in the navbar. Setting `Map:AutoRefreshSeconds` to `0` disables it entirely. The timer is disposed in `IAsyncDisposable.DisposeAsync` to prevent memory leaks.

---

## Authentication

ASP.NET Core Cookie Authentication is used. Credentials are stored in `appsettings.json` with a bcrypt-hashed password. A single shared account is sufficient for v1.

### Flow

1. All routes except `/login` are protected by an `[Authorize]` attribute (applied globally via a route policy in `Program.cs`).
2. Unauthenticated requests redirect to `/login`.
3. `Login.razor` posts a form to `POST /account/login`, which validates credentials and issues an `AuthenticationCookie`.
4. `POST /account/logout` clears the cookie and redirects to `/login`.

### Configuration

```json
"Auth": {
  "Username": "admin",
  "PasswordHash": "<bcrypt hash of password>",
  "CookieExpiryMinutes": 480
}
```

The password is **never stored in plaintext**. Generate the hash with:

```powershell
dotnet run --project TrackViewer -- --hash-password MySecretPassword
```

### Security notes

- Cookie flags: `HttpOnly`, `SameSite=Strict`, `Secure` (enforced in Production).
- Login endpoint is rate-limited: 5 attempts per minute per IP via `RateLimiter` middleware.
- Failed login attempts are logged at Warning level.

---

## GPX Export

### Export Page (`Export.razor`)

Accessible at `/export`. Presents a three-step form:

**Step 1 — Select tracks:** device checkboxes + date range pickers (From / To).

**Step 2 — Select waypoints:** checklist of all waypoints for the selected devices, showing description, `user/device`, and formatted date. Includes "Select all / None" toggle.

**Step 3 — Export:** a single **Download GPX** button. On click, the browser posts to `POST /api/export/gpx` with the `ExportRequest` JSON body; the server streams back the file with `Content-Disposition: attachment`.

### Minimal API endpoint

```csharp
app.MapPost("/api/export/gpx", async (ExportRequest req,
    LocationQueryService lqs, WaypointQueryService wqs,
    GpxExportService gpx) =>
{
    var filter = new TrackFilter
    {
        SelectedDevices = req.Devices,
        From = req.From,
        To   = req.To
    };
    var locations = await lqs.GetLocationsAsync(filter);
    var waypoints = (await wqs.GetWaypointsAsync(req.Devices))
                     .Where(w => req.WaypointIds.Contains(w.Id))
                     .ToList();

    var stream = await gpx.ExportAsync(req, locations, waypoints);
    return Results.File(stream, "application/gpx+xml", "owntracks-export.gpx");
}).RequireAuthorization();

---

## Configuration (`appsettings.json`)

```json
{
  "Database": {
    "Path": "C:\\Data\\owntracks.db"
  },
  "Map": {
    "TileUrl":                  "https://{s}.tile.openstreetmap.org/{z}/{x}/{y}.png",
    "TileAttribution":          "© OpenStreetMap contributors",
    "DefaultZoom":              13,
    "AutoRefreshSeconds":       20
  },
  "Query": {
    "MaxLocationRows":              50000,
    "TripSegmentThresholdMinutes":  30
  },
  "Auth": {
    "Username":            "admin",
    "PasswordHash":        "<bcrypt hash>",
    "CookieExpiryMinutes": 480
  }
}
```

**Notes:**
- `Database:Path` must be an **absolute path** to the `owntracks.db` file. Override via the environment variable `TRACKVIEWER_Database__Path`.
- `Map:DefaultCenterLat` / `Map:DefaultCenterLon` are **intentionally absent** — the map centres on the coordinates of the first location row returned. If the database is empty the map falls back to `[0, 0]` at zoom 2.
- `Map:AutoRefreshSeconds = 0` disables live refresh entirely.
- `Query:TripSegmentThresholdMinutes` is the time gap (minutes) that causes a track to be split into separate polyline segments.

---

## Dependency Injection Setup (`Program.cs`)

```csharp
// Enable WAL mode before building the host so concurrent reads are safe
DbInitialiser.EnableWal(builder.Configuration["Database:Path"]!);

// Authentication
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(o =>
    {
        o.LoginPath  = "/login";
        o.LogoutPath = "/account/logout";
        o.ExpireTimeSpan = TimeSpan.FromMinutes(
            builder.Configuration.GetValue<int>("Auth:CookieExpiryMinutes", 480));
        o.Cookie.HttpOnly     = true;
        o.Cookie.SameSite     = SameSiteMode.Strict;
        o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
    });
builder.Services.AddAuthorization();
builder.Services.AddRateLimiter(o =>
    o.AddFixedWindowLimiter("login", w =>
    {
        w.Window      = TimeSpan.FromMinutes(1);
        w.PermitLimit = 5;
    }));

// Blazor
builder.Services.AddRazorComponents().AddInteractiveServerComponents();

// Application services
builder.Services.AddSingleton<LocationQueryService>();
builder.Services.AddSingleton<WaypointQueryService>();
builder.Services.AddSingleton<GpxExportService>();
```

All three application services are **singletons** — they hold only a connection string; actual `SqliteConnection` objects are opened per-query and disposed immediately.

`DbInitialiser.EnableWal`:

```csharp
public static class DbInitialiser
{
    public static void EnableWal(string path)
    {
        using var conn = new SqliteConnection($"Data Source={path}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA journal_mode=WAL;";
        cmd.ExecuteNonQuery();
    }
}
```

---

## NuGet Packages

| Package | Purpose |
|---|---|
| `Microsoft.Data.Sqlite` | SQLite access — same version as collector |
| `Microsoft.AspNetCore.Components.Web` | Blazor Server (included with ASP.NET Core) |
| `BCrypt.Net-Next` | Password hashing for stored credential verification |

`Microsoft.AspNetCore.RateLimiting` is included in ASP.NET Core 7+ and requires no extra package.

Leaflet.js and Flatpickr are loaded via CDN `<script>` tags in `App.razor` — no npm required.

---

## CDN References (`App.razor` `<head>`)

```html
<!-- Leaflet -->
<link rel="stylesheet" href="https://unpkg.com/leaflet@1.9.4/dist/leaflet.css" />
<script src="https://unpkg.com/leaflet@1.9.4/dist/leaflet.js"></script>

<!-- Leaflet.heat (heatmap plugin) -->
<script src="https://unpkg.com/leaflet.heat@0.2.0/dist/leaflet-heat.js"></script>

<!-- Flatpickr (date picker) -->
<link rel="stylesheet" href="https://cdn.jsdelivr.net/npm/flatpickr/dist/flatpickr.min.css" />
<script src="https://cdn.jsdelivr.net/npm/flatpickr"></script>

<!-- Bootstrap 5 -->
<link rel="stylesheet" href="https://cdn.jsdelivr.net/npm/bootstrap@5.3/dist/css/bootstrap.min.css" />

<!-- Bootstrap Icons -->
<link rel="stylesheet" href="https://cdn.jsdelivr.net/npm/bootstrap-icons@1.11/font/bootstrap-icons.css" />
```

---

## Visual Design

The app uses a **dark theme** throughout. Key design tokens (defined as CSS custom properties in `app.css`):

```css
:root {
  --tv-bg:          #0d1117;   /* page background */
  --tv-surface:     #161b22;   /* sidebar, cards, modals */
  --tv-border:      #30363d;   /* dividers and input borders */
  --tv-text:        #e6edf3;   /* primary text */
  --tv-text-muted:  #8b949e;   /* secondary labels */
  --tv-accent:      #58a6ff;   /* links, active states, highlights */
  --tv-danger:      #f85149;   /* errors, destructive actions */
  --tv-success:     #3fb950;   /* positive indicators */
}
```

**UI expectations:**
- Sidebar has a subtle border-right, no harsh shadows; device rows use hover highlight (`--tv-surface` → slightly lighter).
- Buttons use the accent colour with a gentle hover transition (`transition: opacity 0.15s ease`).
- The map tile layer uses the **CartoDB Dark Matter** tiles by default (no API key required) to complement the dark UI: `https://{s}.basemaps.cartocdn.com/dark_all/{z}/{x}/{y}{r}.png`. OSM is offered as an alternative in config.
- Stats bar is a thin fixed strip with muted text and bold numbers — no heavy borders.
- The login page is centred, card-style, with the app name in large weight-600 type above the form.
- Flatpickr uses the `"dark"` theme stylesheet: `flatpickr/dist/themes/dark.css` (swap the CDN link accordingly).
- Smooth CSS transitions on sidebar open/close and layer toggle state changes (no janky flicker).

---

## Responsive Design Breakpoints

| Breakpoint | Sidebar | Map height | Stats |
|---|---|---|---|
| Mobile < 576 px | Hidden; toggle via FAB button | `calc(100vh - 56px)` | Collapsed accordion below map |
| Tablet 576–991 px | Collapsible off-canvas drawer | `calc(100vh - 112px)` | Inline below map |
| Desktop ≥ 992 px | Fixed left panel 280 px | `100vh - 56px` | Fixed bottom bar |

---

## Performance Considerations

- **Row cap**: Default 50,000 location rows. Enforced with `LIMIT` in SQL. Configurable.
- **GeoJSON serialisation**: Done server-side in C# using `System.Text.Json`; avoid unnecessary intermediate object allocations by streaming directly to a `JsonWriter`.
- **JS transfer size**: Only `[lon, lat]` pairs are sent for track lines — no property bloat. Point markers include properties only when the Points layer is active.
- **Heatmap**: When the Heatmap toggle is active, Points and Tracks layers are automatically hidden to avoid rendering conflicts and reduce DOM node count.
- **Debounce**: The Apply button is debounced (300 ms) to prevent rapid double-submits.
- **Incremental loading** (future): For datasets > 50k rows, consider a `?page=` endpoint returning GeoJSON chunks streamed via SignalR.

---

## Security Considerations

- The app is read-only (no mutations). The only write operations are cookie issuance (auth) and GPX streaming (export).
- The database file path is read from server-side config only; it is never exposed to the browser.
- All SQL queries use parameterised inputs. The dynamic `IN` clause constructs numbered parameters (`$d0`, `$d1`, …) — no raw user data is ever concatenated into SQL strings.
- Cookie Authentication with `HttpOnly`, `SameSite=Strict`. The `Secure` flag is enforced when `ASPNETCORE_ENVIRONMENT=Production`.
- Passwords are stored as bcrypt hashes — never plaintext.
- Login endpoint is rate-limited (5 attempts / minute / IP) to resist brute-force attacks.
- The `/api/export/gpx` endpoint requires an authenticated session (`RequireAuthorization()`).
- If deployed behind a reverse proxy (nginx, Caddy), configure `ForwardedHeadersOptions` to preserve the real client IP for rate limiting.

---

## Accessibility

- All map controls labelled with `aria-label`.
- Colour palette for device tracks passes WCAG AA contrast against both the light OSM tile layer and the dark CartoDB tile layer.
- Sidebar filter controls use semantic `<label for>` associations.
- Keyboard navigation: sidebar focusable, map keyboard-pannable via Leaflet defaults.

---

## Out of Scope (v1)

- Multi-user / multi-tenant isolation (single shared account only).
- Export formats other than GPX (CSV, KML).
- Elevation profile chart.
- Push notifications or mobile app integration.

These are candidates for a v2 iteration.

---

## Implementation Checklist

### Project setup
- [ ] Create `TrackViewer` Blazor Server project and add to `OwnTracksCollector.sln`
- [ ] Add NuGet references: `Microsoft.Data.Sqlite`, `BCrypt.Net-Next`
- [ ] Implement `DbInitialiser.EnableWal` and call from `Program.cs` before host build

### Models
- [ ] `LocationRecord`, `WaypointRecord`, `DeviceIdentifier`, `TrackFilter`, `TrackStatistics`
- [ ] `ExportRequest`

### Services
- [ ] `LocationQueryService` — `GetDevicesAsync`, `GetLocationsAsync` (dynamic `IN` clause + `LIMIT`), `GetStatisticsAsync`
- [ ] `WaypointQueryService` — `GetWaypointsAsync` (dynamic `IN` clause)
- [ ] `GpxExportService` — `ExportAsync` producing GPX 1.1 XML stream with `<wpt>` + trip-segmented `<trkseg>` elements

### Authentication
- [ ] Configure Cookie Authentication + rate limiter in `Program.cs`
- [ ] Implement `Login.razor` form + `POST /account/login` minimal API endpoint
- [ ] Implement `POST /account/logout` endpoint
- [ ] Add `--hash-password` CLI helper
- [ ] Apply `[Authorize]` globally; protect `POST /api/export/gpx` with `RequireAuthorization()`

### Map page
- [ ] Create `wwwroot/js/mapInterop.js` with all `MapInterop.*` functions
- [ ] Implement trip segmentation helper (shared between map GeoJSON and GPX export)
- [ ] Build `MainLayout.razor` — responsive sidebar + map container + stats bar + auto-refresh toggle in navbar
- [ ] Build `MapView.razor` — filter panel, Leaflet map div, stats bar, `PeriodicTimer` for auto-refresh
- [ ] On initial load: read first location row, pass `{lat, lon}` to `MapInterop.init()`; fall back to `[0,0]` zoom 2 if DB empty
- [ ] Wire up `IJSRuntime` calls on Apply / Reset / auto-refresh tick

### GPX Export page
- [ ] Build `Export.razor` — device + date range selection, waypoint checklist, Download GPX button
- [ ] Implement `POST /api/export/gpx` minimal API endpoint streaming GPX response

### Configuration
- [ ] Populate `appsettings.json` with absolute DB path, `AutoRefreshSeconds`, `TripSegmentThresholdMinutes`, `Auth` section
- [ ] Register all services in `Program.cs`

### Testing & QA
- [ ] Test concurrent read (TrackViewer) + write (OwnTracksCollector) with WAL mode enabled
- [ ] Test auto-refresh — verify new fixes appear without a page reload
- [ ] Test trip segmentation with a dataset containing overnight gaps
- [ ] Test GPX export opens correctly in a GPX viewer (e.g. GPSBabel, Viking, OsmAnd)
- [ ] Test auth — unauthenticated access redirects to `/login`; rate limiter fires after 5 bad attempts
- [ ] Verify responsiveness at mobile / tablet / desktop breakpoints
