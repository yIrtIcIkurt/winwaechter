namespace WinWaechter.Core.Actions;

/// <summary>Where one entry of a batch run stands (a line of a list that is worked through one by one).</summary>
public enum ItemState { None, Waiting, Running, Done, Failed, Skipped }

/// <summary>
/// One entry of a batch run changed its state. Item is the object the caller handed in (a Finding, a package …);
/// the list matches it by reference. Text: the reason for Failed or Skipped.
/// </summary>
public sealed record ItemProgress(object Item, ItemState State, string? Text = null);
