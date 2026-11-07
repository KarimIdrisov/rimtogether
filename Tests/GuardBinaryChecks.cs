using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace RimTogether;

public static class GuardBinaryChecks
{
    public static void Run(Action<bool, string> check)
    {
        if (Environment.GetEnvironmentVariable("RIMTOGETHER_GUARD_PAYLOAD") is not { Length: > 0 } folder) return;
        GuardPayload.Load(folder);
        using var common = new PEReader(File.OpenRead(Path.Combine(folder, "1.6/AssembliesCustom/MultiplayerCommon.dll")));
        var meta = common.GetMetadataReader();
        var version = meta.TypeDefinitions.Select(meta.GetTypeDefinition).Single(t => meta.GetString(t.Name) == "MpVersion");
        var protocol = version.GetFields().Select(meta.GetFieldDefinition).Single(f => meta.GetString(f.Name) == "Protocol");
        var blob = meta.GetBlobReader(meta.GetConstant(protocol.GetDefaultValue()).Value);
        check(blob.ReadInt32() == Contract.Protocol, "packaged client Common DLL actually contains guard protocol");
        using var client = new PEReader(File.OpenRead(Path.Combine(folder, "1.6/AssembliesCustom/Multiplayer.dll")));
        var reader = client.GetMetadataReader();
        var types = reader.TypeDefinitions.Select(t => reader.GetString(reader.GetTypeDefinition(t).Name)).ToHashSet();
        check(types.Contains("SeedGravshipCanReachGravship") && types.Contains("RimTogetherRecovery"), "packaged client contains RNG isolation and bounded recovery patches");
        check(reader.TypeReferences.Any(t => reader.GetString(reader.GetTypeReference(t).Name) == "QuestNode_ResolveQuestName"), "quest-name RNG guard is present in packaged client");
        var move = reader.TypeDefinitions.Select(reader.GetTypeDefinition).Single(t => reader.GetString(t.Name) == "DraftedMove_GotoFeedbackPatch");
        check(move.GetMethods().Any(m => reader.GetString(reader.GetMethodDefinition(m).Name) == "CustomEndCurrentJob"), "packaged client contains synchronized drafted-move stop operation");
        if (Environment.GetEnvironmentVariable("RIMTOGETHER_GAME_MANAGED") is not { Length: > 0 } gameManaged) return;
        using var game = new PEReader(File.OpenRead(Path.Combine(gameManaged, "Assembly-CSharp.dll")));
        var definitions = game.GetMetadataReader();
        foreach (var target in new[] { ("RitualBehaviorWorker_GravshipLaunch", "CanReachGravship"), ("QuestNode_ResolveQuestName", "Resolve") })
        {
            var type = definitions.TypeDefinitions.Select(definitions.GetTypeDefinition).Single(t => definitions.GetString(t.Name) == target.Item1);
            check(type.GetMethods().Any(m => definitions.GetString(definitions.GetMethodDefinition(m).Name) == target.Item2), "RNG patch target exists in actual game: " + target.Item1 + "." + target.Item2);
        }
    }
}
