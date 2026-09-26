// Точка входа: вход, вкладки, периодическое обновление активной вкладки.
import { api, onUnauthorized, problemText } from './api.js';
import { $ } from './dom.js';
import { initOverview, refreshOverview } from './overview.js';
import { initAnalytics, refreshAnalytics } from './analytics.js';
import { initConstructor, refreshConstructor, currentConfig } from './constructor.js';
import { buildPreviewForm, fillSample } from './preview.js';
import { initAudit, refreshAudit } from './audit.js';
import { initEditor } from './editor.js';
import { initDemo, refreshDemo, invalidateDemoConfig } from './demo.js';
import { refreshExecutors, setExecutorsEditable } from './executors.js';
import { renderPresets } from './presets.js';

const TABS = {
  demo: { refresh: refreshDemo, every: 2000 },
  overview: { refresh: refreshOverview, every: 2000 },
  executors: { refresh: refreshExecutors, every: 5000 },
  analytics: { refresh: refreshAnalytics, every: 15000 },
  constructor: {
    refresh: async () => {
      await refreshConstructor();
      await renderPresets($('constructor-presets'), async () => { await refreshConstructor(); invalidateDemoConfig(); })
        .catch((e) => { $('constructor-presets').textContent = problemText(e); });
    },
  },
  preview: { refresh: async () => { await refreshConstructor(); buildPreviewForm(currentConfig()); } },
  audit: { refresh: refreshAudit },
};

let active = 'overview';
let demoEnabled = false;
let timer = null;
let busy = false;

async function refresh() {
  if (busy) return;
  busy = true;
  try {
    await TABS[active].refresh();
    $('updated').textContent = `обновлено ${new Date().toLocaleTimeString('ru-RU')}`;
  } catch (e) {
    if (e.status !== 401) $('updated').textContent = 'нет связи с сервером';
  } finally {
    busy = false;
  }
}

function schedule() {
  clearInterval(timer);
  timer = TABS[active].every ? setInterval(refresh, TABS[active].every) : null;
}

function show(tab) {
  const fallback = demoEnabled ? 'demo' : 'overview';
  active = TABS[tab] && (tab !== 'demo' || demoEnabled) ? tab : fallback;
  for (const b of $('tabs').querySelectorAll('.tab')) b.classList.toggle('active', b.dataset.tab === active);
  for (const name of Object.keys(TABS)) $(`tab-${name}`).classList.toggle('hidden', name !== active);
  if (location.hash !== `#${active}`) history.replaceState(null, '', `#${active}`);
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
  // пульт демонстрации включается настройкой Demo:Enabled; выключен — вкладки нет
  demoEnabled = await api('/api/admin/demo/status').then(() => true, (e) => e.status === 502);
  setExecutorsEditable(demoEnabled);
  $('tab-button-demo').classList.toggle('hidden', !demoEnabled);
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

window.addEventListener('hashchange', () => { if (!$('app').classList.contains('hidden')) show(location.hash.slice(1)); });
document.addEventListener('visibilitychange', () => { if (!document.hidden && timer) refresh(); });

initEditor();
initOverview();
initAnalytics(refresh);
initConstructor(invalidateDemoConfig);
initAudit();
initDemo(() => {});
$('preview-sample').addEventListener('click', fillSample);

api('/api/auth/me').then(showApp).catch(() => showLogin());
