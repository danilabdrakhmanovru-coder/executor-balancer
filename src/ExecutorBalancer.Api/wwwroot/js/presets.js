// Шаблоны сфер: одна кнопка — и параметры, правила и веса другой отрасли. Код распределения тот же.
import { api, problemText } from './api.js';
import { el, badge, button, toast } from './dom.js';

export async function renderPresets(container, onApplied) {
  const presets = await api('/api/admin/presets');
  container.replaceChildren(...presets.map((p) => {
    const card = el('div', null, `preset${p.isCurrent ? ' current' : ''}`);
    const head = el('div', null, 'preset-head');
    head.append(el('strong', p.title));
    if (p.isCurrent) head.append(badge('сейчас', 'ok'));
    card.append(head, el('p', p.description, 'muted'));
    card.append(el('p', `Заявка: ${p.orderFields.join(', ')}`, 'fine'));
    card.append(el('p', `Исполнитель: ${p.executorFields.join(', ')}`, 'fine'));
    if (!p.isCurrent) {
      card.append(button('Применить', async () => {
        if (!confirm(`Заменить параметры, правила и веса на шаблон «${p.title}»? Изменение попадёт в журнал.`)) return;
        try {
          await api(`/api/admin/presets/${encodeURIComponent(p.id)}/apply`, { method: 'POST' });
          toast(`Применён шаблон «${p.title}». Заведите исполнителей заново — у прежних другие навыки.`);
          await renderPresets(container, onApplied);
          await onApplied?.();
        } catch (e) {
          toast(problemText(e), 'bad');
        }
      }, 'small'));
    }
    return card;
  }));
}
