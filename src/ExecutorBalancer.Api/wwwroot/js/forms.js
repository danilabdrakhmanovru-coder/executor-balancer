// Форма по справочнику параметров: поле ввода под тип параметра. Используется в проверке заявки,
// в ручной заявке пульта демонстрации и в редакторе исполнителя.
import { el, field, input, select, checkbox } from './dom.js';

function control(f, value) {
  switch (f.type) {
    case 'Enum':
      if (!f.options.length) return { node: input('text', value, { maxlength: '500' }) };
      return { node: select([['', '— не указано —'], ...f.options.map((o) => [o, o])], value ?? '') };
    case 'Boolean':
      return { node: select([['', '— не указано —'], ['true', 'да'], ['false', 'нет']], value === undefined || value === null ? '' : String(value)) };
    case 'Number':
      return { node: input('number', value, { step: 'any' }) };
    case 'Array': {
      if (!f.options.length) return { node: input('text', Array.isArray(value) ? value.join(', ') : '', { placeholder: 'через запятую' }) };
      const wrap = el('div', null, 'value-editor checks');
      const chosen = new Set(Array.isArray(value) ? value.map(String) : []);
      const boxes = f.options.map((o) => { const c = checkbox(chosen.has(o), o); wrap.append(c.wrap); return [o, c.box]; });
      return { node: wrap, boxes };
    }
    default:
      return { node: input('text', value, { maxlength: '500' }) };
  }
}

function read(f, c) {
  if (c.boxes) {
    const values = c.boxes.filter(([, b]) => b.checked).map(([o]) => o);
    return values.length ? values : undefined;
  }
  const raw = c.node.value.trim();
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

/**
 * Поля формы для параметров одного владельца (Order или Executor).
 * read() возвращает объект «ключ → значение» без незаполненных полей.
 */
export function attributeForm(config, owner, values = {}) {
  const fields = config.fields.filter((f) => f.owner === owner);
  const controls = fields.map((f) => [f, control(f, values[f.key])]);
  return {
    nodes: controls.map(([f, c]) => field(f.label, c.node)),
    empty: fields.length === 0,
    read() {
      const result = {};
      for (const [f, c] of controls) {
        const value = read(f, c);
        if (value !== undefined) result[f.key] = value;
      }
      return result;
    },
    sample() {
      for (const [f, c] of controls) {
        if (c.boxes) {
          c.boxes.forEach(([, b], i) => { b.checked = i === 0 || Math.random() < 0.5; });
        } else if (f.type === 'Enum' && f.options.length) {
          c.node.value = f.options[Math.floor(Math.random() * f.options.length)];
        } else if (f.type === 'Number') {
          c.node.value = String(Math.round(1 + Math.random() * 99) * 1000);
        } else if (f.type === 'Boolean') {
          c.node.value = Math.random() < 0.5 ? 'true' : 'false';
        }
      }
    },
  };
}
