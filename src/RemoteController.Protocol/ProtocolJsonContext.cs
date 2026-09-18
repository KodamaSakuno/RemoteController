using System.Text.Json.Serialization;

namespace RemoteController.Protocol;

/// <summary>
/// 源生成序列化上下文：编译期生成 Hello/MouseMessage 的序列化元数据，
/// 替代反射以获得构建期契约校验与 AOT/裁剪兼容性。行为与默认 JsonSerializerOptions 等价。
/// </summary>
[JsonSourceGenerationOptions]
[JsonSerializable(typeof(Hello))]
[JsonSerializable(typeof(MouseMessage))]
public partial class ProtocolJsonContext : JsonSerializerContext;
