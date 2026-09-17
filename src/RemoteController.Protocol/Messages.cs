using System.Text.Json.Serialization;

namespace RemoteController.Protocol;

/// <summary>Server→Client 的首条控制消息：虚拟屏幕尺寸与 DPI，Client 据此建窗口与坐标映射。</summary>
public sealed record Hello(int Width, int Height, int Dpi);

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(MouseMove), "move")]
[JsonDerivedType(typeof(MouseButton), "button")]
public abstract record MouseMessage;

/// <summary>绝对物理像素坐标；原点在虚拟屏幕左上角。</summary>
public sealed record MouseMove(int X, int Y) : MouseMessage;

public sealed record MouseButton(MouseButtonKind Button, bool IsDown) : MouseMessage;

[JsonConverter(typeof(JsonStringEnumConverter<MouseButtonKind>))]
public enum MouseButtonKind
{
    Left,
    Right,
    Middle,
}
