using System.ComponentModel;
using SawnTask.Core;

namespace SawnTask.Ui;

// One row of the Sources page, updated in place every tick. Giving the grid a new list each
// tick made it rebuild every row twice a second; changing a few cells is almost free.
public sealed class SourceRow(string id) : INotifyPropertyChanged
{
    public string Id { get; } = id;
    public SourceHealth Health { get; private set; }
    public string LastGoodText { get; private set; } = "";
    public string Detail { get; private set; } = "";
    public string Description { get; private set; } = "";

    public event PropertyChangedEventHandler? PropertyChanged;

    public void Update(SourceStatus s)
    {
        if (Health != s.Health) { Health = s.Health; Raise(nameof(Health)); }
        if (LastGoodText != s.LastGoodText) { LastGoodText = s.LastGoodText; Raise(nameof(LastGoodText)); }
        if (Detail != s.Detail) { Detail = s.Detail; Raise(nameof(Detail)); }
        if (Description != s.Description) { Description = s.Description; Raise(nameof(Description)); }
    }

    void Raise(string property) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
}
