using System.Text.Json.Serialization;

namespace RemoteController.Protocol;

/// <summary>
/// Server→Client 的首条控制消息。
/// Width/Height 为帧的纹理尺寸（面板原生方向，未旋转）；Rotation 为纹理相对逻辑桌面的旋转角（0/90/180/270），
/// 逻辑尺寸在 90/270 时为宽高互换；X/Y 为采集区域原点（逻辑坐标系），Client 据此建窗口与坐标映射。
/// </summary>
public sealed record Hello(int X, int Y, int Width, int Height, int Dpi, int Rotation);

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(MouseMove), "move")]
[JsonDerivedType(typeof(MouseButton), "button")]
[JsonDerivedType(typeof(MouseWheel), "wheel")]
public abstract record MouseMessage;

/// <summary>绝对物理像素坐标；原点在虚拟屏幕左上角。</summary>
public sealed record MouseMove(int X, int Y) : MouseMessage;

public sealed record MouseButton(MouseButtonKind Button, bool IsDown) : MouseMessage;

/// <summary>滚轮增量（单位：格；允许小数以保留触控板精细滚动），服务端乘 WHEEL_DELTA(120) 注入。</summary>
public sealed record MouseWheel(double Delta) : MouseMessage;

[JsonConverter(typeof(JsonStringEnumConverter<MouseButtonKind>))]
public enum MouseButtonKind
{
    Left,
    Right,
    Middle,
}
