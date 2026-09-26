// Журнал: изменения конфигурации и входы администратора.
import { api } from './api.js';
import { $, el, row, fmtDateTime, emptyRow } from './dom.js';

const ACTION = {
  login: 'вход', login_failed: 'неудачный вход',
  field_created: 'параметр добавлен', field_updated: 'параметр изменён', field_deleted: 'параметр удалён',
  rule_created: 'правило добавлено', rule_updated: 'правило изменено', rule_deleted: 'правило удалено',
  weight_rule_created: 'правило веса добавлено', weight_rule_updated: 'правило веса изменено',
  weight_rule_deleted: 'правило веса удалено',
};
const ENTITY = { session: 'сессия', field: 'параметр', rule: 'правило', weight_rule: 'правило веса' };
const PAGE = 50;

let oldest = null;

function short(value) {
  const text = Array.isArray(value) ? `[${value.join(', ')}]` : typeof value === 'object' && value !== null ? JSON.stringify(value) : String(value);
  return text.length > 80 ? `${text.slice(0, 79)}…` : text;
}

/** Что изменилось: для правки — только отличающиеся поля, для создания и удаления — главное. */
function details(entry) {
  const before = entry.data?.before;
  const after = entry.data?.after;
  if (entry.entity === 'session') return `адрес ${entry.entityId}`;
  if (before && after) {
    const changes = Object.keys(after)
      .filter((k) => JSON.stringify(before[k]) !== JSON.stringify(after[k]))
      .map((k) => `${k}: ${short(before[k])} → ${short(after[k])}`);
    return changes.length ? changes.join('; ') : 'без изменений';
  }
  const state = after || before || {};
  return ['name', 'key', 'label', 'orderField', 'operator', 'value', 'weight']
    .filter((k) => state[k] !== undefined && state[k] !== null)
    .map((k) => `${k}: ${short(state[k])}`).join('; ');
}

async function load(append) {
  const query = new URLSearchParams({ limit: String(PAGE) });
  if (append && oldest) query.set('before', String(oldest));
  const entries = await api(`/api/admin/audit?${query}`);
  const rows = entries.map((e) => row([
    fmtDateTime(e.createdAt), ACTION[e.action] || e.action,
    `${ENTITY[e.entity] || e.entity}${e.entity === 'session' ? '' : ` #${e.entityId}`}`, el('span', details(e), 'wrap-text'),
  ], e.action === 'login_failed' ? 'warn-row' : ''));
  if (append) $('audit').append(...rows);
  else $('audit').replaceChildren(...(rows.length ? rows : [emptyRow(4, 'Записей нет')]));
  if (entries.length) oldest = entries[entries.length - 1].id;
  $('audit-more').classList.toggle('hidden', entries.length < PAGE);
}

export const refreshAudit = () => load(false);

export function initAudit() {
  $('audit-refresh').addEventListener('click', () => load(false));
  $('audit-more').addEventListener('click', () => load(true));
}
