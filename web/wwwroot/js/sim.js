/* Offline 模擬器：在瀏覽器裡重現 PLC 端行為 (FB_XTS_Control / FB_Mover / FB_Station*)。
 * 參數取自 plc/.../GVLs/GVL_Param.TcGVL；命令檢查與 sMsg 字串跟 PLC 一致。
 * 經典 <script>，不用 ES module，所以 file:// 開檔也能跑。
 * 時間單位：內部用秒 (模擬時間)，可用 setSpeed() 加速。 */
(function () {
  'use strict';

  // ---- GVL_Param ----
  const P = {
    moverCount: 4, stationCount: 3,
    trackMm: 3000, stationPosMm: [1600, 2100, 2600],
    maxVelMmS: 1500, defVelMmS: 400, moverAccMmS2: 2000, caGapMm: 110, atStationTolMm: 1.0,
    moveTimeoutS: 600, initStepTimeoutS: 300, resetTimeS: 0.5, stationStepTimeoutS: 120,
    gantryVelMmS: 200, gantryAccMmS2: 1000, zUpMm: 0, zDownMm: 80,
    feederX: 300, feederY: 150, outX: 300, outY: -150, moverX: 0, moverY: 0,
    gripS: 0.3, pressZDownMm: 50, pressHoldS: 1.0,
    // 模擬專用：PLC 裡這些步驟花的時間取決於 XTS 與 UmRT，這裡是示意值
    envInitS: 1.5, waitMoversS: 1.0, findMover1S: 0.5, findMover1WaitS: 2.0, powerOnS: 1.0, enableGroupS: 1.0,
    // mover 啟動位置：XTS 設定 offset 40 mm、間距 400 mm；編號順序為示意 (Descending)
    moverStartMm: [1240, 840, 440, 40],
  };
  const INIT = { Idle: 0, Reset: 5, InitEnvironment: 10, WaitMovers: 20, FindMover1: 21, FindMover1Wait: 22,
    PowerOn: 30, EnableGroup: 40, Ready: 100, Error: 900 };
  const INIT_NAME = {}; Object.keys(INIT).forEach(function (k) { INIT_NAME[INIT[k]] = k; });
  const CYC = { Idle: 0, ToStation: 10, InStation: 20, Done: 100, Stopped: 800, Error: 900 };
  const ST = { Idle: 0, Busy: 10, Done: 20, Error: 900 };
  const ERR = { Move: 0xE101, Station: 0xE201, InitTimeout: 0xE301, IdDetection: 0xE303, LostReady: 0xE310 };

  const ring = function (p, len) { len = len || P.trackMm; var r = p - Math.trunc(p / len) * len; return r < 0 ? r + len : r; };
  const hex = function (n) { return '16#' + n.toString(16).toUpperCase(); };

  // ---- F_CheckCmdAllowed ----
  function check(sim, needManual, needAuto) {
    if (!sim.ready) return 'XTS not ready, call M_InitialXTS first';
    if (needManual && sim.mode !== 0) return 'Rejected in Auto mode, call M_SetMode(0) first';
    if (needAuto && sim.mode !== 1) return 'Needs Auto mode, call M_SetMode(1) first';
    return '';
  }
  const ok = function (msg) { return { accepted: true, sMsg: msg }; };
  const no = function (msg) { return { accepted: false, sMsg: msg }; };

  // ---- 梯形速度軸 (站上 XYZ / 下壓軸，NC 模擬軸) ----
  function SimAxis() { this.pos = 0; this.vel = 0; this.target = 0; this.moving = false; }
  SimAxis.prototype.moveTo = function (t) { this.target = t; this.moving = true; };
  SimAxis.prototype.halt = function () { this.moving = false; this.vel = 0; };
  SimAxis.prototype.tick = function (dt) {
    if (!this.moving) return;
    var rem = this.target - this.pos, dir = Math.sign(rem), a = P.gantryAccMmS2;
    var vStop = Math.sqrt(2 * a * Math.abs(rem));
    var vDes = Math.min(P.gantryVelMmS, vStop) * dir;
    var dv = a * dt;
    this.vel += Math.max(-dv, Math.min(dv, vDes - this.vel));
    var step = this.vel * dt;
    if (Math.abs(rem) <= Math.abs(step) || Math.abs(rem) < 1e-6) { this.pos = this.target; this.vel = 0; this.moving = false; }
    else this.pos += step;
  };

  // ---- FB_Mover ----
  function Mover(idx, startMm) {
    this.idx = idx; this.abs = startMm; this.pos = startMm; this.vel = 0;
    this.powered = false; this.powerT = 0;
    this.state = 'idle'; this.target = startMm; this.cmdVel = 0; this.moveT = 0;
    this.busy = false; this.done = false; this.error = false; this.errorId = 0;
    this.hasPart = false; this.pressed = false;
    this.reqMove = false; this.reqPos = 0; this.reqVel = 0; this.reqStop = false; this.reqReset = false;
  }
  // 內部：總控與 M_MoveTo 共用，不檢查模式
  Mover.prototype.requestMove = function (pos, vel) {
    if (this.reqMove || this.reqStop) return no('Previous command not yet taken by PLC, retry');
    if (this.error) return no('Mover in error, call M_InitialXTS');
    if (!this.powered) return no('Mover not powered');
    if (!(pos >= 0) || pos >= P.trackMm) return no('Position out of range, 0 <= fPosition < 3000.0');
    if (!(vel > 0) || vel > P.maxVelMmS) return no('Velocity out of range, 0 < fVelocity <= 1500.0');
    this.reqPos = pos; this.reqVel = vel; this.reqMove = true;
    return ok('Accepted');
  };
  Mover.prototype.requestStop = function () {
    this.reqStop = true;
    return ok(this.powered ? 'Stop accepted' : 'Not powered, nothing to stop');
  };
  Mover.prototype.tick = function (dt, ctl, movers) {
    // MC_Power
    if (ctl.powerOn) { this.powerT += dt; if (this.powerT >= 0.3) this.powered = true; }
    else { this.powerT = 0; this.powered = false; }
    if (!this.powered && this.state !== 'idle') { this.state = 'idle'; this.vel = 0; }
    if (ctl.initReset) { this.reqReset = false; this.error = false; this.errorId = 0; }
    if (this.reqReset) { this.reqReset = false; this.error = false; this.errorId = 0; }
    // 取走命令
    if (this.reqStop) { this.reqStop = false; this.reqMove = false; this.state = 'halting'; }
    else if (this.reqMove) {
      this.reqMove = false;
      var dist = ring(this.reqPos - ring(this.abs));
      this.target = this.abs + dist; this.cmdVel = this.reqVel; this.done = false; this.moveT = 0;
      this.state = 'moving';
    }
    var a = P.moverAccMmS2;
    if (this.state === 'moving') {
      this.moveT += dt;
      var rem = this.target - this.abs;
      // CA：前方最近的 mover 保持 caGap 距離
      var gapRoom = Infinity, vLead = 0;
      for (var k = 0; k < movers.length; k++) {
        var o = movers[k]; if (o === this) continue;
        var d = ring(o.abs - this.abs);
        if (d < 1e-9) continue;
        var room = Math.max(0, d - P.caGapMm);
        if (room < gapRoom) { gapRoom = room; vLead = Math.max(0, o.vel); }
      }
      var vDes = Math.min(this.cmdVel, Math.sqrt(2 * a * Math.max(0, rem)));
      if (gapRoom !== Infinity) vDes = Math.min(vDes, vLead + Math.sqrt(2 * a * gapRoom));
      var dv = a * dt;
      this.vel += Math.max(-dv, Math.min(dv, vDes - this.vel));
      if (this.vel < 0) this.vel = 0;
      var adv = Math.min(this.vel * dt, gapRoom);
      if (rem <= adv + 1e-9) { this.abs = this.target; this.vel = 0; this.done = true; this.state = 'idle'; }
      else { this.abs += adv; if (adv < this.vel * dt) this.vel = adv / dt; }
      if (this.state === 'moving' && this.moveT >= P.moveTimeoutS) {
        this.error = true; this.errorId = ERR.Move; this.state = 'idle'; this.vel = 0;
      }
    } else if (this.state === 'halting') {
      this.vel = Math.max(0, this.vel - a * dt);
      this.abs += this.vel * dt;
      if (this.vel === 0) this.state = 'idle';
    }
    this.pos = ring(this.abs);
    this.busy = this.state !== 'idle';
  };

  // ---- FB_Station 底層 + 三個子類別 ----
  function Station(idx, kind) {
    this.idx = idx; this.kind = kind;          // 'load' | 'press' | 'unload'
    this.state = ST.Idle; this.step = 0; this.mover = 0; this.error = false; this.errorId = 0; this.doneCount = 0;
    this.reqStart = false; this.reqRelease = false; this.reqReset = false;
    this.moverAt = 0; this.moverHasPart = false; this.moverPressed = false;
    this.stepT = 0; this.stepLast = -1; this.timer = 0; this.grip = false;
    this.ax = [new SimAxis(), new SimAxis(), new SimAxis()];   // X Y Z；Press 只用 Z
    this.powered = false; this.occupied = false;
  }
  Station.prototype.checkStart = function () {
    if (this.kind === 'load') return this.moverHasPart ? 'Mover already has a part' : '';
    if (this.kind === 'press') {
      if (!this.moverHasPart) return 'Mover has no part to press';
      if (this.moverPressed) return 'Part already pressed';
      return '';
    }
    return this.moverHasPart ? '' : 'Mover has no part to unload';
  };
  Station.prototype.requestStart = function () {
    if (this.reqStart) return no('Previous command not yet taken by PLC, retry');
    if (this.state !== ST.Idle) return no('Station not idle (busy, waiting release or error)');
    if (this.moverAt === 0) return no('No mover at station');
    var m = this.checkStart(); if (m) return no(m);
    this.reqStart = true; return ok('Accepted');
  };
  Station.prototype.requestRelease = function () {
    if (this.state === ST.Done) { this.reqRelease = true; return ok('Accepted'); }
    return no('Station not in Done, nothing to release');
  };
  Station.prototype.abortAxes = function () { this.ax.forEach(function (a) { a.halt(); }); };
  Station.prototype.axesDone = function () { return this.ax.every(function (a) { return !a.moving; }); };
  Station.prototype.setTarget = function (x, y, z) {
    if (this.kind === 'press') { this.ax[2].moveTo(z); return; }
    this.ax[0].moveTo(x); this.ax[1].moveTo(y); this.ax[2].moveTo(z);
  };
  // 每 cycle 推進一步；回傳 'done' | 'error' | ''
  Station.prototype.sequence = function (dt) {
    var s = this.step;
    if (this.kind === 'press') {
      if (s === 10) { this.setTarget(0, 0, P.pressZDownMm); this.step = 11; }
      else if (s === 11) { if (this.axesDone()) this.step = 20; }
      else if (s === 20) { if (this.timer >= P.pressHoldS) this.step = 30; }
      else if (s === 30) { this.setTarget(0, 0, P.zUpMm); this.step = 31; }
      else if (s === 31) { if (this.axesDone()) return 'done'; }
      else return 'error';
      return '';
    }
    var pick = this.kind === 'load' ? [P.feederX, P.feederY] : [P.moverX, P.moverY];
    var place = this.kind === 'load' ? [P.moverX, P.moverY] : [P.outX, P.outY];
    switch (s) {
      case 10: this.setTarget(pick[0], pick[1], P.zUpMm); this.step = 11; break;
      case 11: if (this.axesDone()) this.step = 20; break;
      case 20: this.setTarget(pick[0], pick[1], P.zDownMm); this.step = 21; break;
      case 21: if (this.axesDone()) this.step = 30; break;
      case 30: this.grip = true; if (this.timer >= P.gripS) this.step = 40; break;
      case 40: this.setTarget(pick[0], pick[1], P.zUpMm); this.step = 41; break;
      case 41: if (this.axesDone()) this.step = 50; break;
      case 50: this.setTarget(place[0], place[1], P.zUpMm); this.step = 51; break;
      case 51: if (this.axesDone()) this.step = 60; break;
      case 60: this.setTarget(place[0], place[1], P.zDownMm); this.step = 61; break;
      case 61: if (this.axesDone()) this.step = 70; break;
      case 70: this.grip = false; if (this.timer >= P.gripS) this.step = 80; break;
      case 80: this.setTarget(place[0], place[1], P.zUpMm); this.step = 81; break;
      case 81: if (this.axesDone()) return 'done'; break;
      default: return 'error';
    }
    return '';
  };
  Station.prototype.tick = function (dt, ctl) {
    this.powered = ctl.powerOn;
    // 步驟逾時與計時 (TON)
    if (this.state === ST.Busy && this.step === this.stepLast) this.stepT += dt; else this.stepT = 0;
    if (this.step === 30 || this.step === 70 || (this.kind === 'press' && this.step === 20)) this.timer += dt; else this.timer = 0;
    this.stepLast = this.step;
    if (ctl.initReset || this.reqReset) {
      this.reqReset = false;
      if (this.state === ST.Busy) this.abortAxes();
      if (this.state !== ST.Done) { this.state = ST.Idle; this.step = 0; this.mover = 0; }
      this.errorId = 0;
    }
    if (ctl.stopPulse && this.state === ST.Busy) {
      this.abortAxes(); this.state = ST.Idle; this.step = 0; this.mover = 0;
    }
    if (this.state === ST.Idle) {
      if (this.reqStart) { this.mover = this.moverAt; this.step = 10; this.state = ST.Busy; }
    } else if (this.state === ST.Busy) {
      var r = this.sequence(dt);
      if (r === 'error') { this.errorId = 0xE001; this.state = ST.Error; }
      else if (r === 'done') { this.doneCount++; this.step = 0; this.state = ST.Done; }
      else if (this.stepT >= P.stationStepTimeoutS) { this.abortAxes(); this.errorId = ERR.Station; this.state = ST.Error; }
    } else if (this.state === ST.Done) {
      if (this.reqRelease) { this.mover = 0; this.state = ST.Idle; }
    }
    this.reqStart = false; this.reqRelease = false;
    this.error = this.state === ST.Error;
    this.occupied = this.moverAt !== 0 || this.state !== ST.Idle;
    this.ax.forEach(function (a) { a.tick(dt); });
  };

  // ---- FB_XTS_Control ----
  function Sim() {
    this.speed = 1;
    this.failNextInit = false;
    this.reset();
  }
  Sim.prototype.reset = function () {
    this.t = 0;
    this.movers = P.moverStartMm.map(function (p, i) { return new Mover(i + 1, p); });
    this.stations = [new Station(1, 'load'), new Station(2, 'press'), new Station(3, 'unload')];
    this.mode = 0; this.initStep = INIT.Idle; this.initMsg = ''; this.bError = false; this.errorId = 0;
    this.ready = false; this.envInit = false; this.powerOn = false; this.groupEnable = false; this.groupT = 0; this.groupReady = false;
    this.initReset = false; this.stepT = 0; this.stepLast = INIT.Idle;
    this.reqInit = false; this.reqStop = false; this.reqSetMode = false; this.reqMode = 0; this.reqStartCycle = false; this.reqCycleMover = 0;
    this.stopPulse = false;
    this.cycleState = CYC.Idle; this.cycleMover = 0; this.cycleStation = 0; this.cycleCount = 0; this.cycleMsg = '';
    this.cycleMoveIssued = false; this.cycleStart = 0; this.stationDoneMs = [0, 0, 0];
    this.lastDone = [0, 0, 0]; this.moverAt = [0, 0, 0];
  };
  Sim.prototype.initBusy = function () {
    var s = this.initStep;
    return s === INIT.Reset || s === INIT.InitEnvironment || s === INIT.WaitMovers || s === INIT.FindMover1 ||
      s === INIT.FindMover1Wait || s === INIT.PowerOn || s === INIT.EnableGroup;
  };
  Sim.prototype.cycleRunning = function () { return this.cycleState === CYC.ToStation || this.cycleState === CYC.InStation; };
  Sim.prototype.setInitError = function (id, text) {
    this.initMsg = INIT_NAME[this.initStep] + ': ' + text;
    this.bError = true; this.errorId = id || 0xE001; this.initStep = INIT.Error;
  };
  Sim.prototype.enterStep = function (s) { this.initStep = s; this.initMsg = INIT_NAME[s]; this.stepT = 0; };

  // ===== RPC 命令 (回 {accepted, sMsg}) =====
  var C = Sim.prototype;
  C.initial = function () {
    if (this.reqInit) return no('Previous command not yet taken by PLC, retry');
    if (this.initBusy()) return no('Init already running: ' + INIT_NAME[this.initStep]);
    if (this.cycleRunning()) return no('Cycle running, call M_Stop first');
    if (this.ready) return no('Already ready');
    this.reqInit = true; return ok('Init started');
  };
  C.setMode = function (n) {
    if (this.cycleRunning()) return no('Cycle running, call M_Stop first');
    if (n === 0) { this.reqMode = 0; this.reqSetMode = true; return ok('Mode -> Manual'); }
    if (n === 1) { this.reqMode = 1; this.reqSetMode = true; return ok('Mode -> Auto'); }
    return no('nMode must be 0 (Manual) or 1 (Auto)');
  };
  C.stopAll = function () {
    this.reqStop = true;
    return ok(this.ready ? 'Stop accepted' : 'Stop accepted (XTS not ready, movers may be unpowered)');
  };
  C.startCycle = function (n) {
    var m = check(this, false, true); if (m) return no(m);
    if (this.reqStartCycle) return no('Previous command not yet taken by PLC, retry');
    if (this.cycleRunning()) return no('Cycle already running');
    if (!(n >= 1 && n <= P.moverCount)) return no('nMover out of range 1..4');
    var mv = this.movers[n - 1];
    if (mv.hasPart) return no('Mover already has a part, unload it first');
    if (mv.busy || mv.error) return no('Mover busy or in error');
    if (this.stations.some(function (s) { return s.state !== ST.Idle; })) return no('All stations must be Idle');
    var path = ring(P.stationPosMm[2] - mv.pos) + P.caGapMm;
    for (var o = 1; o <= P.moverCount; o++) {
      if (o === n) continue;
      var ahead = ring(this.movers[o - 1].pos - mv.pos);
      if (ahead < path) return no('Mover ' + o + ' is in the path, move it away in Manual first');
    }
    this.reqCycleMover = n; this.reqStartCycle = true;
    return ok('Cycle accepted');
  };
  C.moverMoveTo = function (n, pos, vel) {
    var m = check(this, true, false); if (m) return no(m);
    if (!(n >= 1 && n <= P.moverCount)) return no('nMover out of range 1..4');
    return this.movers[n - 1].requestMove(pos, vel);
  };
  C.moverStop = function (n) {
    if (!(n >= 1 && n <= P.moverCount)) return no('nMover out of range 1..4');
    return this.movers[n - 1].requestStop();
  };
  C.moverReset = function (n) {
    var m = check(this, true, false); if (m) return no(m);
    this.movers[n - 1].reqReset = true; return ok('Reset accepted');
  };
  C.sendToStation = function (n, s) {
    var m = check(this, true, false); if (m) return no(m);
    if (!(n >= 1 && n <= P.moverCount)) return no('nMover out of range 1..4');
    if (!(s >= 1 && s <= P.stationCount)) return no('nStation out of range 1..3');
    var at = this.moverAt[s - 1];
    if (at !== 0 && at !== n) return no('Station occupied by mover ' + at + ', move it away first');
    return this.movers[n - 1].requestMove(P.stationPosMm[s - 1], P.defVelMmS);
  };
  C.stationStart = function (s) {
    var m = check(this, true, false); if (m) return no(m);
    if (!(s >= 1 && s <= P.stationCount)) return no('nStation out of range 1..3');
    return this.stations[s - 1].requestStart();
  };
  C.stationRelease = function (s) {
    var m = check(this, true, false); if (m) return no(m);
    if (!(s >= 1 && s <= P.stationCount)) return no('nStation out of range 1..3');
    return this.stations[s - 1].requestRelease();
  };
  C.stationReset = function (s) {
    var m = check(this, true, false); if (m) return no(m);
    this.stations[s - 1].reqReset = true; return ok('Reset accepted');
  };
  // Offline 專用：製造 mover error，驗證掉出 ready 與命令被擋
  C.injectMoverError = function (n) {
    var mv = this.movers[n - 1]; mv.error = true; mv.errorId = 0x4550; mv.state = 'idle'; mv.vel = 0;
    return ok('Mover ' + n + ' error injected (offline only)');
  };

  // ===== 每 cycle =====
  C.step = function (dt) {
    var self = this, i, n;
    this.t += dt;
    // 1. 取走命令
    this.stopPulse = false;
    if (this.reqStop) {
      this.reqStop = false; this.stopPulse = true;
      this.movers.forEach(function (m) { m.requestStop(); });
      if (this.cycleRunning()) { this.cycleState = CYC.Stopped; this.cycleMsg = 'Stopped by M_Stop'; }
    }
    if (this.reqSetMode) { this.reqSetMode = false; this.mode = this.reqMode; }
    if (this.reqInit) {
      this.reqInit = false; this.bError = false; this.errorId = 0; this.initMsg = 'Reset'; this.enterStep(INIT.Reset); this.initMsg = 'Reset';
    }
    // 2. 初始化：Reset → InitEnvironment → WaitMovers → FindMover1 (真的執行) → PowerOn → EnableGroup
    var busy = this.initBusy();
    if (busy && this.initStep === this.stepLast) this.stepT += dt; else if (!busy) this.stepT = 0;
    this.stepLast = this.initStep;
    switch (this.initStep) {
      case INIT.Reset:
        this.initReset = true; this.powerOn = false; this.groupEnable = false;
        if (this.stepT >= P.resetTimeS) {
          this.initReset = false;
          this.enterStep(this.envInit ? INIT.WaitMovers : INIT.InitEnvironment);
        }
        break;
      case INIT.InitEnvironment:
        if (this.stepT >= P.envInitS) { this.envInit = true; this.enterStep(INIT.WaitMovers); }
        break;
      case INIT.WaitMovers:
        if (this.stepT >= P.waitMoversS) this.enterStep(INIT.FindMover1);
        break;
      case INIT.FindMover1:
        if (this.stepT >= P.findMover1S) { this.initStep = INIT.FindMover1Wait; this.initMsg = 'FindMover1: Mover ID detection triggered, waiting for result'; this.stepT = 0; }
        break;
      case INIT.FindMover1Wait:
        if (this.failNextInit && this.stepT >= P.findMover1WaitS / 2) {
          this.failNextInit = false; this.setInitError(ERR.IdDetection, 'Mover ID detection error');
        } else if (this.stepT >= P.findMover1WaitS) {
          this.initStep = INIT.PowerOn; this.initMsg = 'FindMover1 done (Mover ID detection valid), PowerOn'; this.stepT = 0;
        }
        break;
      case INIT.PowerOn:
        this.powerOn = true;
        if (this.stepT >= P.powerOnS && this.movers.every(function (m) { return m.powered; })) this.enterStep(INIT.EnableGroup);
        break;
      case INIT.EnableGroup:
        this.groupEnable = true;
        if (this.stepT >= P.enableGroupS && this.groupReady) { this.initStep = INIT.Ready; this.initMsg = 'Ready'; this.stepT = 0; }
        break;
      default: break;
    }
    if (busy && this.stepT >= P.initStepTimeoutS) this.setInitError(ERR.InitTimeout, 'step timeout');
    // CA group：enable 0.5 s 後 ready
    if (this.groupEnable) { this.groupT += dt; this.groupReady = this.groupT >= 0.5; } else { this.groupT = 0; this.groupReady = false; }
    // 3. 可控監視
    var allPowered = this.movers.every(function (m) { return m.powered; });
    var anyErr = this.movers.some(function (m) { return m.error; });
    if (this.initStep === INIT.Ready) {
      if (anyErr) this.setInitError(ERR.LostReady, 'mover error');
      else if (!allPowered) this.setInitError(ERR.LostReady, 'mover power lost');
      else if (!this.groupReady) this.setInitError(ERR.LostReady, 'CA group not ready');
      // 'station axis error' (站上軸 error) 離線不模擬：模擬軸不會出錯
    }
    this.ready = this.initStep === INIT.Ready;
    // 4. 哪個 mover 停在哪一站
    for (n = 1; n <= P.stationCount; n++) {
      this.moverAt[n - 1] = 0;
      for (i = 1; i <= P.moverCount; i++) {
        var mv = this.movers[i - 1];
        var d = ring(mv.pos - P.stationPosMm[n - 1] + P.trackMm / 2) - P.trackMm / 2;
        if (!mv.busy && Math.abs(d) <= P.atStationTolMm) this.moverAt[n - 1] = i;
      }
      var st = this.stations[n - 1], j = this.moverAt[n - 1];
      st.moverAt = j;
      st.moverHasPart = j > 0 ? this.movers[j - 1].hasPart : false;
      st.moverPressed = j > 0 ? this.movers[j - 1].pressed : false;
    }
    // 5. 站完成 → 更新 mover 工件狀態
    for (n = 1; n <= P.stationCount; n++) {
      var s2 = this.stations[n - 1];
      if (s2.doneCount !== this.lastDone[n - 1]) {
        this.lastDone[n - 1] = s2.doneCount;
        var jm = s2.mover;
        if (jm >= 1 && jm <= P.moverCount) {
          var m2 = this.movers[jm - 1];
          if (n === 1) { m2.hasPart = true; m2.pressed = false; }
          else if (n === 2) { m2.pressed = true; }
          else { m2.hasPart = false; m2.pressed = false; }
        }
      }
    }
    // 6. 自動循環
    if (!this.ready && this.cycleRunning()) { this.cycleState = CYC.Error; this.cycleMsg = 'Lost ready during cycle'; }
    var pSt = (this.cycleStation >= 1 && this.cycleStation <= P.stationCount) ? this.stations[this.cycleStation - 1] : null;
    switch (this.cycleState) {
      case CYC.Idle: case CYC.Done: case CYC.Stopped: case CYC.Error:
        if (this.reqStartCycle) {
          this.cycleMover = this.reqCycleMover; this.cycleStation = 1; this.cycleMoveIssued = false;
          this.cycleStart = this.t; this.stationDoneMs = [0, 0, 0];
          this.cycleMsg = 'Cycle started'; this.cycleState = CYC.ToStation;
        }
        break;
      case CYC.ToStation: {
        var cm = this.movers[this.cycleMover - 1];
        if (!pSt) { this.cycleState = CYC.Error; this.cycleMsg = 'Invalid station index'; }
        else if (!this.cycleMoveIssued) {
          var rm = cm.requestMove(P.stationPosMm[this.cycleStation - 1], P.defVelMmS);
          this.cycleMoveIssued = rm.accepted; this.cycleMsg = rm.sMsg;
        } else if (cm.done && this.moverAt[this.cycleStation - 1] === this.cycleMover) {
          var rs = pSt.requestStart(); this.cycleMsg = rs.sMsg;
          this.cycleState = rs.accepted ? CYC.InStation : CYC.Error;
        }
        break;
      }
      case CYC.InStation:
        if (!pSt) { this.cycleState = CYC.Error; this.cycleMsg = 'Invalid station index'; }
        else if (pSt.state === ST.Error) { this.cycleState = CYC.Error; this.cycleMsg = 'Station error'; }
        else if (pSt.state === ST.Done) {
          this.stationDoneMs[this.cycleStation - 1] = Math.round((this.t - this.cycleStart) * 1000);
          this.cycleMsg = pSt.requestRelease().sMsg;
          if (this.cycleStation < P.stationCount) { this.cycleStation++; this.cycleMoveIssued = false; this.cycleState = CYC.ToStation; }
          else { this.cycleCount++; this.cycleMsg = 'Cycle done'; this.cycleState = CYC.Done; }
        }
        break;
      default: break;
    }
    this.reqStartCycle = false;
    // 7. 子 FB
    var ctl = { powerOn: this.powerOn, initReset: this.initReset, stopPulse: this.stopPulse };
    this.movers.forEach(function (m) { m.tick(dt, ctl, self.movers); });
    this.stations.forEach(function (s) { s.tick(dt, ctl); });
  };

  Sim.prototype.advance = function (realDt) {
    var left = Math.min(realDt, 0.25) * this.speed, h = 0.02;
    while (left > 1e-9) { var d = Math.min(h, left); this.step(d); left -= d; }
  };

  // ===== 快照：跟 Live 的 SSE 同一個形狀 =====
  Sim.prototype.snapshot = function () {
    var self = this;
    return {
      source: 'offline', connected: true,
      ready: this.ready, mode: this.mode, initStep: this.initStep, initStepName: INIT_NAME[this.initStep],
      initMsg: this.initMsg, initError: this.bError, initErrorId: this.errorId,
      cycleState: this.cycleState, cycleMover: this.cycleMover, cycleStation: this.cycleStation,
      cycleCount: this.cycleCount, cycleMsg: this.cycleMsg, stationDoneMs: this.stationDoneMs.slice(),
      movers: this.movers.map(function (m, i) {
        var at = 0;
        for (var n = 0; n < P.stationCount; n++) if (self.moverAt[n] === i + 1) at = n + 1;
        return { id: i + 1, pos: m.pos, vel: m.vel, powered: m.powered, busy: m.busy, done: m.done, error: m.error,
          errorId: m.errorId, hasPart: m.hasPart, pressed: m.pressed, station: at };
      }),
      stations: this.stations.map(function (s, i) {
        return { id: i + 1, state: s.state, step: s.step, occupied: s.occupied, mover: s.mover, error: s.error,
          errorId: s.errorId, doneCount: s.doneCount, x: s.ax[0].pos, y: s.ax[1].pos, z: s.ax[2].pos, grip: s.grip };
      }),
    };
  };

  // ===== 給 app.js 的 backend 介面 =====
  function createBackend() {
    var sim = new Sim(), timer = null, last = 0, onStatus = null;
    var table = {
      initial: function () { return sim.initial(); },
      setMode: function (a) { return sim.setMode(a.mode); },
      startCycle: function (a) { return sim.startCycle(a.mover); },
      stopAll: function () { return sim.stopAll(); },
      moverMoveTo: function (a) { return sim.moverMoveTo(a.mover, a.position, a.velocity); },
      moverStop: function (a) { return sim.moverStop(a.mover); },
      moverReset: function (a) { return sim.moverReset(a.mover); },
      sendToStation: function (a) { return sim.sendToStation(a.mover, a.station); },
      stationStart: function (a) { return sim.stationStart(a.station); },
      stationRelease: function (a) { return sim.stationRelease(a.station); },
      stationReset: function (a) { return sim.stationReset(a.station); },
      injectMoverError: function (a) { return sim.injectMoverError(a.mover); },
    };
    return {
      kind: 'offline',
      sim: sim,
      start: function (cb) {
        onStatus = cb; last = performance.now();
        timer = setInterval(function () {
          var now = performance.now(); sim.advance((now - last) / 1000); last = now; onStatus(sim.snapshot());
        }, 40);
      },
      stop: function () { if (timer) clearInterval(timer); },
      cmd: function (name, args) { return Promise.resolve(table[name](args || {})); },
      setSpeed: function (v) { sim.speed = v; },
      setFailNextInit: function (b) { sim.failNextInit = !!b; },
      restart: function () { sim.reset(); },
    };
  }

  window.XtsSim = { createBackend: createBackend, params: P };
})();
