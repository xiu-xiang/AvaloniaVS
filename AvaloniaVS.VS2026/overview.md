# AxamlTPreviewer for Visual Studio 2026

AxamlTPreviewer 是一款面向 **Visual Studio 2026** 的 **AXAML 开发增强插件**，fork 自 [kongdetuo/AvaloniaVS](https://github.com/kongdetuo/AvaloniaVS)（基于官方 AvaloniaVS 旧开源版本的定制分支），在保留设计器/预览功能的基础上继续增强开发体验。

> 本包安装目标为 Visual Studio 2026（产品版本 18.x）。若使用 Visual Studio 2022，请安装对应的 VS2022 扩展包。


## 特色功能

- **样式类补全（Style Class Completion）**
  - 在 `Classes=""` 属性内直接补全样式类名（支持空格分隔的多类名、精确/基类/接口/全局候选）
  - 支持 `Classes.class="{Binding}"` 绑定语法，在 `Classes.` 点号后自动补全样式类并生成绑定
  - 自动过滤伪类（`:pseudo`），只提示真实存在的样式类
- **AXAML 实时预览（Live Previewer）**
  - 编辑即预览，无需手动刷新

## 功能列表

- AXAML 实时预览（Previewer / Designer）
- AXAML 智能代码补全（IntelliSense）
- **样式类补全**（特色功能）
- 绑定表达式补全（`{Binding ...}`）
- AXAML 文件图标
- 代码片段（Snippets）

## 捆绑模板

### 新建项目（Project Templates）
- 插件内置 **Avalonia.Templates 12.1.0** 项目模板（创建项目默认引用 Avalonia **12.1.0**）
- 安装后即可在 VS「新建项目」中创建 Avalonia App / Mvvm App 等

### 添加新项（Item Templates）
在解决方案资源管理器中右键项目 → **添加 → 新建项**，选择 **Avalonia** 分类：

| 模板 | 说明 |
|------|------|
| 窗体 Window | `Window.axaml` + 代码隐藏 |
| 用户控件 UserControl | `UserControl.axaml` + 代码隐藏 |
| 模板化控件 TemplatedControl | `TemplatedControl.axaml` + 代码隐藏 |
| 资源字典 ResourceDictionary | 资源字典 AXAML |
| 样式 Styles | 样式 AXAML |
