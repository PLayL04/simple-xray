using System.Collections.ObjectModel;

namespace SimpleXray.App.ViewModels;

/// <summary>
/// Updates a bound collection with minimal edits instead of Clear() + Add(): list controls (ComboBox, ListBox)
/// keep their selection and the closed ComboBox keeps showing the selected item. Clearing right after the user
/// picked an item left the ComboBox selection box empty in Avalonia.
/// </summary>
public static class CollectionSync
{
    /// <returns>True when the collection was changed.</returns>
    public static bool Sync<T>(ObservableCollection<T> target, IReadOnlyList<T> source)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(source);
        var comparer = EqualityComparer<T>.Default;
        var changed = false;

        for (var i = 0; i < source.Count; i++)
        {
            if (i >= target.Count)
            {
                target.Add(source[i]);
                changed = true;
            }
            else if (!comparer.Equals(target[i], source[i]))
            {
                target[i] = source[i];
                changed = true;
            }
        }

        while (target.Count > source.Count)
        {
            target.RemoveAt(target.Count - 1);
            changed = true;
        }

        return changed;
    }
}
