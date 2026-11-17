using System.Globalization;
using Avalonia.Data.Converters;

namespace ImageViewer.ViewModels;

/// <summary>목록 항목 위치가 현재 표시 중인 이미지 위치와 같은지 확인한다.</summary>
public sealed class IsCurrentItemConverter : IMultiValueConverter
{
    public static IsCurrentItemConverter Instance { get; } = new();

    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture) =>
        values.Count == 2 && values[0] is int a && values[1] is int b && a == b;
}
