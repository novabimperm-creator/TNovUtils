using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace TNovUtils.Checklist.UI
{
    /// <summary>
    /// Копия TNovCommon.StringToVisibilityConverter. В XAML тип из TNovCommon подгружается по короткому имени
    /// сборки и в Revit не находится (FileNotFoundException), поэтому для окон Чек-листа конвертер свой.
    /// </summary>
    public class LocalStringToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => string.IsNullOrEmpty(value as string) ? Visibility.Collapsed : Visibility.Visible;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }
}
