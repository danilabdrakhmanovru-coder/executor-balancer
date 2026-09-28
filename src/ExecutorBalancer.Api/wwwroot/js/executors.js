// Исполнители: кто есть, что умеет, насколько загружен. При включённом пульте — правка через АИС,
// как в жизни: настройки исполнителей хранятся во внешней системе, балансировщик получает их от неё.
import { api, problemText, scoped } from './api.js';
import { $, el, badge, button, fmt, toast, field, input, checkbox, select, icon } from './dom.js';
import { attributeForm } from './forms.js';
import { openEditor } from './editor.js';
import { todayCell } from './overview.js';
import { openImport } from './import.js';
import { can } from './session.js';

let demoEnabled = false;
let thresholds = { qualityThreshold: 0.8, heavyQualityThreshold: 0.9 };

export function setExecutorsEditable(enabled) {
  demoEnabled = enabled;
  $('executor-add').classList.toggle('hidden', !enabled);
}

function card(e, source) {
  const card = el('div', null, `card executor${e.isActive ? '' : ' off'}`);
  const box = el('div', null, 'card-body');
  card.append(box);
  const head = el('div', null, 'executor-head');
  const link = el('a', e.fullName, 'executor-name');
  link.href = `#executor-${e.id}`;
  link.title = 'Открыть страницу сотрудника';
  head.append(link, e.isActive ? badge('на работе', 'ok') : badge('не работает', 'bad'));
  box.append(head);

  const stats = el('div', null, 'executor-stats');
  const today = el('span', 'сегодня / норма ');
  today.append(todayCell(e));
  stats.append(
    el('span', `квалификация ${fmt(e.qualificationWeight)}`),
    el('span', `в работе ${e.openCount}`),
    today,
  );
  box.append(stats);
  box.append(qualityLine(e));

  const skills = el('dl', null, 'skills');
  for (const s of e.skills || []) skills.append(el('dt', s.label), el('dd', s.value));
  // без навыков — отдельной строкой с переносом: в сетке «название — значение» длинный текст распирал карточку
  box.append((e.skills || []).length ? skills : el('p', 'навыки не заданы — подходит к любым заявкам', 'warn-text small mb-0'));

  const actions = el('div', null, 'row-actions');
  if (e.dailyLimit != null && can('Manager')) {
    actions.append(button(e.extraPercent > 0 ? `Больше нормы: +${e.extraPercent}%` : 'Больше нормы…', () => extraMode(e),
      e.extraPercent > 0 ? 'btn btn-sm btn-success' : 'btn btn-sm', 'flame'));
  }
  if (can('Manager')) {
    actions.append(button(e.isActive ? 'На перерыв' : 'Вернуть на работу', () => setActive(e, !e.isActive),
      'btn btn-sm', e.isActive ? 'coffee' : 'user-check'));
  }
  if (demoEnabled && source) actions.append(button('Изменить', () => edit(source), 'btn btn-sm', 'pencil'));
  if (can('Manager')) actions.append(button('Уволить', () => dismiss(e), 'btn btn-sm btn-outline-danger', 'trash'));
  if (actions.childElementCount) box.append(actions);
  return card;
}

/** Качество за 7 дней: баллы / максимально возможные баллы; от него зависит режим «больше нормы». */
function qualityLine(e) {
  const q = e.quality;
  const line = el('div', null, 'executor-quality');
  if (!q || q.quality === null || q.quality === undefined) {
    line.append(el('span', `качество: ещё не оценено (закрыто за 7 дней: ${q?.closed ?? 0}, нужно от 5)`, 'muted'));
    return line;
  }
  const value = Math.round(q.quality * 1000) / 10;
  line.append(el('span', 'качество за 7 дней '), badge(`${fmt(value)}%`, q.quality < thresholds.qualityThreshold ? 'bad'
      : q.quality < thresholds.heavyQualityThreshold ? 'warn' : 'ok'),
    el('span', ` · баллов ${fmt(q.points)} · закрыто ${fmt(q.closed)} · быстрых ${fmt(q.fastClosed)} · доработок ${fmt(q.returned)}`, 'muted'));
  return line;
}

/** Режим «готов взять больше нормы»: процент сверх нормы, не выше потолка отдела. */
export async function extraMode(e, after = refreshExecutors) {
  const motivation = await api('/api/admin/motivation');
  const max = motivation.maxExtraPercent;
  const steps = [0, 10, 20, 30, 50, 100].filter((p) => p <= max);
  if (!steps.includes(e.extraPercent)) steps.push(e.extraPercent);
  const percent = select(steps.sort((a, b) => a - b).map((p) => [String(p), p === 0 ? 'выключен' : `+${p}% к норме`]), String(e.extraPercent));
  openEditor(`Больше нормы: ${e.fullName}`, [
    el('p', `Норма — ${e.dailyLimit} заявок в день. В режиме сотрудник получает сверх нормы только излишки — заявки, `
      + 'которые иначе ждали бы, потому что у всех подходящих коллег норма уже набрана. Ни у кого ничего не забирается.', 'hint'),
    field('Готов взять', percent, max > 0 ? `Потолок отдела — +${max}%. Меняется в «Настройки → Рейтинг и сверхнорма».`
      : 'В отделе режим выключен (потолок 0%).'),
    el('p', `Защита: при качестве ниже ${Math.round(motivation.qualityThreshold * 100)}% режим приостанавливается сам; `
      + `сложные заявки (вес от ${fmt(motivation.heavyWeight)}) сверх нормы — только при качестве от `
      + `${Math.round(motivation.heavyQualityThreshold * 100)}%.`, 'hint'),
  ], async () => {
    await api(`/api/admin/executors/${e.id}/extra`, { method: 'PUT', body: { percent: Number(percent.value) } });
    toast(Number(percent.value) > 0 ? `${e.fullName}: режим «больше нормы» +${percent.value}%` : `${e.fullName}: режим выключен`);
  }, after);
}

/**
 * Перерыв и возвращение. Сервер не отпустит, если на работе останется меньше доли отдела из настроек
 * или некому будет брать какой-то вид заявок, — тогда покажем почему.
 */
export async function setActive(e, active, after = refreshExecutors) {
  try {
    await api(`/api/admin/executors/${e.id}/active`, { method: 'POST', body: { isActive: active } });
    toast(active ? `${e.fullName} вернулся — снова получает заявки`
      : `${e.fullName} ушёл — его открытые заявки перераспределяются между коллегами`);
    setTimeout(after, 600);
  } catch (err) { toast(problemText(err), 'bad'); }
}

/** Увольнение: открытые заявки уходят коллегам, сотрудник удаляется; история его назначений остаётся в отчётах. */
export async function dismiss(e, after = refreshExecutors) {
  if (!confirm(`Уволить ${e.fullName}? Его открытые заявки перейдут коллегам, сам он пропадёт из отдела. `
    + 'История назначений останется в отчётах.')) return;
  try {
    await api(`/api/admin/executors/${e.id}`, { method: 'DELETE' });
    toast(`${e.fullName} уволен — открытые заявки переданы коллегам`);
    setTimeout(after, 600);
  } catch (err) { toast(problemText(err), 'bad'); }
}

/** Поля сотрудника: ФИО, квалификация, лимит, активность и параметры отдела (справочник). */
function executorForm(config, source) {
  const form = attributeForm(config, 'Executor', source.attributes || {}, { required: true });
  const name = input('text', source.fullName ?? '', { maxlength: '300', required: '', placeholder: 'Фамилия И. О.' });
  const qualification = input('number', source.qualificationWeight ?? 1, { min: '0.1', max: '100', step: '0.1' });
  const limit = input('number', source.dailyLimit ?? '', { min: '0', max: '100000', step: '1', placeholder: 'без лимита' });
  const active = checkbox(source.isActive ?? true, 'На работе');
  return {
    nodes: [
      field('ФИО', name),
      field('Квалификация (вес)', qualification, 'Больше — опытнее: получает пропорционально больше нагрузки. 1 — обычный сотрудник.'),
      field('Суточная норма (лимит заявок в день)', limit, 'Пусто — без лимита.'),
      active.wrap,
      ...form.nodes,
    ],
    sample: () => form.sample(),
    read: () => ({
      fullName: name.value.trim(),
      isActive: active.box.checked,
      dailyLimit: limit.value === '' ? null : Number(limit.value),
      qualificationWeight: Number(String(qualification.value).replace(',', '.')),
      attributes: form.read(),
    }),
  };
}

export async function edit(source, after = refreshExecutors) {
  const config = await api('/api/admin/config');
  const form = executorForm(config, source);
  openEditor(`Сотрудник #${source.id}`, [
    el('p', 'Изменения уходят в АИС, а она передаёт их балансировщику — так в жизни настройки сотрудников и меняются.', 'hint'),
    ...form.nodes,
  ], async () => {
    await api(`/api/admin/demo/executors/${source.id}`, { method: 'PUT', body: form.read() });
    toast('Сохранено в АИС');
  }, () => new Promise((r) => setTimeout(r, 500)).then(after));
}

/** Новый сотрудник отдела: заводится в АИС, АИС передаёт его балансировщику. */
async function create() {
  const config = await api('/api/admin/config');
  const form = executorForm(config, {});
  const sample = el('button', null, 'btn btn-sm');
  sample.type = 'button';
  sample.append(icon('wand'), 'Заполнить навыки примером');
  sample.addEventListener('click', () => form.sample());
  openEditor('Новый сотрудник', [
    el('p', 'Сотрудник заводится в АИС (как в жизни — в кадровой или учётной системе), а АИС передаёт его сюда. '
      + 'Навыки — параметры отдела: по ним правила решают, какие заявки ему подходят.', 'hint'),
    sample,
    ...form.nodes,
  ], async () => {
    const created = await api('/api/admin/demo/executors', { method: 'POST', body: form.read() });
    toast(`Сотрудник заведён в АИС под номером ${created.id}`);
    setTimeout(() => { location.hash = `#executor-${created.id}`; }, 700);
  }, () => new Promise((r) => setTimeout(r, 500)).then(refreshExecutors));
}

export function initExecutors() {
  $('executor-add').addEventListener('click', () => { create().catch((e) => toast(problemText(e), 'bad')); });
  $('executor-import').addEventListener('click', () => openImport(refreshExecutors));
}

export async function refreshExecutors() {
  $('executor-template').href = scoped('/api/admin/executors/template.csv'); // шаблон — по текущему отделу
  const [summary, sources, motivation] = await Promise.all([
    api('/api/dashboard/summary'),
    demoEnabled ? api('/api/admin/demo/executors').catch(() => []) : Promise.resolve([]),
    api('/api/admin/motivation'),
  ]);
  thresholds = motivation;
  const byId = new Map(sources.map((s) => [s.id, s]));
  const list = $('executors-list');
  if (!summary.executors.length) {
    const empty = el('div', null, 'card card-body');
    empty.append(el('p', demoEnabled
      ? 'В отделе пока нет сотрудников. Заведите их на «Тестовом стенде», кнопкой «Добавить сотрудника» или загрузите из файла.'
      : 'В отделе пока нет сотрудников — их передаёт АИС, или загрузите их из файла (кнопка выше).', 'muted'));
    list.replaceChildren(empty);
    return;
  }
  list.replaceChildren(...summary.executors.map((e) => card(e, byId.get(e.id))));
  renderSkillWarning(summary.executors);
}

/**
 * Сотрудники без навыков: правила их не ограничивают, и они получают заявки любого вида — больше остальных.
 * Обычно так бывает, когда сотрудники заведены до смены сферы отдела. Подсказываем, как заполнить навыки.
 */
function renderSkillWarning(executors) {
  const box = $('executors-warning');
  const missing = executors.filter((e) => e.isActive && !(e.skills || []).length);
  box.classList.toggle('hidden', !missing.length);
  if (!missing.length) { box.replaceChildren(); return; }
  const text = el('div', null);
  text.append(el('strong', `У ${missing.length} из ${executors.length} сотрудников не заданы навыки. `),
    'Правила подбора их не ограничивают, поэтому они подходят к любой заявке и получают больше остальных. '
    + 'Так бывает, если сотрудники заведены до смены сферы отдела или АИС не передала их параметры.');
  const bar = el('div', null, 'btn-list mt-2');
  if (can('Manager')) {
    bar.append(button('Загрузить навыки из файла', () => openImport(refreshExecutors), 'btn btn-sm', 'upload'));
  }
  if (demoEnabled) {
    const count = Math.min(100, Math.max(executors.length, 10));
    const reseed = button(`Заменить тестовыми с навыками (${count})`, async () => {
      if (!confirm(`Завести ${count} тестовых сотрудников с навыками по параметрам отдела? Прежние тестовые сотрудники отдела будут заменены.`)) return;
      reseed.disabled = true;
      try {
        await api('/api/admin/demo/executors/seed', { method: 'POST', body: { count } });
        toast('Тестовые сотрудники с навыками заведены');
        await refreshExecutors();
      } catch (e) {
        toast(problemText(e), 'bad');
        reseed.disabled = false;
      }
    }, 'btn btn-sm btn-warning', 'wand');
    bar.append(reseed);
  }
  box.replaceChildren(text, ...(bar.childElementCount ? [bar] : []));
}
