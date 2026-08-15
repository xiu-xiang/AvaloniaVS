# AxamlCraft for Visual Studio

AxamlCraft 是一款基于 AvaloniaVS **旧开源版本**深度定制的 **AXAML 开发增强插件**，在保留其设计器/预览功能的基础上，进一步增强了开发体验。

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

- 插件内置 **Avalonia.Templates 11.3.0** 项目模板与项模板
- 安装插件后即可在 VS「新建项目」中直接创建 Avalonia 应用（App / Mvvm App / UserControl / Window 等）