// Аналитика: периоды, динамика, справедливость, рейтинг и «количество против качества», типы назначений, выгрузка.
import { api, scoped } from './api.js';
import { $, el, row, fmt, deviation, emptyRow, tile, badge } from './dom.js';
import { lines, diverging, columns, scatter } from './charts.js';

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
function renderRating(executors, motivation) {
  const threshold = percent(motivation.qualityThreshold);
  // место — только при качестве не ниже порога; остальные ниже, с пометкой «вне рейтинга»
  const rated = executors.filter((e) => e.closed > 0 || e.extra > 0)
    .sort((a, b) => (a.rank ?? 1e9) - (b.rank ?? 1e9) || b.points - a.points);
  $('an-rating-hint').textContent = `Место в рейтинге — по баллам, но только при качестве от ${fmt(threshold)}%: `
    + 'иначе быстрый и небрежный сотрудник обошёл бы всех за счёт количества. Качество оценивается от 5 закрытых заявок; '
    + 'ниже порога приостанавливается и режим «больше нормы».';
  if (!rated.length) {
    $('an-rating').replaceChildren(emptyRow(8, 'За период ещё нет закрытых заявок'));
  } else {
    $('an-rating').replaceChildren(...rated.map((e) => {
      const q = percent(e.quality);
      const quality = q === null ? '—' : badge(`${fmt(q)}%`, q < threshold ? 'bad' : q < percent(motivation.heavyQualityThreshold) ? 'warn' : 'ok');
      const name = el('span');
      const link = el('a', e.name, 'executor-name');
      link.href = `#executor-${e.id}`;
      name.append(link);
      if (e.rank && e.rank <= 3) name.append(' ', badge(`${e.rank} место`, 'ok'));
      let place = e.rank ? String(e.rank) : '—';
      if (!e.rank && e.closed > 0) {
        const out = badge('вне рейтинга', 'bad');
        out.title = `Качество ${fmt(q)}% ниже порога ${fmt(threshold)}% — баллы за количество не засчитываются в рейтинг`;
        name.append(' ', out);
        place = '—';
      }
      return row([place, name, fmt(e.points), fmt(e.closed), quality, fmt(e.fastClosed), fmt(e.returned),
        fmt(e.extra)], e.rank || !e.closed ? (e.isActive ? '' : 'inactive') : 'warn-row');
    }));
  }
  scatter($('an-scatter'), rated.filter((e) => e.quality !== null).map((e) => ({
    x: e.closed, y: percent(e.quality), label: e.name.split(' ')[0],
    cls: percent(e.quality) < threshold ? 'dev-bad' : 'dev-ok',
  })), { threshold, empty: 'Точки появятся, когда у сотрудников наберётся хотя бы по 5 закрытых заявок' });
}

export async function refreshAnalytics() {
  const [report, motivation] = await Promise.all([
    api(`/api/dashboard/analytics?period=${encodeURIComponent(period)}`),
    api('/api/admin/motivation'),
  ]);
  renderTiles(report);
  const labels = report.timeline.map((p) => label(p, report.bucketHours));
  lines($('an-timeline'), labels, [
    { name: 'назначено', cls: 'series-a', values: report.timeline.map((p) => p.assigned) },
    { name: 'решено и отклонено', cls: 'series-b', values: report.timeline.map((p) => p.closed) },
    { name: 'на доработку', cls: 'series-c', values: report.timeline.map((p) => p.returned) },
  ]);
  diverging($('an-deviation'), report.executors.map((e) => ({ label: e.name, value: e.deviationPercent })));
  const k = report.kinds;
  columns($('an-kinds'), [
    { label: 'первичные', value: k.primary },
    { label: 'перераспред.', value: k.reassign },
    { label: 'от родителя', value: k.parent },
    { label: 'вторичные', value: k.secondary },
    { label: 'сверх нормы', value: k.extra },
  ], { cls: 'series-b' });
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
