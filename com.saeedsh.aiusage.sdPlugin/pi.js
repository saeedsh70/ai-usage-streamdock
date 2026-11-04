// Property inspector for the AI Usage key.
// Per-key options are stored by the host (setSettings); options shared by all keys
// are kept by the plugin itself (sendToPlugin -> config.json).

(function () {
  'use strict';

  var socket = null, piUuid = null, actionUuid = null, actionContext = null;
  var settings = {};
  var lastState = null;
  var configLoaded = false;
  var queue = [];

  var DEFAULTS = { provider: 'claude', metric: 'session', filter: '', style: 'fill', label: '',
                   showReset: true, warnAt: 60, dangerAt: 85 };

  // What the list next to "Show" offers: the models / limits / quota groups the provider reported.
  var OTHER = '\u0001other';
  var NOUN = {
    claude:      { model: 'Model' },
    chatgpt:     { model: 'Limit' },
    antigravity: { model: 'Model', group: 'Group' }
  };
  var WAIT = {
    claude: 'Waiting for Claude data...',
    chatgpt: 'Waiting for ChatGPT data...',
    antigravity: 'Open Antigravity to load its list'
  };

  /** [{value, text}] for the current provider and metric, from the plugin's last state. */
  function choices(provider, metric) {
    var s = lastState && lastState.providers ? lastState.providers[provider] : null;
    var limits = (s && s.limits) || [];
    var seen = {}, out = [];
    function add(value, text) {
      if (!value || seen[value.toLowerCase()]) return;
      seen[value.toLowerCase()] = true;
      out.push({ value: value, text: text || value });
    }
    if (metric === 'model') {
      limits.forEach(function (l) {
        if (l.kind !== 'model') return;
        // Codex extra limits carry their name in scope and the window in name.
        if (provider === 'chatgpt' && l.scope) add(l.scope);
        else add(l.window || l.name);
      });
    } else {
      limits.forEach(function (l) {
        if ((l.kind === 'session' || l.kind === 'weekly') && l.scope) add(l.scope);
      });
    }
    return out;
  }

  function filterUsed(provider, metric) {
    return metric === 'model' || (provider === 'antigravity' && metric !== 'extra');
  }

  function renderFilter() {
    var p = $('provider').value, m = $('metric').value;
    var sel = $('filterSelect');
    var current = get('filter') || '';
    var list = choices(p, m);
    var noun = NOUN[p] || NOUN.claude;
    $('filterLabel').textContent = m === 'model' ? noun.model : (noun.group || 'Group');

    sel.textContent = '';
    function opt(value, text) {
      var o = document.createElement('option');
      o.value = value; o.textContent = text;
      sel.appendChild(o);
      return o;
    }
    if (m !== 'model') opt('', 'All (first group)');
    else if (!list.length && !current) opt('', WAIT[p] || 'Waiting for data...');
    var known = !current;
    list.forEach(function (c) {
      if (c.value.toLowerCase() === current.toLowerCase()) known = true;
      opt(c.value, c.text);
    });
    // Keep a saved choice visible even while the provider can't list it (e.g. Antigravity closed).
    if (!known && current) opt(current, current);
    opt(OTHER, 'Other (type a name)...');

    var custom = sel.dataset.custom === '1';
    sel.value = custom ? OTHER : current;
    if (!custom && sel.value !== current) sel.value = list.length ? list[0].value : '';
    $('filterTextRow').classList.toggle('hidden', !custom || !filterUsed(p, m));
    $('filter').value = current;
    $('filter').placeholder = m === 'model' ? 'part of the name, e.g. Opus' : 'part of the group name, e.g. Gemini';

    var hint = '';
    if (m === 'model' && p === 'claude') hint = 'Listed when your Claude plan has a separate limit for a model.';
    if (m === 'model' && p === 'chatgpt') hint = 'Listed when your plan has extra Codex limits.';
    if (m !== 'model' && p === 'antigravity') hint = 'Antigravity has separate 5-hour and weekly quotas per model group.';
    $('filterHint').textContent = hint;
  }

  function $(id) { return document.getElementById(id); }

  function send(msg) {
    if (!socket || socket.readyState !== 1) { queue.push(msg); return; }
    socket.send(JSON.stringify(msg));
  }

  function toPlugin(payload) {
    payload.ctx = actionContext; // the key this inspector belongs to, whatever the host puts in "context"
    send({ event: 'sendToPlugin', action: actionUuid, context: piUuid, payload: payload });
  }

  function saveSettings() {
    send({ event: 'setSettings', context: actionContext, payload: settings });
    // Some host builds do not echo didReceiveSettings back to the plugin.
    toPlugin({ command: 'settings', settings: settings });
  }

  function get(key) {
    if (key === 'filter' && settings.filter === undefined && settings.model) return settings.model; // older settings
    return settings[key] === undefined || settings[key] === null ? DEFAULTS[key] : settings[key];
  }

  // ---- per-key UI -------------------------------------------------------

  function renderKey() {
    $('provider').value = get('provider');
    $('metric').value = get('metric');
    $('filter').value = get('filter');
    $('style').value = get('style');
    $('label').value = get('label');
    $('showReset').checked = get('showReset') !== false && get('showReset') !== 'false';
    $('warnAt').value = get('warnAt');
    $('dangerAt').value = get('dangerAt');
    toggleKeyRows();
  }

  function toggleKeyRows() {
    var p = $('provider').value, m = $('metric').value, st = $('style').value;
    var showFilter = filterUsed(p, m);
    renderFilter();
    $('filterRow').classList.toggle('hidden', !showFilter);
    $('filterHint').classList.toggle('hidden', !showFilter || !$('filterHint').textContent);
    if (!showFilter) $('filterTextRow').classList.add('hidden');
    $('styleRow').classList.toggle('hidden', m === 'overview' || m === 'extra');
    $('resetRow').classList.toggle('hidden', m === 'overview' || m === 'extra' || st === 'bar' || st === 'ring');
    if (lastState) renderStatus(lastState);
  }

  function bindKey(id, key, read) {
    $(id).addEventListener('change', function () {
      settings[key] = read($(id));
      if (key === 'filter') delete settings.model;
      toggleKeyRows();
      saveSettings();
    });
  }

  function clampNum(el, lo, hi, fallback) {
    var n = parseInt(el.value, 10);
    if (isNaN(n)) n = fallback;
    n = Math.max(lo, Math.min(hi, n));
    el.value = n;
    return n;
  }

  bindKey('provider', 'provider', function (el) { return el.value; });
  bindKey('metric', 'metric', function (el) { return el.value; });
  bindKey('filter', 'filter', function (el) { return el.value.trim(); });

  $('filterSelect').addEventListener('change', function () {
    var sel = $('filterSelect');
    if (sel.value === OTHER) {
      sel.dataset.custom = '1';
      toggleKeyRows();
      $('filter').focus();
      return;
    }
    sel.dataset.custom = '';
    settings.filter = sel.value;
    delete settings.model;
    toggleKeyRows();
    saveSettings();
  });

  // A new provider or metric starts from that provider's own list.
  ['provider', 'metric'].forEach(function (id) {
    $(id).addEventListener('change', function () {
      $('filterSelect').dataset.custom = '';
      if (settings.filter) { settings.filter = ''; delete settings.model; toggleKeyRows(); saveSettings(); }
    });
  });
  bindKey('style', 'style', function (el) { return el.value; });
  bindKey('label', 'label', function (el) { return el.value.trim(); });
  bindKey('showReset', 'showReset', function (el) { return el.checked; });
  bindKey('warnAt', 'warnAt', function (el) { return clampNum(el, 1, 100, 60); });
  bindKey('dangerAt', 'dangerAt', function (el) { return clampNum(el, 1, 100, 85); });

  // ---- shared options ---------------------------------------------------

  function renderConfig(state) {
    var c = state.config || {};
    if (configLoaded) return;
    $('refreshMinutes').value = c.refreshMinutes || 5;
    $('proxy').value = c.proxy || '';
    $('claudeDir').value = c.claudeDir || '';
    $('codexDir').value = c.codexDir || '';
    configLoaded = true;
  }

  $('save').addEventListener('click', function () {
    toPlugin({ command: 'saveConfig', config: {
      refreshMinutes: clampNum($('refreshMinutes'), 1, 120, 5),
      proxy: $('proxy').value.trim(),
      claudeDir: $('claudeDir').value.trim(),
      codexDir: $('codexDir').value.trim()
    } });
    $('status').textContent = 'Saved. Refreshing...';
  });

  $('refresh').addEventListener('click', function () { toPlugin({ command: 'refresh' }); $('status').textContent = 'Refreshing...'; });
  $('openUsage').addEventListener('click', function () { toPlugin({ command: 'openUsage' }); });
  $('openLog').addEventListener('click', function () { toPlugin({ command: 'openLog' }); });

  // ---- status -----------------------------------------------------------

  function el(tag, cls, text) {
    var e = document.createElement(tag);
    if (cls) e.className = cls;
    if (text !== undefined) e.textContent = text;
    return e;
  }

  function renderStatus(state) {
    var all = state.providers || {};
    var s = all[$('provider').value] || {};
    var box = $('status');
    box.textContent = '';
    if (s.error) {
      box.appendChild(el('div', 'err', '✗ ' + s.error));
      box.appendChild(el('div', 'hint', s.detail || ''));
    }
    if (s.limits) {
      var head = (s.error ? 'Last good data' : '✓ ' + (s.name || 'Connected')) +
        (s.plan ? ' (' + s.plan + ')' : '') + ' — ' + (s.fetchedAt || '');
      box.appendChild(el('div', s.error ? 'hint' : 'ok', head));
      var table = el('table');
      s.limits.forEach(function (l) {
        var tr = el('tr');
        tr.appendChild(el('td', '', l.name));
        tr.appendChild(el('td', 'n', l.percent + '%'));
        tr.appendChild(el('td', 'n hint', l.reset ? 'reset ' + l.reset : ''));
        table.appendChild(tr);
      });
      if (s.extra) {
        var tr = el('tr');
        tr.appendChild(el('td', '', s.extra.label === 'EXTRA' ? 'Extra usage' : 'Credits'));
        tr.appendChild(el('td', 'n', s.extra.enabled ? (s.extra.value || '-') : 'off'));
        tr.appendChild(el('td', 'n hint', s.extra.enabled ? (s.extra.sub || '') : ''));
        table.appendChild(tr);
      }
      box.appendChild(table);
    }
    if (!s.error && !s.limits) box.textContent = s.fetching ? 'Refreshing...' : 'No data yet.';
    if (s.where) {
      var w = el('div', 'hint path', 'Reads: ' + s.where);
      w.style.marginTop = '6px';
      box.appendChild(w);
    }
  }

  // ---- host entry points ------------------------------------------------

  function connect(port, uuid, registerEvent, appInfo, actionInfo) {
    piUuid = uuid;
    var info = {};
    try { info = actionInfo ? JSON.parse(actionInfo) : {}; } catch (e) { info = {}; }
    actionUuid = info.action || 'com.saeedsh.aiusage.meter';
    actionContext = info.context || uuid;
    settings = (info.payload && info.payload.settings) || {};
    renderKey();

    socket = new WebSocket('ws://127.0.0.1:' + port);
    socket.onopen = function () {
      socket.send(JSON.stringify({ event: registerEvent, uuid: uuid }));
      queue.forEach(function (m) { socket.send(JSON.stringify(m)); });
      queue = [];
      toPlugin({ command: 'getState' });
    };
    socket.onmessage = function (evt) {
      var data;
      try { data = JSON.parse(evt.data); } catch (e) { return; }
      if (data.event === 'didReceiveSettings') {
        settings = (data.payload && data.payload.settings) || {};
        renderKey();
      } else if (data.event === 'sendToPropertyInspector' && data.payload && data.payload.event === 'state') {
        lastState = data.payload;
        renderConfig(data.payload);
        if (document.activeElement !== $('filterSelect')) toggleKeyRows();
        renderStatus(data.payload);
      }
    };
  }

  window.connectElgatoStreamDeckSocket = connect;
  window.connectSocket = connect;
  window.connectMiraBoxSDSocket = connect;

  renderKey();
})();
