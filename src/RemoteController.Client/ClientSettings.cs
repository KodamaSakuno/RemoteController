using System;
using System.IO;
using System.Text.Json;

namespace RemoteController.Client;

/// <summary>客户端用户级配置：记住上次连接的主机，免重复输入。</summary>
internal sealed record ClientSettings(string? LastHost)
{
    private static readonly string DirectoryPath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RemoteController");
    private static readonly string FilePath = Path.Combine(DirectoryPath, "client-settings.json");

    public static ClientSettings Load()
    {
        try
        {
            return JsonSerializer.Deserialize<ClientSettings>(File.ReadAllText(FilePath)) ?? new ClientSettings((string?)null);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return new ClientSettings((string?)null);
        }
    }

    public void Save()
    {
        Directory.CreateDirectory(DirectoryPath);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this));
    }
}
