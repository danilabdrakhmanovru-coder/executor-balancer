// Мастер своей сферы: параметры заявки и то, как по ним подбирать сотрудника. Сервер сам создаёт парные
// параметры сотрудника, правила подбора и веса и проверяет их тем же компилятором, что и конструктор.
import { el, field, input, select, button, checkbox } from './dom.js';

const TYPES = [['Enum', 'выбор из списка'], ['Number', 'число'], ['Boolean', 'да / нет'], ['String', 'текст']];
const MATCHES = {
  Enum: [['Skills', 'сотрудник должен работать с этим значением'], ['None', 'не влияет на выбор сотрудника']],
  Number: [['Max', 'не больше допуска сотрудника (максимума)'], ['Min', 'не меньше минимума сотрудника'],
    ['None', 'не влияет на выбор сотрудника']],
  Boolean: [['None', 'не влияет на выбор (можно задать вес)']],
  String: [['None', 'не влияет на выбор (можно задать вес)']],
};
const EXECUTOR_HINT = { Skills: 'например, «Языки» — список, что сотрудник умеет', Max: 'например, «Допуск по сумме»', Min: 'например, «Минимальный стаж»' };

/** Пример — чтобы было с чего начать: запись в клинику. */
const EXAMPLE = {
  title: 'Клиника: запись к врачу',
  params: [
    { label: 'Специальность', type: 'Enum', options: 'терапевт\nхирург\nлор\nокулист', match: 'Skills', executorLabel: 'Специальности' },
    { label: 'Язык пациента', type: 'Enum', options: 'русский\nтатарский\nбашкирский', match: 'Skills', executorLabel: 'Языки' },
    { label: 'Возраст пациента', type: 'Number', match: 'Max', executorLabel: 'Принимает пациентов до, лет', heavy: '65', weight: '2' },
    { label: 'Срочно', type: 'Boolean', match: 'None', heavy: 'true', weight: '3' },
  ],
};

function paramRow(data, onRemove, onChange) {
  const box = el('div', null, 'sphere-param');
  const label = input('text', data.label ?? '', { maxlength: '120', placeholder: 'например, Язык клиента' });
  const type = select(TYPES, data.type ?? 'Enum');
  const options = el('textarea', null, 'form-control');
  options.rows = 3;
  options.placeholder = 'по одному значению в строке';
  options.value = data.options ?? '';
  const match = select([], '');
  const executorLabel = input('text', data.executorLabel ?? '', { maxlength: '120', placeholder: 'подберётся сам' });
  const heavy = checkbox(Boolean(data.weight), 'Сложная заявка — весит больше');
  const weight = input('number', data.weight ?? '2', { min: '0.1', max: '1000', step: 'any' });
  const heavyValue = input('text', data.heavy ?? '', { maxlength: '100', placeholder: 'значение' });
  const heavyEnum = select([], '');

  const optionsField = field('Значения', options);
  const executorField = field('Параметр сотрудника', executorLabel);
  const heavyBox = el('div', null, 'sphere-heavy');
  const heavyValueField = field('Когда', heavyValue);
  const heavyEnumField = field('Когда значение', heavyEnum);
  heavyBox.append(field('Вес', weight), heavyValueField, heavyEnumField);

  const list = () => options.value.split('\n').map((v) => v.trim()).filter(Boolean);
  function refresh() {
    const t = type.value;
    optionsField.classList.toggle('hidden', t !== 'Enum');
    const previous = match.value || data.match;
    match.replaceChildren(...MATCHES[t].map(([v, text]) => { const o = el('option', text); o.value = v; return o; }));
    match.value = MATCHES[t].some(([v]) => v === previous) ? previous : MATCHES[t][0][0];
    executorField.classList.toggle('hidden', match.value === 'None');
    executorLabel.placeholder = EXECUTOR_HINT[match.value] || 'подберётся сам';
    heavyBox.classList.toggle('hidden', !heavy.box.checked);
    heavyValueField.classList.toggle('hidden', t === 'Enum' || t === 'Boolean');
    heavyEnumField.classList.toggle('hidden', t !== 'Enum');
    heavyValueField.querySelector('.form-label').textContent = t === 'Number' ? 'Когда значение не меньше' : 'Когда значение равно';
    const keep = heavyEnum.value || data.heavy;
    heavyEnum.replaceChildren(...list().map((v) => { const o = el('option', v); o.value = v; return o; }));
    if (list().includes(keep)) heavyEnum.value = keep;
    onChange();
  }
  for (const control of [type, match, options, heavy.box, label, executorLabel]) control.addEventListener('input', refresh);
  type.addEventListener('change', refresh);
  match.addEventListener('change', refresh);

  const head = el('div', null, 'sphere-param-head');
  head.append(field('Параметр заявки', label), field('Тип', type),
    button('Убрать', () => { box.remove(); onRemove(); }, 'btn btn-sm btn-ghost-danger', 'trash'));
  box.append(head, optionsField, field('Как подбирать сотрудника', match), executorField, heavy.wrap, heavyBox);
  refresh();

  return {
    box,
    read() {
      const t = type.value;
      let heavyJson = null;
      if (heavy.box.checked) {
        heavyJson = t === 'Enum' ? heavyEnum.value : t === 'Boolean' ? true
          : t === 'Number' ? Number(String(heavyValue.value).replace(',', '.')) : heavyValue.value.trim();
      }
      return {
        label: label.value.trim(),
        type: t,
        options: t === 'Enum' ? list() : null,
        match: match.value,
        executorLabel: executorLabel.value.trim() || null,
        heavyValue: heavyJson,
        heavyWeight: heavy.box.checked ? Number(String(weight.value).replace(',', '.')) : null,
      };
    },
    /** Какое правило получится — словами, чтобы было видно до сохранения. */
    describe() {
      const p = this.read();
      if (!p.label) return null;
      const who = p.executorLabel || (p.match === 'Skills' ? `${p.label}: с чем работает` : p.match === 'Max' ? `${p.label}: максимум` : `${p.label}: минимум`);
      const rule = p.match === 'Skills' ? `${p.label} заявки — одно из — «${who}» сотрудника`
        : p.match === 'Max' ? `${p.label} заявки — не больше — «${who}» сотрудника`
          : p.match === 'Min' ? `${p.label} заявки — не меньше — «${who}» сотрудника` : null;
      const w = p.heavyWeight ? `вес ${p.heavyWeight}, если ${p.label} ${p.type === 'Number' ? 'не меньше' : '='} ${p.heavyValue}` : null;
      return [rule && `Правило: ${rule}`, w && `Вес: ${w}`].filter(Boolean).join(' · ') || `${p.label}: только для отчётов`;
    },
  };
}

/** Построитель сферы для диалога: nodes — разметка, read() — тело запроса. */
export function sphereBuilder() {
  const wrap = el('div', null, 'sphere-builder');
  const title = input('text', '', { maxlength: '120', placeholder: 'например, Клиника: запись к врачу' });
  const rows = el('div', null, 'sphere-params');
  const summary = el('ul', null, 'sphere-summary');
  let items = [];

  function update() {
    items = items.filter((i) => i.box.isConnected);
    summary.replaceChildren(...items.map((i) => i.describe()).filter(Boolean).map((t) => el('li', t)));
  }
  function add(data = {}) {
    const item = paramRow(data, update, update);
    items.push(item);
    rows.append(item.box);
    update();
  }
  function example() {
    title.value = EXAMPLE.title;
    rows.replaceChildren();
    items = [];
    EXAMPLE.params.forEach(add);
  }

  const tools = el('div', null, 'btn-list');
  tools.append(button('Параметр заявки', () => add(), 'btn btn-sm', 'plus'), button('Заполнить примером (клиника)', example, 'btn btn-sm', 'wand'));
  wrap.append(
    el('p', 'Опишите, чем заявки этой сферы отличаются друг от друга, и как по этому выбирать сотрудника. '
      + 'Параметры сотрудника, правила подбора и веса создадутся сами; потом их можно поправить в «Параметрах и правилах».', 'hint'),
    field('Название сферы', title), rows, tools, el('div', 'Что получится:', 'form-label mt-3'), summary,
  );
  add();
  return {
    nodes: [wrap],
    read: () => ({ title: title.value.trim(), params: items.map((i) => i.read()) }),
  };
}
