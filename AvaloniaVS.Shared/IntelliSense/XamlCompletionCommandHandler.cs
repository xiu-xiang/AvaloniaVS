using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Avalonia.Ide.CompletionEngine;
using EnvDTE;
using EnvDTE80;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Language.Intellisense;
using Microsoft.VisualStudio.LanguageServices;
using Microsoft.VisualStudio.OLE.Interop;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.TextManager.Interop;
using Microsoft.VisualStudio.Threading;
using IServiceProvider = System.IServiceProvider;

namespace AvaloniaVS.IntelliSense
{
    /// <summary>
    /// Handles key presses for the Avalonia XAML intellisense completion.
    /// </summary>
    /// <remarks>
    /// Adds a command handler to text views and listens for keypresses which should cause a
    /// completion to be opened or comitted.
    /// 
    /// Yes, this is horrible, but it's apparently the official way to do this. Eurgh.
    /// </remarks>
    internal class XamlCompletionCommandHandler : IOleCommandTarget
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly ICompletionBroker _completionBroker;
        private readonly IOleCommandTarget _nextCommandHandler;
        private readonly ITextView _textView;
        private readonly CompletionEngine _engine;
        private ICompletionSession _session;
        // 提交后待处理的光标偏移（属性补全插入 ="" 时需移入引号内）
        private XamlCompletion _pendingCursorCompletion;
        // 本处理器正在 Commit，避免 Committed 事件重复调整光标
        private bool _applyingCommitInHandler;

        public XamlCompletionCommandHandler(
            IServiceProvider serviceProvider,
            ICompletionBroker completionBroker,
            ITextView textView,
            IVsTextView textViewAdapter,
            CompletionEngine completionEngine)
        {
            _serviceProvider = serviceProvider;
            _completionBroker = completionBroker;
            _textView = textView;
            _engine = completionEngine;

            // Add ourselves as a command to the text view.
            textViewAdapter.AddCommandFilter(this, out _nextCommandHandler);
        }

        public int QueryStatus(ref Guid pguidCmdGroup, uint cCmds, OLECMD[] prgCmds, IntPtr pCmdText)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            return _nextCommandHandler.QueryStatus(ref pguidCmdGroup, cCmds, prgCmds, pCmdText);
        }

        public int Exec(ref Guid pguidCmdGroup, uint nCmdID, uint nCmdexecopt, IntPtr pvaIn, IntPtr pvaOut)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            // If we're in an automation function, move to the next command.
            if (VsShellUtilities.IsInAutomationFunction(_serviceProvider))
            {
                return _nextCommandHandler.Exec(ref pguidCmdGroup, nCmdID, nCmdexecopt, pvaIn, pvaOut);
            }

            if (TryGetChar(ref pguidCmdGroup, nCmdID, pvaIn, out var c))
            {
                if (HandleSessionCompletion(c))
                {
                    return VSConstants.S_OK;
                }

                if (_session == null && (c == '\'' || c == '"'))
                {
                    // If a completion session isn't active, and we type a quote, check
                    // if a quote already exists at the position & just move the cursor
                    // so we don't get a double quote
                    // If a completion session is active, that's handled there
                    var cursorPos = _textView.Caret.Position.BufferPosition;
                    var nextChar = _textView.TextSnapshot.GetText(cursorPos, 1)[0];
                    if (nextChar == c)
                    {
                        _textView.Caret.MoveTo(cursorPos + 1);
                        return VSConstants.S_OK;
                    }
                }
                var result = _nextCommandHandler.Exec(ref pguidCmdGroup, nCmdID, nCmdexecopt, pvaIn, pvaOut);

                // Tab 已交给下游（IntelliCode/Copilot 等），勿再 Filter/开新会话，否则会再次抢走后续按键
                if (c == '\t')
                {
                    return result;
                }

                // 输入 = 后若编辑器自动补了 =""，确保光标在引号内（便于继续输入/快捷键接受值建议）
                if (c == '=')
                {
                    MoveCaretInsideEmptyAttributeQuotes();
                }

                if (HandleSessionStart(c))
                {
                    return VSConstants.S_OK;
                }

                if (HandleSessionUpdate())
                {
                    return VSConstants.S_OK;
                }

                return result;
            }

            return _nextCommandHandler.Exec(ref pguidCmdGroup, nCmdID, nCmdexecopt, pvaIn, pvaOut);
        }

        private bool HandleSessionStart(char c)
        {
            // If the pressed key is a key that can start a completion session.
            if (CompletionEngine.ShouldTriggerCompletionListOn(c) || c == '\a')
            {
                if (!IsSessionAlive(_session))
                {
                    if (TriggerCompletion() && c != '<' && c != '.' && c != ' ' && c != '[' && c != '(' && c != '|' && c != '#' && c != '/')
                    {
                        SafeFilter(_session);
                    }

                    return true;
                }
            }
            else if (c == ',')
            {
                if (!IsSessionAlive(_session))
                {
                    if (TriggerCompletion())
                    {
                        SafeFilter(_session);
                    }
                    return true;
                }
            }
            return false;
        }

        private bool HandleSessionUpdate()
        {
            if (IsSessionAlive(_session))
            {
                SafeFilter(_session);
                return true;
            }
            return false;
        }

        private bool HandleSessionCompletion(char c)
        {
            var line = _textView.GetTextViewLineContainingBufferPosition(
                _textView.Caret.Position.BufferPosition);
            var start = line.Start;
            var end = Math.Min(line.End, _textView.Caret.Position.BufferPosition);

            // Adding a xmlns is special-cased here because we don't want '.' triggering
            // a completion, which can complete on the wrong value
            // So we only trigger on ' ' or '\t', and swallow that so it doesn't get 
            // inserted into the text buffer
            var session = GetActiveSession();
            if (IsSessionAlive(session))
            {
                var text = line.Snapshot.GetText(start, end - start);

                if (text.Contains("xmlns"))
                {
                    if (char.IsWhiteSpace(c))
                    {
                        SafeCommit(session);
                        return true;
                    }
                    else if (c == ':')
                    {
                        SafeDismiss(session);
                    }

                    return false;
                }
            }

            // Per UWP designer, the following keys can commit a completion session
            // in the remainder of the document - but only if a completion option
            // is selected
            // ' ' (space, or tab) 
            // '\'' (single quote)
            // '"'
            // '='
            // '>'
            // '.'

            // Also adding '#' for Selectors

            if (char.IsWhiteSpace(c)
                || c == '\'' || c == '"' || c == '=' || c == '>' || c == '.'
                || c == '#' || c == ')' || c == ']')
            {
                // Filter 后常见软选中（IsSelected=false）。Tab/Enter 仍应提交当前高亮项并吞掉按键，
                // 否则 Tab 会落入编辑器变成空格，补全却不生效（官方 IntelliCode 也是先 Tab 提交列表项）。
                PreferAvaloniaCompletionSet(session);
                var selection = TryGetSelectionStatus(session);
                var forceCommitOnTabOrEnter = c is '\t' or '\n';
                var canCommit = IsSessionAlive(session)
                    && selection?.Completion != null
                    && (forceCommitOnTabOrEnter || selection.IsSelected);

                if (canCommit)
                {
                    // 优先使用 Avalonia 补全项，避免多 CompletionSet 时拿不到 CursorOffset
                    var selected = GetSelectedXamlCompletion(session);

                    var bufferPos = _textView.Caret.Position.BufferPosition;

                    // 记录待调整项：Commit 可能走 VS 默认路径，Committed 事件中再兜底移入引号
                    _pendingCursorCompletion = selected;
                    _applyingCommitInHandler = true;
                    try
                    {
                        SafeCommit(session);
                        ApplyPostCommitCaretAdjustment(selected);
                    }
                    finally
                    {
                        _applyingCommitInHandler = false;
                        _pendingCursorCompletion = null;
                    }

                    if (selected?.DeleteTextOffset is int rof)
                    {
                        var newCursorPos = bufferPos.Add(rof);
                        SnapshotSpan deleteSpan = newCursorPos < bufferPos
                            ? new(newCursorPos, -rof)
                            : new(bufferPos, rof);
                        _textView.TextBuffer.Delete(deleteSpan);
                    }

                    // special-cased avoid TriggerCompletion
                    if (selected?.InsertionText == "xmlns:")
                    {
                        return true;
                    }

                    // Ideally, we should only parse the current line of text, where the parser State would return
                    // 'None' if you're spreading control attributes out across multiple lines
                    // BUT, Selectors can span multiple lines (aggregates separated by ',') and this theory
                    // breaks down & and there's no way to determine XML context from just the current line
                    var parser = XmlParser.Parse(_textView.TextSnapshot.GetText().AsMemory(), 0, end);
                    var state = parser.State;

                    bool skip = c != '>';
                    if (state == XmlParser.ParserState.StartElement &&
                        (c == '.' || c == ' '))
                    {
                        // Don't swallow the '.' or ' ' if this is an Xml element, like
                        // Window.Resources. However do swallow tab
                        skip = false;
                    }

                    // Tab/Enter 提交后必须吞掉，禁止再写入空白字符
                    if (c is '\t' or '\n')
                    {
                        skip = true;
                    }

                    if (state == XmlParser.ParserState.AttributeValue ||
                        state == XmlParser.ParserState.AfterAttributeValue)
                    {
                        var type = _engine.Helper.LookupType(parser.TagName);
                        if (type != null && type.Events.FirstOrDefault(x => x.Name == parser.AttributeName) != null
                            && selected?.InsertionText is { } eventMethodName)
                        {
                            GenerateEventHandlerAsync(type.FullName, parser.AttributeName, eventMethodName)
                                .FireAndForget();
                        }
                        var isSelector = parser.AttributeName?.Equals("Selector") == true;
                        if (char.IsWhiteSpace(c))
                        {
                            // For most xml attributes, swallow the space upon completion
                            // For selector, allow it to go into the buffer
                            // Also if in a markupextention
                            skip = !(isSelector && c != '\n' && c != '\t');

                            // If we're in a markup extension, only swallow the space if the
                            // completion isn't on the Markup extension
                            // i.e., where | is the cursor
                            // {DynamicResource -> {DynamicResource |
                            // but {Binding Path= -> {Binding Path=|
                            // similarly, more embedded things like RelativeSource work the same way
                            // {Binding path, RelativeSource={RelativeSource -> ...={RelativeSource |
                            if (parser.AttributeValue?.StartsWith("{") == true)
                            {
                                // If press Tab or CR in expression ignore it in completation session
                                if (c is '\t' or '\n')
                                {
                                    return true;
                                }
                                // To determine, we'll walk back the text from the cursor position
                                // until we hit either something that isn't a character
                                // If that's a {, we apply the space, otherwise we dont
                                // Only using the line text (up to cursor) since xaml can't wrap
                                // Also ignore ':' for namespaces or directives
                                var attrText = line.Snapshot.GetText(start, end - start);
                                for (int i = attrText.Length - 1; i >= 0; i--)
                                {
                                    var lineChar = attrText[i];
                                    if (char.IsLetterOrDigit(lineChar) || lineChar == ':')
                                        continue;

                                    // any other character than [A-z,0-9,:] is a different part
                                    skip = lineChar != '{';
                                    break;
                                }

                                // if in a markup extension, if we skip the entered char, we won't get
                                // to start a new completion session, so force start it
                                // The check for '=' in the insertion text ensures we don't always get this
                                // e.g., {OnPlatform Wind -> {OnPlatform Windows= [New completion session]
                                // but {OnPlatform Windows=Re -> {OnPlatform Windows=Red [no new session]
                                if (skip && selected?.InsertionText?.EndsWith("=") == true)
                                    TriggerCompletion();
                            }
                        }
                        else if (c == '\'' || c == '"')
                        {
                            // If we're accepting a completion using the quotes, and there's already one
                            // in the buffer after the completion, don't insert another quote, swallow
                            // it and just move the cursor
                            var cursorPos = _textView.Caret.Position.BufferPosition;
                            var nextChar = _textView.TextSnapshot.GetText(cursorPos, 1)[0];
                            if (nextChar == c)
                            {
                                skip = true;
                                _textView.Caret.MoveTo(cursorPos + 1);
                            }
                        }
                        else
                        {
                            skip = false;
                        }

                        var insertion = selected?.InsertionText;
                        var lastInsertionChar = (insertion?.Length ?? 0) > 0
                            ? insertion[insertion.Length - 1]
                            : default;

                        // Cases like {Binding Path= result in {Binding Path==
                        // as the completion includes the '=', if the entered char
                        // is the same as the last char here, swallow the entered char
                        if (!skip && lastInsertionChar == c)
                        {
                            skip = true;

                            // Specifically for markup extensions, make sure '=' triggers
                            // a new completion session when entered, but only if we're
                            // skipping the char entered
                            if (c == '=')
                                TriggerCompletion();
                        }
                        else if (isSelector && lastInsertionChar is '=' or '.')
                        {
                            // Trigger Selector property Value Completation
                            if (c is not '=' or '.')
                                TriggerCompletion();
                        }
                    }
                    else if (selected?.TriggerCompletion == true)
                    {
                        TriggerCompletion();
                    }
                    else if (state == XmlParser.ParserState.AttributeValue)
                    {
                        // 仅枚举/Hints/Classes 等需要 Avalonia 值补全时开会话。
                        // RowDefinitions 等自由文本不开会话，避免抢走 Copilot 的 Tab。
                        if (ShouldAutoCompleteAttributeValue(parser))
                        {
                            TriggerCompletion();
                        }
                        else
                        {
                            SafeDismissAllCompletionSessions();
                        }
                    }
                    else if (state != XmlParser.ParserState.StartElement)
                    {
                        TriggerCompletion();
                    }

                    return skip;
                }
                else
                {
                    // Tab 且 Avalonia 下拉无高亮项时：属性值内再尝试 Copilot 幽灵文本；
                    // 有下拉项（如 Foreground="re" → Red）时已在上方 Commit，不会进入此处。
                    if (c == '\t')
                    {
                        if (IsCaretInAttributeValue())
                        {
                            SafeDismissAllCompletionSessions();
                            if (TryAcceptInlineSuggestion())
                            {
                                return true;
                            }
                        }
                        else
                        {
                            SafeDismissAllCompletionSessions();
                        }

                        return false;
                    }

                    if (c != '\n')
                    {
                        SafeDismiss(session);
                    }
                    return false;
                }
            }
            else if (c == ':' && IsSessionAlive(session))
            {
                var parser = XmlParser.Parse(_textView.TextSnapshot.GetText().AsMemory(), 0, end);
                var state = parser.State;

                if (state == XmlParser.ParserState.AttributeValue &&
                    parser.AttributeName?.Equals("Selector") == true)
                {
                    // Force new session to start to suggest pseudoclasses
                    SafeDismiss(session);
                    return false;
                }
            }
            else if (c == '(' && IsSessionAlive(session))
            {
                var parser = XmlParser.Parse(_textView.TextSnapshot.GetText().AsMemory(), 0, end);
                var state = parser.State;
                if ((state == XmlParser.ParserState.AttributeValue || state == XmlParser.ParserState.AfterAttributeValue)
                    && parser.AttributeName?.Equals("Selector") == true)
                {
                    SafeDismiss(session);
                    return false;
                }
            }
            else if (c == '{' && IsSessionAlive(session))
            {
                var parser = XmlParser.Parse(_textView.TextSnapshot.GetText().AsMemory(), 0, end);
                var state = parser.State;

                if (state == XmlParser.ParserState.AttributeValue)
                {
                    // For something like Brushes, restart the completion session if we want
                    // a markup extension
                    SafeDismiss(session);
                    return false;
                }
            }
            else if (c == ',' && IsSessionAlive(session))
            {
                // Typing the comma in a markup extension should trigger a new completion session
                var text = line.Snapshot.GetText(start, end - start);
                for (int i = text.Length - 1; i >= 0; i--)
                {
                    if (text[i] == '{')
                    {
                        SafeDismiss(session);
                        return false;
                    }
                }
            }

            return false;
        }

        private bool TriggerCompletion()
        {
            // The caret must be in a non-projection location.
            var caretPoint = _textView.Caret.Position.Point.GetPoint(
                x => (!x.ContentType.IsOfType("projection")),
                PositionAffinity.Predecessor);

            if (!caretPoint.HasValue)
            {
                return false;
            }

            // When adding an xmlns definition, we were getting 2 intellisense popups because (I think)
            // the VS XML intellisense handler was popping one up and then we are creating our own session
            // here. It turns out one of the completionsets though is an Avalonia one, so if a session already
            // exists and one of the CompletionSets is from Avalonia, use that session instead of creating
            // a new one - and we won't get the double popup
            ICompletionSession existingSession = null;
            var sessions = _completionBroker.GetSessions(_textView);
            if (sessions.Count > 0)
            {
                for (int i = sessions.Count - 1; i >= 0; i--)
                {
                    var candidate = sessions[i];
                    if (!IsSessionAlive(candidate))
                    {
                        continue;
                    }

                    // 先读取 CompletionSets；空会话再 Dismiss。Dismiss 后绝不能再访问该对象。
                    IList<Microsoft.VisualStudio.Language.Intellisense.CompletionSet> sets;
                    try
                    {
                        sets = candidate.CompletionSets;
                    }
                    catch (ObjectDisposedException)
                    {
                        continue;
                    }

                    if (sets == null || sets.Count == 0)
                    {
                        SafeDismiss(candidate);
                        continue;
                    }

                    for (int j = sets.Count - 1; j >= 0; j--)
                    {
                        if (sets[j].Moniker.Equals("Avalonia"))
                        {
                            existingSession = candidate;
                            break;
                        }
                    }

                    if (existingSession != null)
                        break;
                }
            }

            var session = existingSession ?? _completionBroker.CreateCompletionSession(
                _textView,
                caretPoint?.Snapshot.CreateTrackingPoint(caretPoint.Value.Position, PointTrackingMode.Positive),
                true);

            // Subscribe to the Dismissed / Committed events on the session.
            session.Dismissed -= SessionDismissed;
            session.Dismissed += SessionDismissed;
            session.Committed -= SessionCommitted;
            session.Committed += SessionCommitted;
            _session = session;
            if (existingSession == null)
            {
                session.Start();
            }

            // 无补全项的空会话会挡住 Copilot Tab；Dismiss 前先判断，Dismiss 后不再读会话
            if (!SessionHasCompletions(session))
            {
                SafeDismiss(session);
                if (ReferenceEquals(_session, session))
                {
                    _session = null;
                }
                return false;
            }

            return true;
        }

        private void SessionDismissed(object sender, EventArgs e)
        {
            if (sender is ICompletionSession session)
            {
                try
                {
                    session.Dismissed -= SessionDismissed;
                    session.Committed -= SessionCommitted;
                }
                catch (ObjectDisposedException)
                {
                    // 会话已释放，忽略退订失败
                }
            }
            if (ReferenceEquals(_session, sender))
            {
                _session = null;
            }
            _pendingCursorCompletion = null;
        }

        private void SessionCommitted(object sender, EventArgs e)
        {
            if (_applyingCommitInHandler)
            {
                return;
            }

            // Commit 后会话可能已 Dispose，勿再读 SelectedCompletionSet
            var selected = _pendingCursorCompletion;
            if (selected == null && IsSessionAlive(sender as ICompletionSession))
            {
                selected = GetSelectedXamlCompletion(sender as ICompletionSession);
            }

            ApplyPostCommitCaretAdjustment(selected);
            _pendingCursorCompletion = null;
        }

        /// <summary>
        /// 取得当前 Avalonia 补全会话（优先 _session，否则从 Broker 查找）。
        /// </summary>
        private ICompletionSession GetActiveSession()
        {
            if (IsSessionAlive(_session))
            {
                return _session;
            }

            _session = null;
            var found = FindAvaloniaSession();
            if (found != null)
            {
                found.Dismissed -= SessionDismissed;
                found.Dismissed += SessionDismissed;
                found.Committed -= SessionCommitted;
                found.Committed += SessionCommitted;
                _session = found;
            }

            return found;
        }

        /// <summary>
        /// 从 Broker 中查找含 Avalonia CompletionSet 的会话。
        /// </summary>
        private ICompletionSession FindAvaloniaSession()
        {
            var sessions = _completionBroker.GetSessions(_textView);
            for (int i = sessions.Count - 1; i >= 0; i--)
            {
                var candidate = sessions[i];
                if (!IsSessionAlive(candidate))
                {
                    continue;
                }

                try
                {
                    var sets = candidate.CompletionSets;
                    if (sets == null)
                    {
                        continue;
                    }

                    for (int j = sets.Count - 1; j >= 0; j--)
                    {
                        if (sets[j].Moniker.Equals("Avalonia"))
                        {
                            return candidate;
                        }
                    }
                }
                catch (ObjectDisposedException)
                {
                    // 跳过已释放会话
                }
            }

            return null;
        }

        /// <summary>
        /// 将 SelectedCompletionSet 切到 Avalonia，避免选中其它集导致无法提交。
        /// </summary>
        private static void PreferAvaloniaCompletionSet(ICompletionSession session)
        {
            if (!IsSessionAlive(session))
            {
                return;
            }

            try
            {
                var avaloniaSet = session.CompletionSets?.FirstOrDefault(s => s.Moniker.Equals("Avalonia"));
                if (avaloniaSet != null)
                {
                    session.SelectedCompletionSet = avaloniaSet;
                }
            }
            catch (ObjectDisposedException)
            {
                // 忽略
            }
        }

        /// <summary>
        /// 优先取 Avalonia CompletionSet 中的当前选中项。
        /// </summary>
        private static XamlCompletion GetSelectedXamlCompletion(ICompletionSession session)
        {
            if (!IsSessionAlive(session))
            {
                return null;
            }

            PreferAvaloniaCompletionSet(session);

            try
            {
                return session.SelectedCompletionSet?.SelectionStatus?.Completion as XamlCompletion;
            }
            catch (ObjectDisposedException)
            {
                return null;
            }
        }

        /// <summary>
        /// 提交补全后调整光标：按 CursorOffset，或兜底移入空属性引号内。
        /// </summary>
        private void ApplyPostCommitCaretAdjustment(XamlCompletion selected)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (selected?.CursorOffset > 0)
            {
                var cursorPos = _textView.Caret.Position.BufferPosition;
                var newCursorPos = cursorPos - selected.CursorOffset;
                if (newCursorPos.Position >= 0)
                {
                    _textView.Caret.MoveTo(newCursorPos);
                    return;
                }
            }

            // 兜底：插入 Name="" 后光标停在引号外时，移到两个引号之间，便于继续输入/Tab 接受值建议
            MoveCaretInsideEmptyAttributeQuotes();
        }

        /// <summary>
        /// 若光标紧跟在 ="" 之后，则移入引号内。
        /// </summary>
        private void MoveCaretInsideEmptyAttributeQuotes()
        {
            var pos = _textView.Caret.Position.BufferPosition;
            if (pos.Position < 3)
            {
                return;
            }

            var snapshot = pos.Snapshot;
            // 形如 ...=""|
            if (snapshot[pos.Position - 1] == '"'
                && snapshot[pos.Position - 2] == '"'
                && snapshot[pos.Position - 3] == '=')
            {
                _textView.Caret.MoveTo(pos - 1);
            }
        }

        /// <summary>
        /// 安全关闭当前文本视图上所有补全会话（Dismiss 后对象已 Dispose，不可再访问）。
        /// </summary>
        private void SafeDismissAllCompletionSessions()
        {
            ICompletionSession[] sessions;
            try
            {
                sessions = _completionBroker.GetSessions(_textView).ToArray();
            }
            catch
            {
                _session = null;
                _pendingCursorCompletion = null;
                return;
            }

            foreach (var session in sessions)
            {
                SafeDismiss(session);
            }

            _session = null;
            _pendingCursorCompletion = null;
        }

        /// <summary>
        /// 会话是否仍可用（未 Dismiss/Dispose）。
        /// </summary>
        private static bool IsSessionAlive(ICompletionSession session)
        {
            if (session == null)
            {
                return false;
            }

            try
            {
                return !session.IsDismissed;
            }
            catch (ObjectDisposedException)
            {
                return false;
            }
        }

        private static bool SessionHasCompletions(ICompletionSession session)
        {
            if (!IsSessionAlive(session))
            {
                return false;
            }

            try
            {
                return session.CompletionSets != null
                    && session.CompletionSets.Any(s => s.Completions != null && s.Completions.Count > 0);
            }
            catch (ObjectDisposedException)
            {
                return false;
            }
        }

        private static CompletionSelectionStatus TryGetSelectionStatus(ICompletionSession session)
        {
            if (!IsSessionAlive(session))
            {
                return null;
            }

            try
            {
                return session.SelectedCompletionSet?.SelectionStatus;
            }
            catch (ObjectDisposedException)
            {
                return null;
            }
        }

        private static void SafeDismiss(ICompletionSession session)
        {
            if (session == null)
            {
                return;
            }

            try
            {
                if (!session.IsDismissed)
                {
                    session.Dismiss();
                }
            }
            catch (ObjectDisposedException)
            {
                // 已释放
            }
        }

        private static void SafeCommit(ICompletionSession session)
        {
            if (!IsSessionAlive(session))
            {
                return;
            }

            try
            {
                session.Commit();
            }
            catch (ObjectDisposedException)
            {
                // 已释放
            }
        }

        private static void SafeFilter(ICompletionSession session)
        {
            if (!IsSessionAlive(session))
            {
                return;
            }

            try
            {
                session.Filter();
            }
            catch (ObjectDisposedException)
            {
                // 已释放
            }
        }

        /// <summary>
        /// 光标是否在属性值内（RowDefinitions="|" 等）。
        /// </summary>
        private bool IsCaretInAttributeValue()
        {
            try
            {
                var pos = _textView.Caret.Position.BufferPosition;
                var parser = XmlParser.Parse(
                    _textView.TextSnapshot.GetText().AsMemory(),
                    0,
                    pos.Position);
                return parser.State == XmlParser.ParserState.AttributeValue;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 该属性值是否应由 Avalonia 自动弹出补全（枚举/Hints 等）。
        /// RowDefinitions 等自由文本返回 false，交给 Copilot。
        /// </summary>
        private bool ShouldAutoCompleteAttributeValue(XmlParser parser)
        {
            if (parser?.AttributeName == null || parser.TagName == null)
            {
                return false;
            }

            if (parser.AttributeName.Equals("Classes", StringComparison.Ordinal)
                || parser.AttributeName.Equals("Selector", StringComparison.Ordinal)
                || parser.AttributeName.Equals("xmlns", StringComparison.Ordinal)
                || parser.AttributeName.Contains("xmlns:"))
            {
                return true;
            }

            MetadataProperty prop = null;
            if (parser.AttributeName.Contains('.'))
            {
                var split = parser.AttributeName.Split('.');
                if (split.Length == 2)
                {
                    prop = _engine.Helper.LookupProperty(split[0], split[1]);
                }
            }
            else
            {
                prop = _engine.Helper.LookupProperty(parser.TagName, parser.AttributeName);
            }

            if (prop?.Type?.HasHintValues == true)
            {
                return true;
            }

            if (prop?.Type?.Name == typeof(Type).FullName)
            {
                return true;
            }

            var type = _engine.Helper.LookupType(parser.TagName);
            if (type?.Events?.Any(e => e.Name == parser.AttributeName) == true)
            {
                return true;
            }

            return false;
        }

        /// <summary>
        /// 尝试接受 Copilot/IntelliCode 内联幽灵文本（Edit.AcceptSuggestion）。
        /// </summary>
        private bool TryAcceptInlineSuggestion()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            try
            {
                var beforeVersion = _textView.TextSnapshot.Version.VersionNumber;
                var beforePos = _textView.Caret.Position.BufferPosition.Position;

                var dte = Package.GetGlobalService(typeof(DTE)) as DTE2;
                if (dte == null)
                {
                    return false;
                }

                dte.ExecuteCommand("Edit.AcceptSuggestion");

                var afterVersion = _textView.TextSnapshot.Version.VersionNumber;
                var afterPos = _textView.Caret.Position.BufferPosition.Position;
                // 文本或光标变化说明建议已被接受
                return afterVersion != beforeVersion || afterPos != beforePos;
            }
            catch (Exception)
            {
                // 命令不可用、无内联建议，或内部触及已释放会话
                return false;
            }
        }

        private IEnumerable<ParameterSyntax> GetParametersList(string[] parameterTypes, string[] parameterNames)
        {
            for (int i = 0; i < parameterTypes.Length; i++)
            {
                yield return SyntaxFactory.Parameter(SyntaxFactory.List<AttributeListSyntax>(),
                                                     SyntaxFactory.TokenList(),
                                                     SyntaxFactory.ParseTypeName(parameterTypes[i]),
                                                     SyntaxFactory.Identifier(parameterNames[i]),
                                                     null);
            }
        }

        private async System.Threading.Tasks.Task GenerateEventHandlerAsync(string controlType, string eventName, string generatedMethodName)
        {
            var currentScheduler = TaskScheduler.FromCurrentSynchronizationContext();
            try
            {
                var componentModel = (IComponentModel)Package.GetGlobalService(typeof(SComponentModel));
                var dte = Package.GetGlobalService(typeof(DTE)) as DTE2;
                var workspace = componentModel.GetService<VisualStudioWorkspace>();

                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                var activeDocument = dte.ActiveDocument;
                var activeDocumentName = $"{activeDocument.Name}.cs";
                if (activeDocument.ProjectItem?.ContainingProject?.UniqueName is { } uniqueName)
                {
                    var currentDocumentCodeBehind = workspace.CurrentSolution.Projects
                         .FirstOrDefault(x => x.FilePath?.EndsWith(uniqueName) == true)
                         .Documents
                         .FirstOrDefault(x => string.Equals(x.Name, activeDocumentName, StringComparison.OrdinalIgnoreCase));

                    if (currentDocumentCodeBehind is null)
                    {
                        return;
                    }

                    var compilation = await currentDocumentCodeBehind.Project.GetCompilationAsync();
                    var root = await currentDocumentCodeBehind.GetSyntaxRootAsync();
                    var codeBehindClass = root.DescendantNodes()
                        .FirstOrDefault(x => x.IsKind(SyntaxKind.ClassDeclaration)) as ClassDeclarationSyntax;

                    await TaskScheduler.Default;

                    var currentEvent = GetAllEvents(compilation.References.Select(compilation.GetAssemblyOrModuleSymbol)
                        .OfType<IAssemblySymbol>().Select(a => a.GetTypeByMetadataName(controlType))
                        .FirstOrDefault(x => x != null))
                        .FirstOrDefault(x => x.Name == eventName) as IEventSymbol;
                    var parameters = (currentEvent.Type as INamedTypeSymbol).DelegateInvokeMethod.Parameters;
                    string[] parameterNames = new string[parameters.Length];
                    string[] parameterTypes = new string[parameters.Length];
                    for (int i = 0; i < parameters.Length; i++)
                    {
                        parameterNames[i] = parameters[i].MetadataName;
                        parameterTypes[i] = parameters[i].Type.ToString();
                    }
                    var methodToInsert = GetMethodDeclarationSyntax("void", generatedMethodName, parameterTypes, parameterNames);
                    var duplicatingMethodIds = new List<int>();
                    foreach (MethodDeclarationSyntax item in codeBehindClass.DescendantNodes().Where(x => x.IsKind(SyntaxKind.MethodDeclaration)))
                    {
                        if (item.ReturnType is PredefinedTypeSyntax predefinedTypeSyntax &&
                            predefinedTypeSyntax.Keyword.IsKind(SyntaxKind.VoidKeyword))
                        {
                            var itemParameters = item.ParameterList.Parameters.Select(x => x.Type.ToString()).ToArray();
                            var methodToInsertParameters = methodToInsert.ParameterList.Parameters.Select(x => x.Type.ToString()).ToArray();
                            if (itemParameters.Length == methodToInsertParameters.Length)
                            {
                                var sameMethods = true;
                                for (int i = 0; i < itemParameters.Length; i++)
                                {
                                    if (itemParameters[i] != methodToInsertParameters[i])
                                    {
                                        sameMethods = false;
                                        break;
                                    }
                                }

                                if (sameMethods)
                                {
                                    var methodNameParts = item.Identifier.Text.Split('_');
                                    if (methodNameParts.Length == 3 && int.TryParse(methodNameParts.Last(), out var methodId))
                                    {
                                        duplicatingMethodIds.Add(methodId);
                                    }
                                    else
                                    {
                                        duplicatingMethodIds.Add(0);
                                    }
                                }
                            }
                        }
                    }

                    if (duplicatingMethodIds.Count > 0)
                    {
                        methodToInsert = methodToInsert.WithIdentifier(SyntaxFactory.Identifier(generatedMethodName + $"_{duplicatingMethodIds.Max() + 1}"));
                    }
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    var newMethodDeclaration = codeBehindClass.AddMembers(methodToInsert);
                    var newRoot = root.ReplaceNode(codeBehindClass, newMethodDeclaration);
                    newRoot = Formatter.Format(newRoot, Formatter.Annotation, workspace);
                    workspace.TryApplyChanges(currentDocumentCodeBehind.WithSyntaxRoot(newRoot).Project.Solution);

                    // Hack to add method id to xaml file because i can't find a way to generate it from completions
                    // Apply these changes after adding method because otherwise workspace will fail to add method
                    if (duplicatingMethodIds.Count > 0)
                    {
                        var textDocument = dte.ActiveDocument.Object() as EnvDTE.TextDocument;
                        var editPoint = textDocument.CreateEditPoint();
                        editPoint.MoveToAbsoluteOffset(textDocument.Selection.ActivePoint.AbsoluteCharOffset);
                        editPoint.Insert($"_{duplicatingMethodIds.Max() + 1}");
                    }
                }
            }
            finally
            {
                if (currentScheduler is not null && currentScheduler.Id != TaskScheduler.FromCurrentSynchronizationContext()?.Id)
                {
                    await currentScheduler;
                }
            }
        }

        private MethodDeclarationSyntax GetMethodDeclarationSyntax(string returnTypeName, string methodName, string[] parameterTypes, string[] paramterNames)
        {
            var parameterList = SyntaxFactory.ParameterList(SyntaxFactory.SeparatedList(GetParametersList(parameterTypes, paramterNames)));
            return SyntaxFactory.MethodDeclaration(SyntaxFactory.List<AttributeListSyntax>(),
                                                   SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.PrivateKeyword)),
                                                   SyntaxFactory.ParseTypeName(returnTypeName),
                                                   null,
                                                   SyntaxFactory.Identifier(methodName),
                                                   null,
                                                   parameterList,
                                                   SyntaxFactory.List<TypeParameterConstraintClauseSyntax>(),
                                                   SyntaxFactory.Block(),
                                                   SyntaxFactory.Token(SyntaxKind.None)).WithAdditionalAnnotations(Formatter.Annotation);
        }

        private static IEnumerable<ISymbol> GetAllEvents(INamedTypeSymbol t)
        {
            foreach (var p in t.GetMembers().Where(x => x.Kind == SymbolKind.Event))
                yield return p;
            if (t.BaseType != null)
                foreach (var p in GetAllEvents(t.BaseType))
                    yield return p;
        }

        private static bool TryGetChar(ref Guid pguidCmdGroup, uint nCmdID, IntPtr pvaIn, out char c)
        {
            c = '\0';

            if (pguidCmdGroup == VSConstants.VSStd2K)
            {
                switch ((VSConstants.VSStd2KCmdID)nCmdID)
                {
                    case VSConstants.VSStd2KCmdID.TYPECHAR:
                        c = (char)(ushort)Marshal.GetObjectForNativeVariant(pvaIn);
                        break;
                    case VSConstants.VSStd2KCmdID.RETURN:
                        c = '\n';
                        break;
                    case VSConstants.VSStd2KCmdID.TAB:
                        c = '\t';
                        break;
                    case VSConstants.VSStd2KCmdID.BACKSPACE:
                    case VSConstants.VSStd2KCmdID.DELETE:
                        c = '\b';
                        break;
                    // Translate Ctrl+Space into a '\a'.
                    case VSConstants.VSStd2KCmdID.COMPLETEWORD:
                        c = ' ';
                        break;
                }
            }

            return c != '\0';
        }
    }
}
