using System;
using System.Runtime.InteropServices;
using AvaloniaVS.Shared.Views;
using EnvDTE;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.OLE.Interop;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.TextManager.Interop;
using Serilog;
using IOleServiceProvider = Microsoft.VisualStudio.OLE.Interop.IServiceProvider;

namespace AvaloniaVS.Services
{
    /// <summary>
    /// Implements <see cref="IVsEditorFactory"/> to create <see cref="DesignerPane"/>s containing
    /// an Avalonia XAML designer.
    /// </summary>
    [Guid(AvaloniaVS.Constants.AvaloviaFactoryEditorGuidString)]
    internal sealed class EditorFactory : IVsEditorFactory, IDisposable
    {
        private readonly AvaloniaPackage _package;
        private IOleServiceProvider _oleServiceProvider;
        private ServiceProvider _serviceProvider;

        private const string csExt = ".cs";
        private const string fsExt = ".fs";
        private const string vbExt = ".vb";
        /// <summary>
        /// Initializes a new instance of the <see cref="EditorFactory"/> class.
        /// </summary>
        /// <param name="package">The package that the factory belongs to.</param>
        public EditorFactory(AvaloniaPackage package) => _package = package;

        /// <inheritdoc/>
        public int SetSite(IOleServiceProvider psp)
        {
            _oleServiceProvider = psp;
            _serviceProvider = new ServiceProvider(psp);
            return VSConstants.S_OK;
        }

        /// <inheritdoc/>
        public int MapLogicalView(ref Guid rguidLogicalView, out string pbstrPhysicalView)
        {
            pbstrPhysicalView = null;

            if (rguidLogicalView == VSConstants.LOGVIEWID_Primary ||
                rguidLogicalView == VSConstants.LOGVIEWID_Debugging ||
                rguidLogicalView == VSConstants.LOGVIEWID_TextView ||
                rguidLogicalView == VSConstants.LOGVIEWID_Designer)
            {
                return VSConstants.S_OK;
            }

            if(rguidLogicalView == VSConstants.LOGVIEWID_Code)
            {
                pbstrPhysicalView = "Code";
                return VSConstants.S_OK;
            }

            return VSConstants.E_NOTIMPL;
        }

        /// <inheritdoc/>
        public int CreateEditorInstance(
            uint grfCreateDoc,
            string pszMkDocument,
            string pszPhysicalView,
            IVsHierarchy pvHier,
            uint itemid,
            IntPtr punkDocDataExisting,
            out IntPtr ppunkDocView,
            out IntPtr ppunkDocData,
            out string pbstrEditorCaption,
            out Guid pguidCmdUI,
            out int pgrfCDW)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            Log.Logger.Verbose("Started EditorFactory.CreateEditorInstance({Filename})", pszMkDocument);

            ppunkDocView = IntPtr.Zero;
            ppunkDocData = IntPtr.Zero;
            pguidCmdUI = Constants.AvaloviaFactoryEditorGuid;
            pgrfCDW = 0;
            pbstrEditorCaption = string.Empty;

            if (pszPhysicalView == "Code")
            {

                // We only want to handle "View Code" if our designer pane is active
                if (!pszMkDocument.EndsWith(".axaml", StringComparison.OrdinalIgnoreCase) &&
                    !pszMkDocument.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase))
                    return VSConstants.E_INVALIDARG;
                else
                {
                    if (GetExtensionObject(pvHier,itemid) is ProjectItem pi)
                    {
                        var codeFile = FindCodeFileForXaml(pi, out var codeProjectItem);
                        if (codeFile)
                        {
                            var wnd = codeProjectItem.Open(EnvDTE.Constants.vsViewKindTextView);
                            wnd.Activate();
                            return VSConstants.S_OK;
                        }
                    }
                }
                pszPhysicalView = null;
            }

            if ((grfCreateDoc & (VSConstants.CEF_OPENFILE | VSConstants.CEF_SILENT)) == 0)
            {
                return VSConstants.E_INVALIDARG;
            }

            // For reference of the new way this works:
            // https://github.com/madskristensen/EditorConfigLanguage/blob/master/src/LanguageService/EditorFactory.cs
            // and this sample
            // https://github.com/microsoft/VSSDK-Extensibility-Samples/tree/master/WPFDesigner_XML/WPFDesigner_XML

            // 修复：punkDocDataExisting 为空时仍可能已在 RDT 中；若再新建 VsTextBuffer，
            // 会出现「编辑缓冲已脏但标签无 * / Ctrl+S 不落盘」的双 DocData 分裂。
            var passedExisting = punkDocDataExisting != IntPtr.Zero;
            IntPtr resolvedDocData = punkDocDataExisting;
            IVsTextLines textLines;
            var createdNewBuffer = false;

            if (!passedExisting)
            {
                if (TryAcquireTextBufferFromRdt(pszMkDocument, out textLines, out resolvedDocData))
                {
                    Log.Logger.Debug("EditorFactory 复用 RDT DocData: {Filename}", pszMkDocument);
                }
                else
                {
                    // 修复：RDT 有 cookie 但 DocData 为空（僵尸/占位项）时，新建缓冲无法挂上标签脏标记。
                    // 先驱逐该条目，让壳层随后用我们返回的 DocData 正常 RegisterAndLockDocument。
                    if (TryEvictEmptyRdtEntry(pszMkDocument))
                        Log.Logger.Debug("EditorFactory 已驱逐空 DocData 的 RDT 项: {Filename}", pszMkDocument);

                    textLines = CreateNewTextBuffer();
                    resolvedDocData = Marshal.GetIUnknownForObject(textLines);
                    createdNewBuffer = true;
                }
            }
            else
            {
                textLines = GetTextBuffer(punkDocDataExisting);
                Marshal.AddRef(resolvedDocData);
            }

            ppunkDocData = resolvedDocData;

            try
            {
                // Prepare the way for the IVsCodeWindow...Note that it may not be created yet
                // as we need to wait for the IVsTextBuffer to fully initialize first. This is all
                // handled from CreateDocumentView and the TextEditorHost
                var docViewObject = CreateDocumentView(pszMkDocument, pszPhysicalView, textLines, createdNewBuffer);

                // Create the pane that will host our previewer and will be associated with this text data
                var pane = new EditorPane(GetProject(pvHier), docViewObject);

                ppunkDocView = Marshal.GetIUnknownForObject(pane);
            }
            finally
            {
                // 视图创建失败：释放我们为 ppunkDocData 取得的引用
                if (ppunkDocView == IntPtr.Zero && ppunkDocData != IntPtr.Zero)
                {
                    Marshal.Release(ppunkDocData);
                    ppunkDocData = IntPtr.Zero;
                }
            }

            Log.Logger.Verbose("Finished EditorFactory.CreateEditorInstance({Filename})", pszMkDocument);
            return VSConstants.S_OK;
        }
                
        /// <inheritdoc/>
        public int Close() => VSConstants.S_OK;

        /// <inheritdoc/>
        public void Dispose()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            _serviceProvider?.Dispose();
            _serviceProvider = null;
        }

        private IVsTextLines GetTextBuffer(IntPtr docDataExisting)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (docDataExisting == IntPtr.Zero)
                return CreateNewTextBuffer();

            Log.Logger.Verbose("Using Existing IVsTextBuffer");
            var textLines = ExtractTextLines(docDataExisting);
            if (textLines == null)
                throw Marshal.GetExceptionForHR(VSConstants.VS_E_INCOMPATIBLEDOCDATA);

            return textLines;
        }

        /// <summary>
        /// 从 RDT 取得已注册的 DocData（若可转为 IVsTextLines）。
        /// FindAndLockDocument(RDT_NoLock) 返回的 docData 带引用，成功时所有权交给调用方。
        /// </summary>
        private bool TryAcquireTextBufferFromRdt(string moniker, out IVsTextLines textLines, out IntPtr docDataPtr)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            textLines = null;
            docDataPtr = IntPtr.Zero;

            var rdt = Package.GetGlobalService(typeof(SVsRunningDocumentTable)) as IVsRunningDocumentTable;
            if (rdt == null)
                return false;

            var hr = rdt.FindAndLockDocument(
                (uint)_VSRDTFLAGS.RDT_NoLock,
                moniker,
                out _,
                out _,
                out var punkDocData,
                out var cookie);

            if (ErrorHandler.Failed(hr) || cookie == 0)
                return false;

            // cookie 在但 DocData 空：僵尸/占位项，交由 TryEvictEmptyRdtEntry 处理
            if (punkDocData == IntPtr.Zero)
                return false;

            try
            {
                textLines = ExtractTextLines(punkDocData);
                if (textLines == null)
                    return false;

                docDataPtr = punkDocData;
                punkDocData = IntPtr.Zero; // 所有权转移给调用方
                return true;
            }
            finally
            {
                if (punkDocData != IntPtr.Zero)
                    Marshal.Release(punkDocData);
            }
        }

        /// <summary>
        /// 驱逐「有 cookie、无 DocData」的 RDT 项，避免新建缓冲无法绑定到 Shell 标签/保存链路。
        /// 打开过程中不主动 CloseFrame，以免关掉正在打开的窗口。
        /// </summary>
        /// <returns>驱逐后 moniker 已不在 RDT 中则为 true。</returns>
        private bool TryEvictEmptyRdtEntry(string moniker)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            var rdt = Package.GetGlobalService(typeof(SVsRunningDocumentTable)) as IVsRunningDocumentTable;
            if (rdt == null)
                return false;

            var hr = rdt.FindAndLockDocument(
                (uint)_VSRDTFLAGS.RDT_NoLock,
                moniker,
                out _,
                out _,
                out var punkDocData,
                out var cookie);

            if (ErrorHandler.Failed(hr) || cookie == 0)
                return false;

            if (punkDocData != IntPtr.Zero)
            {
                Marshal.Release(punkDocData);
                return false; // 已有真实 DocData，不驱逐
            }

            uint readLocks = 0, editLocks = 0;
            rdt.GetDocumentInfo(
                cookie,
                out _,
                out readLocks,
                out editLocks,
                out _,
                out _,
                out _,
                out var infoDocData);
            if (infoDocData != IntPtr.Zero)
                Marshal.Release(infoDocData);

            var rdt2 = rdt as IVsRunningDocumentTable2;
            if (rdt2 != null)
            {
                // 按 cookie 关闭文档（NoSave），清掉占位项
                ErrorHandler.Succeeded(rdt2.CloseDocuments(
                    (uint)__FRAMECLOSE.FRAMECLOSE_NoSave,
                    null,
                    cookie));
            }

            // 残留锁逐个解开
            rdt.GetDocumentInfo(
                cookie,
                out _,
                out readLocks,
                out editLocks,
                out _,
                out _,
                out _,
                out infoDocData);
            if (infoDocData != IntPtr.Zero)
                Marshal.Release(infoDocData);

            while (editLocks > 0)
            {
                rdt.UnlockDocument(
                    (uint)(_VSRDTFLAGS.RDT_EditLock | _VSRDTFLAGS.RDT_Unlock_NoSave),
                    cookie);
                editLocks--;
            }

            while (readLocks > 0)
            {
                rdt.UnlockDocument((uint)_VSRDTFLAGS.RDT_ReadLock, cookie);
                readLocks--;
            }

            if (rdt2 != null)
                rdt2.QueryCloseRunningDocument(moniker, out _);

            // 确认是否已从表中消失
            hr = rdt.FindAndLockDocument(
                (uint)_VSRDTFLAGS.RDT_NoLock,
                moniker,
                out _,
                out _,
                out var afterDocData,
                out var afterCookie);
            if (afterDocData != IntPtr.Zero)
                Marshal.Release(afterDocData);

            return ErrorHandler.Failed(hr) || afterCookie == 0;
        }

        private IVsTextLines CreateNewTextBuffer()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            Log.Logger.Verbose("Creating new IVsTextBuffer");

            Type textLinesType = typeof(IVsTextLines);
            Guid riid = textLinesType.GUID;
            Guid clsid = typeof(VsTextBufferClass).GUID;
            var textLines = _package.CreateInstance(ref clsid, ref riid, textLinesType) as IVsTextLines;
            ((IObjectWithSite)textLines).SetSite(_serviceProvider.GetService(typeof(IOleServiceProvider)));
            return textLines;
        }

        private static IVsTextLines ExtractTextLines(IntPtr docData)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (docData == IntPtr.Zero)
                return null;

            object dataObject = Marshal.GetObjectForIUnknown(docData);
            if (dataObject is IVsTextLines lines)
                return lines;

            if (dataObject is IVsTextBufferProvider textBufferProvider)
            {
                textBufferProvider.GetTextBuffer(out lines);
                return lines;
            }

            return null;
        }

        private TextEditorHost CreateDocumentView(string documentMoniker, string physicalView, IVsTextLines textLines, bool createdDocData)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            Log.Logger.Verbose("Creating Document View");
            if (string.IsNullOrEmpty(physicalView))
            {
                var componentModel = _serviceProvider.GetService<IComponentModel, SComponentModel>();
                var editorHost = new TextEditorHost(textLines, documentMoniker, componentModel, _oleServiceProvider);

                // 新建缓冲走 OnLoadCompleted 事件；复用已有 DocData 时可能已初始化，需主动触发。
                // EnsureInitialized 内部会处理 Inert 延迟重试。
                if (!createdDocData)
                    editorHost.EnsureInitialized();

                return editorHost;
            }

            throw Marshal.GetExceptionForHR(VSConstants.VS_E_UNSUPPORTEDFORMAT);
        }

        private static Project GetProject(IVsHierarchy hierarchy)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            ErrorHandler.ThrowOnFailure(hierarchy.GetProperty(
                VSConstants.VSITEMID_ROOT,
                (int)__VSHPROPID.VSHPROPID_ExtObject,
                out var objProj));
            return objProj as Project;
        }

        private static EnvDTE.ProjectItem GetExtensionObject(IVsHierarchy hierarchy, uint itemId)
        {
            object project;

            ThreadHelper.ThrowIfNotOnUIThread();

            ErrorHandler.ThrowOnFailure(
                hierarchy.GetProperty(
                    itemId,
                    (int)__VSHPROPID.VSHPROPID_ExtObject,
                    out project
                )
            );

            return (project as EnvDTE.ProjectItem);
        }

#pragma warning disable VSTHRD010 // Only called from ExecuteCommand, which does the thread check
        private bool FindCodeFileForXaml(ProjectItem projectItem, out ProjectItem codeProjectItem)
        {
            codeProjectItem = null;
            if (projectItem.ProjectItems.Count == 0)
                return false;

            // Search the project items under the current project item
            // "MainWindow.axaml" <-- projectItem
            //   "MainWindow.axaml.cs" <-- projectItem.ProjectItems
            foreach (ProjectItem pi in projectItem.ProjectItems)
            {
                if (IsCodeFile(pi.Name))
                {
                    codeProjectItem = pi;
                    return true;
                }
            }

            // TODO: If we reach here, it means VS isn't nesting the files like its suppposed
            // to:
            // Project
            //   MainWindow.axaml
            //   MainWindow.axaml.cs
            // We need to find the parent item, and search for it as a sibling
            // The parent item can be obtained through 'projectItem.Collection.Parent'
            // which will probably return either a ProjectItem or Project
            // and then run a search to see where it matches the name and ends in a lang extension
            // The issue here is how does this handle files with partial classes? Since I don't
            // have a way to test this (since VS seems to nest normally for me), marking as
            // a TODO and we'll return false to not handle it

            Log.Logger.Verbose("Attempted to view code for {Document}, but was unable to find nested code file", projectItem.Name);

            return false;
        }
#pragma warning restore

        private bool IsCodeFile(string name)
        {
            if (name.EndsWith(csExt, StringComparison.OrdinalIgnoreCase) ||
                name.EndsWith(fsExt, StringComparison.OrdinalIgnoreCase) ||
                name.EndsWith(vbExt, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return false;
        }
    }
}
