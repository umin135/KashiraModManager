using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace Kashira.Editor;

/// <summary>
/// 자체 벡터 아이콘(채워진 실루엣, PathIcon 으로 렌더). 무의존 — Avalonia 12 호환.
/// 데이터 바인딩: <c>Data="{Binding Icon, Converter={StaticResource IconKind}}"</c>(키 문자열).
/// 정적: <c>Data="{Binding Converter={StaticResource IconKind}, ConverterParameter=folder}"</c>.
/// 알 수 없는 키는 file 로 폴백.
/// </summary>
public sealed class IconKindConverter : IValueConverter
{
    private static readonly Dictionary<string, Geometry> Icons = new(StringComparer.OrdinalIgnoreCase)
    {
        ["folder"]   = Geometry.Parse("M2,6 L9,6 L11,8 L21,8 L21,19 L2,19 Z"),
        ["texture"]  = Geometry.Parse("F0 M3,5 L21,5 L21,19 L3,19 Z M5,17 L9.5,11 L12.5,14.5 L16,10 L20,17 Z"),
        ["mesh"]     = Geometry.Parse("M12,2 L21,7 L21,17 L12,22 L3,17 L3,7 Z"),
        ["material"] = new EllipseGeometry(new Avalonia.Rect(2, 2, 20, 20)),
        ["file"]     = Geometry.Parse("M6,2 L14,2 L20,8 L20,22 L6,22 Z"),
    };

    public static Geometry Get(string? key)
        => key is not null && Icons.TryGetValue(key, out var g) ? g : Icons["file"];

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Get(value as string ?? parameter as string);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
