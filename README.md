# RimTogether

Windows launcher and two-player RimWorld Multiplayer server for Radmin VPN.

**[Скачать RimTogether.Start.exe](https://github.com/KarimIdrisov/rimtogether/releases/latest/download/RimTogether.Start.exe)** — один раз на каждом ПК. При последующих запусках стартовый EXE скачивает совместимый комплект лаунчера, сервера и мода. Игрокам не нужны Git, .NET SDK или учётная запись GitHub.

Хост выбирает Radmin IP и начальный MP ZIP, передаёт другу приглашение и запускает игру. Друг вставляет приглашение, выбирает свою игру и отдельный ник. Подробности: [START-HERE-RU.txt](START-HERE-RU.txt).

## What is synchronized

The repository contains source, upstream server patches, release workflow and documentation. Releases contain self-contained Windows x64 executables and a SHA256-verified official Multiplayer archive. The updater downloads the entire cohort, validates it, then atomically switches the current pointer. Existing versions remain available for fallback.

Game files, DLC, arbitrary third-party mods, passwords, logs, local settings and player saves are excluded. Runtime state stays in `%LOCALAPPDATA%\RimTogether`. Updates are deferred while the game, launcher or server is running. The server remains hosted on the owner's PC, with password authentication and two-player limit.

## Development and releases

Requires Git, .NET 9 SDK, Python 3 and Windows x64. Run `./BUILD.ps1 -Version 1.1.0` in PowerShell 7. The script checks out upstream [rwmt/Multiplayer v0.11.5](https://github.com/rwmt/Multiplayer/tree/v0.11.5), verifies its commit, applies `server-fixes.patch`, runs synthetic regression/network tests, publishes all executables and tests server restart/backup recovery. No real game installation or personal save is required by CI.

Every push to `main` runs GitHub Actions. Only after all checks pass does the publish job create release `v1.1.<run_number>`, including `RimTogether-win-x64.zip`, `RimTogether-manifest.json` and `RimTogether.Start.exe`. Failed builds never advance the update channel. Pull requests build and test without publishing.

The current cohort targets RimWorld 1.6.4633, Multiplayer 0.11.5 / protocol 54 and checkpoint schema 1. A future upstream upgrade must update and verify the client/server pair and checkpoint compatibility together. Do not change the mod tag alone.

## Limits

Automated tests cover installation/rollback, update archive validation, fallback, checkpoints, authentication, two UDP clients, world transfer and rejection of a third connection. An actual two-PC game session over Radmin still needs verification. Desync detection remains enabled.

Based on the MIT-licensed RimWorld Multiplayer project. See [LICENSE-Multiplayer.txt](LICENSE-Multiplayer.txt).
