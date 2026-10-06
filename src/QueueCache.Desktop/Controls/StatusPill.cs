using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using QueueCache.Desktop.ViewModels;

namespace QueueCache.Desktop.Controls;

/// <summary>A health state as an icon plus a word, never color alone. Its look per state lives in
/// Themes/Controls.axaml (pseudo-classes :ok :attention :error :unknown :idle).</summary>
public sealed class StatusPill : TemplatedControl
{
    public static readonly StyledProperty<Health> HealthProperty = AvaloniaProperty.Register<StatusPill, Health>(nameof(Health), Health.Unknown);
    public static readonly StyledProperty<string?> TextProperty = AvaloniaProperty.Register<StatusPill, string?>(nameof(Text));

    public Health Health
    {
        get => GetValue(HealthProperty);
        set => SetValue(HealthProperty, value);
    }
    public string? Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public StatusPill() => UpdateClasses(Health);

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == HealthProperty)
            UpdateClasses(change.GetNewValue<Health>());
    }

    private void UpdateClasses(Health health)
    {
        PseudoClasses.Set(":ok", health == Health.Ok);
        PseudoClasses.Set(":attention", health == Health.Attention);
        PseudoClasses.Set(":error", health == Health.Error);
        PseudoClasses.Set(":unknown", health == Health.Unknown);
        PseudoClasses.Set(":idle", health == Health.Idle);
    }
}
