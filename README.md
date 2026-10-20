# Nusa for Windows

Nusa is a compact desktop companion for Windows, inspired by notch and Dynamic Island interfaces. It keeps music, focus sessions, reminders, tasks, and system information close at hand in a small floating panel at the top of your screen.

Built with **C#**, **.NET 8**, and **WPF**. Nusa lives in the notification area when closed, and its data stays on your PC.

## Features

- **Expandable top panel** — rests as a small pill, then expands on hover to show the selected tool.
- **Music controls** — shows the active Windows media session when available and provides play/pause, previous, and next controls.
- **Synced lyrics** — find timed lyrics through [LRCLIB](https://lrclib.net/), follow the current line, and seek within the track when timing information is available.
- **Focus timer** — configurable focus and break lengths, with start, pause, resume, and reset controls.
- **Water tracker** — log drinks, set a bottle size and daily goal, review an hourly chart, and optionally enable hourly reminders.
- **Tasks** — add and complete tasks, with optional due-time reminders.
- **File shelf** — pin files using the picker or drag and drop, then open or drag them out from the panel.
- **System status** — view battery and charging state, network availability, Caps Lock, and the current keyboard language.
- **Windows integration** — notification-area menu, optional sign-in startup, and automatic hiding over full-screen apps.

## Requirements

- Windows 10 version 2004 (build 19041) or newer, or Windows 11
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- Windows desktop development support for WPF

## Run from source

```powershell
git clone https://github.com/yashinrush/dynamic-ai-.git
cd dynamic-ai-
dotnet run --project .\Nusa\Nusa.csproj
```

You can also open `Nusa\Nusa.csproj` in Visual Studio 2022 with the **.NET desktop development** workload installed, then run the project.

To create a release build:

```powershell
dotnet build .\Nusa\Nusa.csproj -c Release
```

## Using Nusa

Move the pointer to the top-center pill to expand it, then choose a tool from the panel. The panel collapses after you move away. Closing the window tucks Nusa into the notification area; use its tray icon to reopen the app or quit. The tray menu also lets you turn sign-in startup on or off.

For lyrics, enter the track and artist when prompted. An internet connection is needed to look up lyrics through LRCLIB. Music controls and track details depend on the active player exposing a Windows media session.

## Data and privacy

Tasks, water logs and preferences, focus settings, file shelf entries, and the last track details are stored locally in:

```text
%LOCALAPPDATA%\Nusa\nusa-data.json
```

File shelf entries store file paths; the files themselves are not copied into Nusa. Lyrics lookup sends the requested track and artist to LRCLIB. Nusa does not include a remote account or cloud sync service.

## Windows integration notes

- Media metadata and transport controls work with players that publish a Windows Global System Media Transport Controls session. Some players expose limited information.
- Earbud battery levels are not currently read by Nusa. Windows and the connected device must expose a supported battery interface before an app can report them.
- Battery and charging information comes from Windows power status. Brightness adjustment is device-specific; Nusa opens Windows display settings for that control.

## Project layout

```text
Nusa/
  App.xaml                 WPF application entry point
  MainWindow.xaml          Window and shared visual styles
  MainWindow.xaml.cs       Panel UI, timers, media, reminders, and Windows integration
  AppDataStore.cs          Local JSON data models and persistence
  Nusa.csproj              Windows WPF project configuration
README.md                  Setup and project documentation
```

## License

No license has been added to this repository yet. Until one is chosen, the source is visible on GitHub but reuse and redistribution are not granted by an open-source license.
