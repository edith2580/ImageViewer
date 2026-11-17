using System.Runtime.CompilerServices;
using Avalonia.Skia;

namespace ImageViewer.IntegrationTests;

internal static class AvaloniaTestInit
{
    [ModuleInitializer]
    internal static void Init()
    {
        SkiaPlatform.Initialize();
    }
}
