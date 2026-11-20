# SwayWin

**Тайловый оконный менеджер для Windows в духе [Sway](https://swaywm.org/) / i3.**

SwayWin перехватывает обычные окна Win32, раскладывает их плиткой по рабочим столам и управляет ими
Sway-подобными командами и биндами. Конфиг Sway читается как есть — существующий `~/.config/sway/config`
переносится почти без изменений, а всё, что специфично для Linux, аккуратно игнорируется с диагностикой
вместо падения.

```
Win+1 … Win+0       рабочие столы
Win+h/j/k/l         фокус (стрелки тоже работают)
Win+Shift+h/j/k/l   перемещение окна
Win+b / Win+v       разделить горизонтально / вертикально
Win+s / Win+w       stacking / tabbed
Win+f               fullscreen
Alt+перетаскивание  плавающие окна
```

---

## Возможности

- **Иерархическая тайлинг-раскладка** — сплиты, вложенные контейнеры, `tabbed` и `stacking`, resize-режим с шагом в пикселях.
- **Рабочие столы** — до 10 именованных столов на каждый монитор, перенос окон между ними, `workspace_auto_back_and_forth`.
- **Плавающие окна и scratchpad** — `floating toggle`, `floating_modifier`, `move scratchpad` / `scratchpad show`.
- **Fullscreen** — окно занимает весь монитор, а не рабочую область (панель прячется).
- **Мультимониторность** — отдельный активный рабочий стол и панель на каждый выход, маппинг `windows_output`.
- **Панель (bar)** — номера рабочих столов с подсветкой активного/занятого, режим биндов, заголовок окна и часы.
- **Трей** — перезагрузка конфига, открытие лога, выход с восстановлением окон.
- **Лаунчер** — `$menu` (`swaywin-launcher`, `wofi --show run`) с историей запусков; запускаются только Windows-пути, ярлыки и URI, никакого POSIX-шелла.
- **Скриншоты** — полный экран и выделение области, сохранение в каталог из конфига и копирование в буфер обмена.
- **Медиа-клавиши** — громкость и mute вывода/микрофона (Core Audio), яркость ноутбучной матрицы (WMI).
- **Восстановление** — журнал исходных позиций, стилей и DWM-оформления окон; после падения менеджера окна возвращаются на место.
- **Безопасность** — один экземпляр на сессию, IPC только для текущего пользователя, конфиг не исполняет произвольные shell-команды.

## Требования

- Windows 10/11 x64
- [.NET 10 SDK](https://dotnet.microsoft.com/download) (для сборки) или .NET 10 Desktop Runtime (для готовой сборки из `dist`)

## Сборка и запуск

```bat
build.cmd     :: тесты + publish в dist\
launch.cmd    :: запуск dist\SwayWin.exe (при отсутствии — соберёт)
```

Вручную:

```bat
dotnet build SwayWin.slnx -c Release
dotnet publish src\SwayWin.App\SwayWin.App.csproj -c Release -r win-x64 --self-contained false -o dist
dist\SwayWin.exe
```

Выход с восстановлением окон: **меню в трее → Exit and restore windows** или **Ctrl+Alt+Shift+F12**.

## Командная строка

```
SwayWin.exe [--config <файл или каталог>] [--overrides <windows.conf|none>]
            [--no-autostart] [--no-bar] [--keep-titlebars]

SwayWin.exe --check-config [<путь>]   разобрать конфиг, ничего не меняя
SwayWin.exe command "workspace number 2"
SwayWin.exe get_tree | get_workspaces | get_outputs | reload | exit
SwayWin.exe --recover                 восстановить окна после упавшего менеджера
SwayWin.exe --smoke-test              автотест на собственных временных окнах
```

Команды отправляются работающему менеджеру через именованный канал и печатают JSON-ответ.
Команды — обычный Sway-синтаксис: `focus left`, `move container to workspace number 3`,
`layout tabbed`, `floating toggle`, `resize grow width 20px`, `fullscreen toggle`, `kill`, `exec wt.exe` и т.д.

## Конфигурация

По умолчанию загружается `config\config` рядом с исполняемым файлом, затем поверх него — `config\windows.conf`
(Windows-специфичные переопределения). Пути можно задать явно:

```bat
dist\SwayWin.exe --config "C:\Users\me\.config\sway\config" --overrides none
```

Поддерживается разумное подмножество Sway: `set`, `include`, `bindsym`/`bindcode` (включая `--to-code`, `--locked`),
`mode { … }`, `bar { … }`, `for_window`, `assign`, `no_focus`, `exec`/`exec_always`, `gaps`, `smart_gaps`,
`smart_borders`, `hide_edge_borders`, `default_border`, `focus_follows_mouse`, `mouse_warping`,
`workspace_auto_back_and_forth`, `floating_modifier`, критерии окон (`class`, `title`, `app_id`, `floating` и др.).

Пример минимального конфига:

```
set $mod Mod4
set $term wt.exe -w new
gaps inner 6
focus_follows_mouse yes
bindsym $mod+Return exec $term
bindsym $mod+1 workspace number 1
bindsym $mod+Shift+1 move container to workspace number 1
bindsym $mod+q kill
bar { position top; height 24 }
```

`--check-config` печатает разобранные настройки, бинды и предупреждения, не трогая окна и хуки.

### Что не переносится

Linux-специфичные директивы (выключение выходов, `swaybg`, `wpctl`, `brightnessctl`, `grim`/`slurp`, POSIX-шелл)
не исполняются как есть: поддерживаемые действия переводятся в нативные вызовы Windows
(громкость, яркость, скриншоты, лаунчер), остальные попадают в диагностику. Обои, тема оформления приложений
и системный стиль Windows не меняются.

## Устройство проекта

| Путь | Назначение |
| --- | --- |
| `src/SwayWin.Core` | Чистая логика: парсер Sway-конфига (`Configuration/`) и модель раскладки/состояния (`Layout/`). Без Win32. |
| `src/SwayWin.App` | WinForms-хост: хуки ввода, управление окнами Win32, панель, трей, IPC, скриншоты, восстановление. |
| `tests/SwayWin.Tests` | Консольный раннер тестов ядра (35 кейсов: конфиг + раскладка). |
| `config/` | `config` — портируемый Sway-конфиг по умолчанию, `windows.conf` — Windows-переопределения. |

```bat
dotnet run --project tests\SwayWin.Tests\SwayWin.Tests.csproj -c Release   :: 35 тестов
dist\SwayWin.exe --smoke-test                                            :: 8 нативных проверок
```

Логи: `%LOCALAPPDATA%\SwayWin\` (там же журнал восстановления `recovery-<session>.json`).

## Ограничения

- Только Windows x64; сборка под Linux/macOS не поддерживается.
- Некоторые окна не тайлятся намеренно: диалоги, тултипы, окна без рамки, оболочка (панель задач, `Progman`) — они остаются плавающими.
- Приложения с собственным нестандартным хромом могут сопротивляться изменению размера/позиции.
- `Win+L` остаётся системной блокировкой; для `focus right` используется `Win+Right`.
- Произвольные shell-команды из Sway-конфига не выполняются — только известные действия и Windows-исполняемые файлы.

## Лицензия

GPL-3.0 — см. [LICENSE](LICENSE).
