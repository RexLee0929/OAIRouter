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
            App.Config.WorkBuddyLogPath = System.IO.Path.Combine(AppContext.BaseDirectory, $"synthetic-forbidden-workbuddy-{Guid.NewGuid():N}.log");
            using var forbiddenLog = new System.IO.FileStream(App.Config.WorkBuddyLogPath, System.IO.FileMode.CreateNew, System.IO.FileAccess.ReadWrite, System.IO.FileShare.ReadWrite | System.IO.FileShare.Delete, 4096, System.IO.FileOptions.DeleteOnClose);
            var root = Guid.Parse("11111111-1111-4111-8111-111111111111");
            var childId = Guid.Parse("22222222-2222-4222-8222-222222222222");
            var evidence = $"2026-11-04 12:00:00 INFO pid=34564 [waitFor] Child session skipped registration: id={childId:D}, parentSessionId={root:D}, meta=" +
                System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object> { ["sessionId"] = childId.ToString("D"), ["parentSessionId"] = root.ToString("D"), ["subagentRootSessionId"] = root.ToString("D"), ["codebuddy.ai/rootSessionId"] = root.ToString("D"), ["parentConversationId"] = root.ToString("D"), ["rootRequestId"] = "synthetic-request", ["subagentDepth"] = 1, ["agentId"] = "synthetic-agent", ["taskId"] = "synthetic-task" }) + "\n";
            var evidenceBytes = System.Text.Encoding.UTF8.GetBytes(evidence);
            forbiddenLog.Write(evidenceBytes); forbiddenLog.Flush();
            Check(WorkBuddyLogEvidence.Read(App.Config.WorkBuddyLogPath).Count == 1, "合成日志包含有效关联证据");
            var window = new MainWindow();
            var items = (List<UsageRecord>)typeof(UsageStats).GetField("Items", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
            var now = DateTime.Now;
            var syntheticChild = WorkBuddyLogEvidence.HashSession(childId);
            var syntheticRoot = WorkBuddyLogEvidence.HashSession(root);
            items.Add(new UsageRecord { Time = now, Client = "workbuddy", SessionId = syntheticChild, DialogId = "synthetic-dialog" });
            window.Show();
            window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(() => { }));
            void Invoke(string name, params object[] args) => typeof(MainWindow).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, args);
            Invoke("SetStatTab", true); Invoke("OnGroupBySession", window, new RoutedEventArgs()); Invoke("RefreshUsage");
            var unassociated = UsageStats.GroupSessions(App.Config, null);
            Check(unassociated.Any(g => g.Id == syntheticChild) && !unassociated.Any(g => g.Id == syntheticRoot), "非空旧路径在启动及刷新后不参与会话关联");
            items.Clear();
            items.Add(new UsageRecord { Time = now, StartedAt = now, Client = "workbuddy", Model = "alias-one", Target = "real-one", SessionId = new string('a', 64), TraceId = "trace", DialogId = "dialog", Prompt = 10, Completion = 3 });
            items.Add(new UsageRecord { Time = now, StartedAt = now, Client = "workbuddy", Model = "alias-one", Target = "real-one", SessionId = new string('b', 64), TraceId = "trace", DialogIdMissing = true, Prompt = 10, Completion = 3 });
            Check(window.FindName("WorkBuddyLogHint") == null && !Desc<Button>(window).Any(b => Equals(b.Content, "选择 WorkBuddy 日志") || Equals(b.Content, "清除选择")), "界面不存在 WorkBuddy 日志选择入口");
            Check(typeof(MainWindow).GetMethod("RefreshWorkBuddyLogAsync", BindingFlags.NonPublic | BindingFlags.Instance) == null, "不存在 WorkBuddy 日志刷新处理器");
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
            Check(main.Key == $"{now:yyyy-MM-dd HH:mm} · WorkBuddy · {new string('a', 20)}", $"会话标题顺序与时间精度正确（实际：{main.Key}）");
            Check(!child.Key.Contains("workbuddy"), "对话标题不包含客户端");
            Check(main.ModelName == "" && child.ModelName == "alias-one", "会话模型留空，对话初始显示别名");
            Check(child.Key.Contains($"{now:yyyy-MM-dd HH:mm} · dialog"), "对话标题显示日期和时分");
            Check(!Desc<TextBlock>(control.ItemContainerGenerator.ContainerFromIndex(0)!).Any(t => t.IsVisible && t.Text == "alias-one"), "会话模型列实际留空");
            var nameBox = (ComboBox)window.FindName("UsageNameBox");
            nameBox.SelectedIndex = 1; window.UpdateLayout();
            var selectedRows = control.Items.Cast<object>().ToList();
            Check(((UsageRow)Get(selectedRows[0], "Row")).ModelName == "" && ((UsageRow)Get(selectedRows[1], "Row")).ModelName == "real-one", "切换真实名后会话留空且对话显示真实模型");
            Check(Desc<TextBlock>(control.ItemContainerGenerator.ContainerFromIndex(1)!).Any(t => t.IsVisible && t.Text == "real-one"), "对话模型列实际显示真实模型");
            nameBox.SelectedIndex = 0; window.UpdateLayout();
            var summary = (TextBlock)window.FindName("UsageSummary"); Check(summary.Text.Contains("2 次") && !summary.Text.Contains("4 次"), "总计仅计算父级请求");
            var childContainer = control.ItemContainerGenerator.ContainerFromIndex(1) as DependencyObject;
            var texts = Desc<TextBlock>(childContainer!).Where(x => x.IsVisible).Select(x => x.Text).ToArray();
            Check(texts.Any(x => x.Contains("推断")), "推断标志在实际子行可见");
            Check(texts.Contains("2") && texts.Contains("20") && texts.Contains("6"), "子行请求与 token 列绑定正确");
            var scroll = (ScrollViewer)window.FindName("UsageScroll");
            var panel = (Grid)window.FindName("UsagePanel");
            var header = (Grid)window.FindName("UsageHeader");
            void CheckAdjacent(string first, string second, string stage)
            {
                var texts = Desc<TextBlock>(header).Where(t => t.IsVisible && !string.IsNullOrWhiteSpace(t.Text)).ToList();
                var a = texts.Single(t => t.Text == first); var b = texts.Single(t => t.Text == second);
                var left = a.TranslatePoint(new Point(0, 0), header).X;
                var right = b.TranslatePoint(new Point(0, 0), header).X;
                Check(right > left && Math.Abs(right - left - header.ColumnDefinitions[Grid.GetColumn(a)].ActualWidth) < 1, stage + "名称后紧邻模型列");
            }
            CheckAdjacent("会话", "模型", "会话分组");
            var exports = Desc<Button>(panel).Where(b => b.IsVisible && Equals(b.Content, "导出")).ToList();
            Check(exports.Count == 1, "仅有一个导出按钮");
            var exportRight = exports[0].TranslatePoint(new Point(exports[0].ActualWidth, 0), panel).X;
            Check(Math.Abs(exportRight - panel.ActualWidth) < 8, "导出按钮右对齐");
            var rowGrid = Desc<Grid>(childContainer!).First(g => g.ColumnDefinitions.Count == header.ColumnDefinitions.Count);
            for (int i = 0; i < header.ColumnDefinitions.Count; i++) Check(Math.Abs(header.ColumnDefinitions[i].ActualWidth - rowGrid.ColumnDefinitions[i].ActualWidth) < 1, $"第 {i + 1} 列表头与子行对齐");
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
            var lastContainer = (FrameworkElement)control.ItemContainerGenerator.ContainerFromIndex(control.Items.Count - 1);
            var lastGrid = Desc<Grid>(lastContainer).First(g => g.ColumnDefinitions.Count == header.ColumnDefinitions.Count);
            for (int i = 0; i < header.ColumnDefinitions.Count; i++) Check(Math.Abs(header.ColumnDefinitions[i].ActualWidth - lastGrid.ColumnDefinitions[i].ActualWidth) < 1, $"滚动条出现后第 {i + 1} 列对齐");
            scroll.ScrollToBottom(); Layout();
            var lastPosition = lastContainer.TranslatePoint(new Point(0, lastContainer.ActualHeight), scroll);
            Check(Math.Abs(scroll.VerticalOffset - scroll.ScrollableHeight) < 1 && lastPosition.Y <= scroll.ViewportHeight + 1 && lastPosition.Y > 0, "滚动到底可看到最后一项");
            Invoke("RefreshUsage"); Layout();
            Check(control.Items.Count == 21 && Math.Abs(scroll.VerticalOffset - scroll.ScrollableHeight) < 1, "刷新保留展开状态与滚动位置");
            CheckHeights("刷新后");
            scroll.ScrollToTop(); Layout(); ToggleParent();
            Check(control.Items.Count == 1, "大量对话可再次收起");
            CheckHeights("收起后");
            var range = (ComboBox)window.FindName("UsageRangeBox");
            var bucket = (ComboBox)window.FindName("BucketBox");
            Check(range.IsVisible && !bucket.IsVisible, "会话分组仅显示范围选择");
            Check(range.Items.Cast<string>().SequenceEqual(new[] { "过去 1 小时", "过去 6 小时", "过去 12 小时", "过去 24 小时", "过去 3 天", "过去 7 天" }), "其他视图保留原范围选项");
            range.SelectedIndex = 0; Layout();
            var rangePosition = range.TranslatePoint(new Point(0, 0), panel);
            items.Add(new UsageRecord { Time = now.AddDays(-2), StartedAt = now.AddDays(-2), Client = "workbuddy", Model = "within-seven", SessionId = "within-seven", Prompt = 1 });
            items.Add(new UsageRecord { Time = now.AddDays(-8), StartedAt = now.AddDays(-8), Client = "workbuddy", Model = "outside-seven", SessionId = "outside-seven", Prompt = 1 });
            Invoke("OnGroupByTime", window, new RoutedEventArgs()); Layout();
            Check(bucket.IsVisible && !range.IsVisible, "时间分组仅显示粒度选择");
            Check(Math.Abs(bucket.TranslatePoint(new Point(0, 0), panel).X - rangePosition.X) < 1, "粒度选择占据原范围选择位置");
            Check(bucket.Items.Cast<string>().SequenceEqual(new[] { "15 分钟", "30 分钟", "01 小时", "06 小时", "12 小时", "24 小时" }), "时间粒度选项完整且顺序正确");
            var modelBox = (ComboBox)window.FindName("UsageModelBox");
            Check(modelBox.Items.Cast<string>().Contains("within-seven") && !modelBox.Items.Cast<string>().Contains("outside-seven"), "时间视图模型筛选固定最近七天，不受其他视图范围影响");
            Check(summary.Text.Contains("21 次"), "时间视图统计仅包含最近七天记录");
            var expectedBuckets = new[] { TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(30), TimeSpan.FromHours(1), TimeSpan.FromHours(6), TimeSpan.FromHours(12), TimeSpan.FromHours(24) };
            for (int i = 0; i < expectedBuckets.Length; i++)
            {
                bucket.SelectedIndex = i; Layout();
                Check((TimeSpan)typeof(MainWindow).GetField("_bucket", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)! == expectedBuckets[i], $"粒度选项 {i + 1} 对应正确时间间隔");
            }
            CheckHeights("切换时间粒度后");
            Invoke("OnGroupByRoute", window, new RoutedEventArgs()); Layout();
            Check(range.IsVisible && !bucket.IsVisible && range.SelectedIndex == 0 && summary.Text.Contains("20 次"), "切换其他视图恢复原范围及过滤结果");
            CheckAdjacent("上游名称", "上游类型", "上游分组");
            CheckAdjacent("上游类型", "模型", "上游分组类型后");
            Invoke("OnGroupByModel", window, new RoutedEventArgs()); Layout();
            Check(control.IsVisible, "切换模型分组显示统计列表");
            CheckAdjacent("模型别名", "实际名称", "模型分组");
            CheckHeights("切换模型分组后");
            Invoke("OnGroupBySession", window, new RoutedEventArgs()); Layout();
            Check(control.IsVisible && control.Items.Count == 1, "切回会话分组仍为收起状态");
            CheckHeights("切回会话分组后");
            window.AllowClose(); window.Close(); app.Shutdown(); return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}
