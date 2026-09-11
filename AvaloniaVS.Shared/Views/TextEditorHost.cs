using System;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using AvaloniaVS;
using AvaloniaVS.Models;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Editor;
using Microsoft.VisualStudio.OLE.Interop;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.TextManager.Interop;
using Serilog;
using IOleServiceProvider = Microsoft.VisualStudio.OLE.Interop.IServiceProvider;

namespace AvaloniaVS.Shared.Views
{
    internal class TextEditorHost : IVsTextBufferDataEvents, IDisposable
    {
        private readonly IConnectionPoint _connectionPoint;
        private readonly uint _cookie;
        private readonly Guid _xmlLanguageServiceGuid = new Guid("f6819a78-a205-47b5-be1c-675b3c7f0b8e");
        private readonly IVsTextLines _textLines;
        private readonly string _fileName;
        private readonly IComponentModel _componentModel;
        private IOleServiceProvider _oleServiceProvider;
        // 避免在 Inert 缓冲区上重复调度重试
        private bool _retryScheduled;
        private bool _unadvised;
        private bool _disposed;

        public TextEditorHost(IVsTextLines textLines, string fileName, IComponentModel componentModel, IOleServiceProvider serviceProvider)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            _textLines = textLines;
            _fileName = fileName;
            _componentModel = componentModel;
            _oleServiceProvider = serviceProvider;

            // This allows us to subscribe to the "event" for when the text buffer is finally loaded
            // and ready to be used, COM event style
            var connectionPointContainer = textLines as IConnectionPointContainer;
            Guid bufferEventsGuid = typeof(IVsTextBufferDataEvents).GUID;
            connectionPointContainer.FindConnectionPoint(ref bufferEventsGuid, out _connectionPoint);
            _connectionPoint.Advise(this, out _cookie);
        }

        public IVsCodeWindow VsCodeWindow { get; private set; }

        public IWpfTextViewHost WpfTextViewHost { get; private set; }

        public IVsTextView WpfTextView { get; private set; }

        public string FileName => _fileName;

        public IVsTextLines TextBuffer => _textLines;

        public event EventHandler CodeWindowCreated;

        void IVsTextBufferDataEvents.OnFileChanged(uint grfChange, uint dwFileAttrs) { }

        public int OnLoadCompleted(int fReload)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            // 标签关闭后不能让延迟回调重新创建编辑器视图。
            if (_disposed || VsCodeWindow != null)
                return VSConstants.S_OK;

            var eafs = _componentModel.GetService<IVsEditorAdaptersFactoryService>();
            var buffer = eafs.GetDocumentBuffer(_textLines);

            // 关键修复：ContentType 仍为 Inert 时不能建窗口，否则会出现「可编辑/可预览但不脏、不落盘」
            if (IsBufferUninitialized(buffer))
            {
                Log.Logger.Debug(
                    "TextEditorHost.OnLoadCompleted 延迟：缓冲区未就绪 ({File}, ContentType={ContentType})",
                    _fileName,
                    buffer?.ContentType?.TypeName ?? "(null)");
                ScheduleRetryLoad();
                return VSConstants.S_OK;
            }

            if (!_unadvised)
            {
                _connectionPoint.Unadvise(_cookie);
                _unadvised = true;
            }

            // Set up the language service - this will activate intellisense and syntax highlighting
            _textLines.SetLanguageServiceID(ref Unsafe.AsRef(_xmlLanguageServiceGuid));

            // Now we can create the IVsCodeWindow
            // If we don't wait until the text buffer is fully initialized before creating the IVsCodeWindow
            // it will fail completely and VS will abort loading our designer
            CreateCodeWindow(eafs, buffer);

            return VSConstants.S_OK;
        }

        /// <summary>
        /// 判断文档缓冲区是否尚未完成 VS 侧初始化（Inert / 空）。
        /// </summary>
        internal static bool IsBufferUninitialized(ITextBuffer buffer)
        {
            if (buffer == null)
                return true;

            var typeName = buffer.ContentType?.TypeName;
            if (!string.IsNullOrEmpty(typeName) &&
                typeName.Equals("inert", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return false;
        }

        /// <summary>
        /// OnLoadCompleted 过早触发时轮询等待缓冲区就绪，避免永久卡在未绑定文档状态。
        /// </summary>
        private void ScheduleRetryLoad()
        {
            if (_disposed || _retryScheduled || VsCodeWindow != null)
                return;

            _retryScheduled = true;
            ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                try
                {
                    for (var i = 0; i < 100 && !_disposed && VsCodeWindow == null; i++)
                    {
                        await Task.Delay(50).ConfigureAwait(false);
                        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                        if (_disposed)
                            return;

                        OnLoadCompleted(0);
                        if (VsCodeWindow != null)
                            return;
                    }

                    if (!_disposed && VsCodeWindow == null)
                    {
                        Log.Logger.Error(
                            "等待 IVsTextBuffer 初始化超时，设计器可能无法正确保存：{File}",
                            _fileName);
                    }
                }
                catch (Exception ex)
                {
                    Log.Logger.Error(ex, "重试 OnLoadCompleted 失败：{File}", _fileName);
                }
                finally
                {
                    _retryScheduled = false;
                }
            }).Task.FireAndForget();
        }

        /// <summary>
        /// 主动检查一次缓冲区状态；若仍未就绪，启动有界重试以覆盖加载通知竞态。
        /// </summary>
        internal void EnsureInitialized()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            OnLoadCompleted(0);
        }

        /// <summary>
        /// 停止延迟初始化并释放缓冲区事件订阅。
        /// </summary>
        public void Dispose()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (_disposed)
                return;

            _disposed = true;
            if (!_unadvised)
            {
                _connectionPoint.Unadvise(_cookie);
                _unadvised = true;
            }

            CodeWindowCreated = null;
        }

        private void CreateCodeWindow(IVsEditorAdaptersFactoryService eafs, ITextBuffer buffer)
        {
            var window = eafs.CreateVsCodeWindowAdapter(_oleServiceProvider);

            // Disable the splitter - which apparently causes a crash
            // amwx - not entirely sure if this is still the case, but I don't want to remove
            //        this just in case it is
            ((IVsCodeWindowEx)window).Initialize(
                (uint)_codewindowbehaviorflags.CWB_DISABLESPLITTER,
                VSUSERCONTEXTATTRIBUTEUSAGE.VSUC_Usage_Filter,
                szNameAuxUserContext: "",
                szValueAuxUserContext: "",
                InitViewFlags: 0,
                pInitView: new INITVIEW[1]);

            // Set the TextBuffer to the IVsCodeWindow
            ErrorHandler.ThrowOnFailure(window.SetBuffer(_textLines));

            // 关联 Avalonia XAML 元数据（补全等）
            buffer.Properties.GetOrCreateSingletonProperty(() => new XamlBufferMetadata());

            // Get the view that the IVsCodeWindow hosts - so that we have a WPF control we 
            // can later insert into our designer control
            var primaryView = window.GetPrimaryView(out var ppView);
            var textViewHost = eafs.GetWpfTextViewHost(ppView);

            VsCodeWindow = window;
            WpfTextView = ppView;
            WpfTextViewHost = textViewHost;
            CodeWindowCreated?.Invoke(this, EventArgs.Empty);
        }
    }
}
