## **{NAME}** **{VERSION}**

{TAGLINE}

把本机多个模型来源收拢到一个地址上：按路径前缀分流转发，按别名替换模型名，并在需要时把 Chat Completions 翻译成 Responses API，让工具调用与思考档位可以同时使用。

**主要功能**

- 路径分流：/openai、/newapi 各自指向不同上游，互不干扰
- 模型映射：客户端填 aa，转发时替换为真实模型名
- 协议翻译：Chat Completions 请求转为 Responses，响应再转回 Chat Completions
- 用量统计：筛选时间范围、查看会话分组并导出请求明细
- 上游配置：余额查询、密钥映射和 Windows DPAPI 凭据保护
- 运行日志：记录路由、模型、状态和耗时等；上游错误或异常可能回显敏感内容，分享前请检查
- 托盘常驻：可隐藏到托盘，关键事件静音通知

**快捷键**

Ctrl+L 清空日志（连按两次确认）　　Ctrl+Q 退出

{NAME} 是免费软件，依据 MIT License 分发。
© 2026 {AUTHOR}　保留所有权利。
