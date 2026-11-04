using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using OAIRouter;

internal static class Program
{
    static void Check(bool value, string label) { if (!value) throw new Exception(label); Console.WriteLine("PASS " + label); }
    static IEnumerable<T> Desc<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T t) yield return t;
            foreach (var nested in Desc<T>(child)) yield return nested;
        }
    }
    [STAThread] static int Main()
    {
        try
        {
            typeof(App).GetProperty("IsDev")!.SetValue(null, true);
            Check(App.IsDev && ConfigStore.Dir.StartsWith(AppContext.BaseDirectory, StringComparison.OrdinalIgnoreCase), "数据隔离在测试目录");
            Directory.CreateDirectory(ConfigStore.Dir);
            var old = ConfigStore.FromJson("{\"ModelMap\":[{\"Alias\":\"old\",\"Target\":\"model\"}]} ")!;
            Check(old.ModelMap.Single().Mode == "" && old.MapColumnWidths.SequenceEqual(new double[] {150,150,106}), "旧配置缺少模式和列宽时兼容默认值");
            Check(ConfigStore.Save(old), "写入独立旧配置");
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/OAIRouter;component/Theme.xaml", UriKind.Relative) });
            app.Resources["BoolToVis"] = new BoolToVis(); app.Resources["InverseBoolToVis"] = new InverseBoolToVis(); app.Resources["LevelToBrush"] = new LevelToBrush();
            Accent.Init(); app.Resources["AccentBrush"] = Accent.MainBrush; app.Resources["AccentDarkBrush"] = Accent.DarkBrush; app.Resources["AccentSoftBrush"] = Accent.SoftBrush; app.Resources["AccentHoverBrush"] = Accent.HoverBrush;
            var window = new MainWindow(); window.Show();
            void Layout() { window.UpdateLayout(); window.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(() => { })); window.UpdateLayout(); }
            void Invoke(string name, params object[] args) => typeof(MainWindow).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, args);
            Layout();
            var items = (ItemsControl)window.FindName("MapItems"); var scroll = (ScrollViewer)window.FindName("MapScroll");
            var cfg = (AppConfig)typeof(MainWindow).GetField("_cfg", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;
            for (int i = 0; i < 30; i++) cfg.ModelMap.Add(new ModelMapping { Alias = "existing" + i, Target = "model" });
            Layout(); scroll.ScrollToBottom(); Layout(); Check(scroll.VerticalOffset > 0, "添加前映射列表已滚动到底部");
            var formerFirst = cfg.ModelMap[0]; Invoke("AddMap"); Layout();
            var map = cfg.ModelMap[0]; Check(map.IsEditing && map.Alias.StartsWith("alias") && ReferenceEquals(cfg.ModelMap[1], formerFirst), "新映射置顶并进入编辑");
            Check(scroll.VerticalOffset == 0, "新增映射后滚动到顶部");
            var container = (DependencyObject)items.ItemContainerGenerator.ContainerFromIndex(0);
            var combos = Desc<ComboBox>(container).Where(c => c.IsVisible).ToArray();
            var text = Desc<TextBox>(container).First(t => t.IsVisible && !Desc<ComboBox>(container).Any(c => ReferenceEquals(c.Template.FindName("PART_EditableTextBox", c), t)));
            void Enter(UIElement target) { var source = PresentationSource.FromVisual(target)!; var e = new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, Key.Enter) { RoutedEvent = Keyboard.PreviewKeyDownEvent }; target.RaiseEvent(e); }
            Enter(text); Check(!map.IsEditing, "普通文本框回车结束编辑");
            map.IsEditing = true; Layout();
            combos = Desc<ComboBox>(container).Where(c => c.IsVisible).ToArray();
            var editable = combos.First(c => c.IsEditable); editable.ApplyTemplate();
            var editor = (TextBox)editable.Template.FindName("PART_EditableTextBox", editable); Check(editor != null, "找到可编辑下拉框输入框");
            editable.IsDropDownOpen = true; Layout(); Enter(editor!); Check(map.IsEditing, "下拉打开时回车不提前结束编辑");
            editable.IsDropDownOpen = false; Layout(); Enter(editor!); Check(!map.IsEditing, "可编辑下拉输入框回车结束编辑");
            var header = (Grid)window.FindName("MapHeader"); var thumbs = Desc<Thumb>(header).OrderBy(Grid.GetColumn).ToArray(); Check(thumbs.Length == 3, "三列均提供拖动控件");
            for (int i = 0; i < 3; i++) { var before = header.ColumnDefinitions[i].Width.Value; thumbs[i].RaiseEvent(new DragDeltaEventArgs(17, 0) { RoutedEvent = Thumb.DragDeltaEvent }); Check(Math.Abs(header.ColumnDefinitions[i].Width.Value - before - 17) < .01, $"第{i + 1}列拖动调整宽度"); }
            for (int i = 0; i < 3; i++) thumbs[i].RaiseEvent(new DragDeltaEventArgs(350, 0) { RoutedEvent = Thumb.DragDeltaEvent });
            Layout();
            var headerScroll = (ScrollViewer)window.FindName("MapHeaderScroll");
            Check(scroll.ScrollableWidth > 0, "加宽后映射列表可横向滚动");
            scroll.ScrollToHorizontalOffset(scroll.ScrollableWidth / 2); Layout();
            Check(scroll.HorizontalOffset > 0 && Math.Abs(scroll.HorizontalOffset - headerScroll.HorizontalOffset) < 1, "横向滚动时表头与内容偏移一致");
            thumbs[0].RaiseEvent(new DragCompletedEventArgs(17, 0, false) { RoutedEvent = Thumb.DragCompletedEvent });
            var loaded = ConfigStore.FromJson(File.ReadAllText(ConfigStore.Path))!;
            Check(loaded.MapColumnWidths.SequenceEqual(header.ColumnDefinitions.Take(3).Select(c => c.Width.Value)), "拖动完成立即保存列宽");
            var row = Desc<Grid>(container).First(g => g.ColumnDefinitions.Count == 5);
            Layout(); for (int i = 0; i < 3; i++) Check(Math.Abs(row.ColumnDefinitions[i].ActualWidth - header.ColumnDefinitions[i].ActualWidth) < 1, $"第{i + 1}列表头与行宽对齐");
            var expectedWidths = loaded.MapColumnWidths;
            var upstream = new UpstreamSettingsWindow(new RouteRule(), _ => false) { Owner = window };
            upstream.Show(); Layout();
            Check(Desc<TextBlock>(upstream).Any(t => t.IsVisible && t.Text == "用户 ID（可选）"), "上游配置显示用户 ID 标签");
            upstream.Close();
            window.AllowClose(); window.Close();
            window = new MainWindow(); window.Show(); Layout();
            var restored = (Grid)window.FindName("MapHeader");
            Check(restored.ColumnDefinitions.Take(3).Select(c => c.Width.Value).SequenceEqual(expectedWidths), "重新构造窗口恢复保存的三列宽度");
            window.AllowClose(); window.Close(); app.Shutdown(); return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}
