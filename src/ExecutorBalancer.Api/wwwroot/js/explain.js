// Объяснение решения: кто рассматривался, почему отсеян, какой score, почему выбран.
import { el, row, badge, fmt } from './dom.js';

export const KIND = { Primary: 'первичная', Parent: 'от родителя', Secondary: 'вторичная', Reassign: 'перераспределение' };

const VERDICT = {
  chosen: ['выбран', 'ok'], eligible: ['подходил', ''], matched: ['подходил', ''],
  rule_failed: ['не подходит', 'bad'], inactive: ['неактивен', 'bad'], daily_limit_exceeded: ['лимит исчерпан', 'warn'],
};

export function renderExplanation(x) {
  const wrap = el('div');
  wrap.append(el('h3', x.decision || 'Решение не принято', x.chosenExecutorId ? 'decision' : 'decision none'));
  const meta = [`вес заявки ${fmt(x.orderWeight)}`];
  if (x.kind) meta.push(KIND[x.kind] || x.kind);
  wrap.append(el('p', meta.join(' · '), 'muted'));
  for (const note of x.notes || []) wrap.append(el('p', note, 'note'));

  const table = el('table', null, 'table table-sm table-vcenter mt-3');
  const head = el('thead');
  head.append(row(['Исполнитель', 'Вердикт', 'Score', 'Сегодня', 'Причина']));
  const body = el('tbody');
  const order = { chosen: 0, eligible: 1, matched: 1, daily_limit_exceeded: 2, rule_failed: 3, inactive: 4 };
  const sorted = [...(x.candidates || [])].sort((a, b) =>
    (order[a.verdict] ?? 9) - (order[b.verdict] ?? 9) || (a.score ?? 1e12) - (b.score ?? 1e12));
  for (const c of sorted) {
    const [label, cls] = VERDICT[c.verdict] || [c.verdict, ''];
    body.append(row([c.name, badge(label, cls), c.score ?? '—', c.assignedToday ?? '—', el('span', c.reason || '', 'wrap-text')]));
  }
  table.append(head, body);
  const scroll = el('div', null, 'table-responsive');
  scroll.append(table);
  wrap.append(scroll);
  wrap.append(el('p', 'Score = (открытый вес исполнителя + вес заявки) / квалификация. Выбирается минимальный; при равенстве — '
    + 'меньше назначений за сутки на единицу квалификации, затем меньший id.', 'hint'));
  return wrap;
}
