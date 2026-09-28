// Журнал: изменения конфигурации и входы администратора — обычными словами, без внутренних ключей.
import { api } from './api.js';
import { $, el, row, badge, fmtDateTime, emptyRow } from './dom.js';
import { ROLE_TITLE } from './session.js';

const ACTION = {
  login: ['Вход', 'ok'], login_failed: ['Неверный пароль', 'bad'], preset_applied: ['Смена сферы', 'warn'],
  field_created: ['Новый параметр', ''], field_updated: ['Параметр изменён', ''], field_deleted: ['Параметр удалён', 'bad'],
  rule_created: ['Новое правило', ''], rule_updated: ['Правило изменено', ''], rule_deleted: ['Правило удалено', 'bad'],
  weight_rule_created: ['Новое правило веса', ''], weight_rule_updated: ['Правило веса изменено', ''],
  weight_rule_deleted: ['Правило веса удалено', 'bad'],
  department_created: ['Новый отдел', 'ok'], department_renamed: ['Отдел переименован', ''],
  department_deleted: ['Отдел удалён', 'bad'],
  motivation_updated: ['Мотивация изменена', 'warn'], extra_mode_changed: ['Больше нормы', ''],
  executors_imported: ['Загрузка из файла', 'ok'], executor_break: ['Перерыв', ''], executor_back: ['Вернулся на работу', 'ok'],
  executor_dismissed: ['Сотрудник уволен', 'bad'],
  demo_reset: ['Демо в исходном', 'ok'],
  user_created: ['Новый пользователь', 'ok'], user_updated: ['Пользователь изменён', 'warn'], user_deleted: ['Пользователь удалён', 'bad'],
};
const MOTIVATION = [
  ['fastCloseSeconds', 'быстрое закрытие, с', (v) => v],
  ['fastClosePenalty', 'штраф за быстрое', (v) => `${Math.round(v * 100)}%`],
  ['reworkPenalty', 'штраф за доработку', (v) => `${Math.round(v * 100)}%`],
  ['maxExtraPercent', 'потолок «больше нормы»', (v) => `${v}%`],
  ['qualityThreshold', 'порог приостановки', (v) => `${Math.round(v * 100)}%`],
  ['heavyQualityThreshold', 'порог для сложных', (v) => `${Math.round(v * 100)}%`],
  ['heavyWeight', 'сложная — вес от', (v) => v],
  ['minOnDutyPercent', 'минимум на работе', (v) => `${v}%`],
];
const TYPE = { String: 'строка', Number: 'число', Boolean: 'да/нет', Enum: 'справочник', Array: 'список' };
const OWNER = { Order: 'заявки', Executor: 'сотрудника' };
const PAGE = 50;

let oldest = null;
let config = null;

const quote = (text) => `«${text}»`;

/** Значение для человека: да/нет вместо true/false, списки через запятую. */
function plain(value) {
  if (value === null || value === undefined || value === '') return '—';
  if (value === true) return 'да';
  if (value === false) return 'нет';
  if (Array.isArray(value)) return value.map(plain).join(', ');
  if (typeof value === 'object') return Object.values(value).map(plain).join(', ');
  return String(value);
}

function fieldLabel(owner, key) {
  const field = config?.fields.find((f) => f.owner === owner && f.key === key);
  return field ? field.label : key;
}

function operatorLabel(op) {
  return config?.operators.find((o) => o.value === op)?.label ?? op;
}

/** Условие правила словами: «Тематика — одно из — Тематики сотрудника». */
function condition(rule) {
  const left = fieldLabel('Order', rule.orderField);
  const op = operatorLabel(rule.operator);
  if (rule.target === 'ExecutorField') {
    const right = rule.operator === 'between'
      ? `от «${fieldLabel('Executor', rule.executorField)}» до «${fieldLabel('Executor', rule.executorFieldUpper)}» сотрудника`
      : `${quote(fieldLabel('Executor', rule.executorField))} сотрудника`;
    return `${left} ${op} ${right}`;
  }
  const value = rule.operator === 'between' && Array.isArray(rule.value)
    ? `от ${plain(rule.value[0])} до ${plain(rule.value[1])}` : plain(rule.value);
  return `${left} ${op} ${value}`;
}

const weightText = (r) => `если ${condition({ ...r, target: 'Constant' })} — вес ${plain(r.weight)}`;

/** Что поменялось в правке: только изменённые свойства, с понятными названиями. */
function changes(before, after, describe) {
  const parts = [];
  for (const [key, label, show] of describe) {
    if (JSON.stringify(before[key]) !== JSON.stringify(after[key])) parts.push(`${label}: ${show(before)} → ${show(after)}`);
  }
  return parts.length ? parts.join('; ') : 'без изменений';
}

const RULE_PROPS = [
  ['name', 'название', (r) => quote(r.name)],
  ['isEnabled', 'включено', (r) => plain(r.isEnabled)],
  ['priority', 'приоритет', (r) => plain(r.priority)],
  ['isStrict', 'строгое', (r) => plain(r.isStrict)],
];
const RULE_CONDITION = ['orderField', 'operator', 'target', 'executorField', 'executorFieldUpper', 'value'];

function describe(entry) {
  const before = entry.data?.before ?? {};
  const after = entry.data?.after ?? {};
  const address = String(entry.entityId || '').replace(/^::ffff:/, '');
  switch (entry.action) {
    case 'user_created': return `Создан пользователь ${after.login} — ${ROLE_TITLE[after.role] || after.role}`
      + `${after.isActive === false ? ', заблокирован' : ''}`;
    case 'user_updated': return `Пользователь ${after.login}: ${ROLE_TITLE[after.role] || after.role}`
      + `${after.isActive === false ? ', заблокирован' : ''}${after.passwordChanged ? ', пароль сменён' : ''}`
      + `${before.role && before.role !== after.role ? ` (была роль «${ROLE_TITLE[before.role] || before.role}»)` : ''}`;
    case 'user_deleted': return `Удалён пользователь ${before.login}`;
    case 'login': return `Вход · адрес ${address}`;
    case 'login_failed': return `Попытка входа с неверным логином или паролем · адрес ${address}`;
    case 'preset_applied': {
      const was = before.preset ? ` Было: ${quote(before.preset)}.`
        : Array.isArray(before.fields) ? ` Было: своя настройка — параметров ${before.fields.length}, правил ${(before.rules || []).length}.` : '';
      return `Применён шаблон ${quote(after.preset)}: параметров ${(after.fields || []).length}, правил ${(after.rules || []).length}.${was}`;
    }
    case 'department_created':
      return `Создан отдел ${quote(after.name)}${after.preset ? ` — сфера ${quote(after.preset)}` : ' без шаблона'}, код для АИС: ${after.code}`;
    case 'department_renamed': return `Отдел ${quote(before.name)} переименован в ${quote(after.name)}`;
    case 'department_deleted': return `Удалён отдел ${quote(before.name)}`;
    case 'executors_imported': {
      const names = (after.names || []).join(', ');
      return `Загружены сотрудники из файла: новых ${after.created ?? 0}, обновлено ${after.updated ?? 0}, `
        + `переведено из других отделов ${after.moved ?? 0}${names ? `. ${names}` : ''}`;
    }
    case 'motivation_updated':
      return `Настройки рейтинга и сверхнормы: ${changes(before, after, MOTIVATION.map(([key, label, show]) =>
        [key, label, (x) => show(x[key])]))}`;
    case 'executor_break': return `${after.fullName} ушёл на перерыв — открытые заявки переданы коллегам`;
    case 'executor_back': return `${after.fullName} вернулся на работу`;
    case 'demo_reset': return `Демо возвращено в исходное${after.automatic ? ' автоматически после гостей' : ''}: `
      + `на работу вернулись ${after.back ?? 0}, «больше нормы» выключено у ${after.extra ?? 0}, заведено заново ${after.missing ?? 0}`;
    case 'executor_dismissed': return `${after.fullName} уволен — открытые заявки переданы коллегам`;
    case 'extra_mode_changed': {
      const show = (p) => (p > 0 ? `+${p}% к норме` : 'выключен');
      return `${after.fullName}: режим «больше нормы» ${show(before.extraPercent)} → ${show(after.extraPercent)}`;
    }
    case 'field_created':
      return `Добавлен параметр ${OWNER[after.owner] || ''} ${quote(after.label)} — ${TYPE[after.type] || after.type}`
        + `${after.options?.length ? `: ${after.options.join(', ')}` : ''}`;
    case 'field_updated':
      return `Параметр ${quote(after.label)}: ${changes(before, after, [
        ['label', 'название', (f) => quote(f.label)],
        ['options', 'справочник', (f) => plain(f.options)],
      ])}`;
    case 'field_deleted': return `Удалён параметр ${OWNER[before.owner] || ''} ${quote(before.label)}`;
    case 'rule_created': return `Добавлено правило ${quote(after.name)}: ${condition(after)}`;
    case 'rule_deleted': return `Удалено правило ${quote(before.name)}: ${condition(before)}`;
    case 'rule_updated': {
      const conditionChanged = RULE_CONDITION.some((k) => JSON.stringify(before[k]) !== JSON.stringify(after[k]));
      const text = changes(before, after, RULE_PROPS);
      const parts = [text === 'без изменений' && conditionChanged ? '' : text,
        conditionChanged ? `условие: ${condition(before)} → ${condition(after)}` : ''].filter(Boolean);
      return `Правило ${quote(after.name)}: ${parts.join('; ')}`;
    }
    case 'weight_rule_created': return `Добавлено правило веса: ${weightText(after)}`;
    case 'weight_rule_deleted': return `Удалено правило веса: ${weightText(before)}`;
    case 'weight_rule_updated':
      return `Правило веса: ${weightText(before)} → ${weightText(after)}`
        + `${before.isEnabled !== after.isEnabled ? ` (включено: ${plain(before.isEnabled)} → ${plain(after.isEnabled)})` : ''}`;
    default: return plain(entry.data);
  }
}

async function load(append) {
  const query = new URLSearchParams({ limit: String(PAGE) });
  if (append && oldest) query.set('before', String(oldest));
  const [entries, cfg] = await Promise.all([api(`/api/admin/audit?${query}`), config && append ? config : api('/api/admin/config')]);
  config = cfg;
  const rows = entries.map((e) => {
    const [label, cls] = ACTION[e.action] || [e.action, ''];
    return row([fmtDateTime(e.createdAt), badge(label, cls), el('span', describe(e), 'wrap-text')],
      e.action === 'login_failed' ? 'warn-row' : '');
  });
  if (append) $('audit').append(...rows);
  else $('audit').replaceChildren(...(rows.length ? rows : [emptyRow(3, 'Записей нет')]));
  if (entries.length) oldest = entries[entries.length - 1].id;
  $('audit-more-wrap').classList.toggle('hidden', entries.length < PAGE);
}

export const refreshAudit = () => load(false);

/** Другой отдел — другие названия параметров в описаниях. */
export function resetAudit() { config = null; oldest = null; }

export function initAudit() {
  $('audit-refresh').addEventListener('click', () => load(false));
  $('audit-more').addEventListener('click', () => load(true));
}
