// TrackViewer — Leaflet JS interop
// All functions are fire-and-forget from Blazor; no callbacks into C#.

window.MapInterop = (() => {
    let _map          = null;
    let _trackLayer   = null;
    let _pointLayer   = null;
    let _waypointLayer= null;
    let _heatLayer    = null;
    let _tileLayer    = null;
    let _fitting      = false;

    // Key-free basemaps offered in the layer switcher
    const OSM  = '© OpenStreetMap contributors';
    const ESRI = 'Tiles © Esri';
    const BASEMAPS = [
        { name: 'OpenStreetMap', url: 'https://tile.openstreetmap.org/{z}/{x}/{y}.png',
          attribution: OSM, maxZoom: 19 },
        { name: 'OpenTopoMap', url: 'https://{s}.tile.opentopomap.org/{z}/{x}/{y}.png',
          attribution: `${OSM}, SRTM | © OpenTopoMap (CC-BY-SA)`, maxZoom: 17, subdomains: 'abc' },
        { name: 'CyclOSM', url: 'https://{s}.tile-cyclosm.openstreetmap.fr/cyclosm/{z}/{x}/{y}.png',
          attribution: `${OSM}, CyclOSM`, maxZoom: 19, subdomains: 'abc' },
        { name: 'Esri Satellite',
          url: 'https://server.arcgisonline.com/ArcGIS/rest/services/World_Imagery/MapServer/tile/{z}/{y}/{x}',
          attribution: ESRI, maxZoom: 19 },
        { name: 'Esri Streets',
          url: 'https://server.arcgisonline.com/ArcGIS/rest/services/World_Street_Map/MapServer/tile/{z}/{y}/{x}',
          attribution: ESRI, maxZoom: 19 },
        { name: 'Esri Topo',
          url: 'https://server.arcgisonline.com/ArcGIS/rest/services/World_Topo_Map/MapServer/tile/{z}/{y}/{x}',
          attribution: ESRI, maxZoom: 19 },
    ];

    // ── Playback ───────────────────────────────────────────────────────────────
    const SPEEDS = [30, 120, 600, 1800, 3600, 14400];   // simulated seconds per real second
    let _pb = null;

    // Layers that would hide the replay (same colours, drawn underneath) while it runs
    const PB_HIDDEN = ['tracks', 'points', 'heat'];
    const _wanted = { tracks: true, points: true, waypoints: true, heat: true };   // what the page last asked for

    function pbFormat(t) {
        const d = new Date(t * 1000);
        try {
            return new Intl.DateTimeFormat(undefined, {
                timeZone: _pb.tz, dateStyle: 'medium', timeStyle: 'medium'
            }).format(d);
        } catch (_) {
            return d.toLocaleString();   // unknown zone id -> browser time zone
        }
    }

    // Index of the last fix at or before t (-1 if none)
    function pbIndexAt(s, t) {
        let lo = 0, hi = s.t.length - 1, ans = -1;
        while (lo <= hi) {
            const mid = (lo + hi) >> 1;
            if (s.t[mid] <= t) { ans = mid; lo = mid + 1; } else hi = mid - 1;
        }
        return ans;
    }

    function pbRebuildTrail(s, idx) {
        s.group.clearLayers();
        s.line = null;
        for (let i = 0; i <= idx; i++) pbAppendPoint(s, i);
    }

    // Extends the trail with fix i, starting a new segment after a long gap
    function pbAppendPoint(s, i) {
        const ll = [s.lat[i], s.lon[i]];
        const gap = i > 0 ? s.t[i] - s.t[i - 1] : 0;
        if (!s.line || gap > _pb.gap) {
            s.line = L.polyline([ll], { color: s.colour, weight: 4, opacity: .9 }).addTo(s.group);
        } else {
            s.line.addLatLng(ll);
        }
    }

    function pbRender() {
        const t = _pb.cur;
        _pb.series.forEach(s => {
            const idx = pbIndexAt(s, t);
            if (idx < 0) {
                if (s.drawn >= 0) { s.group.clearLayers(); s.line = null; s.drawn = -1; }
                s.marker.setStyle({ opacity: 0, fillOpacity: 0 });
                s.marker.closeTooltip();
                return;
            }
            if (idx < s.drawn) {
                pbRebuildTrail(s, idx);
            } else {
                for (let i = s.drawn + 1; i <= idx; i++) pbAppendPoint(s, i);
            }
            s.drawn = idx;

            // Interpolate towards the next fix unless the gap is too long to trust
            let lat = s.lat[idx], lon = s.lon[idx];
            if (idx < s.t.length - 1) {
                const span = s.t[idx + 1] - s.t[idx];
                if (span > 0 && span <= _pb.gap) {
                    const f = (t - s.t[idx]) / span;
                    lat += (s.lat[idx + 1] - lat) * f;
                    lon += (s.lon[idx + 1] - lon) * f;
                }
            }
            s.marker.setLatLng([lat, lon]);
            s.marker.setStyle({ opacity: 1, fillOpacity: .95 });
            s.marker.openTooltip();
        });
        _pb.slider.value = t - _pb.t0;
        _pb.label.textContent = pbFormat(t);
    }

    function pbFrame(now) {
        if (!_pb || !_pb.playing) return;
        const dt = (now - _pb.last) / 1000;
        _pb.last = now;
        _pb.cur = Math.min(_pb.t1, _pb.cur + dt * _pb.speed);
        pbRender();
        if (_pb.cur >= _pb.t1) { pbSetPlaying(false); return; }
        _pb.raf = requestAnimationFrame(pbFrame);
    }

    function pbSetPlaying(playing) {
        if (!_pb) return;
        if (playing && _pb.cur >= _pb.t1) _pb.cur = _pb.t0;   // replay from the start
        _pb.playing = playing;
        _pb.btn.innerHTML = playing ? '<i class="bi bi-pause-fill"></i>' : '<i class="bi bi-play-fill"></i>';
        if (playing) {
            _pb.last = performance.now();
            _pb.raf = requestAnimationFrame(pbFrame);
        } else {
            cancelAnimationFrame(_pb.raf);
        }
    }

    function pbClose() {
        if (!_pb) return;
        cancelAnimationFrame(_pb.raf);
        _pb.series.forEach(s => { s.group.remove(); s.marker.remove(); });
        _map?.removeControl(_pb.control);
        _pb = null;
        PB_HIDDEN.forEach(n => setLayerVisible(n, _wanted[n]));
    }

    // Colours assigned by the page (device key -> colour); the hash is only a fallback
    // for devices it does not know about.
    let _deviceColours = {};

    function escapeHtml(v) {
        return String(v).replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
    }

    function deviceColour(key) {
        if (_deviceColours[key]) return _deviceColours[key];
        let hash = 0;
        for (let i = 0; i < key.length; i++) {
            hash = (Math.imul(31, hash) + key.charCodeAt(i)) | 0;
        }
        const hue = ((hash >>> 0) % 360);
        return `hsl(${hue},70%,55%)`;
    }

    function ensureLayers() {
        if (!_trackLayer)    _trackLayer    = L.layerGroup().addTo(_map);
        if (!_pointLayer)    _pointLayer    = L.layerGroup().addTo(_map);
        if (!_waypointLayer) _waypointLayer = L.layerGroup().addTo(_map);
    }

    function setLayerVisible(name, visible) {
        const layer = { tracks: _trackLayer, points: _pointLayer, waypoints: _waypointLayer, heat: _heatLayer }[name];
        if (!layer || !_map) return;
        if (visible) { if (!_map.hasLayer(layer)) _map.addLayer(layer); }
        else         { if (_map.hasLayer(layer))  _map.removeLayer(layer); }
    }

    return {
        setDeviceColours(colours) {
            _deviceColours = colours || {};
        },

        getBrowserTimeZone() {
            return Intl.DateTimeFormat().resolvedOptions().timeZone;
        },

        init(elementId, options, dotNetRef) {
            if (_map) { _map.remove(); _map = null; }

            const center = (options.centerLat && options.centerLon)
                ? [options.centerLat, options.centerLon]
                : [0, 0];
            const zoom = (options.centerLat && options.centerLon)
                ? (options.defaultZoom || 13)
                : 2;

            _map = L.map(elementId, { zoomControl: true, preferCanvas: true }).setView(center, zoom);

            // Basemaps: the configured one first, then key-free alternatives
            const configured = {
                name: 'Configured', url: options.tileUrl,
                attribution: options.tileAttribution, maxZoom: 19,
            };
            // Only pass subdomains when the URL template actually uses {s}
            if (options.tileUrl.includes('{s}')) configured.subdomains = 'abcd';

            const baseLayers = {};
            [configured, ...BASEMAPS].forEach(({ name, url, ...opts }) => {
                baseLayers[name] = L.tileLayer(url, opts);
            });

            let saved = null;
            try { saved = localStorage.getItem('tv.basemap'); } catch (_) {}
            const initial = baseLayers[saved] ? saved : configured.name;
            _tileLayer = baseLayers[initial].addTo(_map);

            L.control.layers(baseLayers, null, { position: 'topright', collapsed: true }).addTo(_map);
            _map.on('baselayerchange', e => {
                _tileLayer = e.layer;
                try { localStorage.setItem('tv.basemap', e.name); } catch (_) {}
            });

            ensureLayers();

            // Tell Blazor when the user pans/zooms (debounced); programmatic fits are ignored.
            if (dotNetRef) {
                let timer = null;
                _map.on('moveend', () => {
                    if (_fitting) return;
                    clearTimeout(timer);
                    timer = setTimeout(() => {
                        const b = _map.getBounds();
                        dotNetRef.invokeMethodAsync('OnViewChanged',
                            b.getSouth(), b.getWest(), b.getNorth(), b.getEast())
                            .catch(() => {});
                    }, 500);
                });
            }
        },

        setTracks(geoJson) {
            _trackLayer.clearLayers();
            if (!geoJson || !geoJson.features) return;

            geoJson.features.forEach(f => {
                if (f.geometry.type !== 'LineString') return;
                const key    = `${f.properties.user}/${f.properties.device}`;
                const colour = deviceColour(key);
                L.polyline(
                    f.geometry.coordinates.map(c => [c[1], c[0]]),
                    { color: colour, weight: 3, opacity: .85, lineJoin: 'round' }
                ).bindTooltip(`${key} — segment ${f.properties.segment ?? 0}`, { sticky: true })
                 .addTo(_trackLayer);
            });
        },

        setPoints(geoJson) {
            _pointLayer.clearLayers();
            if (!geoJson || !geoJson.features) return;

            geoJson.features.forEach(f => {
                if (f.geometry.type !== 'Point') return;
                const p      = f.properties;
                const key    = `${p.user}/${p.device}`;
                const colour = deviceColour(key);
                const ts     = new Date(p.timestamp * 1000).toLocaleString();
                L.circleMarker([f.geometry.coordinates[1], f.geometry.coordinates[0]], {
                    radius: 5, color: colour, fillColor: colour,
                    fillOpacity: .85, weight: 1.5
                }).bindPopup(
                    `<b>${key}</b><br>` +
                    `${ts}<br>` +
                    (p.venue     ? `Venue: ${escapeHtml(p.venue)}<br>`  : '') +
                    (p.velocity  != null ? `Speed: ${p.velocity} km/h<br>` : '') +
                    (p.battery   != null ? `Battery: ${p.battery}%<br>`   : '') +
                    (p.accuracy  != null ? `Accuracy: ${p.accuracy} m<br>`: '') +
                    (p.trigger   != null ? `Trigger: ${p.trigger}`         : '')
                ).addTo(_pointLayer);
            });
        },

        setWaypoints(waypoints) {
            _waypointLayer.clearLayers();
            if (!waypoints) return;

            waypoints.forEach(w => {
                const radius = w.radius || 50;
                const colour = deviceColour(w.device);
                L.circle([w.lat, w.lon], {
                    radius,
                    color: colour, fillColor: colour,
                    fillOpacity: .08, weight: 1.5
                }).addTo(_waypointLayer);

                const icon = L.divIcon({
                    className: '',
                    html: `<div style="
                        width:10px;height:10px;border-radius:50%;
                        background:${colour};border:2px solid #fff;
                        box-shadow:0 0 4px rgba(0,0,0,.5)"></div>`,
                    iconSize: [10, 10], iconAnchor: [5, 5]
                });
                L.marker([w.lat, w.lon], { icon })
                 .bindTooltip(
                     `<b>${w.label || 'Waypoint'}</b><br>${w.device}<br>radius ${radius} m`,
                     { direction: 'top' }
                 )
                 .addTo(_waypointLayer);
            });
        },

        setHeatmap(latLonArr) {
            if (_heatLayer) { _map.removeLayer(_heatLayer); _heatLayer = null; }
            if (!latLonArr || latLonArr.length === 0) return;

            _heatLayer = L.heatLayer(
                latLonArr.map(p => [p[0], p[1], p[2] ?? 0.5]),
                { radius: 18, blur: 20, maxZoom: 17 }
            ).addTo(_map);
        },

        showLayer(name, visible) {
            _wanted[name] = visible;
            // While a replay runs the static tracks stay hidden, otherwise the growing
            // trail is drawn on top of an identical line and nothing seems to move.
            setLayerVisible(name, visible && !(_pb && PB_HIDDEN.includes(name)));
        },

        clearAll() {
            _trackLayer?.clearLayers();
            _pointLayer?.clearLayers();
            _waypointLayer?.clearLayers();
            if (_heatLayer) { _map.removeLayer(_heatLayer); _heatLayer = null; }
        },

        fitBounds() {
            const bounds = [];
            [_trackLayer, _pointLayer, _waypointLayer].forEach(l => {
                if (l) {
                    try {
                        const b = l.getBounds();
                        if (b.isValid()) bounds.push(b);
                    } catch (_) {}
                }
            });
            if (bounds.length === 0) return;
            const all = bounds.reduce((a, b) => a.extend(b));
            // Suppress the moveend this fit triggers (it may not fire if the view is unchanged)
            _fitting = true;
            setTimeout(() => { _fitting = false; }, 800);
            _map.fitBounds(all, { padding: [30, 30] });
        },

        getBounds() {
            if (!_map) return null;
            const b = _map.getBounds();
            return {
                minLat: b.getSouth(), maxLat: b.getNorth(),
                minLon: b.getWest(),  maxLon: b.getEast()
            };
        },

        // series: [{ key, t: [unix s], lat: [], lon: [] }] sorted by time per device
        // gapSeconds: fixes further apart than this are not joined or interpolated
        openPlayback(series, tz, gapSeconds) {
            pbClose();
            series = (series || []).filter(s => s.t.length > 0);
            if (!_map || series.length === 0) return false;

            const t0 = Math.min(...series.map(s => s.t[0]));
            const t1 = Math.max(...series.map(s => s.t[s.t.length - 1]));
            const span = Math.max(1, t1 - t0);
            // slowest preset that plays the whole range in about 90 s
            const speed = SPEEDS.find(v => span / v <= 90) ?? SPEEDS[SPEEDS.length - 1];

            PB_HIDDEN.forEach(n => setLayerVisible(n, false));

            const control = L.control({ position: 'bottomleft' });
            control.onAdd = () => {
                const div = L.DomUtil.create('div', 'leaflet-bar tv-playback');
                div.style.cssText = 'background:var(--tv-surface,#222);color:var(--tv-text-primary,#eee);' +
                    'padding:6px 10px;display:flex;gap:8px;align-items:center;min-width:320px;max-width:92vw;flex-wrap:wrap';
                div.innerHTML =
                    '<button type="button" class="tv-quick-btn pb-play" title="Play / pause"><i class="bi bi-play-fill"></i></button>' +
                    '<input type="range" class="pb-slider" style="flex:1;min-width:120px" min="0" max="' + span + '" step="1" value="0">' +
                    '<select class="pb-speed tv-input" style="width:auto" title="Speed">' +
                    SPEEDS.map(v => `<option value="${v}"${v === speed ? ' selected' : ''}>${v}×</option>`).join('') +
                    '</select>' +
                    '<button type="button" class="tv-quick-btn pb-close" title="Close playback"><i class="bi bi-x-lg"></i></button>' +
                    '<div class="pb-label" style="flex-basis:100%;font-size:12px;color:var(--tv-text-muted,#aaa)"></div>';
                L.DomEvent.disableClickPropagation(div);
                L.DomEvent.disableScrollPropagation(div);
                return div;
            };
            control.addTo(_map);
            const el = control.getContainer();

            _pb = {
                control, t0, t1, cur: t0, speed, playing: false, raf: 0, last: 0,
                tz, gap: gapSeconds || 1800,
                btn: el.querySelector('.pb-play'),
                slider: el.querySelector('.pb-slider'),
                label: el.querySelector('.pb-label'),
                series: series.map(s => {
                    const colour = deviceColour(s.key);
                    const marker = L.circleMarker([s.lat[0], s.lon[0]], {
                        radius: 8, color: '#fff', weight: 2, fillColor: colour, fillOpacity: 0, opacity: 0,
                    }).bindTooltip(s.key, { permanent: true, direction: 'top', offset: [0, -8] }).addTo(_map);
                    return { ...s, colour, marker, group: L.layerGroup().addTo(_map), line: null, drawn: -1 };
                }),
            };

            _pb.btn.onclick = () => pbSetPlaying(!_pb.playing);
            el.querySelector('.pb-close').onclick = () => pbClose();
            el.querySelector('.pb-speed').onchange = e => { _pb.speed = Number(e.target.value); };
            _pb.slider.oninput = () => { _pb.cur = _pb.t0 + Number(_pb.slider.value); pbRender(); };

            pbRender();
            return true;
        },

        closePlayback() { pbClose(); },

        invalidateSize() {
            _map?.invalidateSize();
        },
    };
})();

// Saves text as a file (used by the Reports CSV export)
window.downloadText = function (filename, text, mime) {
    const blob = new Blob([text], { type: mime || 'text/plain' });
    const href = URL.createObjectURL(blob);
    const a = document.createElement('a');
    a.href = href;
    a.download = filename;
    document.body.appendChild(a);
    a.click();
    document.body.removeChild(a);
    URL.revokeObjectURL(href);
};

// GPX download helper used by Export.razor
window.downloadGpx = async function (url, jsonBody, filename) {
    const resp = await fetch(url, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: jsonBody,
    });
    if (!resp.ok) throw new Error(`Server returned ${resp.status}`);
    const blob = await resp.blob();
    const href = URL.createObjectURL(blob);
    const a = document.createElement('a');
    a.href = href;
    a.download = filename;
    document.body.appendChild(a);
    a.click();
    document.body.removeChild(a);
    URL.revokeObjectURL(href);
};
