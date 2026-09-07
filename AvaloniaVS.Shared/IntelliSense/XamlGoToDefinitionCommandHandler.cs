using System;
using System.ComponentModel.Composition;
using Avalonia.Ide.CompletionEngine;
using AvaloniaVS.Models;
using AvaloniaVS.Shared.IntelliSense;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Editor;
using Microsoft.VisualStudio.OLE.Interop;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.TextManager.Interop;
using Microsoft.VisualStudio.Utilities;
using IServiceProvider = System.IServiceProvider;

namespace AvaloniaVS.IntelliSense
{
    /// <summary>
    /// 处理 AXAML 中的「转到定义」(F12)：类型 / 属性 / 附加属性 / 事件 / 事件处理方法。
    /// Ctrl+点击由 <see cref="XamlNavigableSymbolSourceProvider"/> 提供。
    /// </summary>
    internal sealed class XamlGoToDefinitionCommandHandler : IOleCommandTarget
    {
        private readonly IOleCommandTarget _nextCommandHandler;
        private readonly ITextView _textView;
        private readonly XamlGoToDefinitionService _service;

        public XamlGoToDefinitionCommandHandler(
            ITextView textView,
            IVsTextView textViewAdapter,
            CompletionEngine completionEngine)
        {
            _textView = textView;
            _service = new XamlGoToDefinitionService(completionEngine);
            textViewAdapter.AddCommandFilter(this, out _nextCommandHandler);
        }

        public int QueryStatus(ref Guid pguidCmdGroup, uint cCmds, OLECMD[] prgCmds, IntPtr pCmdText)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (pguidCmdGroup == VSConstants.GUID_VSStandardCommandSet97)
            {
                for (uint i = 0; i < cCmds; i++)
                {
                    if (prgCmds[i].cmdID == (uint)VSConstants.VSStd97CmdID.GotoDefn)
                    {
                        if (_textView.TextBuffer.Properties.ContainsProperty(typeof(XamlBufferMetadata)))
                        {
                            prgCmds[i].cmdf = (uint)(OLECMDF.OLECMDF_ENABLED | OLECMDF.OLECMDF_SUPPORTED);
                            return VSConstants.S_OK;
                        }
                    }
                }
            }

            return _nextCommandHandler.QueryStatus(ref pguidCmdGroup, cCmds, prgCmds, pCmdText);
        }

        public int Exec(ref Guid pguidCmdGroup, uint nCmdID, uint nCmdexecopt, IntPtr pvaIn, IntPtr pvaOut)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (pguidCmdGroup == VSConstants.GUID_VSStandardCommandSet97
                && nCmdID == (uint)VSConstants.VSStd97CmdID.GotoDefn)
            {
                var caret = _textView.Caret.Position.BufferPosition.Position;
                if (_service.TryResolve(_textView.TextBuffer, caret, out var target))
                {
                    _service.Navigate(target);
                    return VSConstants.S_OK;
                }
            }

            return _nextCommandHandler.Exec(ref pguidCmdGroup, nCmdID, nCmdexecopt, pvaIn, pvaOut);
        }
    }

    /// <summary>
    /// 在 Avalonia AXAML 文本视图上注册转到定义命令过滤器。
    /// </summary>
    [Export(typeof(IVsTextViewCreationListener))]
    [Name("Avalonia XAML go to definition handler")]
    [ContentType("xml")]
    [TextViewRole(PredefinedTextViewRoles.Editable)]
    internal sealed class XamlGoToDefinitionHandlerProvider : IVsTextViewCreationListener
    {
        private readonly IVsEditorAdaptersFactoryService _adapterService;
        private readonly CompletionEngineSource _completionEngineSource;

        [ImportingConstructor]
        public XamlGoToDefinitionHandlerProvider(
            IVsEditorAdaptersFactoryService adapterService,
            CompletionEngineSource completionEngineSource)
        {
            _adapterService = adapterService;
            _completionEngineSource = completionEngineSource;
        }

        public void VsTextViewCreated(IVsTextView textViewAdapter)
        {
            var textView = _adapterService.GetWpfTextView(textViewAdapter);
            if (textView == null)
            {
                return;
            }

            if (!textView.TextBuffer.Properties.ContainsProperty(typeof(XamlBufferMetadata)))
            {
                return;
            }

            textView.Properties.GetOrCreateSingletonProperty(
                () => new XamlGoToDefinitionCommandHandler(
                    textView,
                    textViewAdapter,
                    _completionEngineSource.CompletionEngine));
        }
    }
}
