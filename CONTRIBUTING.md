# Contributing to Meet Simple

Thanks for helping. This project is a local-first Windows app; small, focused changes are easiest to review.

## Before you start

1. Read [`AGENTS.md`](AGENTS.md) — architecture, theming/motion rules, security constraints, and fixes that must not be reintroduced.  
2. Skim [`README.md`](README.md) for product intent and the build story.  
3. Use Windows x64 and the .NET 8 SDK.

## Development

```bat
dotnet restore MeetSimple.sln --locked-mode
dotnet build MeetSimple.sln -c Release
```

CI uses the **.NET 8** SDK and `--locked-mode`. After changing PackageReferences, run `dotnet restore src\MeetSimple\MeetSimple.csproj --force-evaluate` and commit the updated `src/MeetSimple/packages.lock.json`. Keep `Microsoft.NET.ILLink.Tasks` (pinned in the csproj) in sync with the lock file so single-file publish still restores cleanly.

For a shareable folder/zip (what end users run):

```bat
build\build.bat
```

Do not commit `bin/`, `obj/`, `publish/`, or zip artifacts (see `.gitignore`).

## Pull requests

- **One concern per PR** when practical (UI craft vs pipeline vs docs).  
- Preserve privacy guarantees: no telemetry, no relaxing download allowlists/checksums/Authenticode without a clear reason in the PR.  
- UI: use `{DynamicResource ...}`, `Motion` / `CubicBezierEase`, and existing button/card styles (`ChoiceCardButton`, `Sidebar*`, etc.).  
- Describe **what** changed and **how you tested** (build only, launch, record, summarize, …).

## Issues

Include OS version, what you clicked, and whether you used `publish\MeetSimple.exe` or a debugger build. For crashes, paste the scrubbed error dialog text when you can.

Security-sensitive reports: see [`SECURITY.md`](SECURITY.md).
