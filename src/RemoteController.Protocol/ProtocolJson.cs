using System.Text.Json;

namespace RemoteController.Protocol;

/// <summary>两端共用的 JSON 序列化选项，保证控制消息口径一致。</summary>
public static class ProtocolJson
{
    public static readonly JsonSerializerOptions Options = new();
}
