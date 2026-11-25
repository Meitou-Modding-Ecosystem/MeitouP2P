using System.Security.Cryptography;
using Meitou.Content;
using Meitou.Data;
using Meitou.Modding;

namespace MeitouP2P;

internal static class Fingerprints
{
    internal static IReadOnlyDictionary<string,string> Read(ModContext context)
    {
        // Resolve the same active load order as the game, never hash machine-specific absolute paths.
        string root = Environment.GetEnvironmentVariable("KENSHI_PATH")
            ?? Directory.GetParent(Directory.GetParent(context.Folder)!.FullName)!.FullName;
        var order = LoadOrder.FromInstall(GameInstall.Open(root));
        var result = new SortedDictionary<string,string>(StringComparer.Ordinal);
        foreach (var entry in order.Entries)
        {
            Hash(entry.Name, entry.Path);
            if (LoadOrder.BaseFiles.Contains(entry.Name)) continue;
            string folder = Path.GetDirectoryName(entry.Path)!;
            foreach (var path in Directory.EnumerateFiles(folder).Where(p => Path.GetExtension(p) is ".dll" or ".json").Order(StringComparer.Ordinal))
                Hash(entry.Name + "/" + Path.GetFileName(path), path);
        }
        context.Log($"Compatibility hashes prepared for {result.Count} files.");
        return result;
        void Hash(string key, string path)
        {
            using var input = File.OpenRead(path);
            result.Add(key, Convert.ToHexString(SHA256.HashData(input)));
        }
    }
}
