# Gradient Native

Копія Tauri-репозиторію з історією Git. Нативні клієнти використовують той самий Rust-код Minecraft-лаунчера через локальний процес `gradient-core`.

| Папка | Що всередині |
| --- | --- |
| `core/` | Оригінальний workspace з Tauri/Vue, ядро `packages/app-lib` і новий адаптер `apps/launcher-bridge`. |
| `winui/` | Нативна оболонка на C# та WinUI 3. |
| `swiftui/` | Нативна оболонка на SwiftUI для macOS 15 і новіших. |

Перший зріз включає бібліотеку, пошук профілів, обкладинку з останнього скриншота, Microsoft та офлайн-вхід, імпорт `.mrpack`, чергу, запуск, зупинку та живий лог. Каталог Discover і розширені вкладки профілю ще доступні через вихідний Tauri-клієнт.

## Windows

Потрібні Rust із toolchain workspace, Java 17 для Gradle, Visual Studio з C++ Build Tools і Windows SDK, .NET 8 та компоненти збірки WinUI 3 / MSIX. Для Microsoft-входу потрібен WebView2 Runtime.

З кореня репозиторію в x64 Developer PowerShell:

```powershell
$env:SQLX_OFFLINE = 'true'
Push-Location core
cargo build --locked --release -p gradient-core
Pop-Location
MSBuild winui/Gradient.WinUI.csproj /restore /p:Platform=x64 /p:Configuration=Release
```

Запустіть `Gradient.WinUI.exe` із каталогу Release під `winui/bin`. Проєкт копіює зібране ядро поруч із застосунком. Для іншого розташування задайте абсолютний шлях до ядра через `GRADIENT_CORE`.

## macOS

Потрібні macOS 15+, Xcode 16+ зі Swift 6, Rust і Java 17 для Gradle. З кореня репозиторію:

```sh
root="$(pwd)"
cd "$root/core"
SQLX_OFFLINE=true cargo build --locked --release -p gradient-core
cd "$root/swiftui"
GRADIENT_CORE="$root/core/target/release/gradient-core" swift run Gradient
```

## GitHub Actions

Workflow **Build Gradient Native** запускається після push у `native/github-actions`. Після додавання workflow до default branch його також можна запускати вручну в **Actions → Build Gradient Native → Run workflow**.

У **Artifacts** будуть збірки для Windows x64, macOS Apple Silicon (`arm64`) і macOS Intel (`x86_64`). Для Windows розпакуйте весь архів і відкрийте `Gradient.WinUI.exe`. Для macOS 15+ розпакуйте `.tar.gz` для свого процесора й виконайте з отриманої папки `GRADIENT_CORE="$PWD/gradient-core" ./Gradient`. Це збірки для тестування. Для Windows потрібен .NET 8 Desktop Runtime, а для Microsoft-входу — WebView2 Runtime.

## Дані та протокол

За замовчуванням ядро відкриває окремий каталог `com.gradient.launcher.native`. Для перевірки на наявних профілях задайте `THESEUS_CONFIG_DIR` на копію каталогу даних Tauri. Якщо в налаштуваннях заданий зовнішній шлях до файлів профілів, скопіюйте їх і виправте шлях у тестових налаштуваннях перед запуском. Нативне блокування захищає від одночасного запису двома нативними клієнтами. При виході з оболонки ядро зберігає процеси гри й активні встановлення до їх завершення, а новий клієнт тієї ж бази зможе підключитися після виходу попереднього ядра.

Перший рядок stdout — `{"protocol":1}`. Далі оболонка надсилає по одному JSON-об’єкту на рядок. На кожну команду ядро повертає один рядок `{"ok":...}` або `{"error":"..."}`. Стан бібліотеки, черги й логів клієнти оновлюють опитуванням. Microsoft-вхід використовує OAuth-потік вихідної Tauri-версії, а WebView використовується для вікна входу.
