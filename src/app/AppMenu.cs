namespace OAIRouter;

public sealed class MenuItemData
{
    public string Text { get; init; } = "";
    public string Command { get; init; } = "";
    public string? Gesture { get; init; }
    public string? Group { get; init; }
    public string? Value { get; init; }
    public string? Icon { get; init; }
    public bool Separator { get; init; }

    public static MenuItemData Of(string text, string command, string? icon = null, string? gesture = null)
        => new() { Text = text, Command = command, Icon = icon, Gesture = gesture };

    public static MenuItemData Check(string text, string command, string group, string value)
        => new() { Text = text, Command = command, Group = group, Value = value };

    public static MenuItemData Sep() => new() { Separator = true };
}

public sealed class MenuGroupData
{
    public string Title { get; init; } = "";
    public List<MenuItemData> Items { get; init; } = new();
}

/// <summary>
/// 菜单树是纯数据（规范 §五）。一级菜单由程序自己的功能长出来，
/// 唯一强制存在的是「帮助」。
/// </summary>
public static class AppMenu
{
    public static List<MenuGroupData> Build() => new()
    {
        new MenuGroupData
        {
            Title = "服务(_S)",
            Items =
            {
                MenuItemData.Of("启动服务", "start", "play"),
                MenuItemData.Of("停止服务", "stop", "stop"),
                MenuItemData.Of("重启服务", "restart", "restart"),
                MenuItemData.Sep(),
                MenuItemData.Of("复制监听地址", "copy-address", "copy", "Ctrl+C"),
            },
        },
        new MenuGroupData
        {
            Title = "规则(_R)",
            Items =
            {
                MenuItemData.Of("添加路由", "add-route", "plus"),
                MenuItemData.Of("添加模型映射", "add-map", "plus"),
                MenuItemData.Sep(),
                MenuItemData.Of("价格设置", "pricing", "coin"),
                MenuItemData.Of("模型库", "models", "list"),
                MenuItemData.Sep(),
                MenuItemData.Of("恢复默认规则", "reset-routes", "restart"),
            },
        },
        new MenuGroupData
        {
            Title = "视图(_V)",
            Items =
            {
                MenuItemData.Check("自动滚动日志", "toggle-scroll", "view", "autoscroll"),
                MenuItemData.Sep(),
                MenuItemData.Of("清空日志", "clear-log", "clear"),
                MenuItemData.Of("打开日志文件", "open-log", "folder"),
            },
        },
        new MenuGroupData
        {
            Title = "数据(_D)",
            Items =
            {
                MenuItemData.Of("导入配置…", "import", "folder"),
                MenuItemData.Of("导出配置…", "export", "copy"),
                MenuItemData.Sep(),
                MenuItemData.Of("重置程序数据", "reset-all", "clear"),
            },
        },
        new MenuGroupData
        {
            Title = "帮助(_H)",
            Items =
            {
                MenuItemData.Of($"关于 {Brand.Name}", "about", "about"),
                MenuItemData.Of(Brand.Email, "email", "mail"),
            },
        },
    };
}

/// <summary>24×24 线性路径图标（规范 §五：不用字体图标、不用 emoji）。</summary>
public static class Icons
{
    public static readonly Dictionary<string, string> Paths = new()
    {
        ["play"] = "M8 5l11 7-11 7z",
        ["stop"] = "M7 7h10v10H7z",
        ["restart"] = "M20 12a8 8 0 1 1-2.4-5.7M20 4.5V10h-5.5",
        ["copy"] = "M9 9h9v11H9zM6 15H4V4h11v2",
        ["plus"] = "M12 5v14M5 12h14",
        ["clear"] = "M6 6l12 12M18 6L6 18",
        ["folder"] = "M4 7h6l2 2h8v9H4z",
        ["about"] = "M12 3a9 9 0 1 0 0 18 9 9 0 0 0 0-18zM12 11v6M12 7.6v.01",
        ["mail"] = "M4 6h16v12H4zM4 7l8 6 8-6",
        ["check"] = "M4.5 12.5l5 5L19.5 7",
        ["arrow"] = "M5 12h14M13 6l6 6-6 6",
        ["window"] = "M3.5 5.5h17v13h-17zM3.5 9h17",
        ["coin"] = "M12 3.5a8.5 8.5 0 1 0 0 17 8.5 8.5 0 0 0 0-17zM12 8v8M9.5 10h5M9.5 14h5",
        ["list"] = "M4 6.5h3M4 12h3M4 17.5h3M10 6.5h10M10 12h10M10 17.5h10",
    };
}
