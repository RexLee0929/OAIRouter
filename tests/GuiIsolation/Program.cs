using System.Collections;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using OAIRouter;
using Button = System.Windows.Controls.Button;
using ComboBox = System.Windows.Controls.ComboBox;
using Point = System.Windows.Point;

internal static class Program
{
    static void Check(bool ok, string text) { if (!ok) throw new Exception(text); Console.WriteLine("PASS " + text); }
    static IEnumerable<T> Desc<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T t) yield return t;
            foreach (var nested in Desc<T>(child)) yield return nested;
        }
    }
    static object Get(object value, string name) => value.GetType().GetProperty(name)!.GetValue(value)!;
    [STAThread] static int Main()
    {
        try
        {
            typeof(App).GetProperty("IsDev")!.SetValue(null, true);
            Check(App.IsDev && ConfigStore.Dir.StartsWith(AppContext.BaseDirectory, StringComparison.OrdinalIgnoreCase), "dev 数据位于独立测试目录");
            var app = new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/OAIRouter;component/Theme.xaml", UriKind.Relative) });
            app.Resources["BoolToVis"] = new BoolToVis();
            app.Resources["InverseBoolToVis"] = new InverseBoolToVis();
            app.Resources["LevelToBrush"] = new LevelToBrush();
            Accent.Init();
            app.Resources["AccentBrush"] = Accent.MainBrush; app.Resources["AccentDarkBrush"] = Accent.DarkBrush;
            app.Resources["AccentSoftBrush"] = Accent.SoftBrush; app.Resources["AccentHoverBrush"] = Accent.HoverBrush;
            var window = new MainWindow();
            var items = (List<UsageRecord>)typeof(UsageStats).GetField("Items", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
            var now = DateTime.Now;
            items.Add(new UsageRecord { Time = now, StartedAt = now, Client = "workbuddy", SessionId = new string('a', 64), TraceId = "trace", DialogId = "dialog", Prompt = 10, Completion = 3 });
            items.Add(new UsageRecord { Time = now, StartedAt = now, Client = "workbuddy", SessionId = new string('b', 64), TraceId = "trace", DialogIdMissing = true, Prompt = 10, Completion = 3 });
            window.Show();
            window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(() => { }));
            void Invoke(string name, params object[] args) => typeof(MainWindow).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, args);
            Invoke("SetStatTab", true); Invoke("OnGroupBySession", window, new RoutedEventArgs()); window.UpdateLayout();
            var control = (ItemsControl)window.FindName("SessionUsageItems");
            var rows = control.Items.Cast<object>().ToList();
            Check(rows.Count == 1 && (bool)Get(rows[0], "IsParent"), "初始折叠仅显示一条主会话");
            var parent = control.ItemContainerGenerator.ContainerFromIndex(0) as DependencyObject;
            var button = Desc<Button>(parent!).Single(x => x.Visibility == Visibility.Visible);
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); window.UpdateLayout();
            rows = control.Items.Cast<object>().ToList();
            Check(rows.Count == 2 && (bool)Get(rows[0], "IsParent") && !(bool)Get(rows[1], "IsParent"), "展开后一主一子");
            var main = (UsageRow)Get(rows[0], "Row"); var child = (UsageRow)Get(rows[1], "Row");
            Check(main.Requests == 2 && child.Requests == 2, "主子均为汇总且请求数不重复计算");
            var summary = (TextBlock)window.FindName("UsageSummary"); Check(summary.Text.Contains("2 次") && !summary.Text.Contains("4 次"), "总计仅计算父级请求");
            var childContainer = control.ItemContainerGenerator.ContainerFromIndex(1) as DependencyObject;
            var texts = Desc<TextBlock>(childContainer!).Where(x => x.IsVisible).Select(x => x.Text).ToArray();
            Check(texts.Any(x => x.Contains("推断")), "推断标志在实际子行可见");
            Check(texts.Contains("2") && texts.Contains("20") && texts.Contains("6"), "子行请求与 token 列绑定正确");
            var scroll = (ScrollViewer)window.FindName("UsageScroll");
            var panel = (Grid)window.FindName("UsagePanel");
            var header = Desc<Grid>(panel).Single(g => g.ColumnDefinitions.Count == 5 && Desc<TextBlock>(g).Any(t => t.Text == "名称") && !Desc<ItemsControl>(g).Any());
            var rowGrid = Desc<Grid>(childContainer!).First(g => g.ColumnDefinitions.Count == 5);
            Check(Math.Abs(header.ActualWidth - scroll.ViewportWidth) < 1, "表头宽度绑定滚动视口");
            for (int i = 0; i < 5; i++) Check(Math.Abs(header.ColumnDefinitions[i].ActualWidth - rowGrid.ColumnDefinitions[i].ActualWidth) < 1, $"第 {i + 1} 列表头与子行对齐");
            parent = control.ItemContainerGenerator.ContainerFromIndex(0) as DependencyObject;
            Desc<Button>(parent!).Single(x => x.Visibility == Visibility.Visible).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); window.UpdateLayout();
            Check(control.Items.Count == 1, "再次点击恢复折叠");

            items.Clear();
            for (int i = 0; i < 20; i++)
                items.Add(new UsageRecord { Time = now, StartedAt = now.AddSeconds(-i), Client = "workbuddy", SessionId = new string('c', 64), TraceId = $"synthetic-trace-{i}", DialogId = $"synthetic-dialog-{i}", Prompt = 10, Completion = 3 });
            Invoke("RefreshUsage"); window.UpdateLayout();
            void Layout() { window.UpdateLayout(); window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(() => { })); window.UpdateLayout(); }
            var service = (FrameworkElement)window.FindName("ServiceCard");
            var stats = (FrameworkElement)window.FindName("StatsCard");
            var log = (FrameworkElement)window.FindName("LogList");
            Layout();
            var windowHeight = window.ActualHeight; var serviceHeight = service.ActualHeight;
            var statsHeight = stats.ActualHeight; var logHeight = log.ActualHeight;
            Check(log.IsVisible && logHeight > 0, "底部日志区域初始可见");
            void CheckHeights(string stage)
            {
                Check(Math.Abs(window.ActualHeight - windowHeight) < 1 && Math.Abs(service.ActualHeight - serviceHeight) < 1 &&
                      Math.Abs(stats.ActualHeight - statsHeight) < 1 && Math.Abs(log.ActualHeight - logHeight) < 1 && log.IsVisible,
                      $"{stage}主窗口、服务卡、统计卡及底部日志高度稳定");
            }
            void ToggleParent()
            {
                var container = control.ItemContainerGenerator.ContainerFromIndex(0) as DependencyObject;
                Desc<Button>(container!).Single(x => x.Visibility == Visibility.Visible).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Layout();
            }
            Check(control.Items.Count == 1, "大量会话初始折叠为一个父项");
            ToggleParent();
            Check(control.Items.Count == 21, "同一会话展开显示 20 个不同对话");
            CheckHeights("展开后");
            Check(scroll.ScrollableHeight > 0 && scroll.ComputedVerticalScrollBarVisibility == Visibility.Visible, "统计区域显示垂直滚动条并可滚动");
            Check(Math.Abs(header.ActualWidth - scroll.ViewportWidth) < 1, "滚动条出现后表头宽度与视口一致");
            var lastContainer = (FrameworkElement)control.ItemContainerGenerator.ContainerFromIndex(control.Items.Count - 1);
            var lastGrid = Desc<Grid>(lastContainer).First(g => g.ColumnDefinitions.Count == 5);
            for (int i = 0; i < 5; i++) Check(Math.Abs(header.ColumnDefinitions[i].ActualWidth - lastGrid.ColumnDefinitions[i].ActualWidth) < 1, $"滚动条出现后第 {i + 1} 列对齐");
            scroll.ScrollToBottom(); Layout();
            var lastPosition = lastContainer.TranslatePoint(new Point(0, lastContainer.ActualHeight), scroll);
            Check(Math.Abs(scroll.VerticalOffset - scroll.ScrollableHeight) < 1 && lastPosition.Y <= scroll.ViewportHeight + 1 && lastPosition.Y > 0, "滚动到底可看到最后一项");
            Invoke("RefreshUsage"); Layout();
            Check(control.Items.Count == 21 && Math.Abs(scroll.VerticalOffset - scroll.ScrollableHeight) < 1, "刷新保留展开状态与滚动位置");
            CheckHeights("刷新后");
            scroll.ScrollToTop(); Layout(); ToggleParent();
            Check(control.Items.Count == 1, "大量对话可再次收起");
            CheckHeights("收起后");
            Invoke("OnGroupByTime", window, new RoutedEventArgs()); Layout();
            var bucket = (ComboBox)window.FindName("BucketBox");
            Check(bucket.IsVisible && bucket.Items.Count > 1, "时间分组显示时间粒度选择");
            bucket.SelectedIndex = (bucket.SelectedIndex + 1) % bucket.Items.Count; Layout();
            CheckHeights("切换时间粒度后");
            Invoke("OnGroupByModel", window, new RoutedEventArgs()); Layout();
            Check(((ItemsControl)window.FindName("UsageItems")).IsVisible, "切换模型分组显示普通列表");
            CheckHeights("切换模型分组后");
            Invoke("OnGroupBySession", window, new RoutedEventArgs()); Layout();
            Check(control.IsVisible && control.Items.Count == 1, "切回会话分组仍为收起状态");
            CheckHeights("切回会话分组后");
            window.AllowClose(); window.Close(); app.Shutdown(); return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}
