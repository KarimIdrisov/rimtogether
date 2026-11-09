# Real-client recovery soak test

Windows-only opt-in test; never part of the shipped launcher/mod payload. It needs a separately copied RimWorld tree at `.build/unity-game`, a matching baseline config at `.build/unity-data/Config`, and an installed verified RimTogether release. No game files or saves are committed here.

Build `SoakHarness/SoakHarness.csproj` after creating the matching `.build/guard` payload. Run `python Testing/run_soak.py --before-seconds 600 --after-seconds 600` from the repository root while the real game/server are closed. UDP/TCP 30502 must be free. The test binds the actual installed production server to loopback and copies the selected seed to a new `.build/soak-*` directory. Each real Unity client receives a distinct save-data folder.

The default uses ordinary windowed Unity rendering. `--batchmode` is available only for diagnosing headless behavior; UI-driven long events and automatic recovery should be validated in normal window mode. `--bundle PATH` explicitly tests a local candidate bundle instead of the installed release, and the result is labelled as a candidate.

The extra mod is installed only in the isolated game, and activates only with the test environment variables. It enters the isolated server's valid password without UI, issues ordinary synchronized speed commands, and records hashes every 300 global ticks. The fingerprint covers world/map ticks and RNG plus sorted spawned object IDs, definitions, positions, rotations, HP, stack sizes, factions and pawn job definitions. It is an additional test assertion, not a proof of every game field.

After the baseline interval, the guest alone changes a building's HP and the map RNG iteration state. The controller verifies that the guest's pre-fault fingerprint matched the host at that exact tick, then requires native desync detection, automatic rejoin, a fresh host upload, TCP world transfer, renewed native verification and matching fingerprints. It resumes through ordinary host time-control commands after recovery; production intentionally remains paused until a player resumes. Another soak interval must pass without extra desyncs or unequal fingerprints, with actual map-tick advancement.

`result.json`, client/server logs, fingerprint TSVs, the injected fault marker and native desync archive stay in the private test directory. Timing is sampled by a one-second controller and is approximate. The harness terminates only the processes it started and requests a clean server stop in `finally`.

Control handoff writes a temporary file and atomically replaces the command file, retrying brief Windows read-sharing locks. The game never observes a partially written command.

Two clients on one PC test real simulation and recovery but do not reproduce the remote player's hardware or the Radmin path. A passing finite soak is not a guarantee against every desync cause. The test-only assembly is excluded by the release archive's explicit allowlist.
