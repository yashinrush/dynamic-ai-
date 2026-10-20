# Nusa for Windows

This folder contains a native Windows desktop companion inspired by the Nusa website preview. It is built with .NET 8 WPF and runs as a small always-on-top notch at the top of the active display.

## Run

Open `Nusa/Nusa.csproj` in Visual Studio 2022 with the .NET desktop development workload, or run:

```powershell
dotnet run --project .\Nusa\Nusa.csproj
```

Close the window to tuck Nusa into the system tray. Use the tray menu to open it, enable sign-in startup, or quit.

## Included features

- Resting pill with clock and eyes; hover to expand, move away to collapse.
- Media play/pause, previous and next system media key actions.
- LRCLIB lyrics search with line timing, local song fields, and clickable lyric seeking within the displayed lyrics.
- Focus and break countdowns with completion chime and tray reminders.
- Water logging, daily goal, bottle size, hourly chart, and optional hourly reminders.
- To-do list with completion and due-time reminders.
- File shelf with file picker, drag-in, drag-out, open, and right-click removal.
- Laptop battery, charging, network, Caps Lock, and keyboard-language status.
- Full-screen window hiding, system tray, and optional Windows sign-in startup.
- Local JSON data stored under `%LOCALAPPDATA%\Nusa\nusa-data.json`.

## Platform integration notes

Windows does not expose metadata from every media player through the generic media keys. This initial implementation sends global transport keys and lets you enter track and artist when requesting lyrics. Headphone battery reporting also depends on the accessory exposing a supported battery service to Windows. Brightness controls vary by device, so Nusa surfaces the status and leaves adjustment to Windows.
