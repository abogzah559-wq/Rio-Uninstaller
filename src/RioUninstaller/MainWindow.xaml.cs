using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using RioUninstaller.Models;
using RioUninstaller.Services;

namespace RioUninstaller;

public sealed class StepVM : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler PropertyChanged;
    public string Key { get; init; }
    public string Title => Loc.T(Key);
    StepState _state;
    public StepState State { get => _state; set { _state = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(State))); } }
}

public partial class MainWindow : Window
{
    ObservableCollection<InstalledApp> _apps = new();
    List<InstalledApp> _all = new();
    ICollectionView _view;
    InstalledApp _sel;
    bool _busy;
    CancellationTokenSource _inspectCts;
    TaskCompletionSource<bool> _dlg;
    bool _detailsOpen;

    public MainWindow()
    {
        InitializeComponent();
        BindList(new List<InstalledApp>());
        ApplyLanguage();
        Loaded += async (_, _) =>
        {
            RoundCorners();
            await LoadAppsAsync();
            SearchBox.Focus();
        };
        StateChanged += (_, _) =>
        {
            Root.Margin = WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(0);
            MaxButton.Content = WindowState == WindowState.Maximized ? "" : "";
        };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                if (SettingsOverlay.Visibility == Visibility.Visible) { CloseSettings(); e.Handled = true; }
                else if (Overlay.Visibility == Visibility.Visible && DlgButtons.Visibility == Visibility.Visible) { DlgCancel_Click(null, null); e.Handled = true; }
            }
        };
    }

    void RoundCorners()
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            int round = 2;      // DWMWCP_ROUND
            Native.DwmSetWindowAttribute(hwnd, 33, ref round, sizeof(int));
            int dark = 1;
            Native.DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int));
        }
        catch { }
    }

    // ================= list =================

    void BindList(List<InstalledApp> list)
    {
        _apps = new ObservableCollection<InstalledApp>(list);
        _view = CollectionViewSource.GetDefaultView(_apps);
        _view.Filter = Filter;
        AppList.ItemsSource = _view;
        ApplySort();
    }

    async Task LoadAppsAsync()
    {
        var keepId = _sel?.Id;
        var list = await Task.Run(() => AppScanner.Scan(Settings.Current.ShowSystem));
        var disabled = await Task.Run(() => BackgroundControl.DisabledIds());
        foreach (var a in list) a.BackgroundDisabled = disabled.Contains(a.Id);
        _all = list;
        BindList(list);
        UpdateCount();

        if (keepId != null)
        {
            var again = list.FirstOrDefault(a => a.Id == keepId);
            if (again != null) AppList.SelectedItem = again; else ShowDetails(null);
        }

        _ = Task.Run(() =>
        {
            foreach (var a in list)
            {
                var ic = IconLoader.Load(a);
                if (ic != null) Dispatcher.InvokeAsync(() => { a.Icon = ic; if (_sel == a) SetDetailIcon(a); });
            }
        });
    }

    bool Filter(object o)
    {
        var q = SearchBox.Text?.Trim();
        if (string.IsNullOrEmpty(q)) return true;
        var a = (InstalledApp)o;
        foreach (var part in q.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            bool hit = (a.Name?.Contains(part, StringComparison.CurrentCultureIgnoreCase) ?? false) ||
                       (a.Publisher?.Contains(part, StringComparison.CurrentCultureIgnoreCase) ?? false);
            if (!hit) return false;
        }
        return true;
    }

    void ApplySort()
    {
        if (_view == null || SortName == null) return;
        using (_view.DeferRefresh())
        {
            _view.SortDescriptions.Clear();
            if (SortSize.IsChecked == true) _view.SortDescriptions.Add(new SortDescription(nameof(InstalledApp.SizeKb), ListSortDirection.Descending));
            else if (SortDate.IsChecked == true) _view.SortDescriptions.Add(new SortDescription(nameof(InstalledApp.InstallDate), ListSortDirection.Descending));
            _view.SortDescriptions.Add(new SortDescription(nameof(InstalledApp.Name), ListSortDirection.Ascending));
        }
    }

    void UpdateCount()
    {
        int n = _view?.Cast<object>().Count() ?? 0;
        CountText.Text = string.Format(Loc.T("Count"), n);
        EmptyText.Visibility = n == 0 && _apps.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        SearchHint.Visibility = string.IsNullOrEmpty(SearchBox.Text) ? Visibility.Visible : Visibility.Collapsed;
        _view?.Refresh();
        UpdateCount();
        if (AppList.SelectedItem == null && !AppList.Items.IsEmpty && !string.IsNullOrEmpty(SearchBox.Text))
            AppList.SelectedIndex = 0;
    }

    void Sort_Checked(object sender, RoutedEventArgs e) => ApplySort();

    async void Refresh_Click(object sender, RoutedEventArgs e) => await LoadAppsAsync();

    void AppList_SelectionChanged(object sender, SelectionChangedEventArgs e) => ShowDetails(AppList.SelectedItem as InstalledApp);

    void AppList_MouseDoubleClick(object sender, MouseButtonEventArgs e) { }

    async void AppList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete && _sel != null)
        {
            e.Handled = true;
            await RunUninstall(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? UninstallMode.Full : UninstallMode.Normal);
        }
    }

    // ================= details =================

    void ShowDetails(InstalledApp a)
    {
        _sel = a;
        _inspectCts?.Cancel();
        Chips.Children.Clear();
        if (a == null)
        {
            Details.Visibility = Visibility.Collapsed;
            Placeholder.Visibility = Visibility.Visible;
            return;
        }
        Placeholder.Visibility = Visibility.Collapsed;
        Details.Visibility = Visibility.Visible;
        Details.ScrollToTop();

        DetName.Text = a.Name;
        DetPublisher.Text = string.IsNullOrWhiteSpace(a.Publisher) ? Loc.T("Unknown") : a.Publisher;
        DetVersion.Text = string.IsNullOrWhiteSpace(a.Version) ? "—" : a.Version;
        DetSize.Text = string.IsNullOrEmpty(a.SizeText) ? "—" : a.SizeText;
        DetDate.Text = string.IsNullOrEmpty(a.DateText) ? "—" : a.DateText;
        DetLocationText.Text = string.IsNullOrEmpty(a.InstallLocation) ? "—" : a.InstallLocation;
        DetLocation.IsEnabled = !string.IsNullOrEmpty(a.InstallLocation);
        SetDetailIcon(a);
        UpdateBgButton();

        var cts = new CancellationTokenSource();
        _inspectCts = cts;
        var all = _all;
        Task.Run(() => BackgroundControl.Inspect(a, all), cts.Token).ContinueWith(t =>
        {
            if (cts.IsCancellationRequested || t.IsFaulted || t.IsCanceled || _sel != a) return;
            BuildChips(t.Result, a);
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    void SetDetailIcon(InstalledApp a)
    {
        DetIcon.Source = a.Icon;
        DetInitial.Text = a.Icon == null ? a.Initial : "";
    }

    void BuildChips(BackgroundControl.Status s, InstalledApp a)
    {
        Chips.Children.Clear();
        if (a.BackgroundDisabled) AddChip(Loc.T("BgDisabledChip"), "AccentSoft", "Accent", "");
        if (s.Processes > 0) AddChip($"{Loc.T("Running")} · {s.Processes}", "GreenSoft", "Green", "");
        if (s.Services > 0) AddChip($"{Loc.T("Services")} · {s.Services}", "BlueSoft", "Blue", "");
        if (s.Startup > 0) AddChip($"{Loc.T("Startup")} · {s.Startup}", "AmberSoft", "Amber", "");
        if (s.Tasks > 0) AddChip($"{Loc.T("Tasks")} · {s.Tasks}", "AmberSoft", "Amber", "");
    }

    void AddChip(string text, string bg, string fg, string glyph)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        sp.Children.Add(new TextBlock
        {
            Text = glyph, FontFamily = (FontFamily)FindResource("Icons"), FontSize = 10,
            Foreground = (Brush)FindResource(fg), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0)
        });
        sp.Children.Add(new TextBlock { Text = text, FontSize = 11.5, Foreground = (Brush)FindResource(fg) });
        Chips.Children.Add(new Border { Style = (Style)FindResource("Chip"), Background = (Brush)FindResource(bg), Child = sp });
    }

    void UpdateBgButton()
    {
        bool dis = _sel?.BackgroundDisabled == true;
        BgTitle.Text = Loc.T(dis ? "BgRestoreTitle" : "BgTitle");
        BgDesc.Text = Loc.T(dis ? "BgRestoreDesc" : "BgDesc");
        BgGlyph.Text = dis ? "" : "";
    }

    void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        var loc = _sel?.InstallLocation;
        if (!string.IsNullOrEmpty(loc) && Directory.Exists(loc))
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{loc}\"") { UseShellExecute = true });
    }

    // ================= actions =================

    async void Normal_Click(object sender, RoutedEventArgs e) => await RunUninstall(UninstallMode.Normal);
    async void Full_Click(object sender, RoutedEventArgs e) => await RunUninstall(UninstallMode.Full);

    async Task RunUninstall(UninstallMode mode)
    {
        var app = _sel;
        if (app == null || _busy) return;
        bool full = mode == UninstallMode.Full;
        var ok = await ConfirmAsync(Loc.T(full ? "FullTitle" : "NormalTitle"),
            string.Format(Loc.T(full ? "ConfirmFull" : "ConfirmNormal"), app.Name),
            Loc.T(full ? "FullTitle" : "NormalTitle"), full, app);
        if (!ok) return;

        _busy = true;
        try
        {
            var keys = new List<string>();
            if (Settings.Current.RestorePoint) keys.Add("StepRestore");
            keys.AddRange(new[] { "StepKill", "StepServices", "StepUninstaller", "StepScan", "StepClean" });
            var steps = new ObservableCollection<StepVM>(keys.Select(k => new StepVM { Key = k }));
            ShowProgress(app.Name, steps, full);

            var r = await UninstallEngine.UninstallAsync(app, _all, mode,
                (k, s) => Dispatcher.Invoke(() => { var st = steps.FirstOrDefault(x => x.Key == k); if (st != null) st.State = s; }),
                AskForceAsync);

            if (r.Cancelled)
            {
                foreach (var s in steps.Where(s => s.State == StepState.Pending)) s.State = StepState.Skipped;
                ShowResult(Loc.T("UninstallerCancelled"), null, false, null);
            }
            else
            {
                var stats = string.Format(Loc.T("ResultStats"), r.Files, r.Registry, r.Services, r.TasksAndStartup);
                var note = r.RebootNeeded ? Loc.T("Reboot") : (!full ? Loc.T("ResultKeptData") : null);
                ShowResult(string.Format(Loc.T(full ? "ResultWiped" : "ResultUninstalled"), app.Name), stats, true, r.Details, note, !r.RebootNeeded && !full);
            }
            await _dlg.Task;
            HideOverlay();
            await LoadAppsAsync();
        }
        finally { _busy = false; }
    }

    async void Bg_Click(object sender, RoutedEventArgs e)
    {
        var app = _sel;
        if (app == null || _busy) return;
        bool restore = app.BackgroundDisabled;
        if (!restore)
        {
            if (!await ConfirmAsync(Loc.T("BgTitle"), string.Format(Loc.T("ConfirmBg"), app.Name), Loc.T("BgTitle"), false, app)) return;
        }
        _busy = true;
        try
        {
            var steps = new ObservableCollection<StepVM> { new StepVM { Key = restore ? "StepEnable" : "StepDisable", State = StepState.Running } };
            ShowProgress(app.Name, steps, false);
            var all = _all;
            var r = await Task.Run(() => restore ? BackgroundControl.Enable(app) : BackgroundControl.Disable(app, all));
            steps[0].State = StepState.Done;
            app.BackgroundDisabled = !restore;
            var stats = string.Format(Loc.T("ResultBgStats"), r.Processes, r.Services, r.StartupEntries, r.Tasks);
            ShowResult(string.Format(Loc.T(restore ? "ResultBgRestore" : "ResultBg"), app.Name), stats, true, r.Details);
            await _dlg.Task;
            HideOverlay();
            if (_sel == app) ShowDetails(app);
        }
        finally { _busy = false; }
    }

    // ================= dialog =================

    Task<bool> ConfirmAsync(string title, string text, string okText, bool danger, InstalledApp app)
    {
        _dlg = new TaskCompletionSource<bool>();
        SetDialogHeader(title, app, danger);
        DlgText.Text = text;
        DlgText.Visibility = Visibility.Visible;
        StepsList.Visibility = Visibility.Collapsed;
        ResultPanel.Visibility = Visibility.Collapsed;
        DlgButtons.Visibility = Visibility.Visible;
        DlgCancel.Visibility = Visibility.Visible;
        DlgCancel.Content = Loc.T("Cancel");
        DlgOk.Content = okText;
        DlgOk.Style = (Style)FindResource(danger ? "DangerButton" : "PrimaryButton");
        Overlay.Visibility = Visibility.Visible;
        DlgOk.Focus();
        return _dlg.Task.ContinueWith(t => { if (!t.Result) HideOverlay(); return t.Result; }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    void SetDialogHeader(string title, InstalledApp app, bool danger)
    {
        DlgTitle.Text = title;
        DlgAppIcon.Source = app?.Icon;
        DlgGlyph.Text = app?.Icon == null ? (danger ? "" : "") : "";
        DlgGlyph.Foreground = (Brush)FindResource(danger ? "Accent" : "Blue");
        DlgIconBg.Background = (Brush)FindResource(danger ? "AccentSoft" : "BlueSoft");
    }

    void ShowProgress(string appName, ObservableCollection<StepVM> steps, bool danger)
    {
        _dlg = new TaskCompletionSource<bool>();
        DlgTitle.Text = appName;
        DlgText.Text = Loc.T("Working");
        DlgText.Visibility = Visibility.Visible;
        StepsList.ItemsSource = steps;
        StepsList.Visibility = Visibility.Visible;
        ResultPanel.Visibility = Visibility.Collapsed;
        DlgButtons.Visibility = Visibility.Collapsed;
        Overlay.Visibility = Visibility.Visible;
    }

    async Task<bool> AskForceAsync()
    {
        var tcs = new TaskCompletionSource<bool>();
        var prev = _dlg;
        _dlg = tcs;
        DlgText.Text = Loc.T("ForceAsk");
        DlgButtons.Visibility = Visibility.Visible;
        DlgCancel.Visibility = Visibility.Visible;
        DlgCancel.Content = Loc.T("Cancel");
        DlgOk.Content = Loc.T("ForceRemove");
        DlgOk.Style = (Style)FindResource("DangerButton");
        var answer = await tcs.Task;
        DlgButtons.Visibility = Visibility.Collapsed;
        DlgText.Text = Loc.T("Working");
        _dlg = prev;
        return answer;
    }

    void ShowResult(string headline, string stats, bool success, List<string> details, string note = null, bool noteIsInfo = false)
    {
        _dlg = new TaskCompletionSource<bool>();
        DlgText.Text = headline;
        DlgText.Foreground = (Brush)FindResource(success ? "Text" : "Accent");
        ResultPanel.Visibility = Visibility.Visible;
        ResultStats.Text = stats ?? "";
        ResultStats.Parent.SetValue(VisibilityProperty, string.IsNullOrEmpty(stats) ? Visibility.Collapsed : Visibility.Visible);
        ResultNote.Text = note ?? "";
        ResultNote.Foreground = (Brush)FindResource(noteIsInfo ? "Green" : "Amber");
        ResultNote.Visibility = string.IsNullOrEmpty(note) ? Visibility.Collapsed : Visibility.Visible;
        DetailsList.ItemsSource = details;
        _detailsOpen = false;
        DetailsBox.Visibility = Visibility.Collapsed;
        DetailsToggle.Visibility = details != null && details.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        DetailsToggleText.Text = Loc.T("ShowDetails");
        DlgButtons.Visibility = Visibility.Visible;
        DlgCancel.Visibility = Visibility.Collapsed;
        DlgOk.Content = Loc.T("Done");
        DlgOk.Style = (Style)FindResource("PrimaryButton");
        DlgOk.Focus();
    }

    void HideOverlay()
    {
        Overlay.Visibility = Visibility.Collapsed;
        DlgText.ClearValue(TextBlock.ForegroundProperty);
        DlgText.Foreground = (Brush)FindResource("Muted");
    }

    void DetailsToggle_Click(object sender, RoutedEventArgs e)
    {
        _detailsOpen = !_detailsOpen;
        DetailsBox.Visibility = _detailsOpen ? Visibility.Visible : Visibility.Collapsed;
        DetailsToggleText.Text = Loc.T(_detailsOpen ? "HideDetails" : "ShowDetails");
    }

    void DlgOk_Click(object sender, RoutedEventArgs e) => _dlg?.TrySetResult(true);
    void DlgCancel_Click(object sender, RoutedEventArgs e) => _dlg?.TrySetResult(false);

    // ================= settings / language =================

    void Settings_Click(object sender, RoutedEventArgs e)
    {
        var s = Settings.Current;
        SetSilent.IsChecked = s.Silent;
        SetRestore.IsChecked = s.RestorePoint;
        SetSystem.IsChecked = s.ShowSystem;
        LangAr.IsChecked = s.Arabic;
        LangEn.IsChecked = !s.Arabic;
        SettingsOverlay.Visibility = Visibility.Visible;
    }

    async void Setting_Changed(object sender, RoutedEventArgs e)
    {
        var s = Settings.Current;
        bool sysChanged = s.ShowSystem != (SetSystem.IsChecked == true);
        s.Silent = SetSilent.IsChecked == true;
        s.RestorePoint = SetRestore.IsChecked == true;
        s.ShowSystem = SetSystem.IsChecked == true;
        s.Save();
        if (sysChanged) await LoadAppsAsync();
    }

    void LangRadio_Click(object sender, RoutedEventArgs e) => SetLanguage(LangAr.IsChecked == true);

    void Lang_Click(object sender, RoutedEventArgs e) => SetLanguage(!Loc.I.IsArabic);

    void SetLanguage(bool arabic)
    {
        Settings.Current.Arabic = arabic;
        Settings.Current.Save();
        Loc.I.SetLanguage(arabic);
        ApplyLanguage();
    }

    void ApplyLanguage()
    {
        bool ar = Loc.I.IsArabic;
        Root.FlowDirection = ar ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        LangLabel.Text = ar ? "EN" : "ع";
        UpdateCount();
        UpdateBgButton();
        if (_sel != null) ShowDetails(_sel);
    }

    void SettingsClose_Click(object sender, RoutedEventArgs e) => CloseSettings();
    void CloseSettings() => SettingsOverlay.Visibility = Visibility.Collapsed;
    void SettingsOverlay_MouseDown(object sender, MouseButtonEventArgs e) => CloseSettings();
    void SettingsCard_MouseDown(object sender, MouseButtonEventArgs e) => e.Handled = true;

    // ================= window chrome =================

    void Min_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    void Max_Click(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    void Close_Click(object sender, RoutedEventArgs e) => Close();
}
