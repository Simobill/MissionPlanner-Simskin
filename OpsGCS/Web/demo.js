/* Demo data for reviewing the operator UI in a plain browser. Not used inside Mission Planner,
   where app.js receives real state over the WebView2 bridge. */
window.OpsDemo = (function () {
  var cb, t0 = Date.now(), layers = { viewshed: true, source: 'Sensor', weather: true }, active = '1:1';
  function H(k, l, v, d, x) { return { key: k, level: l, value: v, detail: d, extra: x }; }
  var route = [[37.990, 23.640], [38.030, 23.700], [38.060, 23.760], [38.040, 23.830], [38.000, 23.860], [37.970, 23.800], [37.975, 23.730]];
  function mission() {
    var items = route.map(function (p, i) { return { seq: i, lat: p[0], lon: p[1], alt: i ? 300 : 0, nav: true }; });
    var legs = [], prof = [], marks = [], d = 0;
    for (var i = 1; i < route.length; i++) {
      var a = route[i - 1], b = route[i];
      var len = Math.hypot((b[0] - a[0]) * 111320, (b[1] - a[1]) * 87700);
      legs.push({ to: i, len: len });
      for (var k = 0; k < 20; k++) {
        var f = k / 20, dd = d + len * f;
        prof.push({ d: dd, amsl: i === 1 ? 120 + 380 * f : 500 - (i === route.length - 1 ? 200 * f : 0), terrain: 140 + 120 * Math.sin(dd / 1800) + 90 * Math.sin(dd / 700) + (dd > 9000 && dd < 11000 ? 180 : 0) });
      }
      d += len;
      marks.push({ d: d, amsl: 500 });
    }
    prof.push({ d: d, amsl: 300, terrain: 150 });
    return { ver: 1, items: items, legs: legs, profile: prof, wpMarks: marks, minClearance: 60 };
  }
  var M = mission();
  function veh(key, name, fw, mode, armed, lat, lon, alt, gs, hdg, batt, link, health, wp) {
    var worst = 0; health.forEach(function (h) { worst = Math.max(worst, h.level); });
    return { key: key, name: name, sysid: +key[0], firmware: fw, connected: true, active: key === active, mode: mode, armed: armed, failsafe: false,
      hasPos: true, lat: lat, lon: lon, alt: alt, altAmsl: alt + 150, gs: gs, as: gs + 1.5, vs: 0.4, hdg: hdg, roll: 8, pitch: 3,
      battPct: batt, battV: 23.9, linkPct: link, wp: wp, wpCount: wp ? 7 : 0, wpDist: 640, distHome: 7420, timeInAir: 1384,
      windFrom: 284, windMs: 10.8, home: { lat: 37.990, lon: 23.640 }, health: health, worst: worst };
  }
  function state() {
    var t = (Date.now() - t0) / 1000;
    var vs = [
      veh('1:1', 'FW-1', 'ArduPlane', 'Auto', true, 38.052 - 0.002 * Math.sin(t / 20), 23.788 + 0.003 * Math.cos(t / 20), 320, 22.2, 117, 62, 96,
        [H('LINK', 1, '96%', 'GCS packet reception'), H('BATT', 1, '62%', '23.9 V, 14.2 A', '23.9 V'), H('GPS', 1, '3D', '17 sats, HDOP 0.8', '17 sats'), H('EKF', 1, '0.12', 'Max EKF variance ratio'), H('VIBE', 1, '14', 'X 8 Y 9 Z 14 m/s²')], 3),
      veh('2:1', 'MR-2', 'ArduCopter2', 'Loiter', true, 38.015, 23.690, 180, 0.4, 40, 31, 74,
        [H('LINK', 2, '74%', 'GCS packet reception'), H('BATT', 2, '31%', '22.1 V, 18.0 A', '22.1 V'), H('GPS', 1, '3D', '14 sats, HDOP 0.9', '14 sats'), H('EKF', 1, '0.21', 'Max EKF variance ratio'), H('VIBE', 1, '22', 'X 12 Y 15 Z 22 m/s²')], 0),
      veh('3:1', 'UGV-3', 'ArduRover', 'RTL', true, 37.985, 23.660, 0, 2.1, 250, 18, 45,
        [H('LINK', 3, '45%', 'GCS packet reception'), H('BATT', 3, '18%', '21.2 V, 6.0 A', '21.2 V'), H('GPS', 1, '3D', '12 sats, HDOP 1.1', '12 sats'), H('EKF', 1, '0.30', 'Max EKF variance ratio'), H('VIBE', 1, '9', 'X 4 Y 6 Z 9 m/s²')], 0)
    ];
    var grid = [];
    for (var r = 0; r < 6; r++) for (var c = 0; c < 6; c++)
      grid.push({ lat: 38.13 - r * 0.036, lon: 23.58 + c * 0.0455, windDir: 285 + r * 3, windKt: 10 + c * 2 + r, precip: (r < 2 && c < 3) ? 1.4 + r : 0 });
    return {
      type: 'state', product: 'OPS GCS', role: 'OPERATOR',
      link: { connected: true, port: 'COM6', baud: '57600', ports: ['COM6', 'UDP', 'TCP', 'AUTO'], bauds: ['57600', '115200'] },
      vehicles: vs,
      commands: [{ label: 'Loiter here', mode: 'Loiter' }, { label: 'Return (RTL)', mode: 'RTL', caution: true }, { label: 'Resume mission', mode: 'Auto' }],
      modes: ['Manual', 'Circle', 'Stabilize', 'FBWA', 'FBWB', 'Cruise', 'Auto', 'RTL', 'Loiter', 'Guided', 'QHover', 'QLoiter', 'QLand', 'QRTL'],
      alerts: [
        { t: '14:31:02Z', veh: 'UGV-3', level: 3, text: 'BATT 18% - 21.2 V, 6.0 A' },
        { t: '14:29:40Z', veh: 'MR-2', level: 2, text: 'LINK 74% - GCS packet reception' },
        { t: '14:28:15Z', veh: 'FW-1', level: 0, text: 'Mode AUTO' }],
      layers: { viewshed: layers.viewshed, source: layers.source, weather: layers.weather, vsStatus: 'Sensor: 58% visible, 41% masked · 5 km · 212 ms' },
      weather: layers.weather ? { ver: 1, fetched: new Date(Date.now() - 6 * 60000).toISOString(), staleMin: 30, cellLat: 0.036, cellLon: 0.0455, grid: grid,
        lines: [['Wind sfc (model)', '290° / 14 kt G22'], ['Wind LGAV', '290 / 14 kt G22'], ['Wind @ vehicle', '284° / 21 kt (EKF)'], ['Ceiling', 'BKN 2400 ft (LGAV)'], ['Visibility', '9.7+ km (LGAV) -RA'], ['Temp / QNH', '14 °C / 1012 hPa']],
        alert: 'Precipitation 2.4 mm, 9 km NW', errors: null } : undefined,
      mission: M
    };
  }
  return {
    start: function (f) { cb = f; f(state()); setInterval(function () { f(state()); }, 250); },
    handle: function (m) {
      if (m.type === 'layers') { if (m.viewshed != null) layers.viewshed = m.viewshed; if (m.weather != null) layers.weather = m.weather; if (m.source) layers.source = m.source; }
      if (m.type === 'select') active = m.key;
      if (cb) cb(state());
    }
  };
})();
