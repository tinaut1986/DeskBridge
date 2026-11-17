# DeskBridge

Use several computers with one keyboard and mouse, and **share the physical monitors between them**: a monitor of
the desktop PC can show a screen of the laptop, the laptop's panel can become one more monitor of the desktop, and
the mouse moves from one to the other as if they were a single desk.

A real setup: a desktop PC with 3 monitors and a work laptop.

| Profile | What happens |
|---|---|
| `work` | Monitor 3 of the desktop shows a virtual screen of the laptop. The desktop's keyboard and mouse drive both computers, and the laptop's sound comes out of the desktop's headphones. |
| `four-screens` | The laptop's own panel shows a virtual screen of the desktop, placed under the desktop's monitors as a fourth one. |
| `personal` | Each computer keeps its own screens; the laptop sits on the desktop's desk, and the mouse crosses over to it at the edge. |

## How it works (in short)

1. The computer that **lends** a screen creates a **virtual display** (Virtual Display Driver), placed where the profile says.
2. That display is captured, encoded to H.264 on the GPU and sent encrypted over the network.
3. The monitor of the other computer shows the video full screen, about 20–30 ms round trip.
4. The **coordinator** (the computer with the keyboard and mouse) hands them over to another computer when the cursor enters one of its screens.
5. Optionally, a computer's sound plays on the coordinator, and the coordinator's microphone is used on the other one.

It works both ways: the desktop can show the laptop, and the laptop can show the desktop. The computer that is
not the coordinator always opens the connections, so it can sit behind a firewall that blocks incoming ones.

More detail in [docs/arquitectura.md](docs/arquitectura.md); to work on the code, [docs/desarrollo.md](docs/desarrollo.md)
(both in Spanish).

## Installation

`DeskBridge.exe` is the same app on every computer; it does not need .NET installed.

- The first time it runs, it copies itself to `%LOCALAPPDATA%\DeskBridge\bin\`, adds a Start menu shortcut and an
  entry in **Settings → Apps** (where it is uninstalled). It does not ask for administrator rights.
- It updates itself from the [GitHub releases](https://github.com/tinaut1986/DeskBridge/releases) (stable or beta
  channel). The coordinator can also install a new build on the other computers, if they allow it.
- Every computer needs the **same shared key** (*Connection and security* page). A computer that does not accept
  incoming connections (a company firewall) also sets the coordinator's address in *Connect to*.
- A computer whose screens are shown elsewhere needs the free **[Virtual Display Driver](https://github.com/VirtualDrivers/Virtual-Display-Driver)**
  (MIT, signed). When a profile needs it and it is missing, DeskBridge shows a banner with an **Install** button
  (it fetches *VDD Control* with winget; press *Install Driver* there). Until then, the main display is mirrored
  instead.

## The app

A sidebar with these pages:

- **Home**: this PC, the agent's state, the applied profile and the computers. Two switches, also in the tray menu:
  - **Pause screen sharing** (coordinator): every computer shows its own screens again; keyboard, mouse, clipboard
    and sound keep working. *Resume* brings the profile back.
  - **Independent machines**: this PC stops talking to the others and each one works on its own. *Connect with the
    other machines* undoes it. Neither switch changes the configuration, and both survive a restart.
    In an emergency, **Ctrl+Alt+Shift+Home** on any computer turns on *Independent machines* at once (on the
    coordinator, even while the keyboard and mouse are on another computer).
- **Layout** (on the coordinator): a map with *your desk* (the coordinator's monitors) and the screens of the other
  computers.
  - Drag a screen of another computer onto the desk to use it from there; the mouse crosses where they touch.
  - Select a screen (on the map or in the list) to choose what it shows, the resolution, Hz and bitrate.
  - **Right-click** a screen to make it the **primary display** of its computer (Start menu, taskbar, new
    windows), or to make what it shows the other computer's primary. Primary displays carry a "★". When the
    coordinator lends its primary display, the nearest one becomes primary by default.
  - A screen of the coordinator shown on a display that sits on the desk (the laptop under the monitors) is created
    at that spot and at the size it has on the map, so the edges the mouse crosses are the ones you see.
  - Each computer has a colour (right-click → *Colour of…*), used on the map and for the screen borders.
  - The profile's "⋯" menu: new, duplicate, rename, delete. **Apply profile** puts it in place on every computer.
- **Machines**: computers, addresses and monitors (*Detect* reads them from Windows, also from another computer
  over the network).
- **Sharing**, each option per computer and most of them off by default (one of the computers may be a work one):
  - keyboard and mouse (on the coordinator: take them to other computers; on the other one: let them in).
    **Ctrl+Alt+Home** on the coordinator always brings them back;
  - **sound**: a computer sends everything it plays to the coordinator (and mutes its own speakers); the
    coordinator plays it, with a volume per computer;
  - **microphone**: the coordinator lends its microphone; the other computer gets it through **VB-CABLE** (to be
    installed), and "CABLE Output" is its default microphone while it is in use;
  - clipboard (text and images) and files (drop them on the window, or *Send files to*).
- **Connection and security**: the computer's name, the shared key (hidden), port, *Connect to*, and whether the
  coordinator may install new builds here (remote maintenance).
- **Updates**: installed version, channel, and check now.
- **Log**: the log, diagnostics of every computer, and a video test.
- **General**: language (11), theme, start with Windows, keep running in the notification area when closed,
  **mark each screen with its computer's colour** (a thin border, only while another computer is connected),
  **show other computers' screens without waiting for the refresh** (less delay, sometimes a tear line),
  **run as administrator**, and **let the coordinator use the lock screen and administrator prompts** (installs a
  service).

**Run as administrator** (on the controlled computer): Windows does not let a normal program send keys or clicks
to an app running as administrator. With this option DeskBridge starts elevated when you sign in (a scheduled
task; Windows asks once). Opened by hand without rights, it hands over to the task.

**Lock screen and UAC prompts**: no normal program can see or type into them. The option installs the
"DeskBridge Helper" service (like AnyDesk does): with it they show on the lent monitor and are used from the
coordinator. It needs "Run as administrator": the service only serves DeskBridge with those rights.

### When something goes away

- A computer that is switched off, restarts or leaves the network is noticed within about 40 s, even if the
  connection was never closed. The coordinator stops showing it at once, and asks for its screens again when it is
  back.
- A computer that loses its coordinator waits 10 s, then removes its virtual displays and works on its own, so the
  cursor and windows cannot end up on a screen nobody shows. If it starts without finding the coordinator, it does
  the same after 20 s. It keeps trying to connect in the background (at once when the network changes), without
  blocking anything.
- If the **Virtual Display Driver** stops (it can, after several reloads in a row), DeskBridge restarts it. A
  resolution the driver does not have yet is added to its settings, and the closest one is used if Windows still
  refuses it.
- If the **graphics driver** stops working (after a crash, Windows falls back to the *Microsoft Basic Display
  Adapter*), DeskBridge says so plainly instead of retrying: shut the computer down completely (hold Shift while
  choosing *Shut down*), as a restart is not always enough.

The configuration lives in `%APPDATA%\DeskBridge\`: `deskbridge.json` (shared, edited on the coordinator and sent
to the others by itself), `deskbridge.local.json` (this computer's id and key) and `settings.json` (this user's
preferences).

## Status

Used daily between a desktop (NVIDIA) and a laptop (Intel Arc), both ways: virtual displays, low-latency video,
keyboard and mouse, sound, microphone, clipboard, files, remote updates, lock screen and UAC prompts.

Video is only sent when the screen changes: a still screen costs almost no network or GPU.

**Android** (tablet or phone as one more computer): phase 1 is done, the protocol in Kotlin and an app that joins
the computers; showing screens comes next. See [docs/android.md](docs/android.md).

Next, in order: full-resolution colour (4:4:4) for coloured text; camera; the Android modes. See the
[roadmap](docs/arquitectura.md#hoja-de-ruta).

## Build and test

Requires the .NET 10 SDK and Windows.

```bash
dotnet test
```

```bash
dotnet run --project src/DeskBridge.Gui
```

The command line (`src/DeskBridge.App`, `deskbridge.exe`) is for tests and for troubleshooting other computers
remotely (their log, displays, the virtual display driver, installing a build); see [docs/desarrollo.md](docs/desarrollo.md).
The Android app builds with Gradle from `android/`; see the same document.

## Releases

A `vX.Y.Z` tag builds `DeskBridge-X.Y.Z-win-x64.exe` (self-contained, a single file) and publishes the release
with its `SHA256SUMS.txt` (`.github/workflows/build-release.yml`). On a commit in `main` it is stable; on an
unmerged `release/*` branch, beta. The notes, in the 11 languages, go first in `docs/release-notes/`.

## Layout

```
src/DeskBridge.Core        No Windows dependencies: model, planner, control and video channels, agent,
                           input (logic), Opus, translations (Locales/), releases
src/DeskBridge.Platform    Windows: monitors, virtual displays, capture/encoding/video (Direct3D 11, Media
                           Foundation), input (hooks, SendInput), sound (WASAPI)
src/DeskBridge.Gui         The WPF app (DeskBridge.exe), with the agent inside
src/DeskBridge.App         Command line (deskbridge.exe)
android/                   Android app (Kotlin): protocol and agent
tests/DeskBridge.Core.Tests
scripts/i18n-keys.cs       Lists the translatable texts (or those missing in a language)
docs/                      Architecture, development, microphone and camera, Android, release notes (Spanish)
```
