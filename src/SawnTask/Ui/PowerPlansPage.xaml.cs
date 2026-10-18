using System.Windows;
using System.Windows.Controls;
using SawnTask.Core;

namespace SawnTask.Ui;

public partial class PowerPlansPage : UserControl
{
    bool loading;

    public PowerPlansPage()
    {
        InitializeComponent();
        // For --snapshot: SAWNTASK_POWER_EXTRA=1 opens the extra settings.
        if (Environment.GetEnvironmentVariable("SAWNTASK_POWER_EXTRA") == "1") ExtraToggle.IsChecked = true;
    }

    // The page was opened: read the plans again (another program may have changed them).
    public void Show() => Reload(SelectedPlan?.Id);

    public void Relocalize() => Reload(SelectedPlan?.Id);

    PowerPlan? SelectedPlan => (Plans.SelectedItem as ListBoxItem)?.Tag as PowerPlan;

    void Reload(Guid? keep)
    {
        loading = true;
        try
        {
            Plans.Items.Clear();
            foreach (var plan in PowerPlans.List())
            {
                var item = new ListBoxItem { Content = plan.Active ? $"{plan.Name}   ✓" : plan.Name, Tag = plan };
                Plans.Items.Add(item);
                if (plan.Id == keep || (keep is null && plan.Active)) Plans.SelectedItem = item;
            }
            if (Plans.SelectedItem is null && Plans.Items.Count > 0) Plans.SelectedIndex = 0;
        }
        catch (Exception ex) { Fail(ex); }
        finally { loading = false; }
        ShowPlan();
    }

    void Plans_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!loading) ShowPlan();
    }

    // The right-hand card: one row per setting, with a value for battery and one for mains.
    void ShowPlan()
    {
        SettingsGrid.Children.Clear();
        SettingsGrid.RowDefinitions.Clear();
        var plan = SelectedPlan;
        PlanTitle.Text = plan?.Name ?? "";
        ActivateButton.IsEnabled = plan is { Active: false };
        RenameButton.IsEnabled = DeleteButton.IsEnabled = plan is not null;
        if (plan is null) return;

        SettingsGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        foreach (var (text, column) in new[] { (Loc.T("PP.OnBattery"), 1), (Loc.T("PP.PluggedIn"), 2) })
        {
            var header = new TextBlock { Text = text, Margin = new Thickness(4, 0, 0, 4) };
            header.SetResourceReference(TextBlock.ForegroundProperty, "Dim");
            Place(header, 0, column);
        }

        try
        {
            for (int i = 0; i < PowerPlans.Settings.Length; i++)
            {
                var setting = PowerPlans.Settings[i];
                SettingsGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var label = new TextBlock { Margin = new Thickness(0, 6, 0, 6), VerticalAlignment = VerticalAlignment.Center };
                label.SetResourceReference(TextBlock.TextProperty, setting.TextKey);
                Place(label, i + 1, 0);
                Place(MakeCombo(plan, setting, pluggedIn: false), i + 1, 1);
                Place(MakeCombo(plan, setting, pluggedIn: true), i + 1, 2);
            }
        }
        catch (Exception ex) { Fail(ex); }
        if (ExtraToggle.IsChecked == true) LoadExtra();
    }

    void Place(UIElement element, int row, int column) => Place(SettingsGrid, element, row, column);

    static void Place(Grid grid, UIElement element, int row, int column, int span = 1)
    {
        Grid.SetRow(element, row);
        Grid.SetColumn(element, column);
        Grid.SetColumnSpan(element, span);
        grid.Children.Add(element);
    }

    // ---- Extra settings (Windows' "Change advanced power settings") ----

    int extraVersion; // a newer load makes an older, still running one throw its result away

    void ExtraToggle_Changed(object sender, RoutedEventArgs e)
    {
        bool open = ExtraToggle.IsChecked == true;
        ExtraPanel.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        if (open) LoadExtra();
    }

    async void LoadExtra()
    {
        int version = ++extraVersion;
        ExtraList.Children.Clear();
        if (SelectedPlan is not { } plan) return;

        ExtraList.Children.Add(new TextBlock { Text = Loc.T("PP.Loading") });
        List<AdvancedGroup> groups;
        try { groups = await Task.Run(() => PowerPlans.Advanced(plan.Id)); }
        catch (Exception ex) { Fail(ex); return; }
        if (version != extraVersion) return;

        ExtraList.Children.Clear();
        var headers = ColumnsGrid();
        headers.Margin = new Thickness(GroupPadding, 0, GroupPadding, 0);
        foreach (var (text, column) in new[] { (Loc.T("PP.OnBattery"), 1), (Loc.T("PP.PluggedIn"), 2) })
        {
            var header = new TextBlock { Text = text, Margin = new Thickness(4, 0, 0, 2) };
            header.SetResourceReference(TextBlock.ForegroundProperty, "Dim");
            Place(headers, header, 0, column);
        }
        ExtraList.Children.Add(headers);

        // The settings of the card above are not repeated here.
        var basic = PowerPlans.Settings.Select(s => s.Id).ToHashSet();
        foreach (var group in groups)
        {
            var settings = group.Settings.Where(s => !basic.Contains(s.Id)).ToList();
            if (settings.Count == 0) continue;

            var title = WithInfo(group.Name, group.Description);
            title.FontSize = 15;
            title.FontWeight = FontWeights.SemiBold;
            title.Margin = new Thickness(0, 0, 0, 6);
            var rows = ColumnsGrid();
            for (int i = 0; i < settings.Count; i++)
            {
                var setting = settings[i];
                rows.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var name = WithInfo(setting.Name, setting.Description);
                name.Margin = new Thickness(0, 6, 8, 6);
                Place(rows, name, i, 0);
                try
                {
                    Place(rows, MakeEditor(plan, setting, pluggedIn: false), i, 1);
                    Place(rows, MakeEditor(plan, setting, pluggedIn: true), i, 2);
                }
                catch (Exception ex) { Place(rows, new TextBlock { Text = ex.Message, TextWrapping = TextWrapping.Wrap }, i, 1, 2); }
            }

            var box = new Border
            {
                CornerRadius = new CornerRadius(6),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(GroupPadding, 10, GroupPadding, 8),
                Margin = new Thickness(0, 8, 0, 0),
                Child = new StackPanel { Children = { title, rows } },
            };
            box.SetResourceReference(Border.BackgroundProperty, "AltRow");
            box.SetResourceReference(Border.BorderBrushProperty, "Line");
            ExtraList.Children.Add(box);
        }
    }

    const double GroupPadding = 12;

    // Name | on battery | plugged in: the same columns for the headers and every group, so they line up.
    static Grid ColumnsGrid() => new()
    {
        ColumnDefinitions =
        {
            new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
            new ColumnDefinition { Width = new GridLength(200) },
            new ColumnDefinition { Width = new GridLength(200) },
        },
    };

    // A name followed by a small (i); hovering the (i) shows Windows' explanation of the setting.
    static TextBlock WithInfo(string name, string description)
    {
        var text = new TextBlock { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        text.Inlines.Add(new System.Windows.Documents.Run(name));
        if (description.Trim().Length == 0) return text;

        var icon = new TextBlock
        {
            Text = "",
            FontFamily = new System.Windows.Media.FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            FontSize = 13,
            Margin = new Thickness(6, 0, 0, -2),
            Cursor = System.Windows.Input.Cursors.Help,
            ToolTip = new TextBlock { Text = description.Trim(), TextWrapping = TextWrapping.Wrap, MaxWidth = 380 },
        };
        icon.SetResourceReference(TextBlock.ForegroundProperty, "Brand");
        ToolTipService.SetInitialShowDelay(icon, 100);
        ToolTipService.SetShowDuration(icon, 60000);
        text.Inlines.Add(new System.Windows.Documents.InlineUIContainer(icon) { BaselineAlignment = BaselineAlignment.Center });
        return text;
    }

    // Named choices: a list. Times in seconds (their range ends at the largest number): the same
    // list as the card above. Other numbers (percentages): a box to type in, checked against the range.
    FrameworkElement MakeEditor(PowerPlan plan, AdvancedSetting setting, bool pluggedIn)
    {
        uint current = PowerPlans.Read(plan.Id, setting.SubGroup, setting.Id, pluggedIn);
        void Save(uint value)
        {
            PowerPlans.Write(plan.Id, setting.SubGroup, setting.Id, pluggedIn, value);
            current = value;
        }

        if (setting.Options.Count > 0 || setting.Max == uint.MaxValue)
        {
            var choices = setting.Options.Count > 0
                ? setting.Options.Select(o => (Value: o.Value, Name: o.Name)).ToList()
                : PowerPlans.SecondChoices.Select(v => (Value: v, Name: Describe(v, PowerUnit.Seconds))).ToList();
            if (!choices.Any(c => c.Value == current))
                choices = [.. choices.Append((Value: current, Name: setting.Options.Count > 0 ? current.ToString() : Describe(current, PowerUnit.Seconds))).OrderBy(c => c.Value)];

            var combo = new ComboBox { Margin = new Thickness(4, 2, 4, 2) };
            foreach (var (value, name) in choices) combo.Items.Add(new ComboBoxItem { Content = name, Tag = value });
            combo.SelectedIndex = choices.FindIndex(c => c.Value == current);
            combo.SelectionChanged += (_, _) =>
            {
                if (combo.SelectedItem is not ComboBoxItem { Tag: uint chosen } || chosen == current) return;
                try { Save(chosen); }
                catch (Exception ex) { Fail(ex); combo.SelectedIndex = choices.FindIndex(c => c.Value == current); }
            };
            return combo;
        }

        var box = new TextBox
        {
            Text = current.ToString(),
            Margin = new Thickness(4, 2, 4, 2),
            ToolTip = Loc.F("PP.Range", setting.Min, setting.Max, setting.Units),
        };
        var units = new TextBlock { Text = setting.Units, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 4, 0) };
        units.SetResourceReference(TextBlock.ForegroundProperty, "Dim");
        void Commit()
        {
            if (uint.TryParse(box.Text.Trim(), out uint typed) && typed >= setting.Min && typed <= setting.Max)
            {
                if (typed == current) return;
                try { Save(typed); return; }
                catch (Exception ex) { Fail(ex); }
            }
            box.Text = current.ToString(); // not a valid number: show the saved value again
        }
        box.LostFocus += (_, _) => Commit();
        box.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Enter) Commit(); };
        var panel = new DockPanel();
        DockPanel.SetDock(units, Dock.Right);
        panel.Children.Add(units);
        panel.Children.Add(box);
        return panel;
    }

    ComboBox MakeCombo(PowerPlan plan, PowerSetting setting, bool pluggedIn)
    {
        uint current = PowerPlans.Read(plan.Id, setting, pluggedIn);
        var choices = (setting.Unit == PowerUnit.Seconds ? PowerPlans.SecondChoices : PowerPlans.PercentChoices).ToList();
        if (!choices.Contains(current)) choices = [.. choices.Append(current).Order()];

        var combo = new ComboBox { Margin = new Thickness(4, 2, 4, 2) };
        foreach (uint value in choices) combo.Items.Add(new ComboBoxItem { Content = Describe(value, setting.Unit), Tag = value });
        combo.SelectedIndex = choices.IndexOf(current);
        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedItem is not ComboBoxItem { Tag: uint chosen } || chosen == current) return;
            try
            {
                PowerPlans.Write(plan.Id, setting, pluggedIn, chosen);
                current = chosen;
            }
            catch (Exception ex) { Fail(ex); ShowPlan(); }
        };
        return combo;
    }

    static string Describe(uint value, PowerUnit unit) => unit switch
    {
        PowerUnit.Percent => $"{value}%",
        _ when value == 0 => Loc.T("PP.Never"),
        _ when value < 3600 => Loc.F("PP.Minutes", value / 60.0),
        _ => Loc.F("PP.Hours", value / 3600.0),
    };

    void Activate_Click(object sender, RoutedEventArgs e) => Run(plan => PowerPlans.Activate(plan.Id));

    void New_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedPlan is not { } source) return;
        string? name = PromptWindow.Ask(Window.GetWindow(this)!, Loc.T("PP.New"), Loc.T("PP.NamePrompt"), Loc.F("PP.CopyName", source.Name));
        if (name is null) return;
        try
        {
            Guid created = PowerPlans.Duplicate(source.Id, name);
            Reload(created);
        }
        catch (Exception ex) { Fail(ex); }
    }

    void Rename_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedPlan is not { } plan) return;
        string? name = PromptWindow.Ask(Window.GetWindow(this)!, Loc.T("PP.Rename"), Loc.T("PP.NamePrompt"), plan.Name);
        if (name is not null) Run(p => PowerPlans.Rename(p.Id, name));
    }

    void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedPlan is not { } plan) return;
        var owner = Window.GetWindow(this);
        if (plan.Active)
        {
            MessageBox.Show(owner, Loc.T("PP.DeleteActive"), Loc.T("PP.Delete"), MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (MessageBox.Show(owner, Loc.F("PP.DeleteAsk", plan.Name), Loc.T("PP.Delete"), MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        try
        {
            PowerPlans.Delete(plan.Id);
            Reload(null);
        }
        catch (Exception ex) { Fail(ex); }
    }

    // Runs an action on the selected plan, then shows the plans again.
    void Run(Action<PowerPlan> action)
    {
        if (SelectedPlan is not { } plan) return;
        try
        {
            action(plan);
            Reload(plan.Id);
        }
        catch (Exception ex) { Fail(ex); }
    }

    void Fail(Exception ex) =>
        MessageBox.Show(Window.GetWindow(this), ex.Message, Loc.T("PP.Title"), MessageBoxButton.OK, MessageBoxImage.Warning);
}
