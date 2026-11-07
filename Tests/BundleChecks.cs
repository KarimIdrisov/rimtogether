using System.IO.Compression;

namespace RimTogether;

public static class BundleChecks
{
    public static void Run(string root, Action<bool, string> check, Action<Action, string> throws)
    {
        Directory.CreateDirectory(root);
        var updater = new BundleUpdate(Path.Combine(root, "app"));
        BundleManifest Package(string tag, bool complete = true)
        {
            var archive = Path.Combine(root, tag + ".zip");
            using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
            {
                var files = complete ? new[] { "RimTogether.exe", "server/RimTogether.Server.exe", "packages/Multiplayer-" + Contract.Tag + ".zip" } : new[] { "RimTogether.exe" };
                foreach (var name in files) { using var writer = new StreamWriter(zip.CreateEntry(name).Open()); writer.Write(tag); }
            }
            return new BundleManifest { Tag = tag, Commit = new string('a', 40), Size = new FileInfo(archive).Length, Sha256 = ModManager.Hash(archive) };
        }
        var first = Package("v1.1.0");
        updater.Install(Path.Combine(root, first.Tag + ".zip"), first);
        check(updater.Current()?.Manifest.Tag == first.Tag, "GitHub bundle installs a complete launcher/server/mod cohort");
        var next = Package("v1.1.1");
        updater.Install(Path.Combine(root, next.Tag + ".zip"), next);
        check(File.Exists(Path.Combine(updater.DirectoryFor(first), "RimTogether.exe")), "bundle update retains previous release directory");
        check(updater.Current()?.Manifest.Tag == next.Tag, "current pointer switches after verified extraction");
        var incomplete = Package("v1.1.2", false);
        throws(() => updater.Install(Path.Combine(root, incomplete.Tag + ".zip"), incomplete), "incomplete launcher/server bundle rejected");
        check(updater.Current()?.Manifest.Tag == next.Tag, "failed extraction does not change current version");
        var corrupt = Package("v1.1.3"); File.AppendAllText(Path.Combine(root, corrupt.Tag + ".zip"), "corrupt");
        throws(() => updater.Install(Path.Combine(root, corrupt.Tag + ".zip"), corrupt), "download size or SHA256 mismatch rejected");
        check(updater.Current()?.Manifest.Tag == next.Tag, "failed download preserves current version");
        File.WriteAllText(Path.Combine(updater.DirectoryFor(next), "RimTogether.exe"), "damaged");
        check(updater.Current()?.Manifest.Tag == first.Tag, "corrupt installed release falls back to last healthy pointer");
        throws(() => BundleUpdate.SafeFile(root, "../escape.exe"), "update metadata cannot escape installation directory");
        first.Schema = 9; throws(() => first.Validate(first.Tag), "future update schema rejected explicitly");
    }
}
