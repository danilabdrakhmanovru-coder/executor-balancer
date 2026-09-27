// Обзор: плитки, живой график, нагрузка исполнителей и лента назначений.
import { api } from './api.js';
import { $, el, row, badge, fmt, fmtTime, deviation, emptyRow, tile } from './dom.js';
import { columns } from './charts.js';
import { KIND, renderExplanation } from './explain.js';
import { showCheck } from './preview.js';

let lastFeedId = 0;
let liveLoadedAt = 0;

function renderTiles(t, fairness) {
  const mean = fairness?.meanAbsDeviationPercent;
  const tiles = [
    ['Заявок всего', fmt(t.orders), 'пришло из АИС', false, 'file-text'],
    ['В работе', fmt(t.open), 'назначены и ещё не решены', false, 'briefcase'],
    ['Ожидают исполнителя', fmt(t.pending), t.pending > 0 ? 'нет свободного подходящего — повтор каждые 5 с' : 'никто не ждёт', t.pending > 0, 'hourglass'],
    ['Назначено за час', fmt(t.assignedLastHour), 'включая возвраты с доработки', false, 'bolt'],
    ['Решено сегодня', fmt(t.closedToday), 'решено и отклонено', false, 'circle-check'],
    ['Не доставлено в АИС', fmt(t.undelivered), t.undelivered > 0 ? 'в очереди на отправку, с повторами' : 'все назначения у АИС', t.undelivered > 50, 'send'],
    ['Сотрудников на работе', fmt(t.activeExecutors), 'участвуют в распределении', false, 'users'],
    ['Отклонение от справедливой доли', mean === null || mean === undefined ? '—' : `${fmt(mean)}%`,
      'насколько распределение за сегодня отличается от идеально ровного; норма — до 2%',
      mean !== null && mean !== undefined && mean > 2, 'scale'],
  ];
  $('tiles').replaceChildren(...tiles.map((args) => tile(...args)));
}

/**
 * «Сегодня / норма»: ∞ — у сотрудника нет суточного лимита. В режиме «больше нормы» видно, сколько ещё можно
 * сверх нормы, а если режим приостановлен защитой — почему.
 */
export function todayCell(e) {
  const cell = el('span', null, 'today-cell');
  if (e.dailyLimit == null) {
    cell.append(el('span', `${e.assignedToday} / ∞`));
    cell.title = 'Без суточного лимита — получает столько, сколько распределится поровну';
    return cell;
  }
  const cap = e.extra?.extraLimit ?? e.dailyLimit;
  const text = `${e.assignedToday} / ${e.dailyLimit}`;
  cell.append(e.assignedToday >= cap ? badge(text, 'warn') : e.assignedToday >= e.dailyLimit ? badge(text, 'ok') : el('span', text));
  if (e.extraPercent > 0 && e.extra?.extraLimit) {
    const extra = badge(`+${e.extraPercent}% до ${e.extra.extraLimit}`, 'ok');
    extra.title = 'Режим «больше нормы»: сверх нормы получает только излишки';
    cell.append(' ', extra);
  } else if (e.extraPercent > 0) {
    const off = badge('режим на паузе', 'bad');
    off.title = e.extra?.note || 'режим «больше нормы» не действует';
    cell.append(' ', off);
  }
  return cell;
}

function renderExecutors(executors) {
  if (!executors.length) {
    $('executors').replaceChildren(emptyRow(7, 'Сотрудников в отделе пока нет — их передаёт АИС (на демонстрации — «Тестовый стенд»)'));
    return;
  }
  const max = Math.max(1, ...executors.filter((e) => e.isActive).map((e) => e.relativeLoad));
  $('executors').replaceChildren(...executors.map((e) => {
    const name = el('span');
    const link = el('a', e.fullName, 'executor-name');
    link.href = `#executor-${e.id}`;
    name.append(link, ' ', e.isActive ? badge('активен', 'ok') : badge('неактивен', 'bad'));
    const bar = el('div', null, 'bar');
    const fill = el('span');
    fill.style.width = `${Math.min(100, (e.relativeLoad / max) * 100)}%`;
    bar.append(fill);
    const barCell = el('div');
    barCell.append(bar, el('small', fmt(e.relativeLoad), 'muted'));
    const limitCell = todayCell(e);
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
    title: 'назначено',
  })), { empty: 'За последние полчаса назначений не было' });
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

const EVENT_DOT = { received: '', assigned: '', delivered: 'ok', delivering: 'warn', await: 'warn', returned: '', closed: 'ok', waiting: 'warn' };
const STATUS_TEXT = { Processed: 'в работе', Await: 'на доработке', Accept: 'решена', Reject: 'отклонена' };

/**
 * Карточка заявки — её путь от поступления до результата. У каждого назначения — «Почему он?»:
 * полное объяснение, а из него — разбор по правилам для любого сотрудника.
 */
export async function openOrder(id) {
  const body = $('details-body');
  $('details-title').textContent = `Заявка #${id}`;
  body.replaceChildren(el('p', 'Загрузка…', 'muted'));
  if (!$('details').open) $('details').showModal();
  try {
    const order = await api(`/api/dashboard/orders/${encodeURIComponent(id)}`);
    const waiting = order.status === 'Processed' && !order.executorId;
    const head = el('div', null, 'check-head');
    head.append(el('strong', order.summary || 'параметры не заполнены'),
      badge(waiting ? 'ждёт сотрудника' : STATUS_TEXT[order.status] || order.status, waiting ? 'warn' : order.status === 'Accept' ? 'ok' : ''));
    if (order.executorName) head.append(el('span', `сотрудник: ${order.executorName}`, 'muted'));
    const kv = el('div', null, 'kv mt-2');
    for (const p of order.parameters) kv.append(el('span', p.label, 'muted'), el('span', p.value));
    kv.append(el('span', 'Вес заявки', 'muted'), el('span', fmt(order.weight)));
    if (order.parentId) kv.append(el('span', 'Родительская', 'muted'), el('span', `#${order.parentId}`));

    const list = el('ol', null, 'timeline');
    for (const e of order.timeline) {
      const li = el('li');
      li.append(el('span', null, `dot ${EVENT_DOT[e.kind] ?? ''}`),
        el('div', e.kind === 'waiting' ? 'сейчас' : new Date(e.at).toLocaleString('ru-RU', { dateStyle: 'short', timeStyle: 'medium' }), 'timeline-time'),
        el('div', e.title, 'timeline-title'), el('div', e.text, 'text-secondary'));
      const assignment = e.assignmentId && order.history.find((h) => h.id === e.assignmentId);
      if (assignment?.explanation) {
        const why = el('button', 'Почему он? Кто ещё мог взять', 'btn btn-sm mt-1');
        why.type = 'button';
        const box = el('div', null, 'hidden mt-2');
        why.addEventListener('click', () => {
          if (!box.childElementCount) {
            box.append(renderExplanation(assignment.explanation, {
              onCandidate: (c) => showCheck(c, order.attributes || {}, () => openOrder(id)),
            }));
          }
          box.classList.toggle('hidden');
        });
        li.append(why, box);
      }
      list.append(li);
    }
    body.replaceChildren(head, kv, el('h4', 'Путь заявки', 'mt-3 mb-0'), list);
  } catch (e) {
    body.replaceChildren(el('p', e.status === 404 ? 'В этом отделе такой заявки нет.' : 'Ошибка загрузки.', 'error'));
  }
}

/** Другой отдел — своя лента и свой график: подсветка «новых» и кэш графика сбрасываются. */
export function resetOverview() {
  lastFeedId = 0;
  liveLoadedAt = 0;
}

export function initOverview() {
  $('lookup').addEventListener('submit', (event) => {
    event.preventDefault();
    const id = $('lookup-id').value;
    if (/^\d{1,18}$/.test(id)) openOrder(id);
  });
  $('details-close').addEventListener('click', () => $('details').close());
}
