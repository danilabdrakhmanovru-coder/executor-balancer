// Пробная проверка: кому ушла бы заявка с такими параметрами. Ничего не назначает.
import { api, problemText } from './api.js';
import { $, el, field, input, select } from './dom.js';
import { renderExplanation } from './explain.js';

let controls = [];

function control(f) {
  switch (f.type) {
    case 'Enum':
      return f.options.length ? select([['', '—'], ...f.options.map((o) => [o, o])], '') : input('text', '', { maxlength: '500' });
    case 'Boolean':
      return select([['', '—'], ['true', 'да'], ['false', 'нет']], '');
    case 'Number':
      return input('number', '', { step: 'any' });
    case 'Array':
      return input('text', '', { placeholder: f.options.length ? f.options.join(', ') : 'через запятую' });
    default:
      return input('text', '', { maxlength: '500' });
  }
}

function read(f, node) {
  const raw = node.value.trim();
  if (raw === '') return undefined;
  if (f.type === 'Number') {
    const n = Number(raw.replace(',', '.'));
    if (!Number.isFinite(n)) throw new Error(`${f.label}: введите число`);
    return n;
  }
  if (f.type === 'Boolean') return raw === 'true';
  if (f.type === 'Array') return raw.split(/[,;]/).map((s) => s.trim()).filter(Boolean);
  return raw;
}

export function buildPreviewForm(config) {
  const form = $('preview-form');
  const fields = config.fields.filter((f) => f.owner === 'Order');
  controls = fields.map((f) => [f, control(f)]);
  const parent = input('number', '', { min: '1', step: '1', placeholder: 'необязательно' });
  const submit = el('button', 'Кому уйдёт заявка?');
  submit.type = 'submit';
  form.replaceChildren(
    ...controls.map(([f, node]) => field(`${f.label} (${f.key})`, node)),
    field('Родительская заявка №', parent, 'Если указана — заявка уйдёт исполнителю родительской, если он подходит.'),
    submit,
  );
  form.onsubmit = async (event) => {
    event.preventDefault();
    const result = $('preview-result');
    try {
      const attributes = {};
      for (const [f, node] of controls) {
        const value = read(f, node);
        if (value !== undefined) attributes[f.key] = value;
      }
      const parentId = parent.value ? Number(parent.value) : null;
      submit.disabled = true;
      const explanation = await api('/api/admin/preview', { method: 'POST', body: { parentId, attributes } });
      result.replaceChildren(renderExplanation(explanation));
    } catch (e) {
      result.replaceChildren(el('p', e instanceof Error && !('status' in e) ? e.message : problemText(e), 'error'));
    } finally {
      submit.disabled = false;
    }
  };
}

export function fillSample() {
  for (const [f, node] of controls) {
    if (f.type === 'Enum' && f.options.length) node.value = f.options[Math.floor(Math.random() * f.options.length)];
    else if (f.type === 'Number') node.value = String(Math.round(10000 + Math.random() * 900000));
    else if (f.type === 'Boolean') node.value = Math.random() < 0.5 ? 'true' : 'false';
    else if (f.type === 'Array' && f.options.length) node.value = f.options[0];
  }
}
