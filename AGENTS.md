# AGENTS.md

## What this is
A cross-platform (Windows/macOS) video mapping tool: a polygon design surface plus a preview window that warps/clips images and videos onto polygons. Built with **.NET 8 + Avalonia 11.3.10** using MVVM (`CommunityToolkit.Mvvm`).

## Layout
- Single project `VideoMap.App` (the `VideoMap.sln` references only it). `Program.cs` is the entrypoint; `App.axaml` + `ViewLocator.cs` wire Views↔ViewModels by convention.
- Code is split into `Models/`, `ViewModels/`, `Views/`, `Services/`. Add a matching `FooView.axaml`/`.cs` + `FooViewModel.cs` and the locator resolves it — no manual registration.
- UI strings and docs are in **Italian**; keep new user-facing strings consistent with that.

## Build / run
- Build: `dotnet build VideoMap.sln`
- Run (dev): `dotnet run --project VideoMap.App`
- There are **no automated tests** in this repo — verification is manual via the running app.

## Hard-won quirks
- **VLC runtime is required** (LibVLC). On macOS, do NOT set `DYLD_LIBRARY_PATH` manually: the app relaunches itself via `LibVlcEngine.TryRelaunchWithEnvironment` after you set "Percorso VLC (Contents/MacOS)" to `/Applications/VLC.app` in the Properties panel and click "Applica". Don't try to force-load LibVLC outside that path.
- `Program.Main` must not touch Avalonia/third-party APIs or `SynchronizationContext` before `AppMain` runs (see the comment in `Program.cs`).
- `AvaloniaUseCompiledBindingsByDefault` is on → bindings need `x:DataType` and compile-time types, not reflection-based `{Binding}`.
- `AllowUnsafeBlocks` is enabled for LibVLC software-callback rendering; unsafe pixel code is expected, not a mistake.
- **macOS dock icon outside bundle:** `MacDockIcon.Apply()` in `App.axaml.cs` uses the ObjC runtime (`NSApplication.setApplicationIconImage:`) to set the dock icon programmatically. This is needed because the dock icon only comes from a `.app` bundle otherwise. Icon source is `Assets/AppIcon.png` (1024×1024 PNG, loaded via `avares://`).

## Packaging / release
- Local macOS bundle: `./build-macos.sh` (publishes `osx-arm64` self-contained, copies `VideoMap.App/Info.plist` + `Assets/AppIcon.icns`, opens the `.app`).
- CI (`publish.yml`) publishes `win-x64` and `osx-arm64` self-contained via `dotnet publish`, then makes a GitHub Release.
- Release trigger: push a tag or run `workflow_dispatch` with a **tag matching `^v[0-9]+(\.[0-9]+)*$`** (e.g. `v1.2.3`). A malformed tag fails the workflow. Publishing order matters: `publish` job must finish before the `release` job (it `needs: publish`).
