namespace QueueCache.Desktop.ViewModels;

/// <summary>How an item is doing, always shown as an icon plus a word. Idle is a normal resting
/// state (no cache, paused, stopped); Unknown means QueueCache has no fresh answer, never zero.</summary>
public enum Health { Ok, Attention, Error, Unknown, Idle }
