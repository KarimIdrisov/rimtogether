using System.IO.Compression;
using Multiplayer.Common;

namespace RimTogether;

public sealed class Checkpoint
{
    public int Schema { get; set; } = 1;
    public const string GuardTag = Contract.Tag + "+" + Contract.GuardRevision;
    public string Tag { get; set; } = GuardTag;
    public int ClientProtocol { get; set; }
    public int Tick { get; set; }
    public int StartingTick { get; set; }
    public int WorkTicks { get; set; }
    public int HostFaction { get; set; }
    public int SpectatorFaction { get; set; }
    public bool AsyncTime { get; set; }
    public bool Multifaction { get; set; }
    public byte[] World { get; set; } = [];
    public byte[] Session { get; set; } = [];
    public Dictionary<int, byte[]> Maps { get; set; } = [];
    public Dictionary<int, List<byte[]>> Commands { get; set; } = [];

    public static Checkpoint FromReplay(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        byte[] Read(string name)
        {
            var entry = zip.GetEntry(name) ?? throw new InvalidDataException("Нет записи " + name);
            if (entry.Length > 250_000_000) throw new InvalidDataException("Слишком большое сохранение.");
            using var stream = entry.Open(); using var bytes = new MemoryStream(); stream.CopyTo(bytes); return bytes.ToArray();
        }
        var info = ReplayInfo.Read(Read("info"));
        if (info.rwVersion == null || !info.rwVersion.StartsWith("1.6.4633")) throw new InvalidDataException("Сохранение требует другую версию RimWorld.");
        if (info.sections.Count == 0) throw new InvalidDataException("В replay отсутствуют секции.");
        var section = info.sections[^1];
        var prefix = (info.sections.Count - 1).ToString("D3");
        var result = new Checkpoint { Tick = section.end, StartingTick = section.start, HostFaction = info.playerFaction,
            SpectatorFaction = info.spectatorFaction, AsyncTime = info.asyncTime, Multifaction = info.multifaction,
            ClientProtocol = info.protocol, Tag = info.protocol == Contract.Protocol ? GuardTag : Contract.Tag,
            World = Compress(Read("world/" + prefix + "_save")) };
        foreach (var entry in zip.Entries.Where(e => e.FullName.StartsWith("maps/" + prefix + "_")))
        {
            var parts = entry.FullName.Split('_');
            if (parts.Length != 3 || !int.TryParse(parts[1], out var id)) continue;
            if (parts[2] == "save") result.Maps[id] = Compress(Read(entry.FullName));
            if (parts[2] == "cmds") result.Commands[id] = ScheduledCommand.DeserializeCmds(Read(entry.FullName)).Select(ScheduledCommand.Serialize).ToList();
        }
        var global = "world/" + prefix + "_cmds";
        result.Commands[-1] = zip.GetEntry(global) != null ? ScheduledCommand.DeserializeCmds(Read(global)).Select(ScheduledCommand.Serialize).ToList() : [];
        result.Validate(); return result;
    }

    public static Checkpoint Capture(MultiplayerServer server) => new()
    {
        Tick = server.gameTimer, StartingTick = server.startingTimer, WorkTicks = server.workTicks,
        HostFaction = server.worldData.hostFactionId, SpectatorFaction = server.worldData.spectatorFactionId,
        World = server.worldData.savedGame!, Session = server.worldData.sessionData ?? [],
        Maps = server.worldData.mapData, Commands = server.worldData.mapCmds,
        AsyncTime = server.settings.asyncTime, Multifaction = server.settings.multifaction,
        ClientProtocol = Contract.Protocol
    };

    public void Validate()
    {
        if (Schema != 1 || World == null || World.Length == 0 || Maps == null || Commands == null || Session == null || Tick < StartingTick)
            throw new InvalidDataException("Контрольная точка несовместима или повреждена.");
        var legacy = ClientProtocol is 0 or 53 or 54;
        if (legacy && (Tick != StartingTick || Commands.Values.Any(commands => commands.Count != 0)))
            throw new InvalidDataException("Старое сохранение содержит историю команд. Загрузите его старым Multiplayer и сделайте обычное полное MP-сохранение, затем выберите новый ZIP. История не переигрывается под изменёнными RNG-правилами.");
        if (!(legacy && Tag == Contract.Tag) && !(ClientProtocol == Contract.Protocol && Tag == GuardTag))
            throw new InvalidDataException("Версия checkpoint не соответствует Guard. Нужна совместимая версия программы.");
        SnapshotCompression.Validate(World); SnapshotCompression.Validate(Session, allowEmpty: true);
        foreach (var map in Maps.Values) SnapshotCompression.Validate(map);
        foreach (var bytes in Commands.Values.SelectMany(v => v)) _ = ScheduledCommand.Deserialize(new ByteReader(bytes));
    }
    public void Apply(MultiplayerServer server)
    {
        Validate(); server.gameTimer = Tick; server.startingTimer = StartingTick; server.workTicks = WorkTicks;
        server.worldData.hostFactionId = HostFaction; server.worldData.spectatorFactionId = SpectatorFaction;
        server.worldData.savedGame = World; server.worldData.sessionData = Session;
        server.worldData.mapData = Maps; server.worldData.mapCmds = Commands;
        // The first reconnect cannot request a fresh joinpoint before any client can provide it.
        server.worldData.lastJoinPointAtWorkTicks = WorkTicks;
    }
    private static byte[] Compress(byte[] bytes)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionMode.Compress, true)) gzip.Write(bytes);
        return output.ToArray();
    }
}
