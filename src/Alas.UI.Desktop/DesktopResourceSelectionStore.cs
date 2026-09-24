using System.Security.Cryptography;
using System.Text;
using Alas.UI.Overview;

namespace Alas.UI.Desktop;

public sealed class DesktopResourceSelectionStore(string? directory = null) : IResourceSelectionStore
{
    private readonly string _directory = directory ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AlasSharp", "resource-cards");

    private string FileName(string instance) => Path.Combine(_directory,
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(instance))) + ".json");

    public string? Read(string instance)
    {
        string path = FileName(instance);
        if (!File.Exists(path)) return null;
        if (new FileInfo(path).Length > 1_000_000) throw new IOException("资源卡片偏好文件过大");
        return File.ReadAllText(path, Encoding.UTF8);
    }

    public void Write(string instance, string json)
    {
        Directory.CreateDirectory(_directory);
        string path = FileName(instance), temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, json, new UTF8Encoding(false));
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
