'use strict';

const $ = (id) => document.getElementById(id);
const REFRESH_MS = 2000;
let timer = null;
let lastFeedId = 0;

const KIND = { Primary: 'первичная', Parent: 'от родителя', Secondary: 'вторичная', Reassign: 'перераспределение' };
const VERDICT = {
  chosen: ['выбран', 'ok'], eligible: ['подходил', ''], matched: ['подходил', ''],
  rule_failed: ['не подходит', 'bad'], inactive: ['неактивен', 'bad'], daily_limit_exceeded: ['лимит исчерпан', 'warn'],
};

function el(tag, text, cls) {
  const node = document.createElement(tag);
  if (text !== undefined && text !== null) node.textContent = String(text);
  if (cls) node.className = cls;
  return node;
}

function row(cells, cls) {
  const tr = el('tr', null, cls);
  for (const cell of cells) {
    const td = el('td');
    if (cell instanceof Node) td.append(cell); else td.textContent = cell ?? '—';
    tr.append(td);
  }
  return tr;
}

function badge(text, cls) { return el('span', text, `badge ${cls || ''}`); }

function fmtTime(iso) { return new Date(iso).toLocaleTimeString('ru-RU'); }

async function api(path, options = {}) {
  const response = await fetch(path, {
    credentials: 'same-origin',
    headers: { 'Content-Type': 'application/json' },
    ...options,
  });
  if (response.status === 401) { showLogin(); throw new Error('unauthorized'); }
  if (!response.ok) throw new Error(`HTTP ${response.status}`);
  return response.status === 204 ? null : response.json();
}

function showLogin() {
  clearInterval(timer);
  timer = null;
  $('app').classList.add('hidden');
  $('login').classList.remove('hidden');
  $('password').focus();
}

function showApp() {
  $('login').classList.add('hidden');
  $('app').classList.remove('hidden');
  refresh();
  if (!timer) timer = setInterval(refresh, REFRESH_MS);
}

function renderTiles(t) {
  const tiles = [
    ['Заявок всего', t.orders], ['В работе', t.open], ['Ожидают исполнителя', t.pending, t.pending > 0],
    ['Назначено за час', t.assignedLastHour], ['Не доставлено в АИС', t.undelivered, t.undelivered > 50],
    ['Активных исполнителей', t.activeExecutors], ['Разброс нагрузки', t.spread],
  ];
  $('tiles').replaceChildren(...tiles.map(([label, value, warn]) => {
    const tile = el('div', null, `tile${warn ? ' warn' : ''}`);
    tile.append(el('div', label, 'muted'), el('div', value, 'value'));
    return tile;
  }));
}

function renderExecutors(executors) {
  const max = Math.max(1, ...executors.filter((e) => e.isActive).map((e) => e.relativeLoad));
  $('executors').replaceChildren(...executors.map((e) => {
    const name = el('span');
    name.append(el('span', `${e.fullName} `), e.isActive ? badge('активен', 'ok') : badge('неактивен', 'bad'));
    const bar = el('div', null, 'bar');
    const fill = el('span');
    fill.style.width = `${Math.min(100, (e.relativeLoad / max) * 100)}%`;
    bar.append(fill);
    const barCell = el('div');
    barCell.append(bar, el('small', e.relativeLoad, 'muted'));
    const limit = e.dailyLimit == null ? '∞' : e.dailyLimit;
    const limitCell = e.dailyLimit != null && e.assignedToday >= e.dailyLimit
      ? badge(`${e.assignedToday} / ${limit}`, 'warn') : `${e.assignedToday} / ${limit}`;
    return row([name, e.qualificationWeight, e.openCount, e.openWeight, barCell, limitCell], e.isActive ? '' : 'inactive');
  }));
}

function renderFeed(items) {
  const newest = items.length ? items[0].id : lastFeedId;
  $('feed').replaceChildren(...items.map((a) => {
    const tr = row([fmtTime(a.createdAt), `#${a.orderId}`, a.executorName, KIND[a.kind] || a.kind, a.score],
      `clickable${lastFeedId && a.id > lastFeedId ? ' fresh' : ''}`);
    tr.addEventListener('click', () => openOrder(a.orderId));
    return tr;
  }));
  lastFeedId = newest;
}

async function refresh() {
  try {
    const [summary, feed] = await Promise.all([api('/api/dashboard/summary'), api('/api/dashboard/feed')]);
    renderTiles(summary.totals);
    renderExecutors(summary.executors);
    renderFeed(feed);
    const errors = Object.entries(summary.ruleErrors || {});
    $('rule-errors').textContent = errors.length
      ? `Правила с ошибками не применяются: ${errors.map(([id, text]) => `#${id} — ${text}`).join('; ')}` : '';
    $('updated').textContent = `обновлено ${new Date().toLocaleTimeString('ru-RU')}`;
  } catch (e) {
    if (e.message !== 'unauthorized') $('updated').textContent = 'нет связи с сервером';
  }
}

async function openOrder(id) {
  const body = $('details-body');
  $('details-title').textContent = `Заявка #${id}`;
  body.replaceChildren(el('p', 'Загрузка…', 'muted'));
  $('details').showModal();
  try {
    const order = await api(`/api/dashboard/orders/${id}`);
    const kv = el('div', null, 'kv');
    const attrs = Object.entries(order.attributes || {}).map(([k, v]) => `${k}: ${Array.isArray(v) ? v.join(', ') : v}`).join('; ');
    for (const [k, v] of [['Статус', order.status], ['Вес', order.weight], ['Родитель', order.parentId ? `#${order.parentId}` : '—'],
      ['Исполнитель', order.executorId ?? '—'], ['Ожидание', order.pendingReason ?? '—'], ['Параметры', attrs || '—']]) {
      kv.append(el('span', k, 'muted'), el('span', v));
    }
    body.replaceChildren(kv);
    const current = [...order.history].reverse().find((h) => h.isCurrent) || order.history[order.history.length - 1];
    if (!current || !current.explanation) {
      body.append(el('p', 'Назначений пока нет.', 'muted'));
      return;
    }
    const x = current.explanation;
    body.append(el('h2', `Решение: ${x.decision || '—'}`));
    for (const note of x.notes || []) body.append(el('p', note, 'note'));
    const table = el('table');
    const head = el('thead');
    head.append(row(['Исполнитель', 'Вердикт', 'Score', 'Сегодня', 'Причина']));
    const rows = el('tbody');
    const sorted = [...x.candidates].sort((a, b) => (a.score ?? 1e9) - (b.score ?? 1e9));
    for (const c of sorted) {
      const [label, cls] = VERDICT[c.verdict] || [c.verdict, ''];
      rows.append(row([c.name, badge(label, cls), c.score, c.assignedToday, c.reason]));
    }
    table.append(head, rows);
    const wrap = el('div', null, 'scroll');
    wrap.append(table);
    body.append(wrap);
    if (order.history.length > 1) {
      body.append(el('p', `История назначений: ${order.history.map((h) => `${KIND[h.kind] || h.kind} → ${h.executorId}`).join(' · ')}`, 'muted'));
    }
  } catch (e) {
    body.replaceChildren(el('p', e.message === 'HTTP 404' ? 'Заявка не найдена.' : 'Ошибка загрузки.', 'error'));
  }
}

$('login-form').addEventListener('submit', async (event) => {
  event.preventDefault();
  $('login-error').textContent = '';
  const response = await fetch('/api/auth/login', {
    method: 'POST', credentials: 'same-origin',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ password: $('password').value }),
  });
  $('password').value = '';
  if (response.ok) showApp();
  else $('login-error').textContent = response.status === 429 ? 'Слишком много попыток, подождите минуту' : 'Неверный пароль';
});

$('logout').addEventListener('click', async () => {
  await fetch('/api/auth/logout', { method: 'POST', credentials: 'same-origin' });
  showLogin();
});

$('lookup').addEventListener('submit', (event) => {
  event.preventDefault();
  const id = $('lookup-id').value;
  if (id) openOrder(id);
});

$('details-close').addEventListener('click', () => $('details').close());

fetch('/api/auth/me', { credentials: 'same-origin' }).then((r) => (r.ok ? showApp() : showLogin())).catch(showLogin);
