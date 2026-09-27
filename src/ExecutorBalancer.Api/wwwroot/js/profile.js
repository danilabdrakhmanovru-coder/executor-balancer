// Страница сотрудника: кто он, что умеет (параметры отдела и в каких правилах они участвуют), норма на сегодня,
// качество и баллы, место в рейтинге, активность за неделю и последние заявки.
import { api, problemText } from './api.js';
import { $, el, row, badge, button, fmt, fmtDateTime, emptyRow, tile, icon } from './dom.js';
import { columns } from './charts.js';
import { KIND } from './explain.js';
import { openOrder, todayCell } from './overview.js';
import { edit, extraMode, setActive } from './executors.js';

const STATUS = { Processed: ['в работе', ''], Await: ['на доработке', 'warn'], Accept: ['решена', 'ok'], Reject: ['отклонена', 'ok'], Moved: ['передана другому', ''] };
let demoEnabled = false;
let current = null;

export function setProfileEditable(enabled) { demoEnabled = enabled; }

const percent = (share) => (share === null || share === undefined ? null : Math.round(share * 1000) / 10);

function initials(name) {
  return name.split(/\s+/).filter(Boolean).slice(0, 2).map((p) => p[0].toUpperCase()).join('');
}

function header(p, source) {
  const card = el('div', null, 'card profile-head');
  const body = el('div', null, 'card-body d-flex flex-wrap gap-3 align-items-center');
  const avatar = el('span', initials(p.fullName), `avatar avatar-xl profile-avatar${p.isActive ? '' : ' off'}`);
  const who = el('div', null, 'flex-fill');
  const title = el('div', null, 'd-flex flex-wrap align-items-center gap-2');
  title.append(el('h2', p.fullName, 'mb-0'), p.isActive ? badge('на работе', 'ok') : badge('не работает', 'bad'));
  if (p.extraPercent > 0) {
    title.append(p.extra?.extraLimit ? badge(`больше нормы +${p.extraPercent}%`, 'ok') : badge('больше нормы — на паузе', 'bad'));
  }
  who.append(title,
    el('div', `${p.department}${p.sphere ? ` · ${p.sphere}` : ''} · № ${p.id} в АИС`, 'text-secondary'),
    el('div', `данные из АИС обновлены ${fmtDateTime(p.updatedAt)}`, 'text-secondary small'));
  if (p.extraPercent > 0 && p.extra?.note) who.append(el('div', p.extra.note, 'warn-text small'));

  const after = () => refreshProfile(p.id);
  const actions = el('div', null, 'btn-list');
  if (p.dailyLimit != null) {
    actions.append(button(p.extraPercent > 0 ? `Больше нормы: +${p.extraPercent}%` : 'Больше нормы…', () => extraMode(p, after),
      p.extraPercent > 0 ? 'btn btn-success' : 'btn', 'flame'));
  }
  if (demoEnabled && source) {
    actions.append(
      button(p.isActive ? 'На перерыв' : 'Вернуть на работу', () => setActive(p, !p.isActive, after), 'btn', p.isActive ? 'coffee' : 'user-check'),
      button('Изменить', () => edit(source, after), 'btn', 'pencil'),
    );
  }
  body.append(avatar, who, actions);
  card.append(body);
  return card;
}

function tiles(p) {
  const wrap = el('div', null, 'row row-cards tiles');
  const q = percent(p.quality?.quality);
  const threshold = percent(p.qualityThreshold);
  const week = p.week;
  const today = el('span');
  today.append(todayCell({ ...p, assignedToday: p.today.assignedToday }));
  wrap.append(
    tile('Сегодня / норма', today, p.dailyLimit == null ? 'без суточного лимита' : 'назначено новых заявок', false, 'calendar-stats'),
    tile('В работе сейчас', fmt(p.today.openCount), `вес ${fmt(p.today.openWeight)} · квалификация ${fmt(p.qualificationWeight)}`, false, 'briefcase'),
    tile('Качество за 7 дней', q === null ? '—' : `${fmt(q)}%`,
      q === null ? `оценивается от 5 закрытых (сейчас ${p.quality?.closed ?? 0})` : `порог ${fmt(threshold)}% · баллы / максимум`,
      q !== null && q < threshold, 'star'),
    tile('Место в рейтинге', week?.rank ? `${week.rank} из ${week.rated}` : '—',
      week?.rank ? `за 7 дней · ${fmt(week.points)} баллов` : week?.closed ? 'вне рейтинга: качество ниже порога' : 'закрытых заявок пока нет',
      Boolean(week?.closed && !week?.rank), 'trophy'),
  );
  return wrap;
}

/** «Что умеет»: параметры сотрудника из справочника отдела — списком значков, с правилами, где участвуют. */
function skills(p) {
  const card = el('div', null, 'card h-100');
  const head = el('div', null, 'card-header');
  head.append(el('h3', 'Что умеет', 'card-title'), el('div', 'параметры отдела — по ним правила подбирают заявки', 'card-actions text-secondary'));
  const body = el('div', null, 'card-body');
  if (!p.skills.length) body.append(el('p', 'В отделе нет параметров сотрудника — подходит к любой заявке.', 'muted'));
  const list = el('div', null, 'skill-list');
  for (const s of p.skills) {
    const item = el('div', null, 'skill');
    item.append(el('div', s.label, 'skill-label'));
    const values = el('div', null, 'skill-values');
    if (!s.values.length) values.append(el('span', 'не указано — правила по нему не ограничивают', 'muted'));
    for (const v of s.values) values.append(el('span', v, 'badge bg-blue-lt skill-chip'));
    item.append(values);
    if (s.rules.length) item.append(el('div', `учитывается в правилах: ${s.rules.join(', ')}`, 'text-secondary small'));
    list.append(item);
  }
  body.append(list);
  card.append(head, body);
  return card;
}

function activity(p) {
  const card = el('div', null, 'card h-100');
  const head = el('div', null, 'card-header');
  head.append(el('h3', 'Активность за неделю', 'card-title'), el('div', 'назначено заявок по дням', 'card-actions text-secondary'));
  const chart = el('div', null, 'chart chart-small');
  const body = el('div', null, 'card-body');
  body.append(chart);
  const w = p.week;
  if (w) {
    body.append(el('p', `За 7 дней: назначено ${fmt(w.assigned)}, закрыто ${fmt(w.closed)}, на доработку ${fmt(w.returned)}, `
      + `закрыто подозрительно быстро ${fmt(w.fastClosed)}, сверх нормы ${fmt(w.extra)}. Отклонение от справедливой доли: `
      + `${w.deviationPercent == null ? '—' : `${fmt(w.deviationPercent)}%`}.`, 'text-secondary small mt-2 mb-0'));
  }
  card.append(head, body);
  // график рисуется после вставки в страницу — ему нужна ширина контейнера
  requestAnimationFrame(() => columns(chart, p.days.map((d) => ({
    label: new Date(`${d.day}T00:00:00`).toLocaleDateString('ru-RU', { weekday: 'short', day: '2-digit' }),
    value: d.assigned,
    title: 'назначено',
  })), { empty: 'За неделю назначений не было' }));
  return card;
}

function recent(p) {
  const card = el('div', null, 'card');
  const head = el('div', null, 'card-header');
  head.append(el('h3', 'Последние заявки', 'card-title'), el('div', 'щёлкните — покажет, почему заявка ушла ему', 'card-actions text-secondary'));
  const table = el('table', null, 'table card-table table-vcenter table-hover');
  const thead = el('thead');
  thead.append(row(['Время', 'Заявка', 'Что за заявка', 'Как досталась', 'Вес', 'Статус', 'Балл']));
  const tbody = el('tbody');
  if (!p.recent.length) tbody.append(emptyRow(7, 'Назначений пока не было'));
  for (const r of p.recent) {
    const [label, cls] = STATUS[r.status] || [r.status ?? '—', ''];
    const tr = row([fmtDateTime(r.createdAt), `#${r.orderId}`, r.summary || '—', KIND[r.kind] || r.kind, fmt(r.orderWeight),
      badge(label, cls), r.points == null ? '—' : fmt(r.points)], 'clickable');
    tr.addEventListener('click', () => openOrder(r.orderId));
    tbody.append(tr);
  }
  table.append(thead, tbody);
  const scroll = el('div', null, 'table-responsive');
  scroll.append(table);
  card.append(head, scroll);
  return card;
}

export async function refreshProfile(id) {
  current = id;
  const box = $('profile');
  try {
    const [p, sources] = await Promise.all([
      api(`/api/dashboard/executors/${encodeURIComponent(id)}`),
      demoEnabled ? api('/api/admin/demo/executors').catch(() => []) : Promise.resolve([]),
    ]);
    if (current !== id) return; // пока грузили, открыли другого
    const cols = el('div', null, 'row row-cards');
    const left = el('div', null, 'col-lg-7');
    const right = el('div', null, 'col-lg-5');
    left.append(skills(p));
    right.append(activity(p));
    cols.append(left, right);
    box.replaceChildren(header(p, sources.find((s) => s.id === p.id)), tiles(p), cols, recent(p));
  } catch (e) {
    const empty = el('div', null, 'card card-body');
    empty.append(icon('user-circle'), el('p', e.status === 404
      ? 'В этом отделе такого сотрудника нет — возможно, выбран другой отдел в шапке.' : problemText(e), 'muted'));
    box.replaceChildren(empty);
  }
}
