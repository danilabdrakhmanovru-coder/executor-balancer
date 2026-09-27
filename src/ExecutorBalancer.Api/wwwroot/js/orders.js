// Заявки отдела: список с фильтром по состоянию, новые сверху; щелчок открывает путь заявки.
import { api } from './api.js';
import { $, row, badge, fmt, fmtDateTime, emptyRow } from './dom.js';
import { openOrder } from './overview.js';

const PAGE = 50;
const LABELS = { all: 'Все', pending: 'Ждут сотрудника', processed: 'В работе', await: 'На доработке', closed: 'Решены и отклонены' };
let state = 'all';
let oldest = null;
let appended = false; // пока открыты «ещё» страницы, список не перерисовывается сам

/** Состояние словами: ждёт / в работе / на доработке / решена / отклонена. */
export function stateBadge(o) {
  if (o.waiting) return badge('ждёт сотрудника', 'warn');
  return {
    Processed: badge('в работе', ''), Await: badge('на доработке', 'warn'), Accept: badge('решена', 'ok'),
    Reject: badge('отклонена', 'bad'),
  }[o.status] ?? badge(o.status, '');
}

async function load(append) {
  const query = new URLSearchParams({ state, limit: String(PAGE) });
  if (append && oldest) query.set('before', String(oldest));
  const data = await api(`/api/dashboard/orders?${query}`);
  for (const b of $('orders-filter').querySelectorAll('button')) {
    const key = b.dataset.state;
    b.classList.toggle('active', key === state);
    b.textContent = `${LABELS[key]} · ${fmt(data.counts[key])}`;
  }
  const rows = data.items.map((o) => {
    const tr = row([`#${o.id}`, fmtDateTime(o.receivedAt), o.summary || '—', fmt(o.weight),
      o.executorName ?? (o.waiting ? o.pendingReason || '—' : '—'), stateBadge(o), o.points == null ? '—' : fmt(o.points)],
    'clickable');
    tr.addEventListener('click', () => openOrder(o.id));
    return tr;
  });
  if (append) $('orders-list').append(...rows);
  else $('orders-list').replaceChildren(...(rows.length ? rows : [emptyRow(7, 'Заявок нет')]));
  if (data.items.length) oldest = data.items[data.items.length - 1].id;
  $('orders-more-wrap').classList.toggle('hidden', data.items.length < PAGE);
}

export const refreshOrders = () => (appended ? Promise.resolve() : load(false));

export function initOrders() {
  $('orders-filter').addEventListener('click', (event) => {
    const value = event.target.closest('button')?.dataset.state;
    if (!value) return;
    state = value;
    oldest = null;
    appended = false;
    load(false);
  });
  $('orders-more').addEventListener('click', () => { appended = true; load(true); });
  $('orders-lookup').addEventListener('submit', (event) => {
    event.preventDefault();
    const id = $('orders-lookup-id').value;
    if (/^\d{1,18}$/.test(id)) openOrder(id);
  });
}
