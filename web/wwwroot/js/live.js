/* Live backend：呼叫 ASP.NET Core minimal API，狀態用 SSE (/api/events) 接收。 */
(function () {
  'use strict';

  var routes = {
    initial: function () { return ['POST', '/api/initial']; },
    setMode: function (a) { return ['POST', '/api/mode', { mode: a.mode }]; },
    startCycle: function (a) { return ['POST', '/api/cycle/start', { mover: a.mover }]; },
    stopAll: function () { return ['POST', '/api/stop']; },
    moverMoveTo: function (a) { return ['POST', '/api/mover/' + a.mover + '/move-to', { position: a.position, velocity: a.velocity }]; },
    moverStop: function (a) { return ['POST', '/api/mover/' + a.mover + '/stop']; },
    moverReset: function (a) { return ['POST', '/api/mover/' + a.mover + '/reset']; },
    sendToStation: function (a) { return ['POST', '/api/mover/' + a.mover + '/send-to-station', { station: a.station }]; },
    stationStart: function (a) { return ['POST', '/api/station/' + a.station + '/start']; },
    stationRelease: function (a) { return ['POST', '/api/station/' + a.station + '/release']; },
    stationReset: function (a) { return ['POST', '/api/station/' + a.station + '/reset']; },
  };

  function createBackend() {
    var es = null;
    return {
      kind: 'live',
      start: function (onStatus, onConn) {
        es = new EventSource('/api/events');
        es.onmessage = function (ev) { var s = JSON.parse(ev.data); s.source = 'live'; onStatus(s); };
        es.onopen = function () { onConn(true); };
        es.onerror = function () { onConn(false); };   // EventSource 會自動重連
      },
      stop: function () { if (es) es.close(); },
      cmd: function (name, args) {
        var r = routes[name](args || {});
        return fetch(r[1], {
          method: r[0],
          headers: { 'Content-Type': 'application/json' },
          body: r[2] ? JSON.stringify(r[2]) : undefined,
        }).then(function (resp) { return resp.json(); })
          .catch(function (e) { return { accepted: false, sMsg: 'Request failed: ' + e.message }; });
      },
    };
  }

  window.XtsLive = { createBackend: createBackend };
})();
