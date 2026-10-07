using Avalonia;
using Avalonia.Headless;
using QueueCache.Desktop;

// Headless checks of the desktop app's view-models and views. Fixtures stand in for the driver and
// the managed-disk service: no volume or disk is opened. Screenshots of every page and dialog, in
// light and dark, are written to the output folder (default artifacts/ui-tests).
AppBuilder.Configure<App>().UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).SetupWithoutStarting();
if (args is ["--readme", var images])
{
    ReadmeImages.Run(images);
    return;
}
var output = args.Length == 0 ? "artifacts/ui-tests" : args[0];
Directory.CreateDirectory(output);
CacheTests.Run();
VirtualDiskTests.Run();
StatisticsTests.Run();
EditorTests.Run();
ViewTests.Run(output);
Console.WriteLine("Desktop checks passed; no real volume or disk operations performed.");
