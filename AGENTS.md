# Repository Guidelines

## Project Structure & Modules
- Root solution: `dama_klient/dama_klient.sln` (Visual Studio/.NET). Primary project `dama_klient_app/` is an Avalonia Desktop client targeting `net9.0`.
- App entry: `Program.cs` (starts Avalonia); app shell in `App.axaml`/`App.axaml.cs`; main window markup in `MainWindow.axaml` with code-behind `MainWindow.axaml.cs`.
- Build outputs land in `dama_klient/dama_klient_app/bin/<Config>/net9.0/`; intermediate files in `obj/`.
- Legacy UDP server code remains in `dama_server/`; leave it untouched unless explicitly asked to change server behavior.

## Build, Run, and Packaging
- Restore/build solution: `dotnet restore dama_klient/dama_klient.sln` then `dotnet build dama_klient/dama_klient.sln`.
- Run the desktop app: `dotnet run --project dama_klient/dama_klient_app`.
- Release build: `dotnet build dama_klient/dama_klient.sln -c Release`; publish (self-contained when needed) via `dotnet publish dama_klient/dama_klient_app -c Release -r <RID>`.
- If you must work on the server, keep the existing CMake flow (`cmake -S dama_server -B dama_server/build` then `cmake --build dama_server/build`), but avoid mixing client/server changes in the same PR.

## Coding Style & UI Conventions
- Language: C# for the client (Avalonia 11); use 4-space indents, braces on new lines (matching current files), and file-scoped namespaces.
- Naming: PascalCase for public members/types, camelCase for locals/parameters, `_camelCase` for private fields.
- Prefer MVVM-friendly patterns: keep UI logic in view models; limit code-behind to wiring. Bind data in XAML instead of manual control mutations where possible.
- Keep XAML readable: group namespaces, use `Grid`/`StackPanel` over absolute positioning, and define reusable styles/resources in `Application.Styles` or dedicated resource dictionaries.
- Fonts/theme: app uses Fluent theme and Inter font packages—stick with them unless a feature requires a new visual identity.

## Testing & Validation
- No automated tests yet. Manually verify flows by running `dotnet run --project dama_klient/dama_klient_app` and exercising new UI interactions.
- If you add tests, place them under a new test project (e.g., `dama_klient/tests/`) and wire into the solution so `dotnet test` succeeds.

## Commits & PRs
- Commit messages: short, imperative (e.g., `add login view`, `wire socket client`).
- PRs should state purpose, main UI/behavior changes, commands run (`dotnet build`, `dotnet run` checks), and any known regressions or unsupported platforms.
- Include brief repro steps for manual validation (inputs/clicks and expected results); attach screenshots only when they clarify behavior.***
