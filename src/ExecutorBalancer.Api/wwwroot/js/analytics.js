// Аналитика: периоды, динамика, справедливость, рейтинг и «количество против качества», типы назначений, выгрузка.
import { api, scoped } from './api.js';
import { $, el, row, fmt, deviation, emptyRow, tile, badge } from './dom.js';
import { groupedColumns, diverging, bars, scatter } from './charts.js';

const PERIODS = ['today', '24h', '7d', '30d'];
let period = 'today';

function stored() {
  try { return localStorage.getItem('eb.period'); } catch { return null; }
}

function remember(value) {
  try { localStorage.setItem('eb.period', value); } catch { /* приватный режим */ }
}

function label(point, bucketHours) {
  const date = new Date(point.start);
  if (bucketHours >= 24) return date.toLocaleDateString('ru-RU', { day: '2-digit', month: '2-digit' });
  if (bucketHours > 1) {
    return `${date.toLocaleDateString('ru-RU', { day: '2-digit', month: '2-digit' })} ${date.toLocaleTimeString('ru-RU', { hour: '2-digit' })}ч`;
  }
  return date.toLocaleTimeString('ru-RU', { hour: '2-digit', minute: '2-digit' });
}

function renderTiles(report) {
  const t = report.timeline.reduce((acc, p) => ({
    assigned: acc.assigned + p.assigned, weight: acc.weight + p.assignedWeight,
    closed: acc.closed + p.closed, returned: acc.returned + p.returned,
  }), { assigned: 0, weight: 0, closed: 0, returned: 0 });
  const f = report.fairness;
  const tiles = [
    ['Назначено', fmt(t.assigned), 'за выбранный период', false, 'bolt'],
    ['Суммарный вес', fmt(t.weight), 'сложные заявки весят больше', false, 'scale'],
    ['Решено и отклонено', fmt(t.closed), 'закрыто сотрудниками', false, 'circle-check'],
    ['На доработку', fmt(t.returned), 'вернули клиенту за уточнением', false, 'arrow-back-up'],
    ['Среднее отклонение', f.meanAbsDeviationPercent == null ? '—' : `${fmt(f.meanAbsDeviationPercent)}%`,
      'от идеально ровного распределения; норма — до 2%', f.meanAbsDeviationPercent > 2, 'scale'],
    ['Максимальное отклонение', f.maxAbsDeviationPercent == null ? '—' : `${fmt(f.maxAbsDeviationPercent)}%`,
      'у одного сотрудника', f.maxAbsDeviationPercent > 5, 'alert-triangle'],
  ];
  $('an-tiles').replaceChildren(...tiles.map((args) => tile(...args)));
}

function renderExecutors(executors) {
  if (!executors.length) {
    $('an-executors').replaceChildren(emptyRow(10, 'Нет данных за период'));
    return;
  }
  $('an-executors').replaceChildren(...executors.map((e) => row([
    e.name, fmt(e.qualification), fmt(e.assigned), fmt(e.assignedWeight), fmt(e.freeWeight), fmt(e.fairWeight),
    deviation(e.deviationPercent), fmt(e.closed), fmt(e.returned),
    e.returnRatePercent == null ? '—' : `${fmt(e.returnRatePercent)}%`,
  ], e.isActive ? '' : 'inactive')));
}

const percent = (share) => (share === null || share === undefined ? null : Math.round(share * 1000) / 10);

/** Рейтинг: больше баллов — выше; качество ниже порога подсвечивается. */
/** Балл строкой с полосой: длина — доля от лидера, чтобы разрыв был виден без чтения чисел. */
function pointsCell(value, max) {
  const cell = el('div', null, 'points-cell');
  const bar = el('div', null, 'points-bar');
  const fill = el('span');
  fill.style.width = `${max > 0 ? Math.max(2, (value / max) * 100) : 0}%`;
  bar.append(fill);
  cell.append(el('span', fmt(value), 'points-value'), bar);
  return cell;
}

function nameCell(e, extra) {
  const name = el('div', null, 'd-flex flex-wrap align-items-center gap-2');
  const link = el('a', e.name, 'executor-name');
  link.href = `#executor-${e.id}`;
  name.append(link);
  if (extra) name.append(extra);
  return name;
}

/**
 * Рейтинг: все сотрудники без внутренней прокрутки. Сначала места (по баллам, при качестве не ниже порога),
 * затем отдельной группой — «вне рейтинга» с причиной.
 */
function renderRating(executors, motivation) {
  const threshold = percent(motivation.qualityThreshold);
  const good = percent(motivation.heavyQualityThreshold);
  const withWork = executors.filter((e) => e.closed > 0 || e.extra > 0);
  const ranked = withWork.filter((e) => e.rank).sort((a, b) => a.rank - b.rank);
  const outside = withWork.filter((e) => !e.rank && e.closed > 0).sort((a, b) => b.points - a.points);
  const max = Math.max(0, ...withWork.map((e) => e.points));
  $('an-rating-hint').textContent = `Место — по баллам, но только при качестве от ${fmt(threshold)}%: иначе быстрый и небрежный `
    + 'сотрудник обошёл бы всех за счёт количества. Качество оценивается от 5 закрытых заявок (до этого — «—»); '
    + 'ниже порога приостанавливается и режим «больше нормы».';
  const line = (e, place, extra, cls = '') => {
    const q = percent(e.quality);
    const quality = q === null ? '—' : badge(`${fmt(q)}%`, q < threshold ? 'bad' : q < good ? 'warn' : 'ok');
    const tr = row([place, nameCell(e, extra), pointsCell(e.points, max), fmt(e.closed), quality, fmt(e.returned),
      fmt(e.fastClosed), fmt(e.extra)], `${cls}${e.isActive ? '' : ' inactive'}`);
    for (const i of [3, 5, 6, 7]) tr.children[i].classList.add('text-end');
    return tr;
  };
  const rows = ranked.map((e) => {
    const medal = e.rank <= 3 ? el('span', String(e.rank), `medal medal-${e.rank}`) : el('span', String(e.rank), 'place');
    return line(e, medal);
  });
  if (outside.length) {
    const head = el('tr', null, 'group-row');
    const td = el('td', `Вне рейтинга — качество ниже ${fmt(threshold)}%: баллы за количество не засчитываются`);
    td.colSpan = 8;
    head.append(td);
    rows.push(head, ...outside.map((e) => line(e, '—', badge('вне рейтинга', 'bad'), 'outside-row')));
  }
  $('an-rating').replaceChildren(...(rows.length ? rows : [emptyRow(8, 'За период ещё нет закрытых заявок')]));

  scatter($('an-scatter'), withWork.filter((e) => e.quality !== null).map((e) => ({
    x: e.closed, y: percent(e.quality), points: e.points, label: e.name.split(' ')[0], fullName: e.name,
  })), { threshold, empty: 'Точки появятся, когда у сотрудников наберётся хотя бы по 5 закрытых заявок' });

  // кому уделить внимание: качество ниже порога, много быстрых закрытий или доработок
  const attention = withWork.map((e) => {
    const reasons = [];
    const q = percent(e.quality);
    if (q !== null && q < threshold) reasons.push(`качество ${fmt(q)}% ниже порога`);
    if (e.closed >= 5 && e.fastClosed / e.closed >= 0.3) reasons.push(`подозрительно быстрых закрытий ${Math.round((e.fastClosed / e.closed) * 100)}%`);
    if (e.returnRatePercent !== null && e.returnRatePercent >= 30 && e.closed + e.returned >= 5) reasons.push(`доработок ${fmt(e.returnRatePercent)}%`);
    return { e, reasons };
  }).filter((a) => a.reasons.length);
  $('an-attention').replaceChildren(...(attention.length ? attention.map(({ e, reasons }) => {
    const item = el('a', null, 'list-group-item list-group-item-action');
    item.href = `#executor-${e.id}`;
    item.append(el('div', e.name, 'fw-bold'), el('div', reasons.join(' · '), 'text-danger small'),
      el('div', `закрыто ${fmt(e.closed)} · баллов ${fmt(e.points)}`, 'text-secondary small'));
    return item;
  }) : [el('div', 'Никто: у всех качество в норме, нет перекоса в сторону скорости.', 'list-group-item text-secondary')]));
}

export async function refreshAnalytics() {
  const [report, motivation] = await Promise.all([
    api(`/api/dashboard/analytics?period=${encodeURIComponent(period)}`),
    api('/api/admin/motivation'),
  ]);
  renderTiles(report);
  // пустые интервалы до первой активности не показываем — иначе половина графика пустая
  let first = report.timeline.findIndex((p) => p.assigned || p.closed || p.returned);
  if (first < 0) first = report.timeline.length;
  const timeline = report.timeline.slice(Math.max(0, Math.min(first - 1, report.timeline.length - 3)));
  groupedColumns($('an-timeline'), timeline.map((p) => label(p, report.bucketHours)), [
    { name: 'назначено', cls: 'series-a', values: timeline.map((p) => p.assigned) },
    { name: 'решено и отклонено', cls: 'series-b', values: timeline.map((p) => p.closed) },
    { name: 'на доработку', cls: 'series-c', values: timeline.map((p) => p.returned) },
  ]);
  diverging($('an-deviation'), report.executors.map((e) => ({
    label: e.name, value: e.deviationPercent, assigned: e.assignedWeight, fair: e.fairWeight,
  })));
  const k = report.kinds;
  bars($('an-kinds'), [
    { label: 'выбор алгоритма', value: k.primary, hint: 'кто из подходящих получил меньше за этот час' },
    { label: 'перераспределены', value: k.reassign, hint: 'прежний сотрудник ушёл или не подходит' },
    { label: 'от родительской', value: k.parent, hint: 'тому, кто ведёт родительскую заявку' },
    { label: 'после доработки', value: k.secondary, hint: 'вернулась к тому же сотруднику' },
    { label: 'сверх нормы', value: k.extra, hint: 'режим «больше нормы»: только излишки' },
  ], { empty: 'За период назначений нет' });
  renderExecutors(report.executors);
  renderRating(report.executors, motivation);
}

function select(value) {
  period = PERIODS.includes(value) ? value : 'today';
  remember(period);
  for (const b of $('period').querySelectorAll('button')) b.classList.toggle('active', b.dataset.period === period);
  exportLink();
}

/** Ссылка выгрузки — за выбранный период и по текущему отделу. */
export function exportLink() {
  $('export').href = scoped(`/api/dashboard/export.csv?period=${encodeURIComponent(period)}`);
}

export function initAnalytics(onChange) {
  select(stored());
  $('period').addEventListener('click', (event) => {
    const value = event.target.closest('button')?.dataset.period;
    if (!value) return;
    select(value);
    onChange();
  });
}
