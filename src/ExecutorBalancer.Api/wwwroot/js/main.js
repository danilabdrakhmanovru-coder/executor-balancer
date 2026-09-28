// Точка входа: вход, отдел, разделы и вкладки с учётом роли, периодическое обновление активной вкладки.
import { api, onUnauthorized, problemText } from './api.js';
import { $, el, icon } from './dom.js';
import { setSession, session, can, demoAdmin, ROLE_TITLE } from './session.js';
import { initUsers, refreshUsers } from './users.js';
import { initOverview, refreshOverview, resetOverview, setOverviewDemo } from './overview.js';
import { initAnalytics, refreshAnalytics, exportLink } from './analytics.js';
import { initConstructor, refreshConstructor, currentConfig } from './constructor.js';
import { buildPreviewForm, fillSample } from './preview.js';
import { initAudit, refreshAudit, resetAudit } from './audit.js';
import { initEditor } from './editor.js';
import { initDemo, refreshDemo, invalidateDemoConfig } from './demo.js';
import { refreshExecutors, setExecutorsEditable, initExecutors } from './executors.js';
import { refreshProfile, setProfileEditable } from './profile.js';
import { renderPresets } from './presets.js';
import { initDepartments, loadDepartments } from './department.js';
import { initDepartmentsAdmin, refreshDepartments, setDepartmentsDemo } from './departments.js';
import { initMotivation, refreshMotivation } from './motivation.js';
import { refreshStart, setStartDemo } from './start.js';
import { initOrders, refreshOrders } from './orders.js';
import { initInsights, refreshInsights } from './insights.js';

const TABS = {
  start: { refresh: refreshStart, every: 3000 },
  orders: { refresh: refreshOrders, every: 5000 },
  overview: { refresh: refreshOverview, every: 2000 },
  executors: { refresh: refreshExecutors, every: 5000 },
  profile: { refresh: () => refreshProfile(profileId), every: 5000 },
  analytics: { refresh: refreshAnalytics, every: 15000 },
  insights: { refresh: refreshInsights, every: 5000 },
  constructor: {
    refresh: async () => {
      await refreshConstructor();
      await renderPresets($('constructor-presets'), async () => {
        await refreshConstructor();
        invalidateDemoConfig();
        await loadDepartments(); // сфера отдела в шапке
      }).catch((e) => { $('constructor-presets').textContent = problemText(e); });
    },
  },
  preview: { refresh: async () => { await refreshConstructor(); buildPreviewForm(currentConfig()); } },
  motivation: { refresh: refreshMotivation },
  departments: { refresh: refreshDepartments },
  audit: { refresh: refreshAudit },
  users: { refresh: refreshUsers },
  demo: { refresh: refreshDemo, every: 2000 },
};
/**
 * Разделы меню и их вкладки: [вкладка, название, иконка, минимальная роль]. Сверху — пять разделов,
 * внутри раздела — подменю; вкладки, недоступные роли, не показываются (права всё равно проверяет сервер).
 */
const GROUPS = {
  home: [['start', 'Как это работает', 'route', 'Viewer']],
  work: [['overview', 'Мониторинг', 'layout-dashboard', 'Viewer'], ['orders', 'Заявки', 'file-text', 'Viewer'],
    ['executors', 'Сотрудники', 'users', 'Viewer']],
  analysis: [['analytics', 'Аналитика', 'chart-bar', 'Viewer'], ['insights', 'Разбор заявок', 'sparkles', 'Viewer']],
  settings: [['constructor', 'Параметры и правила', 'adjustments', 'Viewer'], ['preview', 'Проверка заявки', 'zoom-check', 'Viewer'],
    ['motivation', 'Рейтинг и сверхнорма', 'trophy', 'Viewer'], ['departments', 'Отделы', 'building', 'Viewer'],
    ['users', 'Пользователи', 'shield-lock', 'Admin'], ['audit', 'Журнал', 'history', 'Manager']],
  demo: [['demo', 'Тестовый стенд', 'player-play', 'Admin']],
};
// страница сотрудника живёт в разделе «Работа», рядом со списком
const groupOf = (tab) => (tab === 'profile' ? 'work' : Object.keys(GROUPS).find((g) => GROUPS[g].some(([t]) => t === tab)));
const allowed = (tab) => tab === 'profile' || Object.values(GROUPS).flat()
  .some(([t, , , role]) => t === tab && can(role) && (t !== 'demo' || demoAdmin()));
const lastInGroup = {};

let active = 'start';
let profileId = null;
// страница сотрудника: #executor-15
const PROFILE_HASH = /^executor-(\d{1,18})$/;
let timer = null;
let busy = false;
let again = false;

async function refresh() {
  // обновление уже идёт (например, по таймеру) — повторим сразу после него, иначе смена вкладки
  // или отдела могла бы остаться с данными прежней
  if (busy) { again = true; return; }
  busy = true;
  try {
    await TABS[active].refresh();
    $('updated').textContent = `обновлено ${new Date().toLocaleTimeString('ru-RU')}`;
  } catch (e) {
    if (e.status !== 401) $('updated').textContent = 'нет связи с сервером';
  } finally {
    busy = false;
    if (again) { again = false; refresh(); }
  }
}

function schedule() {
  clearInterval(timer);
  timer = TABS[active].every ? setInterval(refresh, TABS[active].every) : null;
}

function renderSubnav(group) {
  const tabs = GROUPS[group].filter(([t]) => allowed(t));
  const nav = $('subnav');
  nav.classList.toggle('hidden', tabs.length < 2);
  nav.replaceChildren(...tabs.map(([t, title, iconName]) => {
    const b = el('button', null, `btn${t === active || (active === 'profile' && t === 'executors') ? ' active' : ''}`);
    b.type = 'button';
    b.dataset.tab = t;
    b.append(icon(iconName), title);
    return b;
  }));
}

function show(tab) {
  const profile = PROFILE_HASH.exec(tab || '');
  if (profile) profileId = Number(profile[1]);
  active = profile ? 'profile' : TABS[tab] && tab !== 'profile' && allowed(tab) ? tab : 'start';
  const group = groupOf(active);
  lastInGroup[group] = active === 'profile' ? 'executors' : active;
  for (const b of $('tabs').querySelectorAll('.tab')) b.classList.toggle('active', b.dataset.group === group);
  renderSubnav(group);
  for (const name of Object.keys(TABS)) $(`tab-${name}`).classList.toggle('hidden', name !== active);
  const hash = active === 'profile' ? `#executor-${profileId}` : `#${active}`;
  if (location.hash !== hash) history.replaceState(null, '', hash);
  refresh();
  schedule();
}

/** Раздел в меню: открывается вкладка, на которой были в прошлый раз, иначе первая доступная. */
function showGroup(group) {
  const remembered = lastInGroup[group];
  show(remembered && allowed(remembered) ? remembered : GROUPS[group].find(([t]) => allowed(t))?.[0]);
}

function showLogin() {
  clearInterval(timer);
  timer = null;
  for (const d of document.querySelectorAll('dialog[open]')) d.close();
  $('app').classList.add('hidden');
  $('login').classList.remove('hidden');
  $('password').focus();
  showGuestOption();
}

/** Кнопка «Войти как гость» — если гостевой вход включён на сервере (GUEST_ACCESS в .env). */
async function showGuestOption() {
  let guest = null;
  try { ({ guest } = await api('/api/auth/options')); } catch { /* нет связи — кнопку не показываем */ }
  $('guest-box').classList.toggle('hidden', !guest);
  if (!guest) return;
  $('guest-text').textContent = guest === 'Manager' ? 'Войти как гость — можно пробовать' : 'Войти как гость — только просмотр';
  $('guest-hint').textContent = guest === 'Manager'
    ? 'Без пароля, с правами руководителя: мониторинг, аналитика, перерывы и увольнение сотрудников, «больше нормы», ИИ-разбор. '
      + 'Параметры, правила, отделы, пользователи и тестовый стенд — только у администратора. Демо можно вернуть в исходное '
      + 'кнопкой в «Сотрудниках»; через 30 минут без изменений оно возвращается само.'
    : 'Без пароля, только просмотр: мониторинг, заявки, сотрудники, аналитика и настройки — изменить ничего нельзя.';
}

async function showApp(me) {
  setSession(me);
  const { demo } = session();
  $('user-name').textContent = session().name;
  const role = ROLE_TITLE[session().role];
  // имя «Администратор» и роль «администратор» — одно и то же: второй строкой роль не повторяем
  $('user-role').textContent = session().name.toLowerCase() === role ? session().login : role;
  $('login').classList.add('hidden');
  $('app').classList.remove('hidden');
  await loadDepartments();
  exportLink();
  // демо-режим (Demo:Enabled) — значок у всех; тестовый стенд и правка сотрудников через эмулятор — у администратора
  setExecutorsEditable(demoAdmin());
  setOverviewDemo(demoAdmin());
  setProfileEditable(demoAdmin());
  setStartDemo(demoAdmin());
  setDepartmentsDemo(demoAdmin());
  $('tab-button-demo').classList.toggle('hidden', !demoAdmin());
  $('demo-badge').classList.toggle('hidden', !demo);
  for (const node of document.querySelectorAll('.demo-only')) node.classList.toggle('hidden', !demo);
  show(location.hash.slice(1));
}

// тема: светлая по умолчанию (лучше читается на проекторе), выбор запоминается в браузере
let theme = 'light';
try { theme = localStorage.getItem('eb.theme') === 'dark' ? 'dark' : 'light'; } catch { /* приватный режим */ }
document.documentElement.setAttribute('data-bs-theme', theme);
const toTop = () => window.scrollTo({ top: 0, behavior: matchMedia('(prefers-reduced-motion: reduce)').matches ? 'auto' : 'smooth' });
$('scroll-top').addEventListener('click', toTop);
// плавающая «Наверх» — только когда вернуться вверх уже далеко; проверка раз в кадр, без лишней работы при прокрутке
let scrollQueued = false;
window.addEventListener('scroll', () => {
  if (scrollQueued) return;
  scrollQueued = true;
  requestAnimationFrame(() => {
    scrollQueued = false;
    $('scroll-top').classList.toggle('visible', window.scrollY > window.innerHeight * 1.5);
  });
}, { passive: true });
$('theme').addEventListener('click', () => {
  theme = theme === 'light' ? 'dark' : 'light';
  document.documentElement.setAttribute('data-bs-theme', theme);
  try { localStorage.setItem('eb.theme', theme); } catch { /* приватный режим */ }
});

onUnauthorized(showLogin);

$('login-form').addEventListener('submit', async (event) => {
  event.preventDefault();
  $('login-error').textContent = '';
  try {
    await api('/api/auth/login', { method: 'POST', body: { login: $('login-name').value.trim(), password: $('password').value } });
    await showApp(await api('/api/auth/me'));
  } catch (e) {
    $('login-error').textContent = e.status === 429 ? 'Слишком много попыток, подождите минуту' : 'Неверный логин или пароль';
  } finally {
    $('password').value = '';
  }
});

$('guest-login').addEventListener('click', async () => {
  $('login-error').textContent = '';
  try {
    await api('/api/auth/guest', { method: 'POST' });
    await showApp(await api('/api/auth/me'));
  } catch (e) {
    $('login-error').textContent = e.status === 429 ? 'Слишком много попыток, подождите минуту' : 'Гостевой вход сейчас выключен';
    showGuestOption();
  }
});

$('logout').addEventListener('click', async () => {
  try { await api('/api/auth/logout', { method: 'POST' }); } finally { showLogin(); }
});

$('tabs').addEventListener('click', (event) => {
  const group = event.target.closest('.tab')?.dataset.group;
  if (group) showGroup(group);
});
$('subnav').addEventListener('click', (event) => {
  const tab = event.target.closest('button')?.dataset.tab;
  if (tab) show(tab);
});

/** Другой отдел: кэши прежнего отдела сбрасываются, текущая вкладка перечитывается. */
function departmentChanged() {
  resetOverview();
  resetAudit();
  invalidateDemoConfig();
  exportLink();
  for (const d of document.querySelectorAll('dialog[open]')) d.close();
  // сотрудник принадлежит одному отделу — в другом отделе открываем список
  if (active === 'profile') { show('executors'); return; }
  refresh();
}

window.addEventListener('hashchange', () => { if (!$('app').classList.contains('hidden')) show(location.hash.slice(1)); });
document.addEventListener('visibilitychange', () => { if (!document.hidden && timer) refresh(); });

initEditor();
initOverview();
initAnalytics(refresh);
initConstructor(invalidateDemoConfig);
initAudit();
initDemo();
initExecutors();
initOrders();
initInsights();
initUsers();
initDepartments(departmentChanged);
initDepartmentsAdmin();
initMotivation();
$('preview-sample').addEventListener('click', fillSample);

api('/api/auth/me').then(showApp).catch(() => showLogin());
