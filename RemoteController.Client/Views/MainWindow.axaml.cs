using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using RemoteController.Client.Services;
using RemoteController.Client.ViewModels;
using RemoteController.Shared.Protocol;

namespace RemoteController.Client.Views;

public partial class MainWindow : Window
{
    private double _wheelRemainder;

    public MainWindow()
    {
        InitializeComponent();

        RemoteImage.PointerMoved += OnPointerMoved;
        RemoteImage.PointerPressed += OnPointerPressed;
        RemoteImage.PointerReleased += OnPointerReleased;
        RemoteImage.PointerWheelChanged += OnPointerWheelChanged;
        // Tunneling so Tab / arrow keys reach the remote host instead of moving local focus.
        RemoteImage.AddHandler(InputElement.KeyDownEvent, OnRemoteKeyDown, RoutingStrategies.Tunnel);
        RemoteImage.AddHandler(InputElement.KeyUpEvent, OnRemoteKeyUp, RoutingStrategies.Tunnel);
    }

    private MainViewModel? ViewModel => DataContext as MainViewModel;

    /// <summary>
    /// Maps a point on the (Uniform-stretched, letterboxed) image to remote screen
    /// coordinates, clamped to the remote screen bounds.
    /// </summary>
    private bool TryMapToRemote(Point position, out int x, out int y)
    {
        x = y = 0;
        var vm = ViewModel;
        if (vm is not { IsConnected: true } || vm.RemoteWidth <= 0 || vm.RemoteHeight <= 0)
            return false;

        var bounds = RemoteImage.Bounds;
        if (bounds.Width <= 0 || bounds.Height <= 0)
            return false;

        var scale = Math.Min(bounds.Width / vm.RemoteWidth, bounds.Height / vm.RemoteHeight);
        var offsetX = (bounds.Width - vm.RemoteWidth * scale) / 2;
        var offsetY = (bounds.Height - vm.RemoteHeight * scale) / 2;

        x = Math.Clamp((int)((position.X - offsetX) / scale), 0, vm.RemoteWidth - 1);
        y = Math.Clamp((int)((position.Y - offsetY) / scale), 0, vm.RemoteHeight - 1);
        return true;
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (TryMapToRemote(e.GetPosition(RemoteImage), out var x, out var y))
            ViewModel?.Connection.SendMouseMove(x, y);
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        // Keep key input flowing to the remote session while the image has focus.
        RemoteImage.Focus();
        if (!TryMapToRemote(e.GetPosition(RemoteImage), out var x, out var y))
            return;

        // Capture so a drag that leaves the image still reports its release.
        e.Pointer.Capture(RemoteImage);

        if (ToRemoteButton(e.GetCurrentPoint(RemoteImage).Properties.PointerUpdateKind) is { } button)
        {
            ViewModel?.Connection.SendMouseMove(x, y);
            ViewModel?.Connection.SendMouseButton(button, true);
            e.Handled = true;
        }
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (ToRemoteButton(e.GetCurrentPoint(RemoteImage).Properties.PointerUpdateKind) is { } button)
        {
            ViewModel?.Connection.SendMouseButton(button, false);
            e.Handled = true;
        }

        var properties = e.GetCurrentPoint(RemoteImage).Properties;
        if (!properties.IsLeftButtonPressed && !properties.IsRightButtonPressed && !properties.IsMiddleButtonPressed)
            e.Pointer.Capture(null);
    }

    private void OnPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (ViewModel is not { IsConnected: true } vm)
            return;

        // Accumulate fractional deltas (trackpads) into whole wheel steps.
        _wheelRemainder += e.Delta.Y;
        var steps = (int)_wheelRemainder;
        if (steps != 0)
        {
            _wheelRemainder -= steps;
            vm.Connection.SendMouseWheel(steps);
        }

        e.Handled = true;
    }

    private void OnRemoteKeyDown(object? sender, KeyEventArgs e) => ForwardKey(e, true);

    private void OnRemoteKeyUp(object? sender, KeyEventArgs e) => ForwardKey(e, false);

    private void ForwardKey(KeyEventArgs e, bool down)
    {
        if (ViewModel is not { IsConnected: true } vm)
            return;

        if (KeyMapper.ToVirtualKey(e.Key) is { } virtualKey)
        {
            vm.Connection.SendKey(virtualKey, down);
            e.Handled = true;
        }
    }

    private static RemoteMouseButton? ToRemoteButton(PointerUpdateKind kind) => kind switch
    {
        PointerUpdateKind.LeftButtonPressed or PointerUpdateKind.LeftButtonReleased => RemoteMouseButton.Left,
        PointerUpdateKind.RightButtonPressed or PointerUpdateKind.RightButtonReleased => RemoteMouseButton.Right,
        PointerUpdateKind.MiddleButtonPressed or PointerUpdateKind.MiddleButtonReleased => RemoteMouseButton.Middle,
        _ => null,
    };

    protected override void OnClosed(EventArgs e)
    {
        ViewModel?.Connection.Dispose();
        base.OnClosed(e);
    }
}
