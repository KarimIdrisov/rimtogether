using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using HarmonyLib;
using Multiplayer.Client;
using Multiplayer.Client.Util;
using Multiplayer.Common;
using Multiplayer.Common.Networking.Packet;
using UnityEngine;
using Verse;
using MP = Multiplayer.Client.Multiplayer;

namespace RimTogether.SoakHarness;

public sealed class HarnessMod : Mod
{
    public HarnessMod(ModContentPack content) : base(content)
    {
        if (string.IsNullOrEmpty(Harness.Root)) return;
        var harmony = new Harmony("rimtogether.isolated.soak.harness");
        harmony.Patch(AccessTools.Method(typeof(TickManager), nameof(TickManager.TickManagerUpdate)),
            postfix: new HarmonyMethod(typeof(Harness), nameof(Harness.AfterUpdate)));
        harmony.Patch(AccessTools.Method(typeof(ClientJoiningState), nameof(ClientJoiningState.HandleProtocolOk)),
            prefix: new HarmonyMethod(typeof(Harness), nameof(Harness.Authenticate)));
        harmony.Patch(AccessTools.Method(typeof(SaveLoad), "SaveAndReload"),
            prefix: new HarmonyMethod(typeof(Harness), nameof(Harness.SaveBegin)),
            postfix: new HarmonyMethod(typeof(Harness), nameof(Harness.SaveEnd)));
        Log.Message("SOAK harness enabled only for isolated data directory: " + Harness.Root);
    }
}

public static class Harness
{
    public static readonly string Root = Environment.GetEnvironmentVariable("RIMTOGETHER_SOAK_ROOT") ?? "";
    private static readonly string Role = Environment.GetEnvironmentVariable("RIMTOGETHER_SOAK_ROLE") ?? "";
    private static string previousControl = "";
    private static float nextPoll;
    private static int lastProofTick = -1;
    private static bool injectPending;
    private static bool injected;
    private static bool reportedError;
    public static void SaveBegin() => Log.Message($"SOAK SNAPSHOT_BEGIN {DateTime.UtcNow:O} timer={TickPatch.Timer}");
    public static void SaveEnd() => Log.Message($"SOAK SNAPSHOT_END {DateTime.UtcNow:O} timer={TickPatch.Timer}");

    // Only substitutes UI entry of the test server's valid password. The real server
    // authentication and client/server protocol checks still execute normally.
    public static bool Authenticate(ServerProtocolOkPacket packet)
    {
        var password = Environment.GetEnvironmentVariable("RIMTOGETHER_SOAK_PASSWORD");
        if (string.IsNullOrEmpty(Root) || !packet.hasPassword || password == null) return true;
        MP.Client.Send(new ClientUsernamePacket(MP.username, password));
        return false;
    }

    public static void AfterUpdate()
    {
        try
        {
            if (string.IsNullOrEmpty(Root) || MP.game == null || MP.Client?.State != ConnectionStateEnum.ClientPlaying ||
                Current.ProgramState != ProgramState.Playing || TickPatch.Simulating || Find.Maps.Count == 0 ||
                MP.session.desynced) return;
            if (Time.realtimeSinceStartup >= nextPoll)
            {
                nextPoll = Time.realtimeSinceStartup + 0.25f;
                var path = Path.Combine(Root, Role + ".control");
                if (File.Exists(path))
                {
                    var command = File.ReadAllText(path);
                    if (command != previousControl)
                    {
                        previousControl = command;
                        var fields = command.Trim().Split('|');
                        if (fields.Length > 1 && fields[1] == "inject" && Role == "guest" && !injected)
                            injectPending = true;
                        if (fields.Length > 2 && fields[1] == "speed" && MP.session.isHost)
                        {
                            var speed = byte.Parse(fields[2]);
                            MP.Client.SendCommand(CommandType.GlobalTimeSpeed, ScheduledCommand.Global, speed);
                            foreach (var map in Find.Maps)
                                MP.Client.SendCommand(CommandType.MapTimeSpeed, map.uniqueID, speed);
                            Log.Message($"SOAK speed={speed} timer={TickPatch.Timer}");
                        }
                    }
                }
            }
            var tick = TickPatch.Timer;
            if (tick == lastProofTick || tick % 300 != 0) return;
            lastProofTick = tick;
            var before = Fingerprint();
            if (injectPending)
            {
                var target = Find.Maps.OrderBy(m => m.uniqueID).SelectMany(m => m.listerThings.AllThings
                    .Where(t => t.def.category == ThingCategory.Building && t.HitPoints > 10)
                    .OrderBy(t => t.thingIDNumber)).First();
                var hp = target.HitPoints;
                target.HitPoints = Math.Max(1, hp / 2);
                var time = target.Map.AsyncTime();
                var rng = time.randState;
                time.randState ^= 1UL << 32;
                injected = true; injectPending = false;
                var after = Fingerprint();
                var marker = $"INJECT\t{tick}\t{target.thingIDNumber}\t{hp}\t{target.HitPoints}\t{before}\t{after}\t{rng}\t{time.randState}\t{DateTime.UtcNow:O}";
                File.AppendAllText(Path.Combine(Root, "fault.tsv"), marker + Environment.NewLine);
                Log.Message("SOAK " + marker);
            }
            var hash = Fingerprint();
            var line = $"{tick}\t{MP.game.sync.lastValidTick}\t{Find.Maps.Sum(m => (long)m.AsyncTime().mapTicks)}\t{hash}";
            File.AppendAllText(Path.Combine(Root, Role + "-states.tsv"), line + Environment.NewLine);
        }
        catch (Exception error)
        {
            if (reportedError) return;
            reportedError = true;
            Log.Error("SOAK HARNESS ERROR: " + error);
        }
    }

    private static string Fingerprint()
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(MP.AsyncWorldTime.worldTicks); writer.Write(MP.AsyncWorldTime.randState);
            foreach (var map in Find.Maps.OrderBy(m => m.uniqueID))
            {
                writer.Write(map.uniqueID); writer.Write(map.AsyncTime().mapTicks); writer.Write(map.AsyncTime().randState);
                var things = map.listerThings.AllThings.OrderBy(t => t.thingIDNumber).ToArray();
                writer.Write(things.Length);
                foreach (var thing in things)
                {
                    writer.Write(thing.thingIDNumber); writer.Write(thing.def.defName);
                    writer.Write(thing.Position.x); writer.Write(thing.Position.z); writer.Write(thing.Rotation.AsInt);
                    writer.Write(thing.HitPoints); writer.Write(thing.stackCount);
                    writer.Write(thing.Faction?.GetUniqueLoadID() ?? "");
                    writer.Write((thing as Pawn)?.CurJob?.def.defName ?? "");
                }
            }
        }
        using var sha = SHA256.Create();
        return BitConverter.ToString(sha.ComputeHash(stream.ToArray())).Replace("-", "");
    }
}
