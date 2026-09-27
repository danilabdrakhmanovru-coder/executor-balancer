# Сторонние компоненты

Интерфейс использует открытые компоненты. Файлы лежат в проекте (`src/ExecutorBalancer.Api/wwwroot/vendor`),
ничего не загружается из интернета — строгая политика CSP сохраняется.

| Компонент | Версия | Лицензия | Что взято | Текст лицензии |
|---|---|---|---|---|
| [Tabler](https://tabler.io) | 1.6.0 | MIT | CSS-оформление (`vendor/tabler/tabler.min.css`), JavaScript Tabler не используется | `vendor/tabler/LICENSE` |
| [Tabler Icons](https://tabler.io/icons) | 3.48.0 | MIT | 61 иконка, собраны в спрайт `icons.svg` | `vendor/tabler-icons/LICENSE` |
| [Inter](https://rsms.me/inter/) через Fontsource | 5.3.0 | SIL Open Font License 1.1 | шрифт, начертания 400–700, латиница и кириллица | `vendor/inter/LICENSE` |

Обновление: `npm i @tabler/core @tabler/icons @fontsource/inter` во временной папке и копирование тех же файлов.
Спрайт иконок собирается из `@tabler/icons/icons/outline/<имя>.svg`.
