ForzaTriggers 2 — DualSense adaptive triggers + haptics for Forza Horizon on PC
==============================================================================
Project page, updates, help: https://github.com/GefryV3/ForzaTriggers-DualSense-Forza-Horizon

ENGLISH
-------
1. Forza Horizon: Settings -> HUD and Gameplay -> Data Out = On,
   IP = 127.0.0.1, port = 5300.
2. Start ForzaTriggers.exe and pick a mode (Settings -> General -> Mode):

 A) No DSX - direct USB (best): the app creates its own virtual Xbox pad
    (ViGEmBus driver), writes the triggers into the DualSense and streams
    16-bit / 48 kHz haptics with ~10 ms latency.
    - Install ViGEmBus and HidHide (github.com/nefarius), reboot.
    - HidHide: hide the DualSense from games, allow ForzaTriggers.exe
      and ForzaTriggers.Diag.exe.
    - USB cable ONLY, Bluetooth off. Close DSX.

 B) Through DSX (Bluetooth or USB):
    - DSX: Settings -> Controller -> Networking -> UDP on, port 6969.
    - DSX haptics: Haptics -> Audio -> source System Audio; game Rumble OFF
      (it overrides haptics); haptics speaker volume 0.
    - Bluetooth haptics through DSX needs DSX+.

3. Press "Test L2", "Test R2", "Haptics test".

Microsoft Store / Game Pass only (not needed for Steam), admin PowerShell:
   Get-AppxPackage *Forza* | Select Name, PackageFamilyName
   CheckNetIsolation LoopbackExempt -a -n=<PackageFamilyName from above>

Tips: the left panel shows live brake/driven slip - set "ABS threshold" just
above/below it. "Set to stereo" -> Sound -> DualSense -> Configure ->
Quadraphonic. Settings live in ForzaTriggers.ini next to the exe.
Language: RU/EN buttons in the top-right corner.

РУССКИЙ
-------
1. Forza Horizon: Настройки -> HUD и геймплей -> Data Out = Вкл,
   IP = 127.0.0.1, порт = 5300.
2. Запусти ForzaTriggers.exe и выбери режим (Настройки -> Общее -> Режим):

 A) Без DSX - USB напрямую (лучший): приложение само делает виртуальный
    Xbox-геймпад (драйвер ViGEmBus), пишет курки в DualSense и шлёт хаптику
    в 16 битах / 48 кГц с задержкой ~10 мс.
    - Установи ViGEmBus и HidHide (github.com/nefarius), перезагрузись.
    - HidHide: спрячь DualSense от игр, разреши ForzaTriggers.exe
      и ForzaTriggers.Diag.exe.
    - Геймпад ТОЛЬКО по кабелю, Bluetooth выключи. DSX закрой.

 B) Через DSX (Bluetooth или USB):
    - DSX: Settings -> Controller -> Networking -> UDP вкл, порт 6969.
    - Хаптика DSX: Haptics -> Audio -> источник System Audio; Rumble
      (вибрация игры) ВЫКЛ - иначе она глушит хаптику; громкость динамика 0.
    - Хаптика по Bluetooth через DSX - только с DSX+.

3. Нажми «Тест L2», «Тест R2», «Тест хаптики».

Только для Microsoft Store / Game Pass (Steam не нужно), PowerShell от админа:
   Get-AppxPackage *Forza* | Select Name, PackageFamilyName
   CheckNetIsolation LoopbackExempt -a -n=<PackageFamilyName из списка выше>

Советы: слева видно текущее скольжение (торм / вед) - ставь «Порог ABS»
чуть выше/ниже этого числа. «Настроен как стерео» -> Звук -> DualSense ->
Настроить -> Квадрофония. Настройки - в ForzaTriggers.ini рядом с exe.
Язык: кнопки RU/EN в правом верхнем углу.
