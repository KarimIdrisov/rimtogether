using System.IO.Compression;
using Multiplayer.Common;

namespace RimTogether;

public static class SyntheticReplay
{
    // No copyrighted game data or personal colony is used by CI.
    public static string Create(string path)
    {
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        void Add(string name, byte[] data) { using var stream = zip.CreateEntry(name).Open(); stream.Write(data); }
        Add("info", ReplayInfo.Write(new ReplayInfo { name = "Synthetic test", protocol = Contract.Protocol, playerFaction = 14,
            spectatorFaction = 15, rwVersion = "1.6.4633", modIds = ["ludeon.rimworld", "rwmt.multiplayer"],
            modNames = ["Core", "Multiplayer"], sections = [new ReplaySection(100, 100)], asyncTime = false }));
        Add("world/000_save", "<savegame/>"u8.ToArray());
        Add("maps/000_0_save", "<map/>"u8.ToArray());
        Add("world/000_cmds", ScheduledCommand.SerializeCmds([]));
        Add("maps/000_0_cmds", ScheduledCommand.SerializeCmds([]));
        return path;
    }
}
