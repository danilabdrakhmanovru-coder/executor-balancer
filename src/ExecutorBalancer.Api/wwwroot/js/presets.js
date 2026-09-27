// Шаблоны сфер: одна кнопка — и параметры, правила и веса другой отрасли. Код распределения тот же.
import { api, problemText } from './api.js';
import { el, badge, button, toast, icon } from './dom.js';
import { can } from './session.js';
import { currentDepartment } from './department.js';

const ICONS = { bank: 'building-bank', support: 'headset', logistics: 'truck', ecommerce: 'shopping-cart' };

export async function renderPresets(container, onApplied) {
  const presets = await api('/api/admin/presets');
  container.replaceChildren(...presets.map((p) => {
    const card = el('div', null, `preset${p.isCurrent ? ' current' : ''}`);
    const head = el('div', null, 'preset-head');
    const title = el('span', null, 'preset-title');
    title.append(icon(ICONS[p.id] || 'adjustments'), p.title);
    head.append(title);
    if (p.isCurrent) head.append(badge('сейчас', 'ok'));
    card.append(head, el('p', p.description, 'muted'));
    card.append(el('p', `Заявка: ${p.orderFields.join(', ')}`, 'fine'));
    card.append(el('p', `Исполнитель: ${p.executorFields.join(', ')}`, 'fine'));
    if (!p.isCurrent && can('Admin')) {
      card.append(button('Применить', async () => {
        // называем отдел явно: шаблон заменяет настройки именно того отдела, что выбран в шапке
        const department = currentDepartment()?.name ?? 'этого отдела';
        if (!confirm(`Отдел «${department}» станет сферой «${p.title}»: его параметры, правила и веса заменятся шаблоном, а навыки нынешних сотрудников перестанут подходить. Другие отделы не изменятся. Продолжить?`)) return;
        try {
          await api(`/api/admin/presets/${encodeURIComponent(p.id)}/apply`, { method: 'POST' });
          toast(`Применён шаблон «${p.title}». Заведите сотрудников отдела заново — у прежних другие навыки.`);
          await renderPresets(container, onApplied);
          await onApplied?.();
        } catch (e) {
          toast(problemText(e), 'bad');
        }
      }, 'btn btn-primary btn-sm', 'check'));
    }
    return card;
  }));
}
