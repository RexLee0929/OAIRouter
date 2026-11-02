using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using OAIRouter.Ui;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace OAIRouter;

public partial class MainWindow : Window
{
    private readonly AppConfig _cfg = App.Config;
    private readonly DispatcherTimer _statsTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromMilliseconds(700) };

    private Forms.NotifyIcon? _tray;
    private bool _ready;
    private bool _allowClose;
    private bool _startHidden;
    private bool _startServiceOnLoad;
    private UsageGroup _group = UsageGroup.Time;
    private TimeSpan _bucket = TimeSpan.FromDays(1);
    private int _usageDirty;
    private bool _routeAsc = true;
    private bool _mapAsc = true;
    private bool _serviceRunning;

    /// <summary>映射表「模式」下拉的选项</summary>
    public System.Collections.ObjectModel.ObservableCollection<string> ModeOptions { get; } =
        new() { "跟随路由", "responses", "chat", "rewrite" };

    /// <summary>模型库，供映射表下拉使用</summary>
    public System.Collections.ObjectModel.ObservableCollection<string> ModelOptions => App.Config.Models;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;

        Title = $"{Brand.Name} {Brand.Version}";
        Icon = BrandIcon.Source;   // 取 ico 里最大的一帧，别让系统拿首帧去放大（规范 §二）

        // 规范尺寸 1280×820；小屏（1366×768）或高缩放下不能溢出工作区。
        // 注意不要设 MaxWidth/MaxHeight：它们会限制最大化尺寸，
        // 导致窗口可见区域比屏幕小一圈，右侧露出黑边。
        var work = SystemParameters.WorkArea;
        Width = Math.Min(Width, work.Width);
        Height = Math.Min(Height, work.Height);

        BuildMenu();

        // 快捷键的另一半：XAML 里声明 KeyBinding，这里接命令（§五.2 显示与注册同源）
        CommandBindings.Add(new CommandBinding(ShellCommands.ClearLog, (_, _) => ArmClearLog(ClearLogButton)));
        CommandBindings.Add(new CommandBinding(ShellCommands.Quit, (_, _) => App.Quit()));

        RouteItems.ItemsSource = _cfg.Routes;
        MapItems.ItemsSource = _cfg.ModelMap;
        LogList.ItemsSource = LogStore.Instance.Items;
        UsageStats.Load();
        UsageStats.Changed += OnUsageChanged;
        LogStore.Instance.Limit = _cfg.LogLimit;

        PortBox.Text = _cfg.Port.ToString();
        MinTokenBox.Text = _cfg.MinOutputTokens.ToString();
        MaxTokenBox.Text = _cfg.MaxOutputTokens.ToString();
        AutoStartBox.IsChecked = Autostart.IsEnabled();

        BucketBox.ItemsSource = new[] { "按日", "12 小时", "6 小时", "1 小时", "30 分钟", "15 分钟" };
        BucketBox.SelectedIndex = 0;

        _cfg.Routes.CollectionChanged += OnCollectionChanged;
        _cfg.ModelMap.CollectionChanged += OnCollectionChanged;
        foreach (var r in _cfg.Routes) r.PropertyChanged += OnRuleChanged;
        foreach (var m in _cfg.ModelMap) m.PropertyChanged += OnRuleChanged;
        foreach (var p in _cfg.Pricing) p.PropertyChanged += OnRuleChanged;
        _cfg.Pricing.CollectionChanged += OnCollectionChanged;

        App.Proxy.Notify += msg => ShowToast(msg, warn: true);

        _statsTimer.Tick += (_, _) =>
        {
            UpdateStats();
            if (UsagePanel.Visibility == Visibility.Visible && Interlocked.Exchange(ref _usageDirty, 0) != 0)
                RefreshUsage();
        };
        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); ConfigStore.Save(_cfg); };

        CreateTray();

        Loaded += (_, _) =>
        {
            _ready = true;
            UpdateStats();
            RefreshUsage();
            SetStatTab(usage: false);
            _statsTimer.Start();

            if (_startHidden) { HideToTray(silent: true); }

            // 默认不自动启动服务；只有显式传 --start 才起（供开机自启等场景使用）
            if (_startServiceOnLoad) StartService();
        };
    }

    public void StartHidden() => _startHidden = true;

    /// <summary>仅在命令行显式带 --start 时生效；双击打开不会自动启动服务。</summary>
    public void StartServiceOnLoad() => _startServiceOnLoad = true;

    public void AllowClose() => _allowClose = true;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        WindowChrome.Apply(this);
    }

    /* ----------------------------- 菜单 ----------------------------- */

    /// <summary>
    /// 不可逆命令 → 确认文案 + 状态栏说明（§七.3：正文要给后果）。
    /// 集中在菜单分发这一处，加新命令时漏不掉确认（§七 阶梯 ③）。
    /// </summary>
    private static readonly Dictionary<string, (string Confirm, string Hint)> Destructive = new()
    {
        ["clear-log"] = ("确认清空", "再点一次「确认清空」清空运行日志"),
        ["reset-all"] = ("确认重置", "再点一次「确认重置」执行：规则、映射、价格、用量与日志全清，不可撤销"),
    };

    private void BuildMenu()
    {
        MenuBarHost.Children.Clear();
        foreach (var group in AppMenu.Build())
        {
            var button = new Button
            {
                // 助记符原样交给 WPF（§五.2）：模板里的 ContentPresenter 开了
                // RecognizesAccessKey，会把「服务(_S)」渲染成「服务(S)」并注册 Alt+S。
                // 之前在这里把「(_」替换成「(」，等于把助记键抹掉、只留个装饰括号。
                Content = group.Title,
                Style = (Style)FindResource("TopMenuButton"),
            };
            button.Click += (_, _) => OpenMenu(button, group);
            MenuBarHost.Children.Add(button);
        }
    }

    private void OpenMenu(Button anchor, MenuGroupData group)
    {
        var panel = new StackPanel();

        foreach (var item in group.Items)
        {
            if (item.Separator)
            {
                panel.Children.Add(new Rectangle
                {
                    Height = 1,
                    Fill = new SolidColorBrush(Color.FromRgb(0xE2, 0xE8, 0xF1)),
                    Margin = new Thickness(10, 5, 10, 5),
                });
                continue;
            }

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var checkedOn = item.Group == "view" && item.Value == "autoscroll" && _cfg.AutoScrollLog;

            // 勾选行用 check（14px、强调色）；其余按表里的键取，缺图时 Icons 自己兜底
            var iconBrush = item.Group is not null
                ? (checkedOn ? (Brush)FindResource("AccentBrush") : Brushes.Transparent)
                : new SolidColorBrush(Color.FromRgb(0x1B, 0x1B, 0x1B));
            var icon = Icons.Get(item.Group is not null ? "check" : item.Icon, iconBrush,
                item.Group is not null ? 14 : 16);
            if (icon is not null)
            {
                Grid.SetColumn(icon, 0);
                grid.Children.Add(icon);
            }

            var text = new TextBlock
            {
                Text = item.Text,
                FontSize = 12.5,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(9, 0, 0, 0),
                Foreground = new SolidColorBrush(Color.FromRgb(0x1B, 0x1B, 0x1B)),
            };
            Grid.SetColumn(text, 1);
            grid.Children.Add(text);

            if (item.Gesture is { Length: > 0 })
            {
                var gesture = new TextBlock
                {
                    Text = item.Gesture,
                    FontSize = 11.5,
                    Foreground = new SolidColorBrush(Color.FromRgb(0x8A, 0x8A, 0x8A)),
                    Margin = new Thickness(22, 0, 2, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                };
                Grid.SetColumn(gesture, 2);
                grid.Children.Add(gesture);
            }

            var button = new Button
            {
                Content = grid,
                Style = (Style)FindResource("MenuItemButton"),
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
            };
            button.Click += (_, _) =>
            {
                // 不可逆命令：就地变「确认××」，再点一次才执行（§七 阶梯 ③，不开窗）
                if (Destructive.TryGetValue(item.Command, out var confirm))
                {
                    if (InlineConfirm.Arm(button, confirm.Confirm, () => { MenuPopup.IsOpen = false; Run(item); }, text))
                        ShowStatusFlash(confirm.Hint);
                    return;
                }

                MenuPopup.IsOpen = false;
                Run(item);
            };
            panel.Children.Add(button);
        }

        var card = new Border
        {
            Background = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xE2, 0xE8, 0xF1)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(5),
            Margin = new Thickness(10),
            MinWidth = 210,
            Child = panel,
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                BlurRadius = 12,
                ShadowDepth = 2,
                Direction = 270,
                Opacity = 0.16,
                Color = Colors.Black,
            },
        };

        MenuPopup.PlacementTarget = anchor;
        MenuPopup.Child = card;
        MenuPopup.IsOpen = true;
    }

    private void Run(MenuItemData item)
    {
        switch (item.Command)
        {
            case "start": StartService(); break;
            case "stop": StopService(); break;
            case "restart": _ = RestartServiceAsync(); break;
            case "copy-address": CopyAddress(); break;
            case "add-route": AddRoute(); break;
            case "add-map": AddMap(); break;
            case "pricing": OpenPricing(); break;
            case "import": ImportConfig(); break;
            case "export": ExportConfig(); break;
            case "reset-all": ResetAllData(); break;
            case "models": OpenModels(); break;
            case "reset-routes": ResetRules(); break;
            case "toggle-scroll":
                _cfg.AutoScrollLog = !_cfg.AutoScrollLog;
                QueueSave();
                ShowStatusFlash(_cfg.AutoScrollLog ? "日志已开启自动滚动" : "日志已关闭自动滚动");
                break;
            case "clear-log": ClearLog(); break;
            case "open-log": OpenLogFile(); break;
            case "about": new AboutDialog { Owner = this }.ShowDialog(); break;
            case "email": CopyEmail(); break;
            case "quit": App.Quit(); break;
        }
    }

    /* ----------------------------- 服务 ----------------------------- */

    private async void StartService()
    {
        if (_serviceRunning) return;
        if (!TryReadPort(out var port))
        {
            ShowToast("端口需要是 1024–65535 之间的整数", warn: true);
            return;
        }

        _cfg.Port = port;
        ToggleButton.IsEnabled = false;
        var (ok, message) = await App.Proxy.StartAsync(_cfg);
        ToggleButton.IsEnabled = true;

        if (!ok)
        {
            ShowToast($"服务启动失败：{message}", warn: true);
            UpdateServiceUi(false);
            return;
        }

        _serviceRunning = true;
        UpdateServiceUi(true);
        ShowToast($"服务已启动，监听 {_cfg.ListenPrefix}");
        QueueSave();
    }

    private async void StopService()
    {
        if (!_serviceRunning) return;
        await App.Proxy.StopAsync();
        _serviceRunning = false;
        UpdateServiceUi(false);
        ShowToast("服务已停止");
    }

    private async Task RestartServiceAsync()
    {
        if (_serviceRunning) await App.Proxy.StopAsync();
        _serviceRunning = false;
        UpdateServiceUi(false);
        StartService();
    }

    private void UpdateServiceUi(bool running)
    {
        StatusDot.Fill = new SolidColorBrush(running ? Accent.Main : Color.FromRgb(0xB4, 0xB2, 0xA9));
        StatusText.Text = running ? "运行中" : "已停止";
        AddressText.Text = running ? _cfg.ListenPrefix : "启动后可用";
        ToggleButton.Content = running ? "停止服务" : "启动服务";
        UpdateStats();
    }

    private bool TryReadPort(out int port)
    {
        port = _cfg.Port;
        if (!int.TryParse(PortBox.Text.Trim(), out var value)) return false;
        if (value < 1024 || value > 65535) return false;
        port = value;
        return true;
    }

    private void OnToggleService(object sender, RoutedEventArgs e)
    {
        if (_serviceRunning) StopService();
        else StartService();
    }

    private void OnPortChanged(object sender, TextChangedEventArgs e)
    {
        if (!_ready) return;
        if (TryReadPort(out var port) && port != _cfg.Port)
        {
            _cfg.Port = port;
            AddressText.Text = _serviceRunning ? _cfg.ListenPrefix : AddressText.Text;
            QueueSave();
        }
    }

    private void OnMinTokenChanged(object sender, TextChangedEventArgs e)
    {
        if (!_ready) return;
        if (!int.TryParse(MinTokenBox.Text.Trim(), out var value) || value < 1 || value > 4096) return;
        if (value == _cfg.MinOutputTokens) return;
        _cfg.MinOutputTokens = value;
        QueueSave();
    }

    private void OnMaxTokenChanged(object sender, TextChangedEventArgs e)
    {
        if (!_ready) return;
        if (!int.TryParse(MaxTokenBox.Text.Trim(), out var value) || value < 0 || value > 2_000_000) return;
        if (value == _cfg.MaxOutputTokens) return;
        _cfg.MaxOutputTokens = value;
        QueueSave();
    }

    private void CopyAddress()
    {
        try
        {
            Clipboard.SetText(_cfg.ListenPrefix);
            ShowStatusFlash("监听地址已复制");
        }
        catch { ShowToast("复制失败", warn: true); }
    }

    private void OnCopyAddress(object sender, RoutedEventArgs e) => CopyAddress();

    private void CopyEmail()
    {
        try
        {
            Clipboard.SetText(Brand.Email);
            ShowStatusFlash("邮箱已复制");
        }
        catch { ShowToast("复制失败", warn: true); }
    }

    private void OnAutoStartChanged(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        var want = AutoStartBox.IsChecked == true;
        if (Autostart.Set(want))
        {
            _cfg.AutoStart = want;
            QueueSave();
            ShowStatusFlash(want ? "已开启开机自启" : "已关闭开机自启");
        }
        else
        {
            ShowToast("设置开机自启失败，请检查注册表权限", warn: true);
            AutoStartBox.IsChecked = Autostart.IsEnabled();
        }
    }

    /* ----------------------------- 规则 ----------------------------- */

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems is not null)
            foreach (INotifyPropertyChanged item in e.NewItems) item.PropertyChanged += OnRuleChanged;
        if (e.OldItems is not null)
            foreach (INotifyPropertyChanged item in e.OldItems) item.PropertyChanged -= OnRuleChanged;

        QueueSave();
        UpdateStats();
    }

    private void OnRuleChanged(object? sender, PropertyChangedEventArgs e)
    {
        QueueSave();
        UpdateStats();
    }

    private void OnAddRoute(object sender, RoutedEventArgs e) => AddRoute();

    private void AddRoute()
    {
        if (_cfg.Routes.Any(r => r.IsEditing))
        {
            ShowStatusFlash("先把正在编辑的那一行填完或点对勾结束");
            return;
        }

        // 插到最上面，方便立刻编辑；编辑完成后再按名称归位
        _cfg.Routes.Insert(0, new RouteRule
        {
            Prefix = "route" + (_cfg.Routes.Count + 1),
            Name = "新路由",
            Upstream = "https://",
            Mode = "responses",
            IsEditing = true,
        });
    }

    private void OnToggleEditRoute(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: RouteRule rule }) return;

        if (rule.IsEditing)
        {
            rule.IsEditing = false;
            SortRoutes(true);            // 编辑完成后按名称归位
            _routeAsc = false;
            RouteSortBtn.Content = "名称 ↑";
        }
        else
        {
            rule.IsEditing = true;
        }
    }

    private void OnCopyRouteEndpoint(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: RouteRule rule }) return;

        var prefix = (rule.Prefix ?? "").Trim().Trim('/');
        var url = $"{_cfg.ListenPrefix}/{prefix}/v1";
        try
        {
            Clipboard.SetText(url);
            ShowStatusFlash($"已复制 {url}");
        }
        catch
        {
            ShowToast("复制失败", warn: true);
        }
    }

    private void OnSortRoutes(object sender, RoutedEventArgs e)
    {
        SortRoutes(_routeAsc);
        RouteSortBtn.Content = _routeAsc ? "名称 ↑" : "名称 ↓";
        _routeAsc = !_routeAsc;
    }

    private void OnSortMaps(object sender, RoutedEventArgs e)
    {
        SortMaps(_mapAsc);
        MapSortBtn.Content = _mapAsc ? "名称 ↑" : "名称 ↓";
        _mapAsc = !_mapAsc;
    }

    private void SortRoutes(bool asc) => SortBy(_cfg.Routes, r => r.Name, r => r.Prefix, asc);

    private void SortMaps(bool asc) => SortBy(_cfg.ModelMap, m => m.Alias, m => m.Target, asc);

    /// <summary>按主键排序，主键相同再看次键；用 Move 逐项归位，不重建集合。</summary>
    private void SortBy<T>(ObservableCollection<T> list, Func<T, string?> key, Func<T, string?> sub, bool asc)
    {
        var ordered = list.ToList();
        ordered.Sort((a, b) =>
        {
            var c = string.Compare((key(a) ?? "").Trim(), (key(b) ?? "").Trim(), StringComparison.OrdinalIgnoreCase);
            if (c == 0)
                c = string.Compare((sub(a) ?? "").Trim(), (sub(b) ?? "").Trim(), StringComparison.OrdinalIgnoreCase);
            return asc ? c : -c;
        });

        for (var i = 0; i < ordered.Count; i++)
        {
            var current = list.IndexOf(ordered[i]);
            if (current != i) list.Move(current, i);
        }

        QueueSave();
    }

    private void OnToggleEditMap(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ModelMapping map }) map.IsEditing = !map.IsEditing;
    }

    private void OnRemoveRouteRow(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: RouteRule rule }) _cfg.Routes.Remove(rule);
    }

    private void OnAddMap(object sender, RoutedEventArgs e) => AddMap();

    private void AddMap()
    {
        // 上一行还在编辑（说明没填完），不要继续堆新行
        if (_cfg.ModelMap.Any(m => m.IsEditing))
        {
            ShowStatusFlash("先把正在编辑的那一行填完或点对勾结束");
            return;
        }

        _cfg.ModelMap.Add(new ModelMapping
        {
            Alias = "alias" + (_cfg.ModelMap.Count + 1),
            Target = "gpt-6-luna",
            IsEditing = true,
        });
    }

    private void OnRemoveMapRow(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ModelMapping map }) _cfg.ModelMap.Remove(map);
    }

    private void OpenPricing() => ShowToolWindow(() => new PricingWindow());

    private void OpenModels() => ShowToolWindow(() => new ModelsWindow());

    /// <summary>设置类窗口：关闭后统一保存配置并刷新统计。</summary>
    private void ShowToolWindow(Func<Window> create)
    {
        var window = create();

        // Owner 必须给：没有 Owner，XAML 里的 CenterOwner 会退化成系统默认位置，
        // 窗口就永远弹在屏幕左上角那一块（用户说的「位置被固定死了」）。
        window.Owner = this;
        window.ShowDialog();

        QueueSave();
        RefreshUsage();
        UpdateStats();
    }

    private void ExportConfig()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "配置文件 (*.json)|*.json",
            FileName = $"{Brand.Name}-配置-{DateTime.Now:yyyyMMdd-HHmm}.json",
        };
        if (dialog.ShowDialog() != true) return;

        try
        {
            File.WriteAllText(dialog.FileName, ConfigStore.ToJson(_cfg));
            ShowStatusFlash("配置已导出");
        }
        catch (Exception ex)
        {
            ShowToast($"导出失败：{ex.Message}", warn: true);
        }
    }

    private void ImportConfig()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "配置文件 (*.json)|*.json|所有文件 (*.*)|*.*",
        };
        if (dialog.ShowDialog() != true) return;

        AppConfig? incoming;
        try
        {
            incoming = ConfigStore.FromJson(File.ReadAllText(dialog.FileName));
        }
        catch (Exception ex)
        {
            ShowToast($"读取失败：{ex.Message}", warn: true);
            return;
        }

        if (incoming is null)
        {
            ShowToast("这个文件不是有效的配置文件", warn: true);
            return;
        }

        var options = new ImportWindow(incoming) { Owner = this };
        if (options.ShowDialog() != true) return;

        ApplyImport(incoming, options.Selection);
    }

    private void ApplyImport(AppConfig incoming, ImportSelection selection)
    {
        if (selection.Settings)
        {
            _cfg.Port = incoming.Port;
            _cfg.MinOutputTokens = incoming.MinOutputTokens;
            _cfg.MaxOutputTokens = incoming.MaxOutputTokens;
            _cfg.LogLimit = incoming.LogLimit;

            PortBox.Text = _cfg.Port.ToString();
            MinTokenBox.Text = _cfg.MinOutputTokens.ToString();
            MaxTokenBox.Text = _cfg.MaxOutputTokens.ToString();
        }

        if (selection.Routes)
        {
            _cfg.Routes.Clear();
            foreach (var route in incoming.Routes) _cfg.Routes.Add(route);
        }

        if (selection.Maps)
        {
            _cfg.ModelMap.Clear();
            foreach (var map in incoming.ModelMap) _cfg.ModelMap.Add(map);
        }

        if (selection.Pricing)
        {
            _cfg.Pricing.Clear();
            foreach (var rule in incoming.Pricing) _cfg.Pricing.Add(rule);
        }

        if (selection.Models)
        {
            _cfg.Models.Clear();
            foreach (var model in incoming.Models) _cfg.Models.Add(model);
        }

        ConfigStore.Save(_cfg);
        RefreshUsage();
        UpdateStats();
        ShowStatusFlash("已导入所选内容");
    }

    /// <summary>
    /// 重置全部数据。**调用前必须已确认** —— 入口只有菜单里那一项的就地确认（见 <see cref="Destructive"/>），
    /// 不再为一次点击开模态框（§七 阶梯 ③）。
    /// </summary>
    private void ResetAllData()
    {
        var fresh = ConfigStore.CreateDefault();

        _cfg.Routes.Clear();
        foreach (var route in fresh.Routes) _cfg.Routes.Add(route);

        _cfg.ModelMap.Clear();
        foreach (var map in fresh.ModelMap) _cfg.ModelMap.Add(map);

        _cfg.Models.Clear();
        foreach (var model in fresh.Models) _cfg.Models.Add(model);

        _cfg.Pricing.Clear();

        _cfg.Port = fresh.Port;
        _cfg.MinOutputTokens = fresh.MinOutputTokens;
        _cfg.MaxOutputTokens = fresh.MaxOutputTokens;
        _cfg.LogLimit = fresh.LogLimit;
        _cfg.AutoScrollLog = true;

        PortBox.Text = _cfg.Port.ToString();
        MinTokenBox.Text = _cfg.MinOutputTokens.ToString();
        MaxTokenBox.Text = _cfg.MaxOutputTokens.ToString();

        UsageStats.Clear();
        LogStore.Instance.Clear();
        try
        {
            var logFile = LogStore.Instance.LogFile;
            if (File.Exists(logFile)) File.Delete(logFile);
        }
        catch { }

        ConfigStore.Save(_cfg);

        RefreshUsage();
        UpdateStats();
        ShowStatusFlash("已重置为默认状态");
    }

    private void ResetRules()
    {
        _cfg.Routes.Clear();
        _cfg.ModelMap.Clear();
        _cfg.Routes.Add(new RouteRule { Prefix = "openai", Name = "OpenAI 官方", Upstream = "https://api.openai.com", Mode = "responses" });
        ShowStatusFlash("已恢复默认规则");
    }

    /// <summary>滚动条出现时给表头补同样的右边距，避免列错位。</summary>
    private void OnTableScroll(object sender, ScrollChangedEventArgs e)
    {
        var routePad = RouteScroll.ComputedVerticalScrollBarVisibility == Visibility.Visible ? 17 : 0;
        RouteHeader.Margin = new Thickness(0, 0, routePad, 2);

        var mapPad = MapScroll.ComputedVerticalScrollBarVisibility == Visibility.Visible ? 17 : 0;
        MapHeader.Margin = new Thickness(0, 0, mapPad, 2);
    }

    /* ----------------------------- 日志 ----------------------------- */

    /* ----------------------------- 用量与计价 ----------------------------- */

    private void OnShowOverview(object sender, RoutedEventArgs e) => SetStatTab(usage: false);

    private void OnShowUsage(object sender, RoutedEventArgs e)
    {
        SetStatTab(usage: true);
        RefreshUsage();
    }

    private void SetStatTab(bool usage)
    {
        UsagePanel.Visibility = usage ? Visibility.Visible : Visibility.Collapsed;
        OverviewPanel.Visibility = usage ? Visibility.Collapsed : Visibility.Visible;

        var accent = (Brush)FindResource("AccentBrush");
        var soft = (Brush)FindResource("AccentSoftBrush");
        var normal = new SolidColorBrush(Color.FromRgb(0x1B, 0x1B, 0x1B));

        TabOverview.Background = usage ? Brushes.White : soft;
        TabOverview.Foreground = usage ? normal : accent;
        TabUsage.Background = usage ? soft : Brushes.White;
        TabUsage.Foreground = usage ? accent : normal;

        MarkGroupButton(GroupModel, _group == UsageGroup.Model);
        MarkGroupButton(GroupRoute, _group == UsageGroup.Route);
        MarkGroupButton(GroupSession, _group == UsageGroup.Session);
        MarkGroupButton(GroupTime, _group == UsageGroup.Time);
        BucketBox.Visibility = _group == UsageGroup.Time ? Visibility.Visible : Visibility.Collapsed;
    }

    private void MarkGroupButton(Button button, bool active)
    {
        button.Background = active ? (Brush)FindResource("AccentSoftBrush") : Brushes.White;
        button.Foreground = active
            ? (Brush)FindResource("AccentBrush")
            : new SolidColorBrush(Color.FromRgb(0x1B, 0x1B, 0x1B));
    }

    private void OnGroupByModel(object sender, RoutedEventArgs e) => SwitchGroup(UsageGroup.Model);
    private void OnGroupByRoute(object sender, RoutedEventArgs e) => SwitchGroup(UsageGroup.Route);
    private void OnGroupBySession(object sender, RoutedEventArgs e) => SwitchGroup(UsageGroup.Session);
    private void OnGroupByTime(object sender, RoutedEventArgs e) => SwitchGroup(UsageGroup.Time);

    private void OnBucketChanged(object sender, SelectionChangedEventArgs e)
    {
        _bucket = BucketBox.SelectedIndex switch
        {
            1 => TimeSpan.FromHours(12),
            2 => TimeSpan.FromHours(6),
            3 => TimeSpan.FromHours(1),
            4 => TimeSpan.FromMinutes(30),
            5 => TimeSpan.FromMinutes(15),
            _ => TimeSpan.FromDays(1),
        };
        RefreshUsage();
        UsageScroll.ScrollToTop();
    }

    private void SwitchGroup(UsageGroup group)
    {
        _group = group;
        MarkGroupButton(GroupModel, group == UsageGroup.Model);
        MarkGroupButton(GroupRoute, group == UsageGroup.Route);
        MarkGroupButton(GroupSession, group == UsageGroup.Session);
        MarkGroupButton(GroupTime, group == UsageGroup.Time);
        BucketBox.Visibility = group == UsageGroup.Time ? Visibility.Visible : Visibility.Collapsed;
        RefreshUsage();
        UsageScroll.ScrollToTop();
    }

    private void OnUsageChanged() => Interlocked.Exchange(ref _usageDirty, 1);

    private void RefreshUsage()
    {
        var offset = UsageScroll.VerticalOffset;
        UsageItems.ItemsSource = UsageStats.Group(_group, _cfg, _bucket);
        if (UsagePanel.IsVisible && offset > 0)
        {
            UsageScroll.UpdateLayout();
            UsageScroll.ScrollToVerticalOffset(offset);
        }

        var summary = UsageStats.Summary(_cfg);
        var money = summary.Cost > 0 ? $" · {UsageStats.Money(summary.Cost)}" : "";
        UsageSummary.Text = $"{summary.Requests} 次 · 输入 {summary.Prompt:N0} · 输出 {summary.Completion:N0}{money}";
    }

    private void OnClearLog(object sender, RoutedEventArgs e)
    {
        if (sender is Button button) ArmClearLog(button);
        else ClearLog();
    }

    /// <summary>清空日志不可撤销：按钮就地变「确认清空」，再点一次才真清（§七 阶梯 ③）。</summary>
    private void ArmClearLog(Button button)
    {
        if (InlineConfirm.Arm(button, "确认清空", ClearLog))
            ShowStatusFlash("再点一次「确认清空」执行");
    }

    /// <summary>真正的清空。**调用前必须已确认** —— 入口只有内联确认（卡片按钮 / 菜单项 / Ctrl+L）。</summary>
    private void ClearLog()
    {
        LogStore.Instance.Clear();
        ShowStatusFlash("日志已清空");
    }

    private void OnOpenLogFile(object sender, RoutedEventArgs e) => OpenLogFile();

    private void OpenLogFile()
    {
        try
        {
            Directory.CreateDirectory(LogStore.Instance.LogDir);
            if (!File.Exists(LogStore.Instance.LogFile))
                File.WriteAllText(LogStore.Instance.LogFile, "");

            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{LogStore.Instance.LogFile}\"",
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            ShowToast($"打开日志失败：{ex.Message}", warn: true);
        }
    }

    /* ----------------------------- 状态 ----------------------------- */

    private void UpdateStats()
    {
        // 概览与「用量」页签读同一份记录，两边数字必须一致
        var summary = UsageStats.Summary(_cfg);
        StatTotal.Text = summary.Requests.ToString();
        StatFailed.Text = summary.Failed.ToString();
        StatAvg.Text = summary.Requests > 0 ? $"{summary.AverageMs} ms" : "—";
        StatRules.Text = $"{_cfg.Routes.Count} 路由 · {_cfg.ModelMap.Count} 映射";

        RouteEmpty.Visibility = _cfg.Routes.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        MapEmpty.Visibility = _cfg.ModelMap.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        if (_errorStatus is not null)
        {
            // 出错期间状态栏钉住红字，别被统计刷新盖掉
            StatusLeft.Text = _errorStatus;
            StatusLeft.Foreground = (Brush)FindResource("ErrorBrush");
        }
        else
        {
            StatusLeft.Foreground = (Brush)FindResource("HintBrush");
            StatusLeft.Text = _serviceRunning
                ? $"运行中 · 请求 {summary.Requests} / 失败 {summary.Failed}"
                : "已停止 · 点击「启动服务」开始转发";
        }

        StatusRight.Text = $"端口 {_cfg.Port} · 路由 {_cfg.Routes.Count} · 映射 {_cfg.ModelMap.Count}";

        if (_cfg.AutoScrollLog && LogList.Items.Count > 0)
            LogList.ScrollIntoView(LogList.Items[^1]);
    }

    /* ----------------------------- 出错 ----------------------------- */

    private string? _errorStatus;      // 非空时状态栏显示红字
    private DispatcherTimer? _errorTimer;

    /// <summary>
    /// 未捕获异常的出口（规范 §七.4 的三通道）：error.log 已由 App 写过，
    /// 这里负责状态栏红字 + 自绘错误框，绝不「弹个框说完蛋了然后界面啥也没变」。
    /// </summary>
    public void ReportError(Exception ex)
    {
        var detail = FirstLine(ex.Message);

        _errorStatus = $"出错：{detail}";
        UpdateStats();                                  // 红字立刻上屏

        _errorTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _errorTimer.Stop();
        _errorTimer.Tick -= OnErrorStatusExpired;
        _errorTimer.Tick += OnErrorStatusExpired;
        _errorTimer.Start();                            // 30 秒后自动回到统计

        ErrorDialog.Report(this, "刚才的操作没有完成",
            $"{detail}\n\n错误详情已写入 error.log，程序可以继续使用。",
            App.LogPath);
    }

    private void OnErrorStatusExpired(object? sender, EventArgs e)
    {
        _errorTimer?.Stop();
        _errorStatus = null;
        UpdateStats();
    }

    /// <summary>取异常消息的第一行、压到一句话长度：正文只说「发生了什么」。</summary>
    private static string FirstLine(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "程序遇到了未预料的情况。";

        var one = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return one.Length > 110 ? one[..110] + "…" : one;
    }

    private void ShowStatusFlash(string text)
    {
        StatusLeft.Text = text;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.6) };
        timer.Tick += (_, _) => { timer.Stop(); UpdateStats(); };
        timer.Start();
    }

    public void ShowToast(string message, bool warn = false, ToastAnchor anchor = ToastAnchor.ProgramTop)
        => Toast.Show(message, warn, anchor);

    private void QueueSave()
    {
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    /* ----------------------------- 托盘 ----------------------------- */

    private void CreateTray()
    {
        try
        {
            // 托盘图标也按尺寸取原始帧（§十三.8）：问外壳要小图标尺寸，
            // 96 DPI 是 16、150% 是 24，ico 里都有对应帧 —— 不拿 48/256 去缩。
            var size = Math.Max(16, Forms.SystemInformation.SmallIconSize.Width);

            _tray = new Forms.NotifyIcon
            {
                Icon = BrandIcon.DrawingIcon(size),
                Text = $"{Brand.Name} {Brand.Version}",
                Visible = true,
            };

            _tray.MouseUp += (_, e) =>
            {
                if (e.Button == Forms.MouseButtons.Right) Dispatcher.BeginInvoke(ShowTrayMenu);
            };
            _tray.DoubleClick += (_, _) => RestoreFromTray(silent: false);
        }
        catch (Exception ex)
        {
            LogStore.Instance.Warn("托盘", $"创建托盘图标失败：{ex.Message}");
        }
    }

    private void ShowTrayMenu()
    {
        // 复用主菜单树：主菜单加项，托盘自动跟上，不再维护第二份入口列表
        var menu = AppMenu.Build();

        var entries = new List<TrayEntry>
        {
            TrayEntry.Of("显示主窗口", "window", () => RestoreFromTray(silent: false)),
            TrayEntry.Sep(),
        };

        foreach (var item in menu
                     .Where(g => g.Title.StartsWith("服务", StringComparison.Ordinal))
                     .SelectMany(g => g.Items)
                     .Where(i => i.Command != "quit"))   // 退出单独放末位，别在中间出现两次
        {
            if (item.Separator)
            {
                entries.Add(TrayEntry.Sep());
                continue;
            }

            var captured = item;
            entries.Add(TrayEntry.Of(item.Text, item.Icon, () => Run(captured)));
        }

        // 「退出」的文案与图标也取自菜单表：托盘不另写一份字符串（§五.6）
        var quit = menu.SelectMany(g => g.Items).FirstOrDefault(i => i.Command == "quit");

        entries.Add(TrayEntry.Sep());
        entries.Add(TrayEntry.Of(quit?.Text ?? "退出程序", quit?.Icon, App.Quit));

        TrayMenu.Show(entries);
    }

    private void HideToTray(bool silent)
    {
        Hide();
        // 主窗口已经藏起来了，这条提示只能落到任务栏一侧
        if (!silent) ShowToast("已最小化到托盘，双击图标可恢复", anchor: ToastAnchor.TrayCorner);
    }

    public void RestoreFromTray(bool silent)
    {
        if (_allowClose) return;   // 已经在退出流程里，别再拉起窗口

        try
        {
            if (!IsVisible) Show();
            WindowState = WindowState.Normal;
            Activate();
            if (!silent) ShowStatusFlash("窗口已恢复");
        }
        catch (InvalidOperationException)
        {
            // 窗口正处于关闭过程中，忽略即可
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_allowClose)
        {
            e.Cancel = true;
            HideToTray(silent: false);
            return;
        }

        UsageStats.Changed -= OnUsageChanged;
        _statsTimer.Stop();
        _tray?.Dispose();
        base.OnClosing(e);
    }
}
