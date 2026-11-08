using System.Diagnostics;
using System.IO.Compression;
using System.Xml.Linq;
using RimTogether;
using Multiplayer.Common;

var fixture = Path.Combine(AppContext.BaseDirectory, "fixtures-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(fixture);
var count = 0;
void Check(bool value, string message) { if (!value) throw new Exception("FAIL: " + message); Console.WriteLine("PASS " + message); count++; }
void Throws(Action action, string message) { try { action(); } catch { Check(true, message); return; } Check(false, message); }
try
{
    if (args.Contains("--listeners-only")) { ListenerChecks.Run(Check); return 0; }
    GuardBinaryChecks.Run(Check);
    var state = Path.Combine(fixture, "state.json");
    Storage.Write(state, new { Value = 1 }); Storage.Write(state, new { Value = 2 });
    Check(File.ReadAllText(state).Contains('2') && File.ReadAllText(state + ".bak").Contains('1'), "atomic replacement retains previous backup");
    var evil = Path.Combine(fixture, "evil.zip");
    using (var zip = ZipFile.Open(evil, ZipArchiveMode.Create)) { using var writer = new StreamWriter(zip.CreateEntry("../escape.txt").Open()); writer.Write("bad"); }
    Throws(() => ModManager.ExtractSafe(evil, Path.Combine(fixture, "extract")), "archive traversal rejected");
    Check(!File.Exists(Path.Combine(fixture, "escape.txt")), "traversal writes nothing outside staging");
    var copySource = Path.Combine(fixture, "copy-source"); Directory.CreateDirectory(Path.Combine(copySource, "nested", "empty"));
    File.WriteAllBytes(Path.Combine(copySource, "nested", "original.bin"), [1, 2, 3, 4]);
    var copyTarget = Path.Combine(fixture, "copied-backup"); ModManager.CopyVerifiedDirectory(copySource, copyTarget);
    Check(File.ReadAllBytes(Path.Combine(copyTarget, "nested", "original.bin")).SequenceEqual(new byte[] { 1, 2, 3, 4 }) && Directory.Exists(Path.Combine(copyTarget, "nested", "empty")), "verified backup copies nested bytes and empty directories without moving original");
    Check(File.Exists(Path.Combine(copySource, "nested", "original.bin")), "backup leaves original folder intact before same-volume rename");
    Throws(() => ModManager.CopyVerifiedDirectory(copySource, Path.Combine(copySource, "recursive")), "backup inside original tree rejected before recursion");
    using (var locked = new FileStream(Path.Combine(copySource, "nested", "original.bin"), FileMode.Open, FileAccess.Read, FileShare.None))
        Throws(() => ModManager.CopyVerifiedDirectory(copySource, Path.Combine(fixture, "failed-backup")), "failed backup leaves original untouched");
    Check(!Directory.Exists(Path.Combine(fixture, "failed-backup")), "partial backup is never promoted to completed backup");
    if (args.Length < 1) throw new Exception("Provide official mod ZIP path; an optional second argument selects a real replay");
    var replayPath = args.Length > 1 ? args[1] : SyntheticReplay.Create(Path.Combine(fixture, "synthetic-replay.zip"));
    if (Environment.GetEnvironmentVariable("RIMTOGETHER_TEST_REPLAY") is { Length: > 0 } export)
    { Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(export))!); File.Copy(replayPath, export, true); }
    var game = Path.Combine(fixture, "game"); Directory.CreateDirectory(Path.Combine(game, "Mods", "Prepatcher", "About"));
    File.WriteAllText(Path.Combine(game, "RimWorldWin64.exe"), "fixture"); File.WriteAllText(Path.Combine(game, "Version.txt"), "1.6.4633 rev1260");
    File.WriteAllText(Path.Combine(game, "Mods", "Prepatcher", "About", "About.xml"), "<ModMetaData><packageId>zetrith.prepatcher</packageId></ModMetaData>");
    var config = Path.Combine(fixture, "ModsConfig.xml");
    var original = "<ModsConfigData><activeMods><li>zetrith.prepatcher</li><li>ludeon.rimworld</li><li>ludeon.rimworld.odyssey</li><li>hiteke.steam</li><li>rwmt.multiplayer</li><li>custom.mod</li></activeMods></ModsConfigData>";
    File.WriteAllText(config, original);
    var guardFolder = Path.Combine(fixture, "guard");
    var guard = new GuardPayload();
    foreach (var path in GuardPayload.Paths)
    {
        var file = Path.Combine(guardFolder, path); Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllBytes(file, new byte[200_000]); guard.Files[path] = ModManager.Hash(file);
    }
    Storage.Write(Path.Combine(guardFolder, "manifest.json"), guard);
    var manager = new ModManager(new HttpClient(), Path.Combine(fixture, "data"), Console.WriteLine, guardFolder);
    manager.Install(game, args[0], config);
    var ids = XDocument.Load(config).Descendants("li").Select(e => e.Value).ToArray();
    Check(ids.SequenceEqual(new[] { "zetrith.prepatcher", "ludeon.rimworld", "ludeon.rimworld.odyssey", "rwmt.multiplayer", "custom.mod" }), "Radmin profile preserves other mods and positions Multiplayer after DLC");
    var installed = Path.Combine(game, "Mods", "RimTogether-Multiplayer");
    Check(File.Exists(Path.Combine(installed, "1.6", "AssembliesCustom", "Multiplayer.dll")), "official mod installed");
    manager.Install(game, args[0], config);
    Check(Directory.GetDirectories(Path.Combine(game, "Mods")).Count(d => ModManager.PackageId(d) == "rwmt.multiplayer") == 1, "repeat update is idempotent, no duplicate mods");
    var metadataFile = Directory.GetFiles(Path.Combine(fixture, "data"), "installed-*.json").Single();
    File.WriteAllText(metadataFile, "{broken"); manager.Install(game, args[0], config);
    Check(guard.Installed(installed), "corrupt installation metadata repairs from verified guard payload");
    File.WriteAllText(Path.Combine(installed, GuardPayload.Marker), "{broken"); manager.Install(game, args[0], config);
    Check(guard.Installed(installed), "corrupt guard marker repairs instead of blocking recovery forever");
    var dll = Path.Combine(installed, "1.6", "AssembliesCustom", "Multiplayer.dll"); File.WriteAllText(dll, "corrupt");
    manager.Install(game, args[0], config);
    Check(new FileInfo(dll).Length > 100_000, "modified DLL detected and repaired from verified archive");
    var oldHash = ModManager.Hash(dll); File.WriteAllText(Path.Combine(installed, "extra.txt"), "force repair"); File.WriteAllText(config, "<broken>");
    Throws(() => manager.Install(game, args[0], config), "failed config update triggers mod rollback");
    Check(ModManager.Hash(dll) == oldHash && File.Exists(Path.Combine(installed, "extra.txt")), "failed install restores old mod folder");
    Check(Directory.GetFiles(Path.Combine(fixture, "data", "backups"), "ModsConfig.xml", SearchOption.AllDirectories).Any(f => File.ReadAllText(f) == original), "original user config is backed up");
    var managed = Path.Combine(game, "RimWorldWin64_Data", "Managed"); Directory.CreateDirectory(managed);
    File.WriteAllText(Path.Combine(managed, "Assembly-CSharp.dll"), "fixture game assembly");
    foreach (var name in new[] { "Core", "Odyssey" })
    {
        var about = Path.Combine(game, "Data", name, "About"); Directory.CreateDirectory(about);
        File.WriteAllText(Path.Combine(about, "About.xml"), "<ModMetaData><packageId>ludeon.rimworld" + (name == "Core" ? "" : ".odyssey") + "</packageId></ModMetaData>");
    }
    File.WriteAllText(config, "<ModsConfigData><activeMods><li>zetrith.prepatcher</li><li>ludeon.rimworld</li><li>ludeon.rimworld.odyssey</li><li>rwmt.multiplayer</li></activeMods></ModsConfigData>");
    var fingerprint = GameFingerprint.Compute(game, config);
    Check(fingerprint == GameFingerprint.Compute(game, config), "game fingerprint is stable");
    File.AppendAllText(Path.Combine(managed, "Assembly-CSharp.dll"), "modified");
    Check(fingerprint != GameFingerprint.Compute(game, config), "game assembly mismatch detected before joining");
    var native = Path.Combine(game, "UnityPlayer.dll"); File.WriteAllText(native, "native-runtime-v1");
    var nativeFingerprint = GameFingerprint.Compute(game, config); File.WriteAllText(native, "native-runtime-v2");
    Check(nativeFingerprint != GameFingerprint.Compute(game, config), "native runtime mismatch detected before joining");
    var language = Path.Combine(game, "Data", "Core", "Languages", "Russian", "Keyed"); Directory.CreateDirectory(language);
    var mechanics = GameFingerprint.Compute(game, config); File.WriteAllText(Path.Combine(language, "text.xml"), "<text>translation</text>");
    Check(mechanics == GameFingerprint.Compute(game, config), "translation text does not change mechanics fingerprint");
    BundleChecks.Run(Path.Combine(fixture, "bundles"), Check, Throws);
    var checkpoint = Checkpoint.FromReplay(replayPath); checkpoint.Validate();
    CheckpointWriterChecks.Run(checkpoint, Check, Throws);
    var legacyFresh = Checkpoint.FromReplay(SyntheticReplay.Create(Path.Combine(fixture, "legacy-full.zip"), 53));
    Check(legacyFresh.Tick == legacyFresh.StartingTick, "full legacy save safely imports without historical resimulation");
    Throws(() => Checkpoint.FromReplay(SyntheticReplay.Create(Path.Combine(fixture, "legacy-history.zip"), 54, true)), "legacy historical replay is blocked before changed RNG rules can alter its past");
    var unsafeRollback = Checkpoint.Capture(new MultiplayerServer(new ServerSettings()));
    Check(unsafeRollback.Tag != Contract.Tag, "guard checkpoint tag prevents old launcher from silently replaying guard commands");
    RecoveryChecks.Run(checkpoint, Check, Throws);
    ListenerChecks.Run(Check);
    NetworkChecks.Run(checkpoint, Check);
    NetworkChecks.Run(checkpoint, Check, tcp: true);
    NetworkChecks.Run(checkpoint, Check, tcp: true, rejectTcp: true);
    NetworkChecks.Run(checkpoint, Check, tcp: true, lateTcp: true);
    BulkChecks.Run(Check, Throws);
    Checkpoint.FromReplay(SyntheticReplay.Create(Path.Combine(fixture, "compatible-guard.zip"), 5401, true)).Validate();
    Check(true, "previous guard command history remains compatible across transport-only protocol upgrade");
    var server = new MultiplayerServer(new ServerSettings()); checkpoint.Apply(server);
    server.commands.Send(CommandType.PauseAll, -1, -1, []);
    server.gameTimer += 9;
    var snapshot = Checkpoint.Capture(server); Storage.Write(state, snapshot);
    var restored = Storage.Read<Checkpoint>(state); restored.Validate();
    Check(restored.World.SequenceEqual(checkpoint.World) && restored.Tick == checkpoint.Tick + 9 && restored.Commands[-1].Count == checkpoint.Commands[-1].Count, "checkpoint roundtrip preserves world, timer and serialized commands");
    var second = new MultiplayerServer(new ServerSettings()); restored.Apply(second);
    Check(second.worldData.mapData.Count == server.worldData.mapData.Count && second.gameTimer == server.gameTimer, "checkpoint restores independent server instance");
    Check(!second.worldData.TryStartJoinPointCreation(), "first client after checkpoint reload does not wait for an impossible joinpoint");
    restored.Schema = 999; Throws(restored.Validate, "unknown checkpoint schema rejected");
    restored.Schema = 1; restored.Tag = "v9.9.9"; Throws(restored.Validate, "incompatible server checkpoint rejected");
    Console.WriteLine($"ALL {count} CHECKS PASSED");
    return 0;
}
finally
{
    // Fixture is a generated directory under this test executable's own output folder.
    if (Directory.Exists(fixture)) Directory.Delete(fixture, true);
}
