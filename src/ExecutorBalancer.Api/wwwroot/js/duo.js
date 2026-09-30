// «Два сотрудника» на Тестовом стенде: двое одинаковых сотрудников и медленный поток — видно, как алгоритм делит заявки
// поровну. Панель показывает нагрузку каждого за час и оценку, по которой решается следующая заявка, доли за 1, 5 и 60 минут
// и последние решения с оценками обоих. Эксперимент — перерыв (второго на перерыв не отпустят — защита смены);
// опыт ×2 показывается отдельным запуском с чистого листа: смена опыта посреди часа пересчитала бы весь уже
// набранный за час вес по новому опыту, и один получал бы заявки подряд до выравнивания. Сервер — DemoEndpoints (/scenario/duo) и DashboardEndpoints (/split).
import { api, problemText } from './api.js';
import { $, el, button, toast, fmt, badge, deviation } from './dom.js';
import { demoStaff } from './session.js';
import { openOrder } from './overview.js';

/** Панель — только в маленьком отделе: на 15+ сотрудниках доли смотрят в «Аналитике». */
const MAX_PEOPLE = 4;
const WINDOW_TITLE = { 1: 'за минуту', 5: 'за 5 минут', 60: 'за час' };
const KIND_TITLE = { Primary: 'новая', Reassignment: 'передана', Secondary: 'с доработки' };

let busy = false;

async function startDuo(sameQualification) {
  const text = sameQualification
    ? 'Отдел начнётся с чистого листа: заявки и статистика удалятся, все сотрудники будут уволены, заведутся двое одинаковых '
      + '(Анна и Борис: умеют всё, опыт ×1, без дневного лимита) и запустится поток. Продолжить?'
    : 'То же, но у Бориса опыт ×2 — он должен получать примерно вдвое больше по весу. Продолжить?';
  if (!confirm(text)) return;
  busy = true;
  for (const b of document.querySelectorAll('.duo-start')) b.disabled = true;
  try {
    const r = await api('/api/admin/demo/scenario/duo', { method: 'POST', body: { sameQualification } });
    toast(`Двое сотрудников заведены, поток — ${fmt(r.ratePerHour)} заявок в час`);
  } catch (e) {
    toast(problemText(e), 'bad');
  } finally {
    busy = false;
    for (const b of document.querySelectorAll('.duo-start')) b.disabled = false;
  }
}

async function setActive(person, active) {
  try {
    await api(`/api/admin/executors/${person.id}/active`, { method: 'POST', body: { isActive: active } });
    toast(active ? `${person.fullName} снова на работе` : `${person.fullName} на перерыве — заявки идут коллеге`);
  } catch (e) {
    // второго на перерыв не отпустят: правило «на работе не меньше N%» или «каждую заявку кто-то умеет»
    toast(`Не отпустили: ${problemText(e)}`, 'bad');
  }
}

/** Кто получит следующую заявку весом 1 — тем же порядком, что Lua-скрипт выбора. */
function nextWinner(people) {
  const active = people.filter((p) => p.isActive);
  if (!active.length) return { winner: null, why: 'Все на перерыве — заявки ждут.' };
  const sorted = [...active].sort((a, b) => a.nextScore - b.nextScore || a.openWeight / a.qualification
    - b.openWeight / b.qualification || a.assignedToday / a.qualification - b.assignedToday / b.qualification || a.id - b.id);
  const [first, second] = sorted;
  if (!second) return { winner: first, why: 'на работе один сотрудник — все заявки идут сюда' };
  const why = first.nextScore < second.nextScore
    ? `меньше оценка (${fmt(first.nextScore)} < ${fmt(second.nextScore)})`
    : 'оценки за час равны — решают открытые заявки, затем число за день';
  return { winner: first, why };
}

function personCard(p, next, total) {
  const card = el('div', null, `duo-person${next ? ' next' : ''}${p.isActive ? '' : ' off'}`);
  const head = el('div', null, 'd-flex justify-content-between align-items-start gap-2');
  head.append(el('strong', p.fullName, 'duo-name'),
    p.isActive ? (next ? badge('получит следующую', 'ok') : badge('на работе')) : badge('на перерыве', 'warn'));
  const score = el('div', null, 'duo-score');
  score.append(el('span', fmt(p.nextScore), 'duo-score-value'),
    el('span', `= (${fmt(p.hourWeight)} за час + 1) ÷ опыт ${fmt(p.qualification)}`, 'muted'));
  card.append(head, score,
    el('div', `Вес за этот час: ${fmt(p.hourWeight)}${total > 0 ? ` (${fmt(Math.round(p.hourWeight / total * 1000) / 10)}%)` : ''}; `
      + `в работе сейчас — ${fmt(p.openWeight)}; за день заявок — ${fmt(p.assignedToday)}`, 'small text-secondary'));
  if (demoStaff()) {
    const tools = el('div', null, 'btn-list mt-2');
    tools.append(button(p.isActive ? 'На перерыв' : 'Вернуть на работу', () => setActive(p, !p.isActive), 'btn btn-sm',
      p.isActive ? 'coffee' : 'player-play'));
    card.append(tools);
  }
  return card;
}

function sharesTable(people, windows) {
  const active = people.filter((p) => p.isActive);
  const qSum = active.reduce((s, p) => s + p.qualification, 0);
  const table = el('table', null, 'table table-sm table-vcenter mb-0 duo-table');
  const head = el('tr');
  head.append(el('th', 'Период'));
  for (const p of people) head.append(el('th', p.fullName));
  head.append(el('th', 'Отклонение от ожидаемого'));
  const thead = el('thead');
  thead.append(head);
  const body = el('tbody');
  for (const w of windows) {
    const byId = new Map(w.rows.map((r) => [r.executorId, r]));
    const totalWeight = w.rows.reduce((s, r) => s + r.weight, 0);
    const tr = el('tr');
    tr.append(el('td', WINDOW_TITLE[w.minutes] ?? `${w.minutes} мин`));
    let worst = null;
    for (const p of people) {
      const r = byId.get(p.id);
      const share = totalWeight > 0 ? (r?.weight ?? 0) / totalWeight * 100 : null;
      const td = el('td');
      td.append(el('span', `${fmt(r?.count ?? 0)} заявок, вес ${fmt(r?.weight ?? 0)}`),
        el('span', share == null ? '' : ` · ${fmt(Math.round(share * 10) / 10)}%`, 'muted'));
      tr.append(td);
      if (share != null && p.isActive && qSum > 0) {
        const diff = Math.round((share - p.qualification / qSum * 100) * 10) / 10;
        if (worst == null || Math.abs(diff) > Math.abs(worst)) worst = diff;
      }
    }
    const devCell = el('td');
    devCell.append(totalWeight > 0 && active.length > 1 ? deviation(worst) : el('span', '—', 'muted'));
    tr.append(devCell);
    body.append(tr);
  }
  table.append(thead, body);
  return table;
}

function recentList(people, recent) {
  const names = new Map(people.map((p) => [p.id, p.fullName]));
  const list = el('div', null, 'list-group list-group-flush duo-recent');
  if (!recent.length) {
    list.append(el('div', 'Решений пока нет — поток только начался.', 'list-group-item text-secondary'));
    return list;
  }
  for (const a of recent) {
    const item = el('button', null, 'list-group-item list-group-item-action feed-item');
    item.type = 'button';
    item.title = 'Почему этот исполнитель?';
    const scores = a.candidates.filter((c) => c.score != null)
      .map((c) => `${names.get(c.executorId) ?? `#${c.executorId}`} ${fmt(Math.round(c.score * 1000) / 1000)}`).join(' · ');
    item.append(el('span', `#${a.orderId}`, 'feed-id'), el('span', `вес ${fmt(a.orderWeight)}`, 'muted'), el('span', '→', 'muted'),
      el('strong', names.get(a.executorId) ?? `#${a.executorId}`),
      el('span', scores ? `оценки: ${scores}` : KIND_TITLE[a.kind] ?? a.kind, 'feed-what'));
    item.addEventListener('click', () => openOrder(a.orderId));
    list.append(item);
  }
  return list;
}

/** total — сколько сотрудников в отделе (из сводки стенда): в большом отделе панель не запрашивает данные зря. */
export async function refreshDuo(total) {
  const panel = $('duo-panel');
  if (!panel || busy) return;
  const empty = (count) => panel.replaceChildren(el('p', count
    ? `Сейчас в отделе ${count} сотрудников — панель показывает до ${MAX_PEOPLE}. Нажмите кнопку выше, чтобы оставить двоих.`
    : 'В отделе нет сотрудников — нажмите кнопку выше.', 'text-secondary mb-0'));
  if (!total || total > MAX_PEOPLE) { empty(total); return; }
  const split = await api('/api/dashboard/split').catch(() => null);
  const people = split?.executors ?? [];
  if (!people.length || people.length > MAX_PEOPLE) { empty(people.length); return; }
  const hourTotal = people.reduce((s, p) => s + p.hourWeight, 0);
  const { winner, why } = nextWinner(people);
  const grid = el('div', null, 'duo-grid');
  grid.append(...people.map((p) => personCard(p, winner?.id === p.id, hourTotal)));
  panel.replaceChildren(
    grid,
    el('p', winner ? `Следующая заявка уйдёт: ${winner.fullName} — ${why}. Меньше оценка — меньше нагрузки на единицу опыта.` : why,
      'mt-3 mb-2'),
    el('h4', 'Как поделились заявки', 'mt-3'),
    el('p', 'Ожидаемая доля по весу — пропорционально опыту работающих: у двоих одинаковых по 50%. Отклонение — насколько '
      + 'самая далёкая доля отличается от ожидаемой. Если кто-то был на перерыве, после возвращения заявки идут ему подряд, '
      + 'пока оценки не сравняются, — так доля за час выравнивается.', 'text-secondary small'),
    sharesTable(people, split.windows),
    el('h4', 'Последние решения', 'mt-3'),
    recentList(people, split.recent),
  );
}

export function initDuo() {
  $('duo-start')?.addEventListener('click', () => startDuo(true));
  $('duo-start-senior')?.addEventListener('click', () => startDuo(false));
}
