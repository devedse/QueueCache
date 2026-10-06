using Avalonia.Controls.Primitives;

namespace QueueCache.Desktop.Controls;

/// <summary>A quiet "Volatile" mark on every Fast cache and RAM-backed disk: data held there is
/// lost at a crash or power cut until it reaches a disk. Its look lives in Themes/Controls.axaml.</summary>
public sealed class VolatileBadge : TemplatedControl;
