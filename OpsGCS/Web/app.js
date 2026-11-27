/* OpsGCS operator UI. Mission Planner pushes state over the WebView2 bridge; this page only
   renders it and sends requests back. Without the bridge (opened in a normal browser) it runs
   on built-in demo data so the layout can be reviewed. */
(function () {
  'use strict';
  var bridge = window.chrome && window.chrome.webview;
  var $ = function (id) { return document.getElementById(id); };
  var esc = function (s) { return String(s == null ? '' : s).replace(/[&<>"]/g, function (c) { return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c]; }); };
  var LV = ['unknown', 'ok', 'caution', 'warning'];
  var S = { st: null, follow: true, fitted: false, missionVer: -1, vsVer: -1, wxVer: -1, portsSig: '', userPicked: false };

  var screenHooks = [];
  function store(key, val) {
    try { if (val === undefined) return window.localStorage.getItem(key); window.localStorage.setItem(key, val); } catch (e) { /* storage unavailable */ }
    return null;
  }

  function send(m) { if (bridge) bridge.postMessage(m); else if (window.OpsDemo) window.OpsDemo.handle(m); }

  // ---------------------------------------------------------------- map
  var map = L.map('map', { zoomControl: false, attributionControl: true, worldCopyJump: true, maxZoom: 20 }).setView([37.95, 23.72], 11);
  map.attributionControl.setPrefix('');

  // Basemaps: Mission Planner serves every tile (and caches it), so the page only names the source.
  var BASEMAPS = [
    { id: 'mp', label: 'Mission Planner map', group: 'Imagery', max: 19, attr: 'Imagery via Mission Planner map provider' },
    { id: 'esri-img', label: 'Esri World Imagery', group: 'Imagery', max: 19, attr: 'Imagery © Esri, Maxar, Earthstar Geographics' },
    { id: 'google-sat', label: 'Google Satellite', group: 'Imagery', max: 20, attr: 'Imagery © Google' },
    { id: 'google-hyb', label: 'Google Hybrid', group: 'Imagery', max: 20, attr: 'Imagery © Google' },
    { id: 'bing-hyb', label: 'Bing Hybrid', group: 'Imagery', max: 19, attr: '© Microsoft' },
    { id: 'topo', label: 'OpenTopoMap', group: 'Topographic', max: 17, attr: '© OpenStreetMap contributors, SRTM · style © OpenTopoMap (CC-BY-SA)' },
    { id: 'esri-topo', label: 'Esri World Topo', group: 'Topographic', max: 19, attr: 'Tiles © Esri' },
    { id: 'google-ter', label: 'Google Terrain', group: 'Topographic', max: 20, attr: '© Google' },
    { id: 'osm', label: 'OpenStreetMap', group: 'Street', max: 19, attr: '© OpenStreetMap contributors' }
  ];
  var baseLayer = null, baseId = 'mp';
  function basemap(id) { for (var i = 0; i < BASEMAPS.length; i++) if (BASEMAPS[i].id === id) return BASEMAPS[i]; return BASEMAPS[0]; }
  function setBasemap(id) {
    var b = basemap(id);
    baseId = b.id;
    store('opsgcs.basemap', b.id);
    if (baseLayer) { map.removeLayer(baseLayer); baseLayer = null; }
    if (bridge) {
      baseLayer = L.tileLayer('https://tiles.opsgcs.local/' + b.id + '/{z}/{x}/{y}', { maxZoom: 20, maxNativeZoom: b.max, attribution: b.attr });
      baseLayer.addTo(map).bringToBack();
    }
    $('basemap').value = b.id;
    renderLayerSummary();
  }
  var lyrRoute = L.layerGroup().addTo(map);
  var lyrVeh = L.layerGroup().addTo(map);
  var lyrWx = L.layerGroup().addTo(map);
  var vsOverlay = null;
  map.on('dragstart', function () { if (S.follow) setFollow(false); });

  $('zin').onclick = function () { map.zoomIn(); };
  $('zout').onclick = function () { map.zoomOut(); };
  $('fit').onclick = function () { if (!fitAll()) toast('Nothing to fit yet: no vehicle position, mission or overlay.'); };
  $('follow').onclick = function () {
    if (S.follow) { setFollow(false); toast('Follow off'); return; }
    setFollow(true);
    var a = S.st && active(S.st);
    if (a && a.hasPos) map.panTo([a.lat, a.lon]);
    else toast('Follow on: the map will centre on the selected vehicle once it reports a position.');
  };
  function setFollow(on) {
    S.follow = on;
    var b = $('follow');
    b.classList.toggle('on', on);
    b.setAttribute('aria-pressed', on ? 'true' : 'false');
  }
  setFollow(true);

  var toastTimer = null;
  function toast(text) {
    var t = $('maptoast');
    t.textContent = text;
    t.hidden = false;
    clearTimeout(toastTimer);
    toastTimer = setTimeout(function () { t.hidden = true; }, 2600);
  }

  /** Fits vehicles, the mission, then any overlay; false when there is nothing on the map. */
  function fitAll() {
    if (S.screen === 'PLAN' && Ops.planFit) return Ops.planFit();
    var pts = [];
    (S.st && S.st.vehicles || []).forEach(function (v) {
      if (v.hasPos) pts.push([v.lat, v.lon]);
      if (v.home) pts.push([v.home.lat, v.home.lon]);
    });
    var m = S.st && S.st.mission;
    (m && m.items || []).forEach(function (w) { if (w.lat || w.lon) pts.push([w.lat, w.lon]); });
    if (!pts.length && vsOverlay) pts = [vsOverlay.getBounds().getSouthWest(), vsOverlay.getBounds().getNorthEast()];
    if (!pts.length && lyrWx.getLayers().length) {
      var b = L.latLngBounds([]);
      lyrWx.eachLayer(function (l) { if (l.getLatLng) b.extend(l.getLatLng()); else if (l.getBounds) b.extend(l.getBounds()); });
      if (b.isValid()) pts = [b.getSouthWest(), b.getNorthEast()];
    }
    if (!pts.length) return false;
    if (pts.length === 1) map.setView(pts[0], Math.max(map.getZoom(), 15));
    else map.fitBounds(pts, { padding: [60, 60], maxZoom: 17 });
    return true;
  }

  // ---------------------------------------------------------------- collapsible map panels
  // collapsed state is kept per screen; on PLAN the panels start collapsed to leave the map to the route
  function collapsible(panelId, hdId) {
    var p = $(panelId), h = $(hdId);
    var key = function () { return 'opsgcs.collapsed.' + panelId + (S.screen === 'PLAN' ? '.plan' : ''); };
    function set(c) { p.classList.toggle('collapsed', c); h.setAttribute('aria-expanded', c ? 'false' : 'true'); }
    function load() { var v = store(key()); set(v === null ? S.screen === 'PLAN' : v === '1'); }
    load();
    h.onclick = function () { var c = !p.classList.contains('collapsed'); set(c); store(key(), c ? '1' : '0'); };
    screenHooks.push(load);
  }
  collapsible('layers', 'layers-hd');
  collapsible('wxcard', 'wx-hd');

  (function () {
    var groups = {}, order = [];
    BASEMAPS.forEach(function (b) { if (!groups[b.group]) { groups[b.group] = []; order.push(b.group); } groups[b.group].push(b); });
    $('basemap').innerHTML = order.map(function (g) {
      return '<optgroup label="' + esc(g) + '">' + groups[g].map(function (b) { return '<option value="' + b.id + '">' + esc(b.label) + '</option>'; }).join('') + '</optgroup>';
    }).join('');
    $('basemap').onchange = function () { setBasemap(this.value); };
    setBasemap(store('opsgcs.basemap') || 'mp');
  })();

  // ---------------------------------------------------------------- top bar
  document.querySelectorAll('.nav-tab').forEach(function (b) {
    b.onclick = function () { if (!b.disabled) setScreen(b.dataset.nav); };
  });
  function setScreen(name) {
    if (name !== 'PLAN') name = 'FLY';
    if (S.screen === name) return;
    S.screen = name;
    store('opsgcs.screen', name);
    $('app').classList.toggle('plan', name === 'PLAN');
    document.querySelectorAll('.nav-tab').forEach(function (b) { b.classList.toggle('active', b.dataset.nav === name); });
    if (name === 'PLAN') { map.removeLayer(lyrRoute); S.followWas = S.follow; setFollow(false); }
    else { lyrRoute.addTo(map); if (S.followWas) setFollow(true); }
    screenHooks.forEach(function (f) { f(name); });
    setTimeout(function () { map.invalidateSize(); if (S.st) renderTimeline(active(S.st), S.st.mission); }, 0);
  }
  $('connect').onclick = function () { send({ type: 'connect', port: $('port').value, baud: $('baud').value }); };
  $('port').onchange = $('baud').onchange = function () { S.userPicked = true; };
  $('role').onclick = function () { send({ type: 'engineer' }); };

  function fillSelect(sel, items, current) {
    var html = items.map(function (p) { return '<option' + (p === current ? ' selected' : '') + '>' + esc(p) + '</option>'; }).join('');
    if (sel.innerHTML !== html) sel.innerHTML = html;
  }

  function renderTop(st) {
    $('brand').textContent = st.product || 'OPS GCS';
    var l = st.link || {};
    var sig = (l.ports || []).join('|') + '#' + (l.bauds || []).join('|');
    if (sig !== S.portsSig || l.connected || !S.userPicked) {
      fillSelect($('port'), l.ports || [], S.userPicked && !l.connected ? $('port').value : l.port);
      fillSelect($('baud'), l.bauds || [], S.userPicked && !l.connected ? $('baud').value : String(l.baud));
      S.portsSig = sig;
    }
    $('port').disabled = $('baud').disabled = !!l.connected;
    var c = $('connect');
    c.textContent = l.connected ? 'DISCONNECT' : 'CONNECT';
    c.classList.toggle('on', !!l.connected);
    var r = $('role');
    r.textContent = st.role || 'OPERATOR';
    r.classList.toggle('eng', st.role === 'ENGINEER');
  }

  setInterval(function () {
    var d = new Date();
    $('clock').textContent = d.toISOString().substr(11, 8) + 'Z';
  }, 500);

  // ---------------------------------------------------------------- helpers
  function active(st) {
    var a = null;
    (st.vehicles || []).forEach(function (v) { if (v.active) a = v; });
    return a;
  }
  function lvOf(v, key) {
    var r = 0;
    (v && v.health || []).forEach(function (h) { if (h.key === key) r = h.level; });
    return r;
  }
  function fmtDist(m) { return m >= 1000 ? (m / 1000).toFixed(2) + ' km' : Math.round(m) + ' m'; }
  function fmtDur(s) {
    if (!(s >= 0) || !isFinite(s)) return '--:--';
    s = Math.round(s);
    var h = Math.floor(s / 3600), mi = Math.floor(s % 3600 / 60), se = s % 60;
    var p = function (n) { return (n < 10 ? '0' : '') + n; };
    return h ? h + ':' + p(mi) + ':' + p(se) : p(mi) + ':' + p(se);
  }
  function stateText(v) {
    if (!v || !v.connected) return ['--', 'unknown'];
    if (v.failsafe) return ['FAILSAFE', 'warning'];
    return v.armed ? ['ARMED', 'caution'] : ['DISARMED', 'ok'];
  }

  // ---------------------------------------------------------------- strip, fleet, health
  function renderStrip(v) {
    $('s-veh').textContent = v ? v.name : 'NO VEHICLE';
    $('s-veh').className = 'val mono ' + (v && v.connected ? 'accent' : 'lv-unknown');
    $('s-mode').textContent = v && v.mode ? v.mode.toUpperCase() : '--';
    var s = stateText(v);
    $('s-state').textContent = s[0];
    $('s-state').className = 'val mono lv-' + s[1];
    var keys = ['LINK', 'BATT', 'GPS', 'EKF', 'VIBE'];
    var hs = (v && v.health) || keys.map(function (k) { return { key: k, level: 0, value: '--', detail: '' }; });
    $('s-health').innerHTML = hs.map(function (h) {
      var lv = LV[h.level] || 'unknown';
      return '<div class="cell bl-' + lv + '" title="' + esc(h.detail) + '"><div class="lbl">' + esc(h.key) +
        '</div><div class="val mono lv-' + lv + '">' + esc(h.value) + (h.extra ? ' <small>' + esc(h.extra) + '</small>' : '') + '</div></div>';
    }).join('');
  }

  $('fleet').addEventListener('click', function (e) {
    var b = e.target.closest('.veh');
    if (b) { send({ type: 'select', key: b.dataset.key }); setFollow(true); }
  });

  function renderFleet(st) {
    var vs = st.vehicles || [];
    $('fleet-title').textContent = 'FLEET · ' + vs.length;
    if (!vs.length) { $('fleet').innerHTML = '<div class="empty">No vehicles. Connect a link to populate the fleet.</div>'; return; }
    $('fleet').innerHTML = vs.map(function (v) {
      var s = stateText(v);
      var worst = LV[v.worst] || 'unknown';
      var bl = lvOf(v, 'BATT'), ll = lvOf(v, 'LINK');
      return '<button class="veh' + (v.active ? ' active' : '') + '" data-key="' + esc(v.key) + '" style="border-left-color:var(--' + (worst === 'unknown' ? 'unknown' : worst) + ')">' +
        '<div class="top"><span class="name mono">' + esc(v.name) + '</span><span class="st lv-' + s[1] + '">' + s[0] + ' · ' + esc((v.mode || '--').toUpperCase()) + '</span></div>' +
        '<div class="nums mono"><span class="' + (bl >= 2 ? 'lv-' + LV[bl] : '') + '">BATT ' + (v.battPct >= 0 ? v.battPct + '%' : v.battV.toFixed(1) + 'V') + '</span>' +
        '<span class="' + (ll >= 2 ? 'lv-' + LV[ll] : '') + '">LINK ' + v.linkPct + '%</span>' +
        '<span>' + Math.round(v.alt) + ' m</span><span>' + (v.wpCount > 1 ? 'WP ' + v.wp + '/' + (v.wpCount - 1) : (v.mode || '').toUpperCase()) + '</span></div></button>';
    }).join('');
  }

  function renderHealth(v) {
    $('health-title').textContent = (v ? v.name + ' ' : '') + 'HEALTH';
    var hs = (v && v.health) || [];
    if (!hs.length) { $('health').innerHTML = '<span></span><span class="muted" style="grid-column:2/5">No vehicle selected.</span>'; return; }
    $('health').innerHTML = hs.map(function (h) {
      var lv = LV[h.level] || 'unknown';
      return '<span class="dot ' + lv + '"></span><b>' + esc(h.key) + '</b><span class="mono lv-' + lv + '">' + esc(h.value) + '</span><span>' + esc(h.detail) + '</span>';
    }).join('');
  }

  // ---------------------------------------------------------------- map content
  var vehMarkers = {};
  function vehIcon(v) {
    var col = v.active ? '#E0A832' : (v.worst >= 3 ? '#E5484D' : v.worst === 2 ? '#E8B32E' : '#D8DEE6');
    return L.divIcon({
      className: '', iconSize: [28, 28], iconAnchor: [14, 14],
      html: '<svg width="28" height="28" viewBox="-14 -14 28 28" style="transform:rotate(' + (v.hdg || 0) + 'deg)"><path d="M0 -12 L8 10 L0 5 L-8 10Z" fill="' + col + '" stroke="#05080C" stroke-width="1.5"/></svg>'
    });
  }
  function calloutIcon(v) {
    var w = v.worst >= 3 ? ' warning' : v.worst === 2 ? ' caution' : '';
    var txt = '<b>' + esc(v.name) + '</b> ' + Math.round(v.alt) + ' m · ' + (v.gs || 0).toFixed(0) + ' m/s' + (v.active ? '' : ' · ' + esc((v.mode || '').toUpperCase()));
    return L.divIcon({ className: '', iconSize: null, iconAnchor: [-16, 12], html: '<div class="callout' + w + '">' + txt + '</div>' });
  }

  function renderVehicles(st) {
    var seen = {};
    (st.vehicles || []).forEach(function (v) {
      if (!v.hasPos) return;
      seen[v.key] = true;
      var m = vehMarkers[v.key];
      if (!m) {
        m = vehMarkers[v.key] = {
          arrow: L.marker([v.lat, v.lon], { icon: vehIcon(v), interactive: true, zIndexOffset: 1000 }).addTo(lyrVeh),
          label: L.marker([v.lat, v.lon], { icon: calloutIcon(v), interactive: false, zIndexOffset: 900 }).addTo(lyrVeh),
          home: null
        };
        m.arrow.on('click', function () { send({ type: 'select', key: v.key }); });
      }
      m.arrow.setLatLng([v.lat, v.lon]).setIcon(vehIcon(v));
      m.label.setLatLng([v.lat, v.lon]).setIcon(calloutIcon(v));
      if (v.home) {
        if (!m.home) m.home = L.circleMarker([v.home.lat, v.home.lon], { radius: 7, color: '#05080C', weight: 2, fillColor: '#3FB96E', fillOpacity: 1 }).addTo(lyrVeh);
        else m.home.setLatLng([v.home.lat, v.home.lon]);
      }
    });
    Object.keys(vehMarkers).forEach(function (k) {
      if (!seen[k]) {
        var m = vehMarkers[k];
        lyrVeh.removeLayer(m.arrow); lyrVeh.removeLayer(m.label); if (m.home) lyrVeh.removeLayer(m.home);
        delete vehMarkers[k];
      }
    });
    var a = active(st);
    if (a && a.hasPos && S.follow) map.panTo([a.lat, a.lon], { animate: false });
    if (!S.fitted && a && a.hasPos) { S.fitted = true; fitAll(); }
  }

  var routeSig = '';
  function renderRoute(m, cur) {
    var sig = (m ? m.ver : 'none') + '/' + cur;
    if (sig === routeSig) return;
    routeSig = sig;
    lyrRoute.clearLayers();
    if (!m || !m.items || !m.items.length) return;
    var pts = m.items.filter(function (w) { return w.nav && (w.lat || w.lon); });
    if (pts.length > 1) L.polyline(pts.map(function (w) { return [w.lat, w.lon]; }), { color: '#E0A832', weight: 2.5, opacity: .95, interactive: false }).addTo(lyrRoute);
    pts.forEach(function (w) {
      if (w.seq === 0) return;
      L.marker([w.lat, w.lon], {
        interactive: false,
        icon: L.divIcon({ className: '', iconSize: [22, 22], iconAnchor: [11, 11], html: '<div class="wpnum' + (w.seq === cur ? ' cur' : '') + '">' + w.seq + '</div>' })
      }).addTo(lyrRoute);
    });
  }

  // ---------------------------------------------------------------- layers
  $('t-vs').onclick = function () { send({ type: 'layers', viewshed: !st().layers.viewshed }); };
  $('t-wx').onclick = function () { send({ type: 'layers', weather: !st().layers.weather }); };
  document.querySelectorAll('#vs-src button').forEach(function (b) {
    b.onclick = function () { send({ type: 'layers', source: b.dataset.src }); };
  });
  function st() { return S.st || { layers: {} }; }
  function renderLayerSummary() {
    $('layers-sum').textContent = [basemap(baseId).label].concat(S.layerOn || []).join(' · ');
  }

  function renderLayers(s) {
    var l = s.layers || {};
    $('t-vs').classList.toggle('on', !!l.viewshed);
    $('t-wx').classList.toggle('on', !!l.weather);
    $('vs-src').classList.toggle('off', !l.viewshed);
    document.querySelectorAll('#vs-src button').forEach(function (b) { b.classList.toggle('on', b.dataset.src === l.source); });
    $('vs-status').textContent = l.viewshed ? (l.vsStatus || 'Computing…') : 'Off';
    S.layerOn = [];
    if (l.viewshed) S.layerOn.push('Viewshed');
    if (l.weather) S.layerOn.push('Weather');
    renderLayerSummary();
    $('legend').classList.toggle('vs', !!l.viewshed);
    $('legend').classList.toggle('wx', !!l.weather);

    if (s.viewshed && s.viewshed.ver !== S.vsVer) {
      S.vsVer = s.viewshed.ver;
      if (vsOverlay) { map.removeLayer(vsOverlay); vsOverlay = null; }
      if (s.viewshed.png) {
        var b = s.viewshed.bounds;
        vsOverlay = L.imageOverlay('data:image/png;base64,' + s.viewshed.png, [[b.s, b.w], [b.n, b.e]], { className: 'vs', interactive: false }).addTo(map);
      }
    }
    if (!l.viewshed && vsOverlay) { map.removeLayer(vsOverlay); vsOverlay = null; S.vsVer = -1; }

    if (!l.weather) { lyrWx.clearLayers(); $('wxcard').hidden = true; S.wxVer = -1; return; }
    $('wxcard').hidden = false;
    if (s.weather && s.weather.ver !== S.wxVer) {
      S.wxVer = s.weather.ver;
      drawWeather(s.weather);
    }
    renderWxCard(s.weather, active(s));
  }

  function barbSvg(dir, kt) {
    var k = Math.round(Math.max(0, kt) / 5) * 5, pen = Math.floor(k / 50); k -= pen * 50;
    var full = Math.floor(k / 10); k -= full * 10; var half = k >= 5 ? 1 : 0;
    if (pen + full + half === 0) return '<svg width="20" height="20" viewBox="-10 -10 20 20"><circle r="5" fill="none" stroke="#E6EAF0" stroke-width="2"/></svg>';
    var p = '', y = -30;
    for (var i = 0; i < pen; i++) { p += '<path d="M0 ' + y + ' L12 ' + (y + 4) + ' L0 ' + (y + 8) + 'Z" fill="#E6EAF0"/>'; y += 10; }
    for (i = 0; i < full; i++) { p += '<line x1="0" y1="' + y + '" x2="12" y2="' + (y - 4) + '"/>'; y += 5; }
    if (half) { if (pen + full === 0) y += 5; p += '<line x1="0" y1="' + y + '" x2="6" y2="' + (y - 2) + '"/>'; }
    return '<svg width="80" height="80" viewBox="-40 -40 80 80" style="overflow:visible"><g transform="rotate(' + dir + ')" stroke="#E6EAF0" stroke-width="2" stroke-linecap="round" style="filter:drop-shadow(0 0 1.5px #000)">' +
      '<line x1="0" y1="0" x2="0" y2="-30"/>' + p + '<circle r="2.5" fill="#E6EAF0" stroke="none"/></g></svg>';
  }

  function drawWeather(w) {
    lyrWx.clearLayers();
    var dLat = w.cellLat / 2, dLon = w.cellLon / 2;
    (w.grid || []).forEach(function (g) {
      var a = g.precip >= 5 ? .6 : g.precip >= 2 ? .45 : g.precip >= .5 ? .32 : g.precip >= .1 ? .18 : 0;
      if (a > 0) L.rectangle([[g.lat - dLat, g.lon - dLon], [g.lat + dLat, g.lon + dLon]], { stroke: false, fillColor: '#9B7BDE', fillOpacity: a, interactive: false }).addTo(lyrWx);
      if (g.windKt != null && g.windDir != null)
        L.marker([g.lat, g.lon], { interactive: false, icon: L.divIcon({ className: '', iconSize: [80, 80], iconAnchor: [40, 40], html: barbSvg(g.windDir, g.windKt) }) }).addTo(lyrWx);
    });
  }

  function renderWxCard(w, v) {
    var ageEl = $('wx-age');
    if (!w) { ageEl.textContent = 'fetching…'; ageEl.className = 'sum'; $('wx-body').innerHTML = ''; return; }
    var age = (Date.now() - Date.parse(w.fetched)) / 60000;
    var rows = w.lines || [];
    ageEl.textContent = age < 1 ? 'updated just now' : 'updated ' + Math.round(age) + ' min ago';
    ageEl.className = 'sum' + (age > (w.staleMin || 30) ? ' lv-caution' : '') + (w.alert ? ' lv-caution' : '');
    $('wx-body').innerHTML =
      '<div class="grid">' + rows.map(function (r) { return '<span class="k">' + esc(r[0]) + '</span><span>' + esc(r[1]) + '</span>'; }).join('') + '</div>' +
      (w.alert ? '<div class="alert">' + esc(w.alert) + '</div>' : '') +
      (w.errors ? '<div class="err">' + esc(w.errors) + '</div>' : '') +
      '<div class="src">Model: Open-Meteo.com (CC BY 4.0) · METAR: aviationweather.gov</div>';
  }
  function pad3(n) { n = Math.round(n || 0); return (n < 10 ? '00' : n < 100 ? '0' : '') + n; }

  // ---------------------------------------------------------------- right column
  (function ladder() {
    var g = $('ladder'), h = '';
    for (var p = -30; p <= 30; p += 10) {
      if (!p) continue;
      var y = 75 - p * 2, w = p % 20 === 0 ? 50 : 30;
      h += '<line x1="' + (136 - w / 2) + '" y1="' + y + '" x2="' + (136 + w / 2) + '" y2="' + y + '"/>';
    }
    g.innerHTML = h;
  })();

  function renderAttitude(v) {
    $('att-title').textContent = 'ATTITUDE' + (v ? ' · ' + v.name : '');
    var roll = v ? v.roll : 0, pitch = v ? v.pitch : 0;
    $('horizon').setAttribute('transform', 'rotate(' + (-roll) + ' 136 75) translate(0 ' + (Math.max(-40, Math.min(40, pitch)) * 2) + ')');
    $('h-gs').textContent = 'GS ' + (v ? v.gs.toFixed(1) : '--');
    $('h-alt').textContent = 'ALT ' + (v ? Math.round(v.alt) : '--');
    $('h-hdg').textContent = 'HDG ' + (v ? pad3(v.hdg) + '°' : '--');
    $('h-vs').textContent = 'VS ' + (v ? (v.vs >= 0 ? '+' : '') + v.vs.toFixed(1) : '--');
  }

  var cmdSig = '';
  function renderCommands(s, v) {
    $('cmd-title').textContent = 'COMMANDS · ' + (v && v.connected ? v.name : 'NO VEHICLE');
    var cmds = (v && v.connected && s.commands) || [];
    var sig = JSON.stringify(cmds) + (v ? v.key : '');
    if (sig === cmdSig) return;
    cmdSig = sig;
    var list = cmds.concat([{ label: 'Change mode…', mode: '__menu' }]);
    $('cmds').innerHTML = list.map(function (c, i) {
      return '<button data-i="' + i + '"' + (c.caution ? ' class="caution"' : '') + (v && v.connected ? '' : ' disabled') + '>' + esc(c.label) + '</button>';
    }).join('');
    $('cmds').querySelectorAll('button').forEach(function (b) {
      b.onclick = function (e) {
        var c = list[+b.dataset.i];
        if (c.mode === '__menu') showModeMenu(e.clientX, e.clientY);
        else confirmCommand(c.label, c.mode);
      };
    });
  }

  function showModeMenu(x, y) {
    var m = $('modemenu'), modes = (S.st && S.st.modes) || [];
    if (!modes.length) return;
    m.innerHTML = modes.map(function (n) { return '<button>' + esc(n) + '</button>'; }).join('');
    m.hidden = false;
    m.style.left = Math.min(x, innerWidth - 200) + 'px';
    m.style.top = Math.min(y, innerHeight - m.offsetHeight - 8) + 'px';
    m.querySelectorAll('button').forEach(function (b) { b.onclick = function () { m.hidden = true; confirmCommand('Mode ' + b.textContent, b.textContent); }; });
  }
  document.addEventListener('mousedown', function (e) { if (!e.target.closest('#modemenu') && !e.target.closest('#cmds')) $('modemenu').hidden = true; });

  function renderAlerts(alerts) {
    alerts = alerts || [];
    var n = alerts.filter(function (a) { return a.level >= 2; }).length;
    $('alerts-title').textContent = 'ALERTS · ' + n;
    if (!alerts.length) { $('alerts').innerHTML = '<div class="muted">No alerts.</div>'; return; }
    $('alerts').innerHTML = alerts.map(function (a) {
      var cls = a.level === 3 ? 'warning' : a.level === 2 ? 'caution' : a.level === 1 ? 'ok' : '';
      return '<div class="al ' + cls + '"><div class="hd"><b>' + esc(a.veh) + '</b><span class="t">' + esc(a.t) + '</span></div>' + esc(a.text) + '</div>';
    }).join('');
  }

  // ---------------------------------------------------------------- confirm dialog (hold to send)
  var hold = { timer: null, start: 0, label: '', onConfirm: null };
  function confirmHold(o) {
    hold.label = o.hold || 'Hold to send';
    hold.onConfirm = o.onConfirm;
    $('dlg-kicker').textContent = o.kicker || 'CONFIRM COMMAND';
    $('dlg-title').textContent = o.title;
    $('dlg-veh').textContent = o.veh || '';
    $('dlg-vdet').innerHTML = o.vdet || '';
    $('dlg-facts').innerHTML = (o.facts || []).map(function (f) {
      return '<span class="k">' + esc(f[0]) + '</span><span' + (f[2] ? ' class="lv-' + f[2] + '"' : '') + '>' + esc(f[1]) + '</span>';
    }).join('');
    $('dlg-holdtxt').textContent = hold.label;
    $('dlg-fill').style.width = '0';
    $('modal').hidden = false;
    $('dlg-cancel').focus();
  }
  function confirmCommand(label, mode) {
    var v = active(S.st);
    if (!v || !v.connected) return;
    var key = v.key;
    var facts = [['Mode change', (v.mode || '?').toUpperCase() + '  →  ' + mode.toUpperCase()],
      ['Distance home', fmtDist(v.distHome)],
      ['Battery', (v.battPct >= 0 ? v.battPct + '%  ' : '') + '(' + v.battV.toFixed(1) + ' V)']];
    if (/^rtl$/i.test(mode) && v.gs > 1) facts.push(['Time to home', fmtDur(v.distHome / v.gs) + ' at ' + v.gs.toFixed(0) + ' m/s']);
    if (v.wpCount > 1) facts.push(['Mission', 'WP ' + v.wp + '/' + (v.wpCount - 1)]);
    confirmHold({
      title: label, veh: v.name, facts: facts, hold: 'Hold to send ' + mode.toUpperCase(),
      vdet: esc(v.firmware) + ' · sysid ' + esc(v.sysid) + '<br><span class="muted">Only this vehicle receives the command.</span>',
      onConfirm: function () { send({ type: 'setMode', key: key, mode: mode }); }
    });
  }
  function closeDialog() { stopHold(); $('modal').hidden = true; }
  function startHold() {
    if (hold.timer) return;
    hold.start = performance.now();
    hold.timer = setInterval(function () {
      var p = Math.min(1, (performance.now() - hold.start) / 1500);
      $('dlg-fill').style.width = (p * 100) + '%';
      $('dlg-holdtxt').textContent = hold.label + '  ' + (p * 1.5).toFixed(1) + ' s';
      if (p >= 1) { stopHold(); $('modal').hidden = true; var f = hold.onConfirm; hold.onConfirm = null; if (f) f(); }
    }, 30);
  }
  function stopHold() {
    if (hold.timer) { clearInterval(hold.timer); hold.timer = null; }
    $('dlg-fill').style.width = '0';
    $('dlg-holdtxt').textContent = hold.label;
  }
  var hb = $('dlg-hold');
  hb.addEventListener('pointerdown', function (e) { hb.setPointerCapture(e.pointerId); startHold(); });
  hb.addEventListener('pointerup', stopHold);
  hb.addEventListener('pointercancel', stopHold);
  $('dlg-cancel').onclick = closeDialog;
  document.addEventListener('keydown', function (e) {
    if ($('modal').hidden) return;
    if (e.key === 'Escape') closeDialog();
    if (e.code === 'Space' && !e.repeat) { e.preventDefault(); startHold(); }
  });
  document.addEventListener('keyup', function (e) { if (e.code === 'Space' && !$('modal').hidden) stopHold(); });

  // ---------------------------------------------------------------- timeline
  function renderTimeline(v, m) {
    $('tl-title').textContent = 'MISSION · ' + (v ? v.name : '--');
    var flyable = v && v.wpCount > 1 ? v.wpCount - 1 : 0;
    $('m-wp').textContent = flyable ? Math.max(0, v.wp) + '/' + flyable : '--';
    $('m-towp').textContent = v && v.wpDist > 0 ? fmtDist(v.wpDist) : '--';
    $('m-eta').textContent = v && v.wpDist > 0 && v.gs > 0.5 ? fmtDur(v.wpDist / v.gs) : '--:--';
    $('m-home').textContent = v ? fmtDist(v.distHome) : '--';
    $('m-air').textContent = v ? fmtDur(v.timeInAir) : '--:--';
    $('m-gs').textContent = v ? v.gs.toFixed(1) + ' m/s' : '--';

    var prof = m && m.profile || [];
    var total = prof.length ? prof[prof.length - 1].d : 0;
    var along = progressAlong(v, m);
    $('m-pct').textContent = total ? Math.round(100 * along / total) + '%' : '0%';
    drawProfile(prof, m, along);
  }

  function progressAlong(v, m) {
    if (!v || !m || !m.legs || v.wp <= 0) return 0;
    var d = 0;
    for (var i = 0; i < m.legs.length && m.legs[i].to <= v.wp; i++) {
      if (m.legs[i].to === v.wp) return Math.max(d, d + m.legs[i].len - (v.wpDist || 0));
      d += m.legs[i].len;
    }
    return d;
  }

  function drawProfile(prof, m, along) {
    profileSvg($('profile'), prof, { marks: (m && m.wpMarks) || [], legs: m && m.legs, minClearance: m && m.minClearance, along: along, empty: 'No mission loaded on the selected vehicle.' });
  }

  /** Altitude profile: terrain fill, planned line, waypoint marks, shading where a checked leg is too low. */
  function profileSvg(svg, prof, o) {
    var W = svg.clientWidth || 1000, H = svg.clientHeight || 110;
    svg.setAttribute('viewBox', '0 0 ' + W + ' ' + H);
    if (!prof || prof.length < 2) {
      svg.innerHTML = '<text x="44" y="' + (H / 2) + '">' + esc(o.empty || '') + '</text>';
      return;
    }
    var L0 = 44, R = W - 4, T = o.labels ? 18 : 6, B = H - 18;
    var maxA = 0, minA = 1e9;
    prof.forEach(function (p) { maxA = Math.max(maxA, p.amsl, p.terrain); minA = Math.min(minA, p.terrain, p.amsl); });
    minA = Math.max(0, Math.floor(minA / 50) * 50 - 50); maxA = Math.ceil((maxA + 30) / 50) * 50;
    var total = prof[prof.length - 1].d || 1;
    var x = function (d) { return L0 + (R - L0) * d / total; };
    var y = function (a) { return B - (B - T) * (a - minA) / (maxA - minA || 1); };
    var g = '';
    [minA, (minA + maxA) / 2, maxA].forEach(function (a) {
      g += '<line x1="' + L0 + '" y1="' + y(a) + '" x2="' + R + '" y2="' + y(a) + '" stroke="#1E2936"/>' +
        '<text x="0" y="' + (y(a) + 4) + '">' + Math.round(a) + '</text>';
    });
    g += '<text x="' + L0 + '" y="' + (H - 3) + '">0 km</text><text x="' + (R - 60) + '" y="' + (H - 3) + '">' + (total / 1000).toFixed(1) + ' km</text>';
    var terr = 'M' + x(0) + ' ' + B + prof.map(function (p) { return ' L' + x(p.d).toFixed(1) + ' ' + y(p.terrain).toFixed(1); }).join('') + ' L' + x(total) + ' ' + B + 'Z';
    g += '<path d="' + terr + '" fill="#2E3A2C"/>';
    // low clearance only on legs that are checked (not the climb-out or the landing approach)
    var low = o.minClearance || 60, ranges = [], d0 = 0;
    (o.legs || []).forEach(function (l) { if (l.checked !== false) ranges.push([d0, d0 + l.len]); d0 += l.len; });
    var inChecked = function (d) {
      if (!o.legs || !o.legs.length) return true;
      for (var i = 0; i < ranges.length; i++) if (d >= ranges[i][0] - 0.5 && d <= ranges[i][1] + 0.5) return true;
      return false;
    };
    prof.forEach(function (p, i) {
      if (i && p.amsl - p.terrain < low && inChecked(p.d) && inChecked(prof[i - 1].d))
        g += '<rect x="' + x(prof[i - 1].d) + '" y="' + T + '" width="' + Math.max(2, x(p.d) - x(prof[i - 1].d)) + '" height="' + (B - T) + '" fill="#E8B32E" fill-opacity=".16"/>';
    });
    g += '<polyline points="' + prof.map(function (p) { return x(p.d).toFixed(1) + ',' + y(p.amsl).toFixed(1); }).join(' ') + '" fill="none" stroke="#E0A832" stroke-width="2.5"/>';
    (o.marks || []).forEach(function (w) {
      if (o.skipHome !== false && w.seq === 0 && o.marks.length && !o.showHome) return;
      var sel = o.selSeq != null && w.seq === o.selSeq, bad = o.badSeq && o.badSeq[w.seq];
      g += '<circle cx="' + x(w.d) + '" cy="' + y(w.amsl) + '" r="' + (sel ? 6 : 4.5) + '" fill="' + (sel ? '#E0A832' : '#0B1017') + '" stroke="' + (bad ? '#E8B32E' : '#E0A832') + '" stroke-width="' + (bad ? 3 : 2) + '"/>';
      if (o.labels && w.seq) g += '<text x="' + (x(w.d) - 4) + '" y="' + (y(w.amsl) - 9) + '" class="pmk">' + w.seq + '</text>';
    });
    if (o.along > 0) {
      var ax = x(Math.min(total, o.along));
      g += '<line x1="' + ax + '" y1="' + T + '" x2="' + ax + '" y2="' + B + '" stroke="#E0A832" stroke-width="2"/>';
    }
    svg.innerHTML = g;
  }

  // ---------------------------------------------------------------- state in
  function onState(s) {
    var prevMissionVer = S.missionVer;
    S.st = s;
    $('offline').hidden = true;
    var v = active(s);
    renderTop(s);
    renderStrip(v);
    renderFleet(s);
    renderHealth(v);
    if (s.mission && s.mission.ver !== prevMissionVer) S.missionVer = s.mission.ver;
    renderRoute(s.mission, v ? v.wp : -1);
    renderVehicles(s);
    renderLayers(s);
    renderAttitude(v);
    renderCommands(s, v);
    renderAlerts(s.alerts);
    renderTimeline(v, s.mission);
  }

  var listeners = {};
  function dispatch(m) {
    if (!m || !m.type) return;
    if (m.type === 'state') { onState(m); stateHooks.forEach(function (f) { f(m); }); return; }
    (listeners[m.type] || []).forEach(function (f) { f(m); });
  }
  var stateHooks = [];

  // shared with plan.js
  var Ops = window.Ops = {
    map: map, send: send, $: $, esc: esc, toast: toast, store: store, fmtDist: fmtDist, fmtDur: fmtDur,
    state: function () { return S.st; }, active: function () { return S.st ? active(S.st) : null; },
    screen: function () { return S.screen; }, setScreen: function (n) { setScreen(n); },
    on: function (type, f) { (listeners[type] = listeners[type] || []).push(f); },
    onState: function (f) { stateHooks.push(f); }, onScreen: function (f) { screenHooks.push(f); },
    confirmHold: confirmHold, profileSvg: profileSvg, planFit: null
  };

  function boot() {
    if (bridge) {
      bridge.addEventListener('message', function (e) { dispatch(e.data); });
      $('offline').hidden = false;
      send({ type: 'hello' });
    } else if (window.OpsDemo) {
      window.OpsDemo.start(function (st) { dispatch(st); }, dispatch);
    }
    setScreen(store('opsgcs.screen') === 'PLAN' ? 'PLAN' : 'FLY');
  }
  // plan.js loads after this file and registers its hooks before boot
  window.addEventListener('DOMContentLoaded', boot);
  window.addEventListener('resize', function () { if (S.st) renderTimeline(active(S.st), S.st.mission); });
})();
