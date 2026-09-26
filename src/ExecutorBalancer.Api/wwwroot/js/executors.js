// Исполнители: кто есть, что умеет, насколько загружен. При включённом пульте — правка через АИС,
// как в жизни: настройки исполнителей хранятся во внешней системе, балансировщик получает их от неё.
import { api, problemText } from './api.js';
import { $, el, badge, button, fmt, toast, field, input, checkbox } from './dom.js';
import { attributeForm } from './forms.js';
import { openEditor } from './editor.js';

let demoEnabled = false;

export function setExecutorsEditable(enabled) { demoEnabled = enabled; }

function card(e, source) {
  const box = el('div', null, `executor${e.isActive ? '' : ' off'}`);
  const head = el('div', null, 'executor-head');
  head.append(el('strong', e.fullName), e.isActive ? badge('на работе', 'ok') : badge('не работает', 'bad'));
  box.append(head);

  const limit = e.dailyLimit == null ? 'без лимита' : `лимит ${e.dailyLimit} в день`;
  const reached = e.dailyLimit != null && e.assignedToday >= e.dailyLimit;
  const stats = el('div', null, 'executor-stats');
  stats.append(
    el('span', `квалификация ${fmt(e.qualificationWeight)}`),
    el('span', `в работе ${e.openCount}`),
    el('span', `сегодня ${e.assignedToday}`, reached ? 'warn-text' : ''),
    el('span', limit, reached ? 'warn-text' : ''),
  );
  box.append(stats);

  const skills = el('dl', null, 'skills');
  for (const s of e.skills || []) skills.append(el('dt', s.label), el('dd', s.value));
  if (!(e.skills || []).length) skills.append(el('dd', 'параметры не заданы', 'muted'));
  box.append(skills);

  if (demoEnabled && source) {
    const actions = el('div', null, 'row-actions');
    actions.append(
      button(e.isActive ? 'Отправить на перерыв' : 'Вернуть на работу', () => setActive(e, !e.isActive)),
      button('Изменить', () => edit(source)),
    );
    box.append(actions);
  }
  return box;
}

async function setActive(e, active) {
  try {
    await api(`/api/admin/demo/executors/${e.id}/active`, { method: 'POST', body: { isActive: active } });
    toast(active ? `${e.fullName} вернулся — снова получает заявки`
      : `${e.fullName} ушёл — его открытые заявки перераспределяются между коллегами`);
    setTimeout(refreshExecutors, 600);
  } catch (err) { toast(problemText(err), 'bad'); }
}

async function edit(source) {
  const config = await api('/api/admin/config');
  const form = attributeForm(config, 'Executor', source.attributes || {});
  const name = input('text', source.fullName, { maxlength: '300', required: '' });
  const qualification = input('number', source.qualificationWeight ?? 1, { min: '0.1', max: '100', step: '0.1' });
  const limit = input('number', source.dailyLimit ?? '', { min: '0', max: '100000', step: '1', placeholder: 'без лимита' });
  const active = checkbox(source.isActive, 'На работе');
  openEditor(`Исполнитель #${source.id}`, [
    el('p', 'Изменения уходят в АИС, а она передаёт их балансировщику — так в жизни настройки исполнителей и меняются.', 'hint'),
    field('ФИО', name),
    field('Квалификация (вес)', qualification, 'Больше — опытнее: получает пропорционально больше нагрузки. 1 — обычный сотрудник.'),
    field('Лимит заявок в день', limit, 'Пусто — без лимита.'),
    active.wrap,
    ...form.nodes,
  ], async () => {
    await api(`/api/admin/demo/executors/${source.id}`, {
      method: 'PUT',
      body: {
        fullName: name.value.trim(),
        isActive: active.box.checked,
        dailyLimit: limit.value === '' ? null : Number(limit.value),
        qualificationWeight: Number(String(qualification.value).replace(',', '.')),
        attributes: form.read(),
      },
    });
    toast('Сохранено в АИС');
  }, () => new Promise((r) => setTimeout(r, 500)).then(refreshExecutors));
}

export async function refreshExecutors() {
  const [summary, sources] = await Promise.all([
    api('/api/dashboard/summary'),
    demoEnabled ? api('/api/admin/demo/executors').catch(() => []) : Promise.resolve([]),
  ]);
  const byId = new Map(sources.map((s) => [s.id, s]));
  const list = $('executors-list');
  if (!summary.executors.length) {
    const empty = el('div', null, 'card');
    empty.append(el('p', demoEnabled
      ? 'Исполнителей пока нет. Заведите их на вкладке «Демонстрация» (шаг 2) — или их передаст АИС.'
      : 'Исполнителей пока нет — их передаёт АИС.', 'muted'));
    list.replaceChildren(empty);
    return;
  }
  list.replaceChildren(...summary.executors.map((e) => card(e, byId.get(e.id))));
}
