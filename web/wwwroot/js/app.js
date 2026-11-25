/* 畫面與按鈕：Live / Offline 共用。後端差異全部藏在 backend (sim.js / live.js)。 */
(function () {
  'use strict';

  // ---- 軌道示意座標 (SVG 單位 = mm)。環上位置 0 的實際起點未知，這裡把 1550 mm 當上直線段左端 ----
  var TRACK = 3000, STRAIGHT = 1100, ARC = 400, R = ARC / Math.PI;
  var X0 = 175, T = 280, S_TOP = 1550;
  var STATIONS = [
    { id: 1, name: 'Load', pos: 1600, kind: 'gantry', trayName: '供料' },
    { id: 2, name: 'Press', pos: 2100, kind: 'press' },
    { id: 3, name: 'Unload', pos: 2600, kind: 'gantry', trayName: '出料' },
  ];
  var STATE_NAME = { 0: 'Idle', 10: 'Busy', 20: 'Done', 900: 'Error' };
  var CYCLE_NAME = { 0: 'Idle', 10: 'ToStation', 20: 'InStation', 100: 'Done', 800: 'Stopped', 900: 'Error' };
  var STEPS = ['Reset', 'InitEnvironment', 'WaitMovers', 'FindMover1', 'FindMover1Wait', 'PowerOn', 'EnableGroup', 'Ready'];
  var NS = 'http://www.w3.org/2000/svg';

  function $(id) { return document.getElementById(id); }
  function el(tag, attrs, parent) {
    var e = document.createElementNS(NS, tag);
    for (var k in attrs) e.setAttribute(k, attrs[k]);
    if (parent) parent.appendChild(e);
    return e;
  }

  // 環上位置 (mm) → 畫面座標與朝向 (度)；順時針，上直線段向右
  function ringXY(s) {
    var u = (((s - S_TOP) % TRACK) + TRACK) % TRACK, t;
    if (u < STRAIGHT) return { x: X0 + u, y: T, a: 0 };
    if (u < STRAIGHT + ARC) {
      t = (u - STRAIGHT) / R;
      return { x: X0 + STRAIGHT + R * Math.sin(t), y: T + R - R * Math.cos(t), a: t * 180 / Math.PI };
    }
    if (u < 2 * STRAIGHT + ARC) return { x: X0 + STRAIGHT - (u - STRAIGHT - ARC), y: T + 2 * R, a: 180 };
    t = (u - 2 * STRAIGHT - ARC) / R;
    return { x: X0 - R * Math.sin(t), y: T + R + R * Math.cos(t), a: 180 + t * 180 / Math.PI };
  }

  // ---- backend 選擇 ----
  var params = new URLSearchParams(location.search);
  var isFile = location.protocol === 'file:';
  var wantOffline = isFile || params.get('mode') === 'offline';
  var backend = wantOffline ? window.XtsSim.createBackend() : window.XtsLive.createBackend();
  var isOffline = backend.kind === 'offline';

  // ---- 靜態場景 ----
  var svg = $('scene'), gStation = {}, gMover = {}, trayParts = {};
  function buildScene() {
    var d = 'M ' + X0 + ' ' + T + ' L ' + (X0 + STRAIGHT) + ' ' + T +
      ' A ' + R + ' ' + R + ' 0 0 1 ' + (X0 + STRAIGHT) + ' ' + (T + 2 * R) +
      ' L ' + X0 + ' ' + (T + 2 * R) + ' A ' + R + ' ' + R + ' 0 0 1 ' + X0 + ' ' + T + ' Z';
    el('path', { d: d, class: 'track' }, svg);
    el('path', { d: d, class: 'track-mid' }, svg);
    STATIONS.forEach(function (st) {
      var p = ringXY(st.pos);
      el('line', { x1: p.x, y1: T - 36, x2: p.x, y2: T + 36, class: 'tick' }, svg);
      var t = el('text', { x: p.x, y: T + 66, class: 'st-pos' }, svg);
      t.textContent = st.id + ' ' + st.name + ' @' + st.pos;
      var g = el('g', { transform: 'translate(' + p.x + ',0)' }, svg);
      gStation[st.id] = { g: g, x: p.x };
      buildStation(st, gStation[st.id]);
    });
    var ctr = el('text', { x: X0 + STRAIGHT / 2, y: T + R + 8, class: 'st-sub', 'text-anchor': 'middle' }, svg);
    ctr.textContent = '軌道 3000 mm（示意：直線 1100 mm × 2 + 半圓弧 400 mm × 2）';
    for (var i = 1; i <= 4; i++) buildMover(i);
  }

  function partRect(parent, cls, x, y, w, h) {
    return el('rect', { x: x, y: y, width: w, height: h, rx: 2, class: cls }, parent);
  }

  function buildStation(st, o) {
    var g = o.g;
    o.title = el('text', { x: -40, y: 60, class: 'st-title' }, g);
    o.sub = el('text', { x: -40, y: 86, class: 'st-sub' }, g);
    if (st.kind === 'press') {
      el('path', { d: 'M -60 160 V 104 H 60 V 160', class: 'frame' }, g);
      o.rod = el('rect', { x: -5, y: 104, width: 10, height: 46, class: 'rod' }, g);
      o.head = el('rect', { x: -28, y: 146, width: 56, height: 10, rx: 3, class: 'head' }, g);
      o.zText = el('text', { x: 72, y: 130, class: 'st-sub' }, g);
    } else {
      el('line', { x1: -40, y1: 150, x2: 360, y2: 150, class: 'axis-rail' }, g);
      o.carriage = el('rect', { x: -18, y: 136, width: 36, height: 28, rx: 4, class: 'carriage' }, g);
      o.rod = el('rect', { x: -3, y: 164, width: 6, height: 10, class: 'rod' }, g);
      o.jaw = el('rect', { x: -9, y: 170, width: 18, height: 6, class: 'jaw' }, g);
      o.grabbed = partRect(g, 'part-has', -10, 176, 20, 14);
      o.grabbed.setAttribute('visibility', 'hidden');
      o.xyz = el('text', { x: 190, y: 124, class: 'st-sub', 'text-anchor': 'middle' }, g);
      // 取 / 放料盤在 X = 300 mm
      el('rect', { x: 262, y: 250, width: 76, height: 10, rx: 2, class: 'tray' }, g);
      trayParts[st.id] = partRect(g, 'part-has', 290, 236, 20, 14);
      var tl = el('text', { x: 300, y: 226, class: 'st-sub', 'text-anchor': 'middle' }, g);
      tl.textContent = st.trayName;
      if (st.id === 3) o.outCount = el('text', { x: 300, y: 278, class: 'st-sub', 'text-anchor': 'middle' }, g);
    }
  }

  function buildMover(id) {
    var g = el('g', { class: 'mover', 'data-id': id }, svg);
    var body = el('g', {}, g);
    var rect = el('rect', { x: -48, y: -20, width: 96, height: 40, rx: 6, class: 'mover-body' }, body);
    var partG = el('g', {}, body);
    var has = partRect(partG, 'part-has', -10, -40, 20, 20);
    var pressed = partRect(partG, 'part-pressed', -10, -34, 20, 14);
    var empty = partRect(partG, 'part-empty', -10, -40, 20, 20);
    var label = el('text', {}, g);
    label.textContent = id;
    var errRing = el('rect', { x: -53, y: -25, width: 106, height: 50, rx: 9, class: 'err-ring' }, body);
    gMover[id] = { g: g, body: body, rect: rect, has: has, pressed: pressed, empty: empty, label: label, errRing: errRing };
    g.addEventListener('click', function () { $('moverSel').value = id; markSel(); });
  }

  // ---- 更新 ----
  var lastStatus = null;
  function colorOf(m) {
    if (m.error) return 'var(--err)';
    if (!m.powered) return 'var(--off)';
    if (m.busy) return 'var(--busy)';
    return 'var(--idle)';
  }
  function moverState(m) {
    if (m.error) return 'error ' + hex(m.errorId);
    if (!m.powered) return '未送電';
    return m.busy ? 'busy' : 'idle';
  }
  function hex(n) { return '16#' + Number(n).toString(16).toUpperCase(); }
  function show(node, on) { node.setAttribute('visibility', on ? 'visible' : 'hidden'); }
  function f1(v) { return Number(v).toFixed(1); }

  function render(s) {
    lastStatus = s;
    var load = s.stations[0], unload = s.stations[2];
    // mover
    s.movers.forEach(function (m) {
      var o = gMover[m.id], p = ringXY(m.pos);
      o.g.setAttribute('transform', 'translate(' + p.x.toFixed(1) + ',' + p.y.toFixed(1) + ')');
      o.body.setAttribute('transform', 'rotate(' + p.a.toFixed(1) + ')');
      o.label.setAttribute('transform', 'translate(0,0)');
      o.rect.style.fill = colorOf(m);
      show(o.errRing, m.error);
      // 工件三態 (有／已壓／空)；Unload 夾住時工件在夾爪上，Load 放開後到 Done 前先畫在 mover 上
      var has = m.hasPart;
      if (unload.state === 10 && unload.mover === m.id && unload.step >= 30 && unload.step <= 69) has = false;
      if (load.state === 10 && load.mover === m.id && load.step >= 70) has = true;
      var pressed = has && m.pressed;
      show(o.has, has && !pressed);
      show(o.pressed, pressed);
      show(o.empty, !has);
    });
    // 站
    STATIONS.forEach(function (def, i) {
      var st = s.stations[i], o = gStation[def.id];
      o.title.textContent = def.id + ' ' + def.name;
      o.sub.textContent = STATE_NAME[st.state] + (st.step ? ' step ' + st.step : '') + (st.error ? ' ' + hex(st.errorId) : '');
      if (def.kind === 'press') {
        var h = 150 + st.z * 1.92;
        o.rod.setAttribute('height', Math.max(10, h - 104));
        o.head.setAttribute('y', h - 4);
        o.zText.textContent = 'Z ' + f1(st.z);
      } else {
        o.carriage.setAttribute('x', st.x - 18);
        o.rod.setAttribute('x', st.x - 3);
        var tip = 150 + st.z * 1.1;
        o.rod.setAttribute('height', Math.max(10, tip - 164));
        o.jaw.setAttribute('x', st.x - (st.grip ? 9 : 17));
        o.jaw.setAttribute('width', st.grip ? 18 : 34);
        o.jaw.setAttribute('y', tip - 2);
        o.xyz.textContent = 'X ' + f1(st.x) + '  Y ' + f1(st.y) + '  Z ' + f1(st.z);
        show(o.grabbed, st.grip);
        o.grabbed.setAttribute('x', st.x - 10);
        o.grabbed.setAttribute('y', tip + 4);
        var pm = st.mover ? s.movers[st.mover - 1] : null;
        o.grabbed.setAttribute('class', def.id === 3 && pm && pm.pressed ? 'part-pressed' : 'part-has');
        if (def.id === 1) show(trayParts[1], !(st.grip || (st.state === 10 && st.step >= 40)));
        else {
          show(trayParts[3], st.doneCount > 0 && !st.grip);
          o.outCount.textContent = '已出料 ' + st.doneCount;
        }
      }
    });
    renderPanel(s);
  }

  function renderPanel(s) {
    var rb = $('readyBadge'); rb.textContent = s.ready ? 'TRUE' : 'FALSE'; rb.className = 'badge ' + (s.ready ? 'good' : 'bad');
    var mb = $('modeNow'); mb.textContent = s.mode === 1 ? 'Auto' : 'Manual'; mb.className = 'badge ' + (s.mode === 1 ? 'warn' : 'info');
    var cb = $('cycleNow'); cb.textContent = CYCLE_NAME[s.cycleState] + (s.cycleMover ? ' (mover ' + s.cycleMover + ' → 站 ' + s.cycleStation + ')' : '');
    cb.className = 'badge ' + (s.cycleState === 900 ? 'bad' : s.cycleState === 10 || s.cycleState === 20 ? 'warn' : s.cycleState === 100 ? 'good' : '');
    $('initMsg').textContent = 'sStepMsg：' + (s.initMsg || '-') + (s.initError ? '  [error ' + hex(s.initErrorId) + ']' : '');
    $('cycleMsg').textContent = 'sCycle：' + (s.cycleMsg || '-');
    var idx = STEPS.indexOf(s.initStepName), failAt = -1;
    if (s.initStep === 900) failAt = STEPS.indexOf((s.initMsg || '').split(':')[0]);
    var lis = $('initSteps').children;
    for (var i = 0; i < lis.length; i++) {
      var c = '';
      if (s.initStep === 900) c = i < failAt ? 'done' : i === failAt ? 'fail' : '';
      else if (s.initStep === 100) c = 'done';
      else if (idx >= 0) c = i < idx ? 'done' : i === idx ? 'cur' : '';
      lis[i].className = c;
    }
    var mt = $('moverTable').tBodies[0], sel = Number($('moverSel').value);
    s.movers.forEach(function (m, i) {
      var r = mt.rows[i] || mt.insertRow();
      r.className = m.id === sel ? 'sel' : '';
      r.innerHTML = '<td>' + m.id + '</td><td>' + f1(m.pos) + '</td><td>' + f1(m.vel) + '</td><td>' + moverState(m) +
        '</td><td>' + (m.hasPart ? (m.pressed ? '已壓' : '有') : '空') + '</td><td>' + (m.station || '-') + '</td>';
    });
    var stt = $('stationTable').tBodies[0];
    s.stations.forEach(function (st, i) {
      var r = stt.rows[i] || stt.insertRow();
      r.innerHTML = '<td>' + st.id + ' ' + STATIONS[i].name + '</td><td>' + STATE_NAME[st.state] + (st.error ? ' ' + hex(st.errorId) : '') +
        '</td><td>' + st.step + '</td><td>' + (st.mover || '-') + '</td><td>' + f1(st.x) + ' / ' + f1(st.y) + ' / ' + f1(st.z) +
        '</td><td>' + (st.grip ? '閉' : '開') + '</td><td>' + st.doneCount + '</td>';
    });
  }

  function markSel() { if (lastStatus) renderPanel(lastStatus); }

  // ---- 命令 ----
  function run(label, name, args) {
    return backend.cmd(name, args).then(function (r) {
      var box = $('result');
      box.className = 'result ' + (r.accepted ? 'accepted' : 'rejected');
      box.innerHTML = '';
      var tag = document.createElement('span'); tag.className = 'tag'; tag.textContent = r.accepted ? 'accepted' : 'REJECTED';
      box.appendChild(tag);
      box.appendChild(document.createTextNode(label + ' → ' + r.sMsg));
      var li = document.createElement('li');
      li.className = r.accepted ? 'acc' : 'rej';
      li.textContent = new Date().toLocaleTimeString() + ' ' + label + ' → ' + (r.accepted ? 'OK' : 'NO') + ' "' + r.sMsg + '"';
      var log = $('log'); log.insertBefore(li, log.firstChild);
      while (log.children.length > 40) log.removeChild(log.lastChild);
      return r;
    });
  }
  function num(id) { return Number($(id).value); }

  function wire() {
    $('btnInitial').onclick = function () { run('M_InitialXTS', 'initial'); };
    $('btnManual').onclick = function () { run('M_SetMode(0)', 'setMode', { mode: 0 }); };
    $('btnAuto').onclick = function () { run('M_SetMode(1)', 'setMode', { mode: 1 }); };
    $('btnStopAll').onclick = function () { run('M_Stop', 'stopAll'); };
    $('btnCycle').onclick = function () { run('M_StartCycle(' + num('cycleMover') + ')', 'startCycle', { mover: num('cycleMover') }); };
    $('btnMoveTo').onclick = function () {
      var n = num('moverSel');
      run('mover[' + n + '].M_MoveTo(' + num('posIn') + ', ' + num('velIn') + ')', 'moverMoveTo', { mover: n, position: num('posIn'), velocity: num('velIn') });
    };
    $('btnMoverStop').onclick = function () { var n = num('moverSel'); run('mover[' + n + '].M_Stop', 'moverStop', { mover: n }); };
    $('btnMoverReset').onclick = function () { var n = num('moverSel'); run('mover[' + n + '].M_Reset', 'moverReset', { mover: n }); };
    $('btnSend').onclick = function () {
      var n = num('moverSel'), s = num('sendStation');
      run('M_SendToStation(' + n + ', ' + s + ')', 'sendToStation', { mover: n, station: s });
    };
    $('btnStStart').onclick = function () { var n = num('stationSel'); run('station[' + n + '].M_Start', 'stationStart', { station: n }); };
    $('btnStRelease').onclick = function () { var n = num('stationSel'); run('station[' + n + '].M_Release', 'stationRelease', { station: n }); };
    $('btnStReset').onclick = function () { var n = num('stationSel'); run('station[' + n + '].M_Reset', 'stationReset', { station: n }); };
    $('moverSel').onchange = markSel;
  }

  function fillSelects() {
    [['cycleMover', 4, function (i) { return 'mover ' + i; }], ['moverSel', 4, function (i) { return 'mover ' + i; }]].forEach(function (d) {
      for (var i = 1; i <= d[1]; i++) { var o = document.createElement('option'); o.value = i; o.textContent = d[2](i); $(d[0]).appendChild(o); }
    });
    ['sendStation', 'stationSel'].forEach(function (id) {
      STATIONS.forEach(function (st) { var o = document.createElement('option'); o.value = st.id; o.textContent = st.id + ' ' + st.name; $(id).appendChild(o); });
    });
    $('cycleMover').value = '1';
    STEPS.forEach(function (n) { var li = document.createElement('li'); li.textContent = n; $('initSteps').appendChild(li); });
  }

  function header() {
    var mb = $('modeBadge'), cb = $('connBadge'), sw = $('modeSwitch');
    mb.textContent = isOffline ? 'Offline 模擬' : 'Live (PLC)'; mb.className = 'badge ' + (isOffline ? 'warn' : 'info');
    if (isOffline) {
      cb.textContent = '模擬器'; cb.className = 'badge good';
      if (!isFile) sw.innerHTML = '<a href="?mode=live">切換 Live</a>'; else sw.textContent = '開檔 (file://) 只有 Offline';
      $('offlineTools').hidden = false;
      document.querySelectorAll('button.speed').forEach(function (b) {
        b.onclick = function () {
          backend.setSpeed(Number(b.dataset.speed));
          document.querySelectorAll('button.speed').forEach(function (x) { x.classList.toggle('on', x === b); });
        };
      });
      $('btnInject').onclick = function () { var n = num('moverSel'); run('inject error mover ' + n, 'injectMoverError', { mover: n }); };
      $('chkFailInit').onchange = function () { backend.setFailNextInit(this.checked); };
      $('btnRestart').onclick = function () { backend.restart(); $('log').innerHTML = ''; $('result').textContent = '-'; $('result').className = 'result'; };
    } else {
      cb.textContent = '連線中…'; cb.className = 'badge';
      sw.innerHTML = '<a href="?mode=offline">切換 Offline</a>';
    }
  }

  buildScene(); fillSelects(); wire(); header();
  backend.start(function (s) {
    if (s.connected === false) {
      $('connBadge').textContent = 'PLC 未連線'; $('connBadge').className = 'badge bad';
      $('banner').hidden = false; $('banner').textContent = '連不上 PLC：' + (s.error || '') + '（server 會自動重試）';
      return;
    }
    if (!isOffline) {
      $('connBadge').textContent = 'PLC 已連線'; $('connBadge').className = 'badge good';
      var w = s.detailWarnings || [];
      $('banner').hidden = w.length === 0;
      $('banner').textContent = w.length ? 'detailWarnings：' + w.join(' | ') : '';
    }
    render(s);
  }, function (up) {
    if (!up) { $('connBadge').textContent = '與 server 斷線'; $('connBadge').className = 'badge bad'; }
  });
  window.__xts = { backend: backend };   // 給自動化測試用
})();
