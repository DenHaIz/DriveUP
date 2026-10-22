<p align="center"><img src="docs/logo.png" width="96" alt="DriveUP">&nbsp;&nbsp;&nbsp;<img src="docs/logo_pro.png" width="96" alt="DriveUP Pro"></p>

<h1 align="center">DriveUP · DriveUP Pro</h1>

<p align="center">Обновление и установка драйверов Windows в одном exe. Pro — плюс настройка системы.<br>Driver updates and installs for Windows in one exe. Pro adds system tuning.</p>

## Русский

- **Обновления** — новые драйверы из Windows Update одной кнопкой, плюс проверка на сайтах NVIDIA, AMD, Intel, Lenovo и Asus.
- **Устройства** — все устройства, их драйверы и проблемы.
- **Установка** — перетащите папку, `.inf`, `.zip` или `.cab`.
- **Резервные копии** — сохранить драйверы и вернуть их после переустановки Windows.
- **Оптимизация** *(только Pro)* — режимы «Игры» (максимум скорости), «Работа» (баланс) и «Тишина» с подбором под ваш компьютер: питание и процессор, графика, службы, фоновые процессы и приложения, автозагрузка, реклама Windows. Видно, что меняется (было → станет и почему) и что висит в фоне сейчас; рискованное — только после вашего подтверждения; каждую настройку можно вернуть отдельно, группой или все сразу (`DriveUP_Pro.exe /restore` — без окна); прежние значения хранятся в `HKLM\SOFTWARE\DriveUP Pro` и переживают удаление программы.

Скачайте `DriveUP.exe` или `DriveUP_Pro.exe` в [Releases](https://github.com/DenHaIz/DriveUP/releases) и запустите. Установка не нужна, нужны права администратора.

## English

- **Updates** — newer drivers from Windows Update in one click, plus a check on the NVIDIA, AMD, Intel, Lenovo and Asus sites.
- **Devices** — every device, its driver and its problems.
- **Install** — drop a folder, `.inf`, `.zip` or `.cab`.
- **Backups** — save drivers and bring them back after reinstalling Windows.
- **Tuning** *(Pro only)* — Games (top speed), Work (balance) and Quiet modes fitted to your computer: power and processor, graphics, services, background processes and apps, startup apps, Windows ads. You see what changes (was → new, and why) and what runs in the background now; anything risky needs your confirmation; every setting can be returned on its own, by group or all at once (`DriveUP_Pro.exe /restore` — no window); old values are kept in `HKLM\SOFTWARE\DriveUP Pro` and survive removing the app.

Download `DriveUP.exe` or `DriveUP_Pro.exe` from [Releases](https://github.com/DenHaIz/DriveUP/releases) and run it. No install needed, administrator rights required.

## Build · Сборка

`build.cmd` → `bin\DriveUP.exe` and `bin\DriveUP_Pro.exe` (.NET Framework 4.8, built into Windows).

## License · Лицензия

[MIT](LICENSE) © DenHaIz
