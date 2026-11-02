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
                MenuItemData.Of("复制监听地址", "copy-address", "copy"),
                MenuItemData.Sep(),
                MenuItemData.Of("退出程序", "quit", "power", "Ctrl+Q"),
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
                MenuItemData.Of("清空日志", "clear-log", "close", "Ctrl+L"),
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
                MenuItemData.Of("重置程序数据", "reset-all", "close"),
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
