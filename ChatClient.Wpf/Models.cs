using System.Windows;
using System.Windows.Media;
using ChatShared;

namespace ChatClientWpf;

/// <summary>Bảng màu dùng trong code (khớp với App.xaml).</summary>
public static class Palette
{
    public static readonly Brush Accent = Make(0x1D, 0x4E, 0xD8);
    public static readonly Brush Other = Make(0xE5, 0xE7, 0xEB);
    public static readonly Brush SystemBg = Make(0xF3, 0xF4, 0xF6);
    public static readonly Brush White = Make(0xFF, 0xFF, 0xFF);
    public static readonly Brush Ink = Make(0x11, 0x18, 0x27);
    public static readonly Brush Muted = Make(0x4B, 0x55, 0x63);
    public static readonly Brush Danger = Make(0x99, 0x1B, 0x1B);
    public static readonly Brush AdminBg = Make(0xFE, 0xE2, 0xE2);
    public static readonly Brush ModBg = Make(0xFE, 0xF3, 0xC7);
    public static readonly Brush ModFg = Make(0x92, 0x40, 0x0E);

    private static Brush Make(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze(); // brush đã đóng băng dùng được từ mọi luồng
        return brush;
    }
}

public enum LineKind { Mine, Other, System, Error }

/// <summary>Một dòng trong khung chat; các thuộc tính được XAML (DataTemplate) bind tới.</summary>
public sealed class ChatLine
{
    public ChatLine(LineKind kind, string text, string sender = "", string time = "")
    {
        Text = text;
        Header = kind is LineKind.Mine or LineKind.Other ? $"{sender}  {time}" : "";
        HeaderVisibility = Header.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        Align = kind switch
        {
            LineKind.Mine => HorizontalAlignment.Right,
            LineKind.Other => HorizontalAlignment.Left,
            _ => HorizontalAlignment.Center
        };
        Bubble = kind switch
        {
            LineKind.Mine => Palette.Accent,
            LineKind.Other => Palette.Other,
            _ => Palette.SystemBg
        };
        TextBrush = kind switch
        {
            LineKind.Mine => Palette.White,
            LineKind.Error => Palette.Danger,
            LineKind.System => Palette.Muted,
            _ => Palette.Ink
        };
        FontSizeValue = kind is LineKind.System or LineKind.Error ? 12 : 14;
    }

    public string Text { get; }
    public string Header { get; }
    public Visibility HeaderVisibility { get; }
    public HorizontalAlignment Align { get; }
    public Brush Bubble { get; }
    public Brush TextBrush { get; }
    public double FontSizeValue { get; }
}

/// <summary>Một người trong danh sách online.</summary>
public sealed class UserItem
{
    public UserItem(string name, Role role)
    {
        Name = name;
        Role = role;
        RoleText = role.ToWire();
        BadgeVisibility = role == Role.User ? Visibility.Collapsed : Visibility.Visible;
        BadgeBackground = role == Role.Admin ? Palette.AdminBg : Palette.ModBg;
        BadgeForeground = role == Role.Admin ? Palette.Danger : Palette.ModFg;
    }

    public string Name { get; }
    public Role Role { get; }
    public string RoleText { get; }
    public Visibility BadgeVisibility { get; }
    public Brush BadgeBackground { get; }
    public Brush BadgeForeground { get; }
}
