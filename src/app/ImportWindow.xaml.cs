using System.Windows;

namespace OAIRouter;

public sealed class ImportSelection
{
    public bool Routes { get; init; }
    public bool Maps { get; init; }
    public bool Pricing { get; init; }
    public bool Models { get; init; }
    public bool Settings { get; init; }
}

public partial class ImportWindow : Window
{
    public ImportSelection Selection { get; private set; } = new();

    public ImportWindow(AppConfig incoming)
    {
        InitializeComponent();
        ChkRoutes.Content = $"路由规则（{incoming.Routes.Count} 条）";
        ChkMaps.Content = $"模型映射（{incoming.ModelMap.Count} 条）";
        ChkPricing.Content = $"价格表（{incoming.Pricing.Count} 条）";
        ChkModels.Content = $"模型库（{incoming.Models.Count} 个）";
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        WindowChrome.Apply(this);
    }

    private void OnConfirm(object sender, RoutedEventArgs e)
    {
        Selection = new ImportSelection
        {
            Routes = ChkRoutes.IsChecked == true,
            Maps = ChkMaps.IsChecked == true,
            Pricing = ChkPricing.IsChecked == true,
            Models = ChkModels.IsChecked == true,
            Settings = ChkSettings.IsChecked == true,
        };
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
