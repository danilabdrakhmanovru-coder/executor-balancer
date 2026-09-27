// Объяснение решения: кто рассматривался, почему отсеян, какой score, почему выбран.
import { el, row, badge, fmt, icon } from './dom.js';

export const KIND = {
  Primary: 'первичная', Parent: 'от родителя', Secondary: 'вторичная', Reassign: 'перераспределение', Extra: 'сверх нормы',
};

const VERDICT = {
  chosen: ['выбран', 'ok'], eligible: ['подходил', ''], matched: ['подходил', ''],
  rule_failed: ['не подходит', 'bad'], inactive: ['неактивен', 'bad'], daily_limit_exceeded: ['лимит исчерпан', 'warn'],
  over_norm: ['норма набрана', 'warn'],
};

/**
 * onCandidate(c) — если задан, имена сотрудников становятся кнопками: щелчок показывает разбор по правилам.
 */
export function renderExplanation(x, { onCandidate = null } = {}) {
  const wrap = el('div');
  wrap.append(el('h3', x.decision || 'Решение не принято', x.chosenExecutorId ? 'decision' : 'decision none'));
  const meta = [`вес заявки ${fmt(x.orderWeight)}`];
  if (x.kind) meta.push(KIND[x.kind] || x.kind);
  wrap.append(el('p', meta.join(' · '), 'muted'));
  for (const note of x.notes || []) wrap.append(el('p', note, 'note'));

  const table = el('table', null, 'table table-sm table-vcenter mt-3');
  const head = el('thead');
  head.append(row(['Сотрудник', 'Решение', 'Оценка', 'Сегодня', 'Почему']));
  const body = el('tbody');
  const order = { chosen: 0, eligible: 1, matched: 1, over_norm: 2, daily_limit_exceeded: 3, rule_failed: 4, inactive: 5 };
  const sorted = [...(x.candidates || [])].sort((a, b) =>
    (order[a.verdict] ?? 9) - (order[b.verdict] ?? 9) || (a.score ?? 1e12) - (b.score ?? 1e12));
  for (const c of sorted) {
    const [label, cls] = VERDICT[c.verdict] || [c.verdict, ''];
    let name = c.name;
    if (onCandidate) {
      name = el('button', null, 'btn btn-link p-0 candidate-link');
      name.type = 'button';
      name.title = 'Почему ему подходит или не подходит эта заявка';
      name.append(el('span', c.name), icon('chevron-right'));
      name.addEventListener('click', () => onCandidate(c));
    }
    body.append(row([name, badge(label, cls), c.score ?? '—', c.assignedToday ?? '—', el('span', c.reason || '', 'wrap-text')]));
  }
  table.append(head, body);
  const scroll = el('div', null, 'table-responsive');
  scroll.append(table);
  wrap.append(scroll);
  if (onCandidate) wrap.append(el('p', 'Щёлкните по сотруднику — покажет каждое правило: что у заявки, что у него и выполнено ли.', 'hint'));
  wrap.append(el('p', 'Оценка = (вес, полученный сотрудником за этот час, + вес этой заявки) / его квалификация. Выбирается '
    + 'наименьшая — так заявки делятся поровну с учётом опыта, и тот, кто закрывает быстрее, не получает больше других. '
    + 'При равенстве — меньше заявок сейчас в работе, затем меньше назначений за сутки, затем меньший номер сотрудника. Кто уже набрал норму, получает заявку '
    + 'сверх неё только в режиме «больше нормы» и только если у всех остальных норма набрана.', 'hint'));
  return wrap;
}

/** Разбор для одного сотрудника: каждое правило отдельно, затем норма и нагрузка. */
export function renderCheck(check) {
  const wrap = el('div');
  const head = el('div', null, 'check-head');
  head.append(el('h3', check.name, 'mb-0'), badge(check.canTake ? 'может взять' : 'не может взять', check.canTake ? 'ok' : 'bad'));
  wrap.append(head, el('p', check.summary, check.canTake ? 'decision' : 'decision none'));

  const list = el('div', null, 'rule-checks');
  if (!check.rules.length) list.append(el('p', 'В отделе нет правил подбора — подходит любой активный сотрудник.', 'muted'));
  for (const r of check.rules) {
    const item = el('div', null, `rule-check ${r.passed ? 'ok' : 'bad'}`);
    const mark = el('span', null, 'rule-mark');
    mark.append(icon(r.passed ? 'check' : 'x'));
    const text = el('div');
    text.append(el('strong', r.rule));
    const line = el('div', null, 'rule-line');
    line.append(
      el('span', `${r.orderField} заявки: `), el('b', r.orderValue ?? 'не указано'),
      el('span', ` — ${r.operator} — `),
      el('span', r.source ? `${r.source} сотрудника: ` : ''), el('b', r.expected ?? 'не указано'),
    );
    text.append(line);
    if (r.note) text.append(el('div', r.note, 'muted small'));
    if (r.isStrict) text.append(badge('строгое', 'warn'));
    item.append(mark, text);
    list.append(item);
  }
  wrap.append(list);

  const kv = el('div', null, 'kv mt-3');
  for (const [k, v] of [['На работе', check.isActive ? 'да' : 'нет'], ['Норма на сегодня', check.limit],
    ['Вес заявки', fmt(check.orderWeight)], ['Квалификация', fmt(check.qualification)],
    ['Получил за этот час (вес)', fmt(check.hourWeight)], ['Вес в работе сейчас', fmt(check.openWeight)],
    ['Оценка, если получит', fmt(check.score)]]) {
    kv.append(el('span', k, 'muted'), el('span', v));
  }
  wrap.append(kv);
  wrap.append(el('p', 'Из всех, кто может взять, заявка уходит тому, у кого оценка меньше: вес за этот час вместе с заявкой на единицу квалификации.', 'hint'));
  return wrap;
}
