// Настройки → Рейтинг и сверхнорма: как считается балл, потолок режима «больше нормы» и пороги качества.
// Доли (0–1) показываются в процентах — так понятнее; на сервер уходят долями.
import { api, problemText } from './api.js';
import { $, field, input, toast } from './dom.js';
import { can } from './session.js';

const FIELDS = [
  ['fastCloseSeconds', 'Подозрительно быстрое закрытие, секунд', 'seconds', 'Закрытие быстрее — признак работы «на скорость». 0 — не проверять.'],
  ['fastClosePenalty', 'Штраф за быстрое закрытие, %', 'share', 'На столько снижается коэффициент качества заявки.'],
  ['reworkPenalty', 'Штраф за каждую доработку, %', 'share', 'Заявка вернулась на доработку — работа сделана не с первого раза.'],
  ['maxExtraPercent', 'Потолок режима «больше нормы», %', 'percent', 'Сколько сотрудник может взять сверх своей нормы. 0 — режим выключен в отделе.'],
  ['qualityThreshold', 'Приостановить режим при качестве ниже, %', 'share', 'Качество за 7 дней: баллы / максимально возможные баллы.'],
  ['heavyQualityThreshold', 'Сложные заявки сверх нормы — при качестве от, %', 'share', 'Не ниже порога приостановки.'],
  ['heavyWeight', 'Сложная заявка — вес от', 'weight', 'Например, VIP-клиент или крупная сумма (вес задаётся в «Параметрах и правилах»).'],
  ['minOnDutyPercent', 'Минимум сотрудников на работе, %', 'percent',
    'Отправить на перерыв или уволить сверх этого нельзя. Кроме того, для каждого вида заявок всегда остаётся тот, кто умеет его брать.'],
];

let controls = {};

const toShown = (kind, value) => (kind === 'share' ? Math.round(value * 1000) / 10 : value);
const fromShown = (kind, value) => {
  const n = Number(String(value).replace(',', '.'));
  return kind === 'share' ? Math.round(n * 10) / 1000 : n;
};

export async function refreshMotivation() {
  const m = await api('/api/admin/motivation');
  controls = {};
  $('motivation-fields').replaceChildren(...FIELDS.map(([key, label, kind, hint]) => {
    const attrs = kind === 'weight' ? { min: '0.1', max: '1000', step: '0.1' }
      : kind === 'seconds' ? { min: '0', max: '86400', step: '1' } : { min: '0', max: '100', step: '1' };
    const control = input('number', toShown(kind, m[key]), { ...attrs, required: '' });
    control.disabled = !can('Admin'); // остальные роли видят настройки, менять их может администратор
    controls[key] = [control, kind];
    return field(label, control, hint);
  }));
  $('motivation-error').textContent = '';
}

async function save(event) {
  event.preventDefault();
  const body = Object.fromEntries(Object.entries(controls).map(([key, [control, kind]]) => [key, fromShown(kind, control.value)]));
  $('motivation-save').disabled = true;
  $('motivation-error').textContent = '';
  try {
    await api('/api/admin/motivation', { method: 'PUT', body });
    toast('Настройки мотивации сохранены — действуют со следующей заявки');
    await refreshMotivation();
  } catch (e) {
    $('motivation-error').textContent = problemText(e);
  } finally {
    $('motivation-save').disabled = false;
  }
}

export function initMotivation() { $('motivation-form').addEventListener('submit', save); }
