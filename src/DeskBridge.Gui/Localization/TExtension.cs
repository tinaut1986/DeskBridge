using System.ComponentModel;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Markup;
using DeskBridge.Core.Localization;

namespace DeskBridge.Gui.Localization;

/// <summary>
/// {l:T 'English text'} in XAML: the text in the current language, updated live when it changes.
/// Keys are English text, like L.T in code; scripts/tests find them with this exact shape.
/// </summary>
[MarkupExtensionReturnType(typeof(object))]
public sealed class TExtension(string key) : MarkupExtension
{
    [ConstructorArgument("key")]
    public string Key { get; set; } = key;

    public override object ProvideValue(IServiceProvider serviceProvider) =>
        new Binding(nameof(LanguageSource.Version))
        {
            Source = LanguageSource.Instance,
            Mode = BindingMode.OneWay,
            Converter = KeyConverter.Instance,
            ConverterParameter = Key,
        }.ProvideValue(serviceProvider);

    private sealed class KeyConverter : IValueConverter
    {
        public static readonly KeyConverter Instance = new();

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => L.T((string)parameter);

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
    }
}

/// <summary>Changes its <see cref="Version"/> whenever the language changes, so every {l:T} binding refreshes.</summary>
public sealed class LanguageSource : INotifyPropertyChanged
{
    public static readonly LanguageSource Instance = new();

    private LanguageSource() => L.Changed += () =>
    {
        Version++;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Version)));
    };

    public int Version { get; private set; }

    public event PropertyChangedEventHandler? PropertyChanged;
}
