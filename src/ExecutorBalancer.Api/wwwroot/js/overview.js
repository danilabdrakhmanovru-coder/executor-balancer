// Обзор: плитки, живой график, нагрузка исполнителей и лента назначений.
import { api } from './api.js';
import { $, el, row, badge, fmt, fmtTime, deviation, emptyRow } from './dom.js';
import { columns } from './charts.js';
import { KIND, renderExplanation } from './explain.js';

let lastFeedId = 0;
let liveLoadedAt = 0;

function renderTiles(t, fairness) {
  const mean = fairness?.meanAbsDeviationPercent;
  const tiles = [
    ['Заявок всего', fmt(t.orders)],
    ['В работе', fmt(t.open)],
    ['Ожидают исполнителя', fmt(t.pending), t.pending > 0],
    ['Назначено за час', fmt(t.assignedLastHour)],
    ['Решено сегодня', fmt(t.closedToday)],
    ['Не доставлено в АИС', fmt(t.undelivered), t.undelivered > 50],
    ['Активных исполнителей', fmt(t.activeExecutors)],
    ['Отклонение от справедливой доли', mean === null || mean === undefined ? '—' : `${fmt(mean)}%`,
      mean !== null && mean !== undefined && mean > 2,
      'Среднее за сегодня по всем исполнителям: Σ|факт − справедливая доля| / Σ справедливых долей'],
  ];
  $('tiles').replaceChildren(...tiles.map(([label, value, warn, title]) => {
    const tile = el('div', null, `tile${warn ? ' warn' : ''}`);
    if (title) tile.title = title;
    tile.append(el('div', label, 'muted'), el('div', value, 'value'));
    return tile;
  }));
}

function renderExecutors(executors) {
  if (!executors.length) {
    $('executors').replaceChildren(emptyRow(7, 'Исполнителей пока нет — их передаёт АИС (или python tools/loadgen.py --setup)'));
    return;
  }
  const max = Math.max(1, ...executors.filter((e) => e.isActive).map((e) => e.relativeLoad));
  $('executors').replaceChildren(...executors.map((e) => {
    const name = el('span');
    name.append(el('span', `${e.fullName} `), e.isActive ? badge('активен', 'ok') : badge('неактивен', 'bad'));
    const bar = el('div', null, 'bar');
    const fill = el('span');
    fill.style.width = `${Math.min(100, (e.relativeLoad / max) * 100)}%`;
    bar.append(fill);
    const barCell = el('div');
    barCell.append(bar, el('small', fmt(e.relativeLoad), 'muted'));
    const limit = e.dailyLimit == null ? '∞' : e.dailyLimit;
    const limitCell = e.dailyLimit != null && e.assignedToday >= e.dailyLimit
      ? badge(`${e.assignedToday} / ${limit}`, 'warn') : `${e.assignedToday} / ${limit}`;
    return row([name, fmt(e.qualificationWeight), e.openCount, fmt(e.openWeight), barCell, limitCell,
      deviation(e.deviationPercent)], e.isActive ? '' : 'inactive');
  }));
}

function renderFeed(items) {
  const newest = items.length ? items[0].id : lastFeedId;
  if (!items.length) {
    $('feed').replaceChildren(emptyRow(5, 'Назначений пока нет'));
    return;
  }
  $('feed').replaceChildren(...items.map((a) => {
    const tr = row([fmtTime(a.createdAt), `#${a.orderId}`, a.executorName, KIND[a.kind] || a.kind, a.score],
      `clickable${lastFeedId && a.id > lastFeedId ? ' fresh' : ''}`);
    tr.addEventListener('click', () => openOrder(a.orderId));
    return tr;
  }));
  lastFeedId = newest;
}

async function refreshLive() {
  const points = await api('/api/dashboard/live');
  columns($('live-chart'), points.map((p) => ({
    label: new Date(p.minute).toLocaleTimeString('ru-RU', { hour: '2-digit', minute: '2-digit' }),
    value: p.assigned,
  })));
  liveLoadedAt = Date.now();
}

export async function refreshOverview() {
  const [summary, feed] = await Promise.all([api('/api/dashboard/summary'), api('/api/dashboard/feed')]);
  renderTiles(summary.totals, summary.fairness);
  renderExecutors(summary.executors);
  renderFeed(feed);
  const errors = Object.entries(summary.ruleErrors || {});
  $('rule-errors').textContent = errors.length
    ? `Правила с ошибками не применяются: ${errors.map(([id, text]) => `#${id} — ${text}`).join('; ')}` : '';
  if (Date.now() - liveLoadedAt > 10000) await refreshLive();
}

export async function openOrder(id) {
  const body = $('details-body');
  $('details-title').textContent = `Заявка #${id}`;
  body.replaceChildren(el('p', 'Загрузка…', 'muted'));
  if (!$('details').open) $('details').showModal();
  try {
    const order = await api(`/api/dashboard/orders/${encodeURIComponent(id)}`);
    const kv = el('div', null, 'kv');
    const attrs = Object.entries(order.attributes || {})
      .map(([k, v]) => `${k}: ${Array.isArray(v) ? v.join(', ') : v}`).join('; ');
    for (const [k, v] of [['Статус', order.status], ['Вес', fmt(order.weight)],
      ['Родитель', order.parentId ? `#${order.parentId}` : '—'], ['Исполнитель', order.executorId ?? '—'],
      ['Ожидание', order.pendingReason ?? '—'], ['Параметры', attrs || '—']]) {
      kv.append(el('span', k, 'muted'), el('span', v));
    }
    body.replaceChildren(kv);
    const current = [...order.history].reverse().find((h) => h.isCurrent) || order.history[order.history.length - 1];
    if (!current || !current.explanation) {
      body.append(el('p', 'Назначений пока нет.', 'muted'));
      return;
    }
    body.append(renderExplanation(current.explanation));
    if (order.history.length > 1) {
      body.append(el('p', `История назначений: ${order.history.map((h) => `${KIND[h.kind] || h.kind} → ${h.executorId}`).join(' · ')}`, 'muted'));
    }
  } catch (e) {
    body.replaceChildren(el('p', e.status === 404 ? 'Заявка не найдена.' : 'Ошибка загрузки.', 'error'));
  }
}

export function initOverview() {
  $('lookup').addEventListener('submit', (event) => {
    event.preventDefault();
    const id = $('lookup-id').value;
    if (/^\d{1,18}$/.test(id)) openOrder(id);
  });
  $('details-close').addEventListener('click', () => $('details').close());
}
