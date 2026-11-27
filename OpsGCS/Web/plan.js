/* OpsGCS PLAN screen. The page edits a draft mission; Mission Planner supplies terrain profiles
   (planProfile) and does the MAVLink transfer (planGet / planWrite). Write stays locked until
   every check passes, and the write itself is a hold-to-confirm that names the vehicle. */
(function () {
  'use strict';
  var Ops = window.Ops;
  if (!Ops) return;
  var $ = Ops.$, esc = Ops.esc, map = Ops.map, send = Ops.send;

  // ---------------------------------------------------------------- command catalogue (ArduPilot)
  // pos: 'req' needs a position, 'opt' may have one, 'none' never; alt: carries an altitude
  var CMDS = {
    16: { n: 'WAYPOINT', pos: 'req', alt: true, ps: { 1: 'Hold s' } },
    82: { n: 'SPLINE_WAYPOINT', pos: 'req', alt: true, ps: { 1: 'Hold s' } },
    17: { n: 'LOITER_UNLIM', pos: 'req', alt: true, ps: { 3: 'Radius m' } },
    18: { n: 'LOITER_TURNS', pos: 'req', alt: true, ps: { 1: 'Turns', 3: 'Radius m' } },
    19: { n: 'LOITER_TIME', pos: 'req', alt: true, ps: { 1: 'Time s', 3: 'Radius m' } },
    31: { n: 'LOITER_TO_ALT', pos: 'req', alt: true, ps: { 2: 'Radius m' } },
    22: { n: 'TAKEOFF', pos: 'opt', alt: true, ps: { 1: 'Pitch ° (plane)' } },
    84: { n: 'VTOL_TAKEOFF', pos: 'opt', alt: true, ps: {} },
    21: { n: 'LAND', pos: 'opt', alt: false, ps: { 1: 'Abort alt m' } },
    85: { n: 'VTOL_LAND', pos: 'req', alt: false, ps: {} },
    20: { n: 'RETURN_TO_LAUNCH', pos: 'none', alt: false, ps: {} },
    93: { n: 'DELAY', pos: 'none', alt: false, ps: { 1: 'Seconds' } },
    177: { n: 'DO_JUMP', pos: 'none', alt: false, ps: { 1: 'To item #', 2: 'Repeat' } },
    178: { n: 'DO_CHANGE_SPEED', pos: 'none', alt: false, ps: { 1: 'Type 0 air 1 gnd', 2: 'Speed m/s', 3: 'Throttle % (-1 keep)' } },
    206: { n: 'DO_SET_CAM_TRIGG_DIST', pos: 'none', alt: false, ps: { 1: 'Distance m', 3: 'Trigger once (1)' } },
    183: { n: 'DO_SET_SERVO', pos: 'none', alt: false, ps: { 1: 'Servo', 2: 'PWM' } },
    201: { n: 'DO_SET_ROI', pos: 'req', alt: true, ps: {} }
  };
  var ORDER = [16, 82, 17, 18, 19, 31, 22, 84, 21, 85, 20, 93, 177, 178, 206, 183, 201];
  var FRAMES = { REL: 3, AGL: 10, AMSL: 0 };
  var FRAME_SHORT = { 0: 'AMSL', 3: 'REL', 10: 'AGL' };
  var FLOWN = { 16: 1, 82: 1, 17: 1, 18: 1, 19: 1, 31: 1, 22: 1, 84: 1, 21: 1, 85: 1 };
  function cmdOf(c) { return CMDS[c] || { n: 'CMD ' + c, pos: 'opt', alt: true, ps: { 1: 'Param 1', 2: 'Param 2', 3: 'Param 3', 4: 'Param 4' } }; }
  function hasPos(it) { return Math.abs(it.lat) > 1e-7 || Math.abs(it.lon) > 1e-7; }
  function flownPos(it) { return FLOWN[it.cmd] && hasPos(it); }

  // ---------------------------------------------------------------- draft state
  var P = {
    name: 'Untitled mission', items: [], home: null, sel: -1, homeSel: false,
    rev: 0, prof: null, profRev: -1, reqId: 0, reqRev: {}, timer: null,
    baseline: null, baselineFrom: '', busy: null, addMode: false, undo: [], loadedOnce: false,
    defaults: { minClearance: 60, defaultAlt: 120, defaultFrame: 'REL' }
  };

  function sig() {
    var r = function (n) { return Math.round((n || 0) * 1e6) / 1e6; };
    return JSON.stringify({ h: P.home ? [r(P.home.lat), r(P.home.lon)] : null,
      i: P.items.map(function (it) { return [it.cmd, it.frame, r(it.lat), r(it.lon), r(it.alt), r(it.p1), r(it.p2), r(it.p3), r(it.p4)]; }) });
  }
  function save() {
    Ops.store('opsgcs.plan', JSON.stringify({ name: P.name, items: P.items, home: P.home, baseline: P.baseline, baselineFrom: P.baselineFrom }));
  }
  function restore() {
    try {
      var d = JSON.parse(Ops.store('opsgcs.plan') || 'null');
      if (d && Array.isArray(d.items)) {
        P.name = d.name || P.name; P.items = d.items.map(normItem); P.home = d.home || null;
        P.baseline = d.baseline || null; P.baselineFrom = d.baselineFrom || '';
        P.loadedOnce = P.items.length > 0;
      }
    } catch (e) { /* ignore a damaged draft */ }
  }
  function normItem(it) {
    return { cmd: +it.cmd || 16, frame: it.frame == null ? 3 : +it.frame, lat: +it.lat || 0, lon: +it.lon || 0, alt: +it.alt || 0,
      p1: +it.p1 || 0, p2: +it.p2 || 0, p3: +it.p3 || 0, p4: +it.p4 || 0 };
  }

  function snapshot() { return JSON.stringify({ items: P.items, home: P.home, sel: P.sel, name: P.name }); }
  function pushUndo() { P.undo.push(snapshot()); if (P.undo.length > 100) P.undo.shift(); }
  function undo() {
    var s = P.undo.pop();
    if (!s) { Ops.toast('Nothing to undo'); return; }
    var d = JSON.parse(s);
    P.items = d.items; P.home = d.home; P.sel = d.sel; P.name = d.name; P.homeSel = false;
    changed();
  }

  /** Every edit ends here: bump the revision, persist, ask for a fresh profile, redraw. */
  function changed(keepProfile) {
    P.rev++;
    if (P.sel >= P.items.length) P.sel = P.items.length - 1;
    save();
    if (!keepProfile) requestProfile();
    render();
  }

  // ---------------------------------------------------------------- terrain profile from the host
  function requestProfile() {
    clearTimeout(P.timer);
    P.timer = setTimeout(function () {
      if (!P.home) { P.prof = null; P.profRev = P.rev; render(); return; }
      var id = ++P.reqId;
      P.reqRev[id] = P.rev;
      send({ type: 'planProfile', id: id, home: P.home, items: P.items });
    }, 220);
  }
  Ops.on('planProfile', function (m) {
    if (m.id !== P.reqId) return; // an older request
    P.prof = m;
    P.profRev = P.reqRev[m.id];
    if (P.home && P.home.alt == null && m.homeAmsl != null) P.home.alt = m.homeAmsl; // from terrain at home
    if (m.minClearance) P.defaults.minClearance = m.minClearance;
    render();
  });

  // ---------------------------------------------------------------- vehicle transfer
  function target() { var v = Ops.active(); return v && v.connected ? v : null; }

  Ops.on('planData', function (m) {
    var fromVehicle = m.source === 'vehicle';
    var hadDraft = P.items.length > 0 && sig() !== P.baseline;
    if (m.defaults) applyDefaults(m.defaults);
    if (!fromVehicle && P.loadedOnce) return; // cached copy only seeds an empty planner
    if (hadDraft) pushUndo();
    P.items = (m.items || []).map(normItem);
    P.home = m.home || P.home;
    P.sel = P.items.length ? 0 : -1;
    P.homeSel = false;
    P.loadedOnce = true;
    P.baseline = sig();
    P.baselineFrom = m.vehicle || m.key;
    if (fromVehicle || P.name === 'Untitled mission') P.name = (m.vehicle || 'Vehicle') + ' mission';
    changed();
    fit();
    if (hadDraft && fromVehicle) Ops.toast('Draft replaced by the mission on ' + m.vehicle + '. Ctrl+Z restores it.');
  });
  Ops.on('planProgress', function (m) { P.busy = { op: m.op, pct: m.pct, text: m.text }; renderStatus(); renderButtons(); });
  Ops.on('planResult', function (m) {
    P.busy = null;
    if (m.ok && m.op === 'write') { P.baseline = P.lastWriteSig; P.baselineFrom = P.lastWriteVeh; save(); }
    Ops.toast(m.text);
    render();
  });

  function readFromVehicle() {
    var v = target();
    if (!v) { Ops.toast('Connect and select a vehicle to read its mission.'); return; }
    P.busy = { op: 'read', pct: 0, text: 'Requesting mission' };
    send({ type: 'planGet', key: v.key, fromVehicle: true });
    render();
  }

  function writeToVehicle() {
    var v = target(), c = checks();
    if (!v || !c.canWrite) return;
    var navs = P.items.filter(flownPos), alts = navs.map(function (it) { return it.alt; });
    var frames = {};
    navs.forEach(function (it) { frames[FRAME_SHORT[it.frame] || it.frame] = 1; });
    var facts = [['Items', P.items.length + ' (' + navs.length + ' with a position)'],
      ['Route', P.prof && P.prof.totalM ? Ops.fmtDist(P.prof.totalM) : '--'],
      ['Altitude', alts.length ? Math.min.apply(null, alts) + '–' + Math.max.apply(null, alts) + ' m ' + Object.keys(frames).join('/') : '--'],
      ['Lowest clearance', c.lowest != null ? Math.round(c.lowest) + ' m above terrain' : '--', c.lowest != null && c.lowest < P.defaults.minClearance ? 'caution' : null],
      ['Vehicle now', (v.armed ? 'ARMED · ' : 'disarmed · ') + (v.mode || '?').toUpperCase(), v.armed ? 'caution' : null]];
    if (c.overridden) facts.push(['Checks', 'Failed terrain checks overridden', 'caution']);
    var key = v.key, items = JSON.parse(JSON.stringify(P.items)), home = JSON.parse(JSON.stringify(P.home)), override = c.overridden;
    if (home.alt == null && P.prof && P.prof.homeAmsl != null) home.alt = P.prof.homeAmsl;
    Ops.confirmHold({
      kicker: 'CONFIRM MISSION WRITE', title: 'Write "' + P.name + '"', veh: v.name, facts: facts, hold: 'Hold to write mission',
      vdet: esc(v.firmware) + ' · sysid ' + esc(v.sysid) + '<br><span class="muted">Replaces the mission on this vehicle only.</span>',
      onConfirm: function () {
        P.lastWriteSig = sig(); P.lastWriteVeh = v.name;
        P.busy = { op: 'write', pct: 0, text: 'Starting' };
        send({ type: 'planWrite', key: key, home: home, items: items, override: override });
        render();
      }
    });
  }

  // ---------------------------------------------------------------- editing
  function defAlt() { var a = parseFloat($('p-alt').value); return isFinite(a) ? a : P.defaults.defaultAlt; }
  function defFrame() { return FRAMES[$('p-frame').value] != null ? FRAMES[$('p-frame').value] : 3; }
  function newItem(cmd, lat, lon) {
    return { cmd: cmd, frame: defFrame(), lat: lat || 0, lon: lon || 0, alt: cmdOf(cmd).alt ? defAlt() : 0, p1: cmd === 22 ? 15 : 0, p2: 0, p3: 0, p4: 0 };
  }
  function insertAfterSel(it) {
    pushUndo();
    var at = P.sel >= 0 ? P.sel + 1 : P.items.length;
    P.items.splice(at, 0, it);
    P.sel = at; P.homeSel = false;
    changed();
  }
  function addAt(latlng) {
    if (!P.home) {
      pushUndo();
      P.home = { lat: latlng.lat, lon: latlng.lng, alt: null };
      P.homeSel = true;
      changed();
      Ops.toast('Home set here. Keep clicking to add waypoints.');
      return;
    }
    insertAfterSel(newItem(16, latlng.lat, latlng.lng));
  }
  function addItem() {
    var c = map.getCenter();
    if (!P.home) { addAt(c); return; }
    var prev = null;
    for (var i = (P.sel >= 0 ? P.sel : P.items.length - 1); i >= 0; i--) if (hasPos(P.items[i])) { prev = P.items[i]; break; }
    // place it beside the previous position so it is visible and easy to drag
    var lat = prev ? prev.lat + 0.004 : c.lat, lon = prev ? prev.lon + 0.004 : c.lng;
    insertAfterSel(newItem(16, lat, lon));
  }
  function delSel() {
    if (P.sel < 0) return;
    pushUndo();
    P.items.splice(P.sel, 1);
    renumberJumps(P.sel + 1, -1);
    P.sel = Math.min(P.sel, P.items.length - 1);
    changed();
  }
  function move(d) {
    var i = P.sel, j = i + d;
    if (i < 0 || j < 0 || j >= P.items.length) return;
    pushUndo();
    var t = P.items[i]; P.items[i] = P.items[j]; P.items[j] = t;
    P.sel = j;
    changed();
  }
  /** Keeps DO_JUMP targets pointing at the same items after a delete. */
  function renumberJumps(fromSeq, delta) {
    P.items.forEach(function (it) { if (it.cmd === 177 && it.p1 > fromSeq) it.p1 += delta; });
  }
  function setMode(on) {
    P.addMode = on;
    $('p-addmode').classList.toggle('on', on);
    $('p-addmode').setAttribute('aria-pressed', on ? 'true' : 'false');
    $('app').classList.toggle('plan-adding', on);
    if (on) Ops.toast(P.home ? 'Click the map to add waypoints after the selected item. Esc stops.' : 'Click the map to set home, then add waypoints. Esc stops.');
  }

  // ---------------------------------------------------------------- checks
  function checks() {
    var out = [], v = Ops.active(), st = Ops.state() || {}, minC = P.defaults.minClearance;
    var add = function (lvl, text, extra) { var c = { lvl: lvl, text: text }; for (var k in extra || {}) c[k] = extra[k]; out.push(c); return c; };
    var lowest = null, overridable = 0, hardFail = 0, pending = false;

    if (!v || !v.connected) { add('fail', 'No connected vehicle selected', { hard: 1 }); hardFail++; }
    else add('ok', 'Target ' + v.name + ' · ' + v.firmware);
    if (v && v.armed && /auto/i.test(v.mode || '')) add('caution', v.name + ' is flying AUTO: the new mission replaces the current one in flight');

    if (!P.home) { add('fail', 'Home position not set (click the map with +WP on)', { hard: 1 }); hardFail++; }
    else if (v && v.home) {
      var dh = dist(P.home.lat, P.home.lon, v.home.lat, v.home.lon);
      if (dh > 100) add('caution', 'Home is ' + Ops.fmtDist(dh) + ' from ' + v.name + '’s home', { fix: { label: 'Use vehicle home', fn: function () { pushUndo(); P.home = { lat: v.home.lat, lon: v.home.lon, alt: v.home.alt != null ? v.home.alt : null }; changed(); } } });
      else add('ok', 'Home set · ' + Math.round(dh) + ' m from vehicle home');
    } else add('ok', 'Home set');

    var navs = P.items.filter(flownPos);
    if (!navs.length) { add('fail', 'No waypoints yet', { hard: 1 }); hardFail++; }

    P.items.forEach(function (it, i) {
      var c = cmdOf(it.cmd);
      if (c.pos === 'req' && !hasPos(it)) { add('fail', 'Item ' + (i + 1) + ' ' + c.n + ' has no position', { hard: 1, seq: i + 1 }); hardFail++; }
      if (it.cmd === 177 && (it.p1 < 1 || it.p1 > P.items.length || it.p1 === i + 1)) { add('fail', 'Item ' + (i + 1) + ' DO_JUMP points at item ' + it.p1 + ', which does not exist', { hard: 1, seq: i + 1 }); hardFail++; }
    });

    var prof = P.prof;
    if (P.home && navs.length) {
      if (P.profRev !== P.rev || !prof) { add('pending', 'Checking terrain clearance…'); pending = true; }
      else if (prof.error) { add('fail', 'Terrain check failed: ' + prof.error, { ovr: 1 }); overridable++; }
      else {
        var lowLegs = [];
        (prof.legs || []).forEach(function (l) {
          if (!l.checked || l.minClear == null) return;
          if (lowest == null || l.minClear < lowest) lowest = l.minClear;
          if (l.minClear < minC) lowLegs.push(l);
        });
        lowLegs.forEach(function (l) {
          var need = Math.ceil((minC - l.minClear) / 5) * 5;
          add('fail', 'Leg to item ' + l.to + ' passes ' + Math.round(l.minClear) + ' m above terrain; minimum is ' + minC + ' m.', {
            ovr: 1, seq: l.to,
            fix: { label: 'Raise leg by ' + need + ' m', fn: function () { raiseLeg(l.to, need); } }
          });
          overridable++;
        });
        if (!lowLegs.length && lowest != null) add('ok', 'Terrain clearance ≥ ' + minC + ' m on cruise legs (lowest ' + Math.round(lowest) + ' m)');
        if (prof.terrainMissing > 0) add('caution', 'No terrain data for part of the route: clearance there is not checked');
        if (prof.totalM) add('ok', 'Route ' + Ops.fmtDist(prof.totalM));
      }
    }

    if (navs.length) {
      var first = P.items[0], last = P.items[P.items.length - 1];
      var rover = v && /rover|boat/i.test(v.firmware || '');
      if (!rover && first && first.cmd !== 22 && first.cmd !== 84 && !(v && v.armed)) add('caution', 'First item is not a TAKEOFF');
      if (last && [20, 21, 85, 17].indexOf(last.cmd) < 0) add('caution', 'Mission does not end with RTL, LAND or LOITER_UNLIM');
    }

    var eng = st.role === 'ENGINEER';
    var overridden = eng && $('p-override').checked && overridable > 0;
    var canWrite = !!(v && v.connected) && !P.busy && !pending && hardFail === 0 && (overridable === 0 || overridden);
    return { list: out, canWrite: canWrite, lowest: lowest, overridable: overridable, overridden: overridden, pending: pending, hardFail: hardFail, eng: eng };
  }

  /** Raises both ends of a leg (never home) so the whole leg clears the minimum. */
  function raiseLeg(toSeq, by) {
    pushUndo();
    var to = toSeq - 1, from = -1;
    for (var i = to - 1; i >= 0; i--) if (flownPos(P.items[i])) { from = i; break; }
    [from, to].forEach(function (i) { if (i >= 0 && cmdOf(P.items[i].cmd).alt) P.items[i].alt = Math.round((P.items[i].alt + by) * 10) / 10; });
    P.sel = to;
    changed();
  }

  function dist(a, b, c, d) {
    var R = 6371000, r = Math.PI / 180, x = (d - b) * r * Math.cos((a + c) / 2 * r), y = (c - a) * r;
    return Math.sqrt(x * x + y * y) * R;
  }

  // ---------------------------------------------------------------- render
  var lyr = L.layerGroup();
  function render() {
    if (Ops.screen() !== 'PLAN') return;
    var c = checks();
    renderToolbar();
    renderList(c);
    renderChecks(c);
    renderEditor();
    renderMap(c);
    renderProfile(c);
    renderStatus();
    renderButtons(c);
  }

  function badSeqs(c) {
    var b = {};
    c.list.forEach(function (x) { if (x.lvl === 'fail' && x.seq) b[x.seq] = 1; });
    return b;
  }

  function renderToolbar() {
    var st = Ops.state() || {}, vs = st.vehicles || [], a = Ops.active();
    var html = vs.length ? vs.map(function (v) {
      return '<option value="' + esc(v.key) + '"' + (v.active ? ' selected' : '') + '>' + esc(v.name) + ' · ' + esc(v.firmware) + (v.connected ? '' : ' (lost)') + '</option>';
    }).join('') : '<option value="">No vehicle</option>';
    var sel = $('p-veh');
    if (sel.innerHTML !== html && document.activeElement !== sel) sel.innerHTML = html;
    sel.disabled = !vs.length;
    $('p-read').textContent = a ? 'Read from ' + a.name : 'Read';
    $('p-write').textContent = a ? 'Write to ' + a.name + '…' : 'Write…';
    if (document.activeElement !== $('p-name')) $('p-name').value = P.name;
    $('p-minclr').textContent = P.defaults.minClearance;
  }

  function legLen(i) {
    var it = P.items[i];
    if (!flownPos(it)) return null;
    var l = P.prof && P.profRev === P.rev && (P.prof.legs || []).filter(function (x) { return x.to === i + 1; })[0];
    if (l) return l.len;
    for (var j = i - 1; j >= 0; j--) if (flownPos(P.items[j])) return dist(P.items[j].lat, P.items[j].lon, it.lat, it.lon);
    return P.home ? dist(P.home.lat, P.home.lon, it.lat, it.lon) : null;
  }

  function label(it) {
    var c = cmdOf(it.cmd), n = c.n;
    switch (it.cmd) {
      case 22: return n + (it.p1 ? ' ' + it.p1 + '°' : '');
      case 19: return n + ' ' + it.p1 + ' s';
      case 18: return n + ' ' + it.p1 + '×';
      case 16: case 82: return n + (it.p1 ? ' hold ' + it.p1 + ' s' : '');
      case 177: return 'DO_JUMP → ' + it.p1 + (it.p2 ? ' ×' + it.p2 : '');
      case 178: return 'DO_CHANGE_SPEED ' + it.p2 + ' m/s';
      case 206: return 'DO_SET_CAM_TRIGG ' + it.p1 + ' m';
      case 183: return 'DO_SET_SERVO ' + it.p1 + ' → ' + it.p2;
      case 93: return 'DELAY ' + it.p1 + ' s';
      default: return n;
    }
  }

  function renderList(c) {
    var bad = badSeqs(c), errs = {};
    c.list.forEach(function (x) { if (x.hard && x.seq) errs[x.seq] = 1; });
    var total = 0;
    var rows = P.home ? ['<button class="p-row home' + (P.homeSel ? ' sel' : '') + '" data-i="-1"><span class="seq">0</span><span class="cmd">HOME</span><span class="alt">' +
      (P.home.alt != null ? Math.round(P.home.alt) + '<span class="fr">AMSL</span>' : '<span class="dim">—</span>') + '</span><span class="dim">—</span></button>'] : [];
    P.items.forEach(function (it, i) {
      var cm = cmdOf(it.cmd), len = legLen(i);
      if (len) total += len;
      rows.push('<button class="p-row' + (i === P.sel && !P.homeSel ? ' sel' : '') + (bad[i + 1] ? ' bad' : '') + (errs[i + 1] ? ' err' : '') + '" data-i="' + i + '">' +
        '<span class="seq">' + (i + 1) + '</span><span class="cmd">' + esc(label(it)) + '</span>' +
        '<span class="alt">' + (cm.alt ? Math.round(it.alt) + '<span class="fr">' + (FRAME_SHORT[it.frame] || it.frame) + '</span>' : '<span class="dim">—</span>') + '</span>' +
        '<span>' + (len ? (len / 1000).toFixed(2) : '<span class="dim">—</span>') + '</span></button>');
    });
    var html = rows.join('') || '<div class="p-empty">No mission yet.<br>Turn on <b>+WP</b> on the map and click to set home, then click again to add waypoints. Or read the mission from the vehicle, or open a .waypoints file.</div>';
    var list = $('p-list');
    if (list.innerHTML !== html) {
      var top = list.scrollTop;
      list.innerHTML = html;
      list.scrollTop = top;
      var s = list.querySelector('.sel');
      if (s && (s.offsetTop < list.scrollTop || s.offsetTop > list.scrollTop + list.clientHeight - 30)) s.scrollIntoView({ block: 'nearest' });
    }
    $('p-count').textContent = 'ITEMS · ' + P.items.length;
    var km = P.prof && P.profRev === P.rev && P.prof.totalM ? P.prof.totalM : total;
    $('p-total').textContent = km ? Ops.fmtDist(km) : '';
  }
  $('p-list').addEventListener('click', function (e) {
    var b = e.target.closest('.p-row[data-i]');
    if (!b) return;
    var i = +b.dataset.i;
    if (i < 0) { P.homeSel = true; P.sel = -1; } else { P.homeSel = false; P.sel = i; }
    render();
    var it = i < 0 ? P.home : P.items[i];
    if (it && hasPos(it) && !map.getBounds().contains([it.lat, it.lon])) map.panTo([it.lat, it.lon]);
  });

  function renderChecks(c) {
    var icon = { ok: '✓', caution: '!', fail: '!', pending: '…' };
    $('p-checks').innerHTML = c.list.map(function (x, k) {
      return '<div class="chk ' + x.lvl + (x.hard ? ' hard' : '') + '"><span class="ic" aria-hidden="true">' + icon[x.lvl] + '</span><span>' + esc(x.text) +
        (x.fix ? '<button class="fix" data-k="' + k + '">' + esc(x.fix.label) + '</button>' : '') + '</span></div>';
    }).join('');
    $('p-checks').querySelectorAll('.fix').forEach(function (b) { b.onclick = function () { c.list[+b.dataset.k].fix.fn(); }; });
    $('p-override-row').hidden = !(c.eng && c.overridable > 0);
    var note;
    if (P.busy) note = 'Transfer in progress.';
    else if (c.pending) note = 'Write unlocks when the terrain check finishes.';
    else if (c.hardFail) note = 'Write stays locked until the red items are fixed.';
    else if (c.overridable && !c.overridden) note = 'Write stays locked until every check passes' + (c.eng ? ' or you override the terrain checks.' : ' or an engineer overrides it.');
    else note = 'Write asks for a hold-to-confirm naming the vehicle.';
    $('p-locknote').textContent = note;
  }
  $('p-override').onchange = function () { render(); };

  // editor --------------------------------------------------------
  var edSig = '';
  function renderEditor() {
    var ed = $('p-editor');
    var key = P.homeSel ? 'home|' + JSON.stringify(P.home) : P.sel >= 0 ? P.sel + '|' + JSON.stringify(P.items[P.sel]) : 'none';
    if (key === edSig && ed.contains(document.activeElement)) return; // don't clobber typing
    edSig = key;
    if (P.homeSel && P.home) {
      $('p-ed-title').textContent = 'HOME';
      ed.innerHTML = field('LAT', 'h-lat', P.home.lat.toFixed(7)) + field('LON', 'h-lon', P.home.lon.toFixed(7)) +
        field('ALT m AMSL', 'h-alt', P.home.alt != null ? P.home.alt : '', 'full') +
        '<div class="hint">Drag the green H on the map to move home. Leave altitude empty to take it from terrain data. The vehicle sets its own home when it arms.</div>';
      bind('h-lat', function (v) { P.home.lat = clamp(v, -90, 90); }, true);
      bind('h-lon', function (v) { P.home.lon = clamp(v, -180, 180); }, true);
      bind('h-alt', function (v, raw) { P.home.alt = raw === '' ? null : v; }, true, true);
      return;
    }
    if (P.sel < 0 || !P.items[P.sel]) {
      $('p-ed-title').textContent = 'ITEM';
      ed.innerHTML = '<div class="hint">Select an item in the list or on the map to edit it.</div>';
      return;
    }
    var it = P.items[P.sel], c = cmdOf(it.cmd), seq = P.sel + 1;
    $('p-ed-title').textContent = 'ITEM ' + seq + ' · ' + c.n;
    var opts = ORDER.map(function (k) { return '<option value="' + k + '"' + (k === it.cmd ? ' selected' : '') + '>' + CMDS[k].n + '</option>'; }).join('');
    if (!CMDS[it.cmd]) opts += '<option value="' + it.cmd + '" selected>' + esc(c.n) + '</option>';
    var h = '<label class="full">COMMAND<select id="e-cmd">' + opts + '</select></label>';
    if (c.pos === 'opt') h += '<label class="full chkrow"><input type="checkbox" id="e-haspos"' + (hasPos(it) ? ' checked' : '') + '> Has a position</label>';
    if (c.pos !== 'none' && (c.pos === 'req' || hasPos(it))) h += field('LAT', 'e-lat', it.lat.toFixed(7)) + field('LON', 'e-lon', it.lon.toFixed(7));
    if (c.alt) {
      h += field('ALT m', 'e-alt', it.alt) +
        '<label>REFERENCE<select id="e-frame">' + [3, 10, 0].map(function (f) {
          return '<option value="' + f + '"' + (f === it.frame ? ' selected' : '') + '>' + { 3: 'Above home', 10: 'Above terrain', 0: 'AMSL' }[f] + '</option>';
        }).join('') + '</select></label>';
    }
    [1, 2, 3, 4].forEach(function (k) { if (c.ps[k]) h += field(c.ps[k].toUpperCase(), 'e-p' + k, it['p' + k]); });
    var pt = P.prof && P.profRev === P.rev && (P.prof.wpMarks || []).filter(function (w) { return w.seq === seq; })[0];
    if (pt) h += '<div class="hint">Terrain here ' + Math.round(pt.terrain) + ' m AMSL · planned ' + Math.round(pt.amsl) + ' m AMSL · ' + Math.round(pt.amsl - pt.terrain) + ' m above terrain</div>';
    ed.innerHTML = h;
    $('e-cmd').onchange = function () {
      pushUndo();
      var n = +this.value, nc = cmdOf(n);
      it.cmd = n;
      if (nc.pos === 'none') { it.lat = 0; it.lon = 0; }
      else if (nc.pos === 'req' && !hasPos(it)) { var m = map.getCenter(); it.lat = m.lat; it.lon = m.lng; }
      if (nc.alt && !it.alt) it.alt = defAlt();
      if (!nc.alt) it.alt = 0;
      changed();
    };
    if ($('e-haspos')) $('e-haspos').onchange = function () {
      pushUndo();
      if (this.checked) { var m = map.getCenter(); it.lat = m.lat; it.lon = m.lng; } else { it.lat = 0; it.lon = 0; }
      changed();
    };
    bind('e-lat', function (v) { it.lat = clamp(v, -90, 90); });
    bind('e-lon', function (v) { it.lon = clamp(v, -180, 180); });
    bind('e-alt', function (v) { it.alt = v; });
    if ($('e-frame')) $('e-frame').onchange = function () { pushUndo(); it.frame = +this.value; changed(); };
    [1, 2, 3, 4].forEach(function (k) { bind('e-p' + k, function (v) { it['p' + k] = v; }); });
  }
  function field(lbl, id, val, cls) {
    return '<label' + (cls ? ' class="' + cls + '"' : '') + '>' + esc(lbl) + '<input id="' + id + '" value="' + esc(val) + '" inputmode="decimal" autocomplete="off"></label>';
  }
  function clamp(v, a, b) { return Math.max(a, Math.min(b, v)); }
  function bind(id, set, undoable, allowEmpty) {
    var el = $(id);
    if (!el) return;
    el.onchange = function () {
      var raw = this.value.trim(), v = parseFloat(raw);
      if (!(allowEmpty && raw === '') && !isFinite(v)) { this.value = ''; edSig = ''; render(); return; }
      pushUndo();
      set(v, raw);
      edSig = '';
      changed();
    };
    el.onkeydown = function (e) { if (e.key === 'Enter') this.blur(); };
  }

  // map ------------------------------------------------------------
  var mapSig = '';
  function renderMap(c) {
    var bad = badSeqs(c);
    var s = JSON.stringify([P.items, P.home, P.sel, P.homeSel, bad]);
    if (s === mapSig) return;
    mapSig = s;
    lyr.clearLayers();
    var line = [];
    if (P.home) line.push([P.home.lat, P.home.lon]);
    P.items.forEach(function (it) { if (flownPos(it)) line.push([it.lat, it.lon]); });
    if (line.length > 1) L.polyline(line, { color: '#E0A832', weight: 2.5, opacity: .95, interactive: false }).addTo(lyr);
    // the leg the vehicle flies after the last item is not drawn: RTL goes home by its own path
    P.items.forEach(function (it, i) {
      if (!hasPos(it)) return;
      var seq = i + 1, sel = i === P.sel && !P.homeSel, roi = it.cmd === 201;
      var mk = L.marker([it.lat, it.lon], {
        draggable: true, keyboard: false, zIndexOffset: sel ? 1000 : 0,
        icon: L.divIcon({ className: '', iconSize: [24, 24], iconAnchor: [12, 12],
          html: '<div class="pwp' + (sel ? ' sel' : '') + (bad[seq] ? ' bad' : '') + (roi ? ' roi' : '') + '">' + seq + '</div>' })
      }).addTo(lyr);
      if (sel) mk.bindTooltip((bad[seq] ? 'Low clearance · ' : '') + 'Item ' + seq + ' · ' + Math.round(it.alt) + ' m ' + (FRAME_SHORT[it.frame] || '') + ' · drag to move',
        { permanent: true, direction: 'right', offset: [14, 0], className: 'ptip' + (bad[seq] ? ' bad' : '') });
      mk.on('click', function () { P.sel = i; P.homeSel = false; render(); });
      mk.on('dragstart', function () { pushUndo(); P.sel = i; P.homeSel = false; });
      mk.on('dragend', function (e) { var ll = e.target.getLatLng(); it.lat = ll.lat; it.lon = ll.lng; changed(); });
    });
    if (P.home) {
      var hm = L.marker([P.home.lat, P.home.lon], {
        draggable: true, keyboard: false, zIndexOffset: 500,
        icon: L.divIcon({ className: '', iconSize: [24, 24], iconAnchor: [12, 12], html: '<div class="phome' + (P.homeSel ? ' sel' : '') + '">H</div>' })
      }).addTo(lyr);
      hm.on('click', function () { P.homeSel = true; P.sel = -1; render(); });
      hm.on('dragstart', function () { pushUndo(); });
      hm.on('dragend', function (e) { var ll = e.target.getLatLng(); P.home = { lat: ll.lat, lon: ll.lng, alt: null }; P.homeSel = true; changed(); });
    }
  }
  map.on('click', function (e) {
    if (Ops.screen() !== 'PLAN' || !P.addMode) return;
    addAt(e.latlng);
  });

  // profile --------------------------------------------------------
  function renderProfile(c) {
    var fresh = P.prof && P.profRev === P.rev && !P.prof.error;
    var prof = P.prof && !P.prof.error ? P.prof : null; // keep showing the last one while a new one is computed
    Ops.profileSvg($('p-profile'), prof ? prof.profile : [], {
      marks: prof ? prof.wpMarks : [], legs: prof ? prof.legs : null, minClearance: P.defaults.minClearance,
      selSeq: P.homeSel ? 0 : P.sel + 1, badSeq: badSeqs(c), labels: true,
      empty: P.home ? 'Add waypoints to see planned altitude against terrain.' : 'Set home and add waypoints to see the altitude profile.'
    });
    $('p-prof-status').textContent = !P.home || !P.items.length ? '' : fresh ? (P.prof.terrainMissing ? 'terrain data incomplete' : 'terrain: SRTM') : 'updating…';
  }
  $('p-profile').addEventListener('click', function (e) {
    var prof = P.prof;
    if (!prof || !prof.wpMarks || !prof.wpMarks.length) return;
    var r = this.getBoundingClientRect(), total = prof.profile[prof.profile.length - 1].d || 1;
    var d = ((e.clientX - r.left) - 44) / (r.width - 48) * total, best = null;
    prof.wpMarks.forEach(function (w) { if (!best || Math.abs(w.d - d) < Math.abs(best.d - d)) best = w; });
    if (!best) return;
    if (best.seq === 0) { P.homeSel = true; P.sel = -1; } else { P.homeSel = false; P.sel = best.seq - 1; }
    render();
  });

  // status + buttons -----------------------------------------------
  function renderStatus() {
    var el = $('p-status'), a = Ops.active();
    if (P.busy) { el.innerHTML = '<b>' + (P.busy.op === 'write' ? 'Writing' : 'Reading') + '… ' + Math.round(P.busy.pct || 0) + '%</b> · ' + esc(P.busy.text || ''); return; }
    if (!P.items.length) { el.textContent = ''; return; }
    var same = P.baseline && P.baseline === sig();
    el.innerHTML = same ? 'Matches the mission on <b>' + esc(P.baselineFrom) + '</b>' : '<b class="lv-caution">Draft</b> · not on ' + esc(a ? a.name : 'any vehicle') + ' yet';
  }
  function renderButtons(c) {
    c = c || checks();
    var v = target();
    $('p-write').disabled = !c.canWrite;
    $('p-read').disabled = !v || !!P.busy;
    $('p-del').disabled = P.sel < 0;
    $('p-up').disabled = P.sel <= 0;
    $('p-down').disabled = P.sel < 0 || P.sel >= P.items.length - 1;
    $('p-save').disabled = !P.items.length;
  }

  // ---------------------------------------------------------------- files (QGC WPL 110)
  function toWaypoints() {
    var n = function (v, d) { return (+v || 0).toFixed(d); };
    var lines = ['QGC WPL 110'];
    var h = P.home || { lat: 0, lon: 0, alt: 0 };
    lines.push(['0', '1', '0', '16', '0', '0', '0', '0', n(h.lat, 8), n(h.lon, 8), n(h.alt != null ? h.alt : (P.prof && P.prof.homeAmsl) || 0, 2), '1'].join('\t'));
    P.items.forEach(function (it, i) {
      lines.push([i + 1, 0, it.frame, it.cmd, n(it.p1, 6), n(it.p2, 6), n(it.p3, 6), n(it.p4, 6), n(it.lat, 8), n(it.lon, 8), n(it.alt, 2), 1].join('\t'));
    });
    return lines.join('\r\n') + '\r\n';
  }
  function fromWaypoints(text) {
    var lines = text.split(/\r?\n/).filter(function (l) { return l.trim(); });
    if (!/^QGC WPL 1[01]0/.test(lines[0] || '')) throw new Error('not a QGC WPL waypoint file');
    var home = null, items = [];
    var frameMap = { 0: 0, 3: 3, 10: 10, 5: 0, 6: 3, 11: 10, 2: 3 };
    lines.slice(1).forEach(function (l, k) {
      var f = l.trim().split(/\s+/);
      if (f.length < 12) throw new Error('line ' + (k + 2) + ' has ' + f.length + ' fields');
      var it = { cmd: +f[3], frame: frameMap[+f[2]] != null ? frameMap[+f[2]] : 3, p1: +f[4], p2: +f[5], p3: +f[6], p4: +f[7], lat: +f[8], lon: +f[9], alt: +f[10] };
      if (+f[0] === 0 && !items.length && home === null) { home = (it.lat || it.lon) ? { lat: it.lat, lon: it.lon, alt: it.alt || null } : null; return; }
      items.push(normItem(it));
    });
    return { home: home, items: items };
  }
  $('p-save').onclick = function () {
    var blob = new Blob([toWaypoints()], { type: 'text/plain' });
    var a = document.createElement('a');
    a.href = URL.createObjectURL(blob);
    a.download = (P.name || 'mission').replace(/[^\w\- ]+/g, '').trim().replace(/\s+/g, '_') + '.waypoints';
    document.body.appendChild(a); a.click(); a.remove();
    setTimeout(function () { URL.revokeObjectURL(a.href); }, 2000);
  };
  $('p-open').onclick = function () { $('p-file').value = ''; $('p-file').click(); };
  $('p-file').onchange = function () {
    var f = this.files && this.files[0];
    if (!f) return;
    var rd = new FileReader();
    rd.onload = function () {
      try {
        var d = fromWaypoints(String(rd.result));
        pushUndo();
        P.items = d.items; P.home = d.home || P.home; P.sel = P.items.length ? 0 : -1; P.homeSel = false;
        P.name = f.name.replace(/\.[^.]+$/, '').replace(/_/g, ' ');
        P.loadedOnce = true;
        changed(); fit();
        Ops.toast('Opened ' + f.name + ': ' + d.items.length + ' items' + (d.home ? '' : ' (no home in file)'));
      } catch (e) { Ops.toast('Could not open ' + f.name + ': ' + e.message); }
    };
    rd.readAsText(f);
  };

  // ---------------------------------------------------------------- wiring
  $('p-add').onclick = addItem;
  $('p-del').onclick = delSel;
  $('p-up').onclick = function () { move(-1); };
  $('p-down').onclick = function () { move(1); };
  $('p-read').onclick = readFromVehicle;
  $('p-write').onclick = writeToVehicle;
  $('p-addmode').onclick = function () { setMode(!P.addMode); };
  $('p-new').onclick = function () {
    if (!P.items.length && !P.home) return;
    pushUndo();
    var v = Ops.active();
    P.items = []; P.sel = -1; P.homeSel = false; P.name = 'Untitled mission'; P.loadedOnce = true;
    P.home = v && v.home ? { lat: v.home.lat, lon: v.home.lon, alt: v.home.alt != null ? v.home.alt : null } : null;
    changed();
    Ops.toast('New mission. Ctrl+Z brings the previous one back.');
  };
  $('p-name').onchange = function () { pushUndo(); P.name = this.value.trim() || 'Untitled mission'; save(); render(); };
  $('p-name').onkeydown = function (e) { if (e.key === 'Enter') this.blur(); };
  $('p-veh').onchange = function () { if (this.value) send({ type: 'select', key: this.value }); };
  $('p-alt').onchange = function () { Ops.store('opsgcs.plan.alt', this.value); };
  $('p-frame').onchange = function () { Ops.store('opsgcs.plan.frame', this.value); };

  document.addEventListener('keydown', function (e) {
    if (Ops.screen() !== 'PLAN' || !$('modal').hidden) return;
    var typing = /INPUT|SELECT|TEXTAREA/.test((document.activeElement || {}).tagName || '');
    if (e.key === 'Escape' && P.addMode) { setMode(false); return; }
    if (typing) return;
    if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === 'z') { e.preventDefault(); undo(); return; }
    if (e.key === 'Delete') { delSel(); return; }
    if (e.key === 'ArrowDown' || e.key === 'ArrowUp') {
      e.preventDefault();
      var n = P.items.length;
      if (!n) return;
      P.homeSel = false;
      P.sel = Math.max(0, Math.min(n - 1, (P.sel < 0 ? -1 : P.sel) + (e.key === 'ArrowDown' ? 1 : -1)));
      render();
    }
  });

  function applyDefaults(d) {
    var first = !P.defaultsSeen;
    P.defaultsSeen = true;
    for (var k in d) P.defaults[k] = d[k];
    if (first) {
      $('p-alt').value = Ops.store('opsgcs.plan.alt') || P.defaults.defaultAlt;
      var fr = Ops.store('opsgcs.plan.frame') || (P.defaults.defaultFrame || 'REL').toUpperCase();
      $('p-frame').value = FRAMES[fr] != null ? fr : 'REL';
    }
  }

  function fit() {
    var pts = [];
    if (P.home) pts.push([P.home.lat, P.home.lon]);
    P.items.forEach(function (it) { if (hasPos(it)) pts.push([it.lat, it.lon]); });
    if (!pts.length) return false;
    if (pts.length === 1) map.setView(pts[0], Math.max(map.getZoom(), 14));
    else map.fitBounds(pts, { padding: [50, 50], maxZoom: 17 });
    return true;
  }
  Ops.planFit = fit;

  var lastKey = null;
  Ops.onState(function (st) {
    if (st.planDefaults) applyDefaults(st.planDefaults);
    var a = Ops.active();
    // first time PLAN has a vehicle and no draft: start from what Mission Planner holds for it
    if (Ops.screen() === 'PLAN' && a && !P.loadedOnce && a.key !== lastKey) { lastKey = a.key; send({ type: 'planGet', key: a.key, fromVehicle: false }); }
    if (Ops.screen() === 'PLAN') { renderToolbar(); renderStatus(); var c = checks(); renderChecks(c); renderButtons(c); }
  });
  Ops.onScreen(function (name) {
    if (name === 'PLAN') {
      lyr.addTo(map);
      var a = Ops.active();
      if (a && !P.loadedOnce) { lastKey = a.key; send({ type: 'planGet', key: a.key, fromVehicle: false }); }
      edSig = ''; mapSig = '';
      requestProfile();
      render();
      if (!fit() && a && a.hasPos) map.setView([a.lat, a.lon], 13);
    } else {
      map.removeLayer(lyr);
      if (P.addMode) setMode(false);
    }
  });

  applyDefaults(P.defaults);
  restore();
})();
