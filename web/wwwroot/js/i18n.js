/* 介面文字中英對照，全部集中在這裡。PLC / 模擬器回的 sMsg 本來就是英文，不經過這裡。
 * 語言優先序：網址 ?lang=zh|en → localStorage → 預設 zh。
 * 靜態文字：HTML 元素加 data-i18n="key"（textContent）或 data-i18n-aria="key"（aria-label）。
 * 動態文字：app.js 呼叫 XtsI18n.t('key', 參數...)，{0}、{1} 換成參數。 */
(function () {
  'use strict';

  var STRINGS = {
    zh: {
      hint: 'Load 放料 → Press 下壓 → Unload 取出',
      sceneAria: 'XTS 軌道、站與 mover',
      legIdle: 'mover 待命',
      legBusy: 'busy（移動／CA 排隊）',
      legError: 'error',
      legOff: '未送電',
      legHas: '有工件',
      legPressed: '已壓',
      legEmpty: '空',
      thMover: 'mover', thPos: '位置 mm', thVel: '速度', thState: '狀態', thPart: '工件', thStation: '站',
      thStep: '步驟', thXyz: 'X / Y / Z mm', thGrip: '夾', thDone: '完成',
      statusTitle: '狀態',
      modeLabel: '模式',
      cycleLabel: '循環',
      lineTitle: '整線',
      moverTitle: '選 mover',
      posLabel: '位置 mm',
      velLabel: '速度 mm/s',
      stationLabel: '站',
      stationTitle: '選站',
      resultTitle: '結果',
      offlineTitle: 'Offline 工具',
      offlineOnly: '只在模擬器',
      speedLabel: '速度',
      inject: '對所選 mover 製造 error',
      failInit: '下次 Initial 失敗',
      restart: '重設模擬',
      langLabel: '語言',
      // 動態
      trayLoad: '供料',
      trayUnload: '出料',
      trackCaption: '軌道 3000 mm（示意：直線 1100 mm × 2 + 半圓弧 400 mm × 2）',
      notPowered: '未送電',
      outCount: '已出料 {0}',
      cycleTarget: ' (mover {0} → 站 {1})',
      labelValue: '{0}：{1}',
      partPressed: '已壓', partHas: '有', partEmpty: '空',
      gripClosed: '閉', gripOpen: '開',
      badgeOffline: 'Offline 模擬',
      badgeLive: 'Live (PLC)',
      badgeSimulator: '模擬器',
      switchLive: '切換 Live',
      switchOffline: '切換 Offline',
      fileOnlyOffline: '開檔 (file://) 只有 Offline',
      connecting: '連線中…',
      plcConnected: 'PLC 已連線',
      plcDisconnected: 'PLC 未連線',
      plcConnectFail: '連不上 PLC：{0}（server 會自動重試）',
      serverDown: '與 server 斷線',
    },
    en: {
      hint: 'Load part → Press → Unload part',
      sceneAria: 'XTS track, stations and movers',
      legIdle: 'mover idle',
      legBusy: 'busy (moving / CA queue)',
      legError: 'error',
      legOff: 'not powered',
      legHas: 'has part',
      legPressed: 'pressed',
      legEmpty: 'empty',
      thMover: 'mover', thPos: 'pos mm', thVel: 'vel', thState: 'state', thPart: 'part', thStation: 'station',
      thStep: 'step', thXyz: 'X / Y / Z mm', thGrip: 'grip', thDone: 'done',
      statusTitle: 'Status',
      modeLabel: 'Mode',
      cycleLabel: 'Cycle',
      lineTitle: 'Line',
      moverTitle: 'Mover',
      posLabel: 'Position mm',
      velLabel: 'Velocity mm/s',
      stationLabel: 'Station',
      stationTitle: 'Station',
      resultTitle: 'Result',
      offlineTitle: 'Offline tools',
      offlineOnly: 'simulator only',
      speedLabel: 'Speed',
      inject: 'Inject error on selected mover',
      failInit: 'Fail next Initial',
      restart: 'Reset simulation',
      langLabel: 'Language',
      trayLoad: 'Input',
      trayUnload: 'Output',
      trackCaption: 'Track 3000 mm (schematic: 2 × 1100 mm straight + 2 × 400 mm arc)',
      notPowered: 'not powered',
      outCount: 'Unloaded {0}',
      cycleTarget: ' (mover {0} → station {1})',
      labelValue: '{0}: {1}',
      partPressed: 'pressed', partHas: 'yes', partEmpty: 'empty',
      gripClosed: 'closed', gripOpen: 'open',
      badgeOffline: 'Offline sim',
      badgeLive: 'Live (PLC)',
      badgeSimulator: 'Simulator',
      switchLive: 'Switch to Live',
      switchOffline: 'Switch to Offline',
      fileOnlyOffline: 'Opened as file:// (Offline only)',
      connecting: 'Connecting…',
      plcConnected: 'PLC connected',
      plcDisconnected: 'PLC not connected',
      plcConnectFail: 'Cannot reach the PLC: {0} (the server keeps retrying)',
      serverDown: 'Server disconnected',
    },
  };
  var HTML_LANG = { zh: 'zh-Hant', en: 'en' };
  var KEY = 'xts-rpc-demo.lang';

  function valid(l) { return l === 'zh' || l === 'en' ? l : null; }
  function stored() { try { return valid(localStorage.getItem(KEY)); } catch (e) { return null; } }
  function store(l) { try { localStorage.setItem(KEY, l); } catch (e) { /* 私密模式等：只是不記住 */ } }

  var lang = valid(new URLSearchParams(location.search).get('lang')) || stored() || 'zh';
  var listeners = [];

  function t(key) {
    var s = STRINGS[lang][key];
    if (s === undefined) s = STRINGS.zh[key] !== undefined ? STRINGS.zh[key] : key;
    for (var i = 1; i < arguments.length; i++) s = s.split('{' + (i - 1) + '}').join(String(arguments[i]));
    return s;
  }

  function applyStatic() {
    document.documentElement.lang = HTML_LANG[lang];
    document.querySelectorAll('[data-i18n]').forEach(function (e) { e.textContent = t(e.dataset.i18n); });
    document.querySelectorAll('[data-i18n-aria]').forEach(function (e) { e.setAttribute('aria-label', t(e.dataset.i18nAria)); });
  }

  /** 換語言：記到 localStorage；網址上有 ?lang= 時同步改掉，重新整理後不會被舊參數蓋回去。 */
  function setLang(l) {
    l = valid(l);
    if (!l || l === lang) return;
    lang = l;
    store(l);
    var u = new URL(location.href);
    if (u.searchParams.has('lang')) {
      u.searchParams.set('lang', l);
      try { history.replaceState(null, '', u.href); } catch (e) { /* file:// 某些瀏覽器不允許 */ }
    }
    applyStatic();
    listeners.forEach(function (f) { f(lang); });
  }

  window.XtsI18n = {
    t: t,
    lang: function () { return lang; },
    setLang: setLang,
    applyStatic: applyStatic,
    onChange: function (f) { listeners.push(f); },
  };
})();
