# windows-ipad-control

A **free, open-source** way to use your **Windows laptop's keyboard, trackpad, and gestures to control your iPad** — Universal-Control style — with **no extra hardware, no phone, and no driver changes.** Your laptop presents itself directly to the iPad as a Bluetooth Low Energy keyboard + mouse.

Apple's Universal Control does this natively but is Mac-only; a couple of commercial apps do it on Windows for a fee. This is an open take on the same idea — including trackpad gesture forwarding — that you can read, build, and modify yourself.

Push your mouse into the right edge of the laptop screen and it crosses onto the iPad. Sweep off the iPad's left edge to come back. Three-finger swipes drive Home, the App Switcher, and app switching.

```
Laptop  ──────── Bluetooth LE (HID) ────────►  iPad
(captures kb/trackpad,                     (sees a normal BLE
 forwards as HID reports)                   keyboard + mouse)
```

## How it compares

| Approach | Feel | Notes |
|---|---|---|
| Apple Universal Control | Native glide-over | Mac ↔ iPad only |
| Screen-mirroring tools (AirDroid Cast, Splashtop, TeamViewer) | Control a mirror of the iPad on your PC | The iPad screen is shown on the laptop; different from controlling the iPad in place |
| Commercial "software KVM" apps | Native glide-over | Paid, closed-source |
| **This project** | Native glide-over | Free, open-source, Windows; adds 3-finger trackpad gestures + edge-crossing |

## Why this works technically

For years the Windows Bluetooth stack blocked apps from advertising the HID service (UUID `0x1812`), so a PC couldn't pretend to be a Bluetooth keyboard/mouse — the standard workaround was to route through a phone or a dedicated dongle. That restriction has since been **lifted**: on current Windows 11 builds, `GattServiceProvider.CreateAsync(0x1812)` succeeds. This project uses that to make the laptop a **BLE HID peripheral in pure software**, using the normal Windows Bluetooth stack — so Windows Bluetooth keeps working for everything else at the same time.

For years the Windows Bluetooth stack blocked apps from advertising the HID service (UUID `0x1812`), so a PC couldn't pretend to be a Bluetooth keyboard/mouse — the standard workaround was to route through a phone or a dedicated dongle. That restriction has since been **lifted**: on current Windows 11 builds, `GattServiceProvider.CreateAsync(0x1812)` succeeds. This project uses that to make the laptop a **BLE HID peripheral in pure software**, using the normal Windows Bluetooth stack — so Windows Bluetooth keeps working for everything else at the same time.

The iPad pairs with the laptop like any Bluetooth keyboard/mouse and renders the pointer natively, so movement and scrolling feel like a real trackpad.

## Features

- **Native pointer + scrolling** rendered by iPadOS (smooth, real cursor)
- **Edge crossing** — right edge of laptop → iPad; sweep off iPad's left edge → back to laptop
- **Three-finger gestures** (via raw precision-touchpad capture):
  - swipe **up** → Home
  - swipe **up and hold** → App Switcher
  - swipe **left / right** → switch apps
- **Media hotkeys** (work from either machine): `Ctrl+Alt+Space` play/pause, `Ctrl+Alt+←/→` track, `Ctrl+Alt+↑/↓` volume, `Ctrl+Alt+M` mute
- **Ctrl → Cmd mapping** so `Ctrl+C` / `Ctrl+V` work on the iPad
- Adjustable mouse sensitivity and scroll direction
- Runs quietly in the system tray

## Requirements

- Windows 11 (tested on build 26200) with Bluetooth LE
- [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0)
- An iPad on iPadOS 13.4+ (trackpad support)

## Build & run

```powershell
cd src
dotnet build -c Release
.\bin\Release\net8.0-windows10.0.19041.0\IpadBridgeBle.exe
```

The app lives in the system tray. A `config.json` is created next to the exe on first run.

## One-time setup

**On the iPad — pair and enable gestures:**
1. Run the app on the laptop, then on the iPad go to **Settings → Bluetooth** and pair with your PC (it appears under its Windows computer name).
2. To make the 3-finger gestures work, remap a key to Globe: **Settings → General → Keyboard → Hardware Keyboard → Modifier Keys → Caps Lock → 🌐 Globe.** (The gestures send Globe-key shortcuts; this is the only way iPadOS exposes them to an external keyboard.)

**On the laptop — free up the trackpad gestures:**
- **Settings → Bluetooth & devices → Touchpad** → set **Three-finger** and **Four-finger** *Swipes* to **Nothing.** (Windows processes these before any app can, so they must be off to avoid double actions. This app then forwards them to the iPad.)

## Usage

| Action | How |
|---|---|
| Enter iPad mode | Push mouse into the **right screen edge** (or `Ctrl+Alt+I`) |
| Return to laptop | Sweep the cursor off the iPad's **left edge** (or `Ctrl+Alt+I`) |
| Home | 3-finger swipe **up** |
| App Switcher | 3-finger swipe **up and hold** |
| Switch apps | 3-finger swipe **left / right** |
| Play/pause on iPad | `Ctrl+Alt+Space` |

## Configuration (`config.json`)

| Key | Meaning |
|---|---|
| `EnterEdge` | Screen edge that enters iPad mode (`right`/`left`/`top`/`bottom`/`none`) |
| `MapCtrlToCmd` | Map Ctrl→Cmd (and Win→Ctrl) in iPad mode |
| `MediaHotkeys` | Enable the `Ctrl+Alt+…` media keys |
| `InvertScroll` | Flip scroll direction |
| `MouseSensitivity` | Pointer speed multiplier (e.g. `0.55`) |

## How it works

- **`BleHidPeripheral`** — publishes an HID-over-GATT keyboard+mouse+consumer peripheral via WinRT `GattServiceProvider` and advertises it. Holds the HID state and pushes input reports as GATT notifications. (iPadOS accepts the peripheral without the Device Information Service, which Windows still blocks by policy.)
- **`InputCapture`** — low-level keyboard/mouse hooks. Outside iPad mode input flows to Windows normally; in iPad mode it's swallowed and forwarded. Handles edge crossing by integrating relative motion from a pinned reference at the iPad's left edge.
- **`RawTouchpad`** — reads raw precision-touchpad HID contacts (Raw Input) that the mouse hook can't see, detects 3-finger swipes (ending on input-silence, with a horizontal bias so angled swipes read correctly), and maps them to iPad shortcuts.
- **`OverlayForm`** — a nearly-invisible fullscreen window that catches precision-touchpad smooth-scroll, which bypasses the low-level mouse hook.

## Limitations

- **No finger-following gesture animations.** iPadOS reserves the interactive, animated multi-finger gestures for genuine Apple trackpads (Magic Keyboard / Magic Trackpad) — it's gated by device identity, which a pure-software BLE peripheral can't present (Windows also blocks publishing the Device Information Service needed to spoof it). The gestures here trigger the same iPad actions, but fire on release instead of animating under your fingers.
- Windows 11 only (relies on the current WinRT BLE peripheral APIs and the lifted HID-service restriction).

## License

MIT
