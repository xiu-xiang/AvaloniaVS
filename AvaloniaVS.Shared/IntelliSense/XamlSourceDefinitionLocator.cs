using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio.Shell;
using VSLangProj;

namespace AvaloniaVS.IntelliSense
{
    /// <summary>
    /// 在解决方案的 AXAML/XAML 源文件中定位样式类（Selector）与资源键（x:Key）。
    /// </summary>
    internal static class XamlSourceDefinitionLocator
    {
        /// <summary>
        /// 查找样式类在 Selector 中的定义位置。
        /// </summary>
        public static bool TryFindStyleClass(
            string className,
            string preferredFilePath,
            out string filePath,
            out int offset)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            filePath = null;
            offset = -1;

            if (string.IsNullOrEmpty(className))
            {
                return false;
            }

            // Avalonia：Button.accent / ^.accent / .accent / Border.accent:pointerover
            // 注意：'.' 前经常是类型名字母，不能要求「非单词字符」
            var classPattern = new Regex(
                $@"\.{Regex.Escape(className)}(?=$|[^\w-])",
                RegexOptions.CultureInvariant);

            return TryFindInXamlFiles(
                preferredFilePath,
                content => FindSelectorClassOffset(content, classPattern),
                out filePath,
                out offset);
        }

        /// <summary>
        /// 查找 x:Key 资源定义位置。
        /// </summary>
        public static bool TryFindResourceKey(
            string resourceKey,
            string preferredFilePath,
            out string filePath,
            out int offset)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            filePath = null;
            offset = -1;

            if (string.IsNullOrEmpty(resourceKey))
            {
                return false;
            }

            // x:Key="DemoBrush" / x:Key='DemoBrush'
            var keyPattern = new Regex(
                $@"\bx:Key\s*=\s*([""']){Regex.Escape(resourceKey)}\1",
                RegexOptions.CultureInvariant);

            return TryFindInXamlFiles(
                preferredFilePath,
                content =>
                {
                    var match = keyPattern.Match(content);
                    if (!match.Success)
                    {
                        return -1;
                    }

                    // 跳到键名本身（引号之后）
                    var quoteIndex = match.Value.LastIndexOf(match.Groups[1].Value[0]);
                    return match.Index + quoteIndex + 1;
                },
                out filePath,
                out offset);
        }

        private static int FindSelectorClassOffset(string content, Regex classPattern)
        {
            // 匹配 Selector="..." / Selector='...'
            var selectorAttr = Regex.Matches(
                content,
                @"\bSelector\s*=\s*([""'])(.*?)\1",
                RegexOptions.CultureInvariant | RegexOptions.Singleline);

            foreach (Match attr in selectorAttr)
            {
                var value = attr.Groups[2].Value;
                var valueStart = attr.Groups[2].Index;
                var classMatch = classPattern.Match(value);
                if (classMatch.Success)
                {
                    // 指向 '.' 之后的类名
                    var dotInGroup = classMatch.Value.LastIndexOf('.');
                    return valueStart + classMatch.Index + Math.Max(0, dotInGroup) + 1;
                }
            }

            return -1;
        }

        private static bool TryFindInXamlFiles(
            string preferredFilePath,
            Func<string, int> findOffset,
            out string filePath,
            out int offset)
        {
            filePath = null;
            offset = -1;

            foreach (var path in EnumerateSearchPaths(preferredFilePath))
            {
                // 优先读已打开文档的缓冲区（含未保存的本地 Styles）
                if (!TryReadXamlContent(path, out var content))
                {
                    continue;
                }

                var found = findOffset(content);
                if (found >= 0)
                {
                    filePath = path;
                    offset = found;
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// 读取 AXAML 内容：已打开文档用编辑器文本，否则读磁盘。
        /// </summary>
        private static bool TryReadXamlContent(string path, out string content)
        {
            content = null;
            ThreadHelper.ThrowIfNotOnUIThread();

            try
            {
                var dte = Package.GetGlobalService(typeof(DTE)) as DTE2;
                if (dte?.Documents != null)
                {
                    foreach (Document doc in dte.Documents)
                    {
                        string fullName = null;
                        try
                        {
                            fullName = doc.FullName;
                        }
                        catch
                        {
                            continue;
                        }

                        if (string.IsNullOrEmpty(fullName)
                            || !string.Equals(fullName, path, StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        if (doc.Object("TextDocument") is TextDocument textDoc)
                        {
                            var editPoint = textDoc.StartPoint.CreateEditPoint();
                            content = editPoint.GetText(textDoc.EndPoint);
                            return !string.IsNullOrEmpty(content);
                        }
                    }
                }
            }
            catch
            {
                // 回退到磁盘
            }

            try
            {
                if (!File.Exists(path))
                {
                    return false;
                }

                content = File.ReadAllText(path);
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 优先搜索当前文件与 App.axaml，再遍历解决方案中其它 AXAML/XAML。
        /// </summary>
        private static IEnumerable<string> EnumerateSearchPaths(string preferredFilePath)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (!string.IsNullOrEmpty(preferredFilePath) && seen.Add(preferredFilePath))
            {
                yield return preferredFilePath;
            }

            List<string> all;
            try
            {
                all = EnumerateSolutionXamlFiles().ToList();
            }
            catch
            {
                yield break;
            }

            // App.axaml / Application.axaml 优先（全局样式与资源常在此）
            foreach (var path in all.Where(IsAppXaml).OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                if (seen.Add(path))
                {
                    yield return path;
                }
            }

            foreach (var path in all.OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                if (seen.Add(path))
                {
                    yield return path;
                }
            }
        }

        private static bool IsAppXaml(string path)
        {
            var name = Path.GetFileName(path);
            return name.Equals("App.axaml", StringComparison.OrdinalIgnoreCase)
                   || name.Equals("App.xaml", StringComparison.OrdinalIgnoreCase)
                   || name.Equals("Application.axaml", StringComparison.OrdinalIgnoreCase)
                   || name.Equals("Application.xaml", StringComparison.OrdinalIgnoreCase);
        }

        private static IEnumerable<string> EnumerateSolutionXamlFiles()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var dte = Package.GetGlobalService(typeof(DTE)) as DTE2;
            if (dte?.Solution?.Projects == null)
            {
                yield break;
            }

            foreach (var project in FlattenProjects(dte.Solution.Projects.OfType<Project>()))
            {
                foreach (var path in EnumerateProjectXamlFiles(project))
                {
                    yield return path;
                }
            }
        }

        private static IEnumerable<Project> FlattenProjects(IEnumerable<Project> projects)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            foreach (var project in projects)
            {
                if (project == null)
                {
                    continue;
                }

                if (project.Object is VSProject)
                {
                    yield return project;
                }
                else if (project.Object is SolutionFolder && project.ProjectItems != null)
                {
                    foreach (var child in FlattenSubProjects(project.ProjectItems))
                    {
                        yield return child;
                    }
                }
            }
        }

        private static IEnumerable<Project> FlattenSubProjects(ProjectItems items)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            foreach (ProjectItem item in items)
            {
                var project = item.SubProject;
                if (project?.Object is VSProject)
                {
                    yield return project;
                }
                else if (project?.Object is SolutionFolder && project.ProjectItems != null)
                {
                    foreach (var child in FlattenSubProjects(project.ProjectItems))
                    {
                        yield return child;
                    }
                }
            }
        }

        private static IEnumerable<string> EnumerateProjectXamlFiles(Project project)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (project?.ProjectItems == null)
            {
                yield break;
            }

            foreach (var path in EnumerateItems(project.ProjectItems))
            {
                yield return path;
            }
        }

        private static IEnumerable<string> EnumerateItems(ProjectItems items)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            foreach (ProjectItem item in items)
            {
                string path = null;
                try
                {
                    path = item.FileCount > 0 ? item.FileNames[1] : null;
                }
                catch
                {
                    // 忽略无法读取的项
                }

                if (!string.IsNullOrEmpty(path)
                    && (path.EndsWith(".axaml", StringComparison.OrdinalIgnoreCase)
                        || path.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase)))
                {
                    yield return path;
                }

                if (item.ProjectItems != null && item.ProjectItems.Count > 0)
                {
                    foreach (var child in EnumerateItems(item.ProjectItems))
                    {
                        yield return child;
                    }
                }
            }
        }
    }
}
