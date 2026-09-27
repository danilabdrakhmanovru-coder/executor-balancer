// Точка входа: вход, отдел, вкладки, периодическое обновление активной вкладки.
import { api, onUnauthorized, problemText } from './api.js';
import { $ } from './dom.js';
import { initOverview, refreshOverview, resetOverview } from './overview.js';
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
import { initDepartmentsAdmin, refreshDepartments } from './departments.js';
import { initMotivation, refreshMotivation } from './motivation.js';
import { refreshStart, setStartDemo } from './start.js';
import { initOrders, refreshOrders } from './orders.js';

const TABS = {
  start: { refresh: refreshStart, every: 3000 },
  orders: { refresh: refreshOrders, every: 5000 },
  overview: { refresh: refreshOverview, every: 2000 },
  executors: { refresh: refreshExecutors, every: 5000 },
  profile: { refresh: () => refreshProfile(profileId), every: 5000 },
  analytics: { refresh: refreshAnalytics, every: 15000 },
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
  demo: { refresh: refreshDemo, every: 2000 },
};
// разделы «Настроек»: во вкладках одна кнопка, внутри — подменю
const SETTINGS = ['constructor', 'motivation', 'preview', 'departments'];

let active = 'start';
let profileId = null;
// страница сотрудника: #executor-15
const PROFILE_HASH = /^executor-(\d{1,18})$/;
let demoEnabled = false;
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

function show(tab) {
  const profile = PROFILE_HASH.exec(tab || '');
  if (profile) profileId = Number(profile[1]);
  active = profile ? 'profile' : TABS[tab] && tab !== 'profile' && (tab !== 'demo' || demoEnabled) ? tab : 'start';
  const inSettings = SETTINGS.includes(active);
  for (const b of $('tabs').querySelectorAll('.tab')) {
    b.classList.toggle('active', b.dataset.tab === active || (inSettings && b.dataset.group === 'settings')
      || (active === 'profile' && b.dataset.tab === 'executors'));
  }
  for (const b of $('settings-nav').querySelectorAll('button')) b.classList.toggle('active', b.dataset.tab === active);
  $('settings-nav').classList.toggle('hidden', !inSettings);
  for (const name of Object.keys(TABS)) $(`tab-${name}`).classList.toggle('hidden', name !== active);
  const hash = active === 'profile' ? `#executor-${profileId}` : `#${active}`;
  if (location.hash !== hash) history.replaceState(null, '', hash);
  refresh();
  schedule();
}

function showLogin() {
  clearInterval(timer);
  timer = null;
  for (const d of document.querySelectorAll('dialog[open]')) d.close();
  $('app').classList.add('hidden');
  $('login').classList.remove('hidden');
  $('password').focus();
}

async function showApp() {
  $('login').classList.add('hidden');
  $('app').classList.remove('hidden');
  await loadDepartments();
  exportLink();
  // пульт демонстрации включается настройкой Demo:Enabled; выключен — вкладки нет
  demoEnabled = await api('/api/admin/demo/status').then(() => true, (e) => e.status === 502);
  setExecutorsEditable(demoEnabled);
  setProfileEditable(demoEnabled);
  setStartDemo(demoEnabled);
  $('tab-button-demo').classList.toggle('hidden', !demoEnabled);
  // демо-режим виден сразу: значок в шапке и пояснения про тестовый стенд; в бою их нет
  $('demo-badge').classList.toggle('hidden', !demoEnabled);
  for (const node of document.querySelectorAll('.demo-only')) node.classList.toggle('hidden', !demoEnabled);
  show(location.hash.slice(1));
}

// тема: светлая по умолчанию (лучше читается на проекторе), выбор запоминается в браузере
let theme = 'light';
try { theme = localStorage.getItem('eb.theme') === 'dark' ? 'dark' : 'light'; } catch { /* приватный режим */ }
document.documentElement.setAttribute('data-bs-theme', theme);
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
    await api('/api/auth/login', { method: 'POST', body: { password: $('password').value } });
    await showApp();
  } catch (e) {
    $('login-error').textContent = e.status === 429 ? 'Слишком много попыток, подождите минуту' : 'Неверный пароль';
  } finally {
    $('password').value = '';
  }
});

$('logout').addEventListener('click', async () => {
  try { await api('/api/auth/logout', { method: 'POST' }); } finally { showLogin(); }
});

$('tabs').addEventListener('click', (event) => {
  const tab = event.target.closest('.tab')?.dataset.tab;
  if (tab) show(tab);
});
$('settings-nav').addEventListener('click', (event) => {
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
initDepartments(departmentChanged);
initDepartmentsAdmin();
initMotivation();
$('preview-sample').addEventListener('click', fillSample);

api('/api/auth/me').then(showApp).catch(() => showLogin());
