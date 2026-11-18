using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using SameEasDecoder.Services;
using SameEasDecoder.ViewModels;

namespace SameEasDecoder;

public partial class MapWindow : Window
{
    public MapWindow(AlertItem alert)
    {
        InitializeComponent();
        DataContext = alert;
        Title = $"{alert.EventName} — affected area";
        SourceInitialized += (_, _) => NativeMethods.UseDarkTitleBar(new WindowInteropHelper(this).Handle);
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
    }
}
