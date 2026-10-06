using QueueCache.Desktop.ViewModels;
using QueueCache.Operations.ManagedDisks;

namespace QueueCache.Desktop.Services;

public enum ChoiceKind { Primary, Normal, Destructive }

/// <summary>One button of a confirmation. Its text says exactly what happens ("Erase and stop").</summary>
public sealed record Choice(string Id, string Text, ChoiceKind Kind = ChoiceKind.Normal);

/// <summary>A question about an action with real consequences. Cancel is always offered; when a
/// choice loses data, Cancel is the default button, so Enter or Esc never confirms it.</summary>
public sealed record Confirmation(string Title, string Message, IReadOnlyList<Choice> Choices, string CancelText = "Cancel");

/// <summary>Everything a view-model shows modally goes through here, so tests can answer it.</summary>
public interface IDialogService
{
    /// <summary>The chosen <see cref="Choice.Id"/>, or null when cancelled.</summary>
    Task<string?> ConfirmAsync(Confirmation confirmation);
    Task<CacheSettingsResult?> EditCacheAsync(CacheSettingsViewModel editor);
    Task<ManagedDiskRuntime?> CreateDiskAsync(CreateDiskViewModel editor);
    Task<ManagedDiskOperationResult?> DiskActionAsync(DiskActionViewModel editor);
}
