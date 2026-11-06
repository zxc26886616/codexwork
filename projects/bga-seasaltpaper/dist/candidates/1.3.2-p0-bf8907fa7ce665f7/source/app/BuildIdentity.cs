using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace SeaSaltDesk;

public static class BuildIdentity
{
    // 冻结包的三个入口报告实际加载位置，包括包含注入 JS 资源的业务程序集。
    public static void Write(params Assembly[] additional)
    {
        static string Hash(string path)
        {
            using var file = File.OpenRead(path);
            return Convert.ToHexString(SHA256.HashData(file));
        }
        var path = Environment.ProcessPath!;
        var assemblies = additional.Prepend(typeof(BuildIdentity).Assembly).Append(Assembly.GetEntryAssembly()!).Distinct();
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            process = path, processSha256 = Hash(path),
            loaded = assemblies.Select(a => new { name = a.GetName().Name, path = a.Location,
                sha256 = a.Location.Length == 0 ? null : Hash(a.Location), module = a.ManifestModule.ModuleVersionId }),
        }));
    }
}
