// Тестовый стенд (вместо настоящей АИС): пошаговый пульт для выбранного отдела (сотрудники → поток заявок → заявка вручную)
// и живая схема пути заявки. Управляет эмулятором АИС через балансировщик; у каждого отдела свой поток.
import { api, problemText } from './api.js';
import { $, el, input, field, toast, fmt, badge, icon } from './dom.js';
import { attributeForm } from './forms.js';
import { renderExplanation, KIND } from './explain.js';
import { openOrder } from './overview.js';
import { showCheck } from './preview.js';

let config = null;
let orderForm = null;
let lastRunning = null;

// ---------- схема пути заявки ----------

function stage(number, iconName, title, value, note, cls = '') {
  const box = el('div', null, `stage ${cls}`);
  box.append(el('span', number, 'stage-no'), icon(iconName), el('div', title, 'stage-title'), el('div', value, 'stage-value'),
    el('div', note, 'stage-note'));
  return box;
}

function renderFlow(status, summary) {
  const sim = status?.simulation;
  const t = summary.totals;
  const flow = $('demo-flow');
  const arrow = () => {
    const a = el('div', null, 'stage-arrow');
    a.append(icon('arrow-right'));
    return a;
  };
  flow.replaceChildren(
    stage('1', 'inbox', 'Клиенты создают заявки в АИС', fmt(sim?.created ?? t.orders),
      sim?.running ? `поток ${fmt(sim.ratePerHour)} в час` : 'поток остановлен', sim?.running ? 'live' : ''),
    arrow(),
    stage('2', 'route', 'Балансировщик выбирает исполнителя', fmt(t.assignedToday),
      t.pending > 0 ? `ждут подходящего: ${fmt(t.pending)}` : 'назначено сегодня, никто не ждёт', t.pending > 0 ? 'warn' : ''),
    arrow(),
    stage('3', 'mail-forward', 'Назначение возвращается в АИС', t.undelivered > 0 ? `в пути ${fmt(t.undelivered)}` : 'всё доставлено',
      'АИС записывает его через 2–10 с'),
    arrow(),
    stage('4', 'briefcase', 'Исполнители работают', fmt(t.open), 'заявок сейчас в работе'),
    arrow(),
    stage('5', 'circle-check', 'Заявки решены', fmt(t.closedToday), 'решено и отклонено сегодня'),
  );
}

function renderFeed(feed) {
  const list = $('demo-feed');
  if (!feed.length) {
    list.replaceChildren(el('div', 'Назначений пока нет — запустите поток заявок или создайте заявку вручную.', 'list-group-item text-secondary'));
    return;
  }
  list.replaceChildren(...feed.slice(0, 12).map((a) => {
    const item = el('button', null, 'list-group-item list-group-item-action feed-item');
    item.type = 'button';
    item.title = 'Почему этот исполнитель?';
    item.append(el('span', `#${a.orderId}`, 'feed-id'), el('span', a.summary || '—', 'feed-what'),
      el('span', '→', 'muted'), el('strong', a.executorName), badge(KIND[a.kind] || a.kind, a.kind === 'Primary' ? '' : 'warn'));
    item.addEventListener('click', () => openOrder(a.orderId));
    return item;
  }));
}

function renderSimulation(sim) {
  const running = Boolean(sim?.running);
  $('demo-start').disabled = running;
  $('demo-stop').disabled = !running;
  $('demo-sim-status').textContent = running
    ? `Идёт: создано ${fmt(sim.created)}, решено ${fmt(sim.accepted)}, отклонено ${fmt(sim.rejected)}, на доработке было ${fmt(sim.sentToRework)}`
    : 'Поток остановлен.';
  if (lastRunning !== running) $('demo-flow').classList.toggle('running', running);
  lastRunning = running;
}

export async function refreshDemo() {
  const [status, summary, feed] = await Promise.all([
    api('/api/admin/demo/status').catch(() => null),
    api('/api/dashboard/summary'),
    api('/api/dashboard/feed'),
  ]);
  $('demo-ais-error').textContent = status ? '' : 'Эмулятор АИС недоступен — проверьте, что он запущен.';
  renderFlow(status, summary);
  renderSimulation(status?.simulation);
  renderFeed(feed);
  const total = summary.executors?.length ?? 0;
  $('demo-executors-count').textContent = total
    ? `Сейчас в отделе сотрудников: ${total}, из них на работе: ${summary.totals.activeExecutors}.`
    : 'В отделе пока нет сотрудников.';
  if (!config) await reloadConfig();
}

// ---------- шаги ----------

async function reloadConfig() {
  config = await api('/api/admin/config');
  orderForm = attributeForm(config, 'Order');
  $('demo-order-fields').replaceChildren(...orderForm.nodes);
}

function rateText(rate) {
  const perSecond = rate / 3600;
  const pace = perSecond >= 1 ? `≈ ${fmt(Math.round(perSecond * 10) / 10)} в секунду` : `≈ 1 заявка раз в ${fmt(Math.round(1 / perSecond))} с`;
  return `${fmt(rate)} заявок в час (${pace})${rate === 4000 ? ' — как в кейсе' : ''}`;
}

async function sendOrder() {
  const result = $('demo-order-result');
  const send = $('demo-order-send');
  try {
    send.disabled = true;
    const order = await api('/api/admin/demo/orders', { method: 'POST', body: { attributes: orderForm.read() } });
    result.replaceChildren(el('p', `Заявка #${order.id} создана в АИС и отправлена в балансировщик…`, 'muted'));
    // АИС пересылает заявку асинхронно — ждём решения до пары секунд
    for (let i = 0; i < 10; i++) {
      await new Promise((r) => setTimeout(r, 300));
      const details = await api(`/api/dashboard/orders/${order.id}`).catch(() => null);
      const current = details?.history?.find((h) => h.isCurrent);
      if (current?.explanation) {
        result.replaceChildren(el('h3', `Заявка #${order.id}`), renderExplanation(current.explanation, {
          onCandidate: (c) => showCheck(c, details.attributes || {}),
        }));
        return;
      }
      if (details?.pendingReason && i > 3) {
        result.replaceChildren(el('p', `Заявка #${order.id} ждёт исполнителя: ${details.pendingReason}.`, 'note'));
        return;
      }
    }
    result.replaceChildren(el('p', `Заявка #${order.id} ещё в пути — посмотрите её на вкладке «Заявки».`, 'muted'));
  } catch (e) {
    result.replaceChildren(el('p', e instanceof Error && !('status' in e) ? e.message : problemText(e), 'error'));
  } finally {
    send.disabled = false;
  }
}

const MAX_SEED = 100; // как DemoEndpoints.MaxSeedCount

export function initDemo() {
  const count = input('number', '15', { min: '1', max: String(MAX_SEED), step: '1', required: '' });
  $('demo-seed-count').replaceChildren(field(`Сколько сотрудников (от 1 до ${MAX_SEED})`, count));
  /** add — прибавить к имеющимся; exact — сделать в отделе ровно столько. */
  const seed = async (mode) => {
    const n = Number(count.value);
    if (!Number.isInteger(n) || n < 1 || n > MAX_SEED) {
      // не отправляем заведомо неверное число: сразу говорим, что не так
      toast(`Число сотрудников — от 1 до ${MAX_SEED}`, 'bad');
      count.focus();
      return;
    }
    if (mode === 'exact' && !confirm(`Сделать в отделе ровно ${n} сотрудников? Недостающие будут заведены, `
      + 'лишние — уволены (их открытые заявки перейдут коллегам).')) return;
    try {
      const result = await api('/api/admin/demo/executors/seed', { method: 'POST', body: { count: n, mode } });
      toast(result.removed ? `Уволено лишних: ${result.removed}. В отделе теперь ${n}.`
        : result.added ? `Заведено новых сотрудников: ${result.added} — навыки и лимиты случайные по параметрам отдела.`
          : `В отделе уже ${n} сотрудников — ничего менять не нужно.`);
      // АИС передаёт новых сотрудников балансировщику за доли секунды
      setTimeout(refreshDemo, 800);
    } catch (e) { toast(problemText(e), 'bad'); }
  };
  $('demo-seed-add').addEventListener('click', () => seed('add'));
  $('demo-seed-exact').addEventListener('click', () => seed('exact'));

  const rate = $('demo-rate');
  const label = () => { $('demo-rate-label').textContent = rateText(Number(rate.value)); };
  rate.addEventListener('input', label);
  label();
  $('demo-start').addEventListener('click', async () => {
    try {
      await api('/api/admin/demo/simulation/start', { method: 'POST', body: { ratePerHour: Number(rate.value) } });
      toast('Поток заявок отдела запущен');
      await refreshDemo();
    } catch (e) { toast(problemText(e), 'bad'); }
  });
  $('demo-stop').addEventListener('click', async () => {
    try {
      await api('/api/admin/demo/simulation/stop', { method: 'POST' });
      toast('Поток заявок отдела остановлен');
      await refreshDemo();
    } catch (e) { toast(problemText(e), 'bad'); }
  });

  $('demo-order-sample').addEventListener('click', () => orderForm?.sample());
  $('demo-order-form').addEventListener('submit', (event) => { event.preventDefault(); sendOrder(); });
}

/** После смены правил или отдела форма ручной заявки строится заново. */
export function invalidateDemoConfig() {
  config = null;
  $('demo-order-result').replaceChildren(el('p', 'Решение по заявке появится здесь.', 'muted'));
}
