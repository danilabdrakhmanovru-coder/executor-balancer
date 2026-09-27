// «Как это работает»: путь одной заявки от обращения клиента до результата (живые цифры отдела)
// и чек-лист готовности отдела — что сделать, чтобы заявки начали распределяться.
import { api } from './api.js';
import { $, el, icon, fmt } from './dom.js';
import { currentDepartment } from './department.js';

let demoEnabled = false;

export function setStartDemo(enabled) { demoEnabled = enabled; }

function link(hash) {
  const a = el('a', null, 'path-step');
  a.href = hash;
  return a;
}

function step(no, iconName, who, title, value, note, hash) {
  const box = link(hash);
  box.append(el('span', String(no), 'path-no'), icon(iconName), el('span', who, 'path-who'), el('span', title, 'path-title'),
    el('span', value, 'path-value'), el('span', note, 'path-note'));
  return box;
}

function check(done, title, text, action) {
  const item = el('div', null, 'list-group-item check-item');
  const mark = el('span', null, `check-mark ${done ? 'ok' : 'todo'}`);
  mark.append(icon(done ? 'check' : 'arrow-right'));
  const body = el('div', null, 'flex-fill');
  body.append(el('div', title, 'fw-bold'), el('div', text, 'text-secondary small'));
  item.append(mark, body);
  if (action) {
    const a = el('a', action.text, `btn btn-sm ${done ? '' : 'btn-primary'}`);
    a.href = action.hash;
    item.append(a);
  }
  return item;
}

export async function refreshStart() {
  const [summary, config, status] = await Promise.all([
    api('/api/dashboard/summary'),
    api('/api/admin/config'),
    demoEnabled ? api('/api/admin/demo/status').catch(() => null) : Promise.resolve(null),
  ]);
  const t = summary.totals;
  const rules = config.rules.filter((r) => r.isEnabled).length;
  const running = Boolean(status?.simulation?.running);
  const source = demoEnabled ? '#demo' : '#orders';

  $('start-path').replaceChildren(
    step(1, 'inbox', 'клиент → АИС', 'Клиент оставляет обращение', running ? 'поток идёт' : 'поток остановлен',
      demoEnabled ? 'на демонстрации — «Тестовый стенд»' : 'во внешней системе компании', source),
    step(2, 'send', 'АИС → сервис', 'Заявка приходит сюда', fmt(t.orders), 'всего заявок в отделе', '#orders'),
    step(3, 'list-check', 'сервис', 'Правила отбирают подходящих', `${fmt(rules)} правил`,
      'кто умеет и имеет допуск; разбор — «Проверка заявки»', '#preview'),
    step(4, 'route', 'сервис', 'Выбирается тот, кто получил меньше', fmt(t.assignedToday),
      t.pending > 0 ? `назначено сегодня · ждут: ${fmt(t.pending)}` : 'назначено сегодня · никто не ждёт', '#overview'),
    step(5, 'briefcase', 'сотрудник', 'Сотрудник работает в АИС', fmt(t.open),
      t.undelivered > 0 ? `в работе · в пути в АИС: ${fmt(t.undelivered)}` : 'в работе; доработка возвращает к нему же', '#executors'),
    step(6, 'circle-check', 'результат', 'Решена — баллы в рейтинг', fmt(t.closedToday), 'закрыто сегодня · рейтинг и отчёты', '#analytics'),
  );

  const d = currentDepartment();
  $('start-checklist').replaceChildren(
    check(Boolean(d), `Отдел: ${d?.name ?? '—'}`, d?.presetTitle ? `Сфера — ${d.presetTitle}. У каждого отдела свои правила, сотрудники и отчёты.`
      : 'Своя настройка сферы.', { text: 'Отделы', hash: '#departments' }),
    check(rules > 0, rules > 0 ? `Правила подбора заданы: ${rules}` : 'Правил подбора нет',
      rules > 0 ? `Параметров заявки ${config.fields.filter((f) => f.owner === 'Order').length}, сотрудника ${config.fields.filter((f) => f.owner === 'Executor').length}.`
        : 'Без правил подходит любой сотрудник. Задайте параметры и правила или выберите сферу.',
      { text: 'Настроить', hash: '#constructor' }),
    check(t.activeExecutors > 0, t.activeExecutors > 0 ? `Сотрудников на работе: ${t.activeExecutors}` : 'Сотрудников нет',
      t.activeExecutors > 0 ? 'Их навыки — на странице каждого сотрудника.'
        : demoEnabled ? 'Заведите их на «Тестовом стенде» (шаг 1) или добавьте по одному на вкладке «Сотрудники».' : 'Их передаёт АИС.',
      { text: t.activeExecutors > 0 ? 'Сотрудники' : 'Добавить', hash: t.activeExecutors > 0 ? '#executors' : source }),
    check(t.orders > 0, t.orders > 0 ? `Заявки поступают: ${fmt(t.orders)}` : 'Заявок ещё нет',
      running ? 'Поток на тестовом стенде идёт.' : demoEnabled ? 'Запустите поток или отправьте одну заявку вручную на «Тестовом стенде».'
        : 'Их присылает АИС.', { text: t.orders > 0 ? 'Заявки' : 'Запустить', hash: t.orders > 0 ? '#orders' : source }),
    check(t.closedToday > 0, t.closedToday > 0 ? `Есть результат: закрыто сегодня ${fmt(t.closedToday)}` : 'Результатов пока нет',
      'Откройте любую заявку — увидите её путь от поступления до результата. Итоги — в «Аналитике».',
      { text: 'Аналитика', hash: '#analytics' }),
  );
}
