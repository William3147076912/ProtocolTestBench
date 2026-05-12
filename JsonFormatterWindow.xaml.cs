using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace ProtocolTestBench
{
    public partial class JsonFormatterWindow : Window
    {
        // 一键美化时使用的 System.Text.Json 配置。
        // WriteIndented=true 会把紧凑 JSON 输出为带缩进和换行的可读格式。
        private static readonly JsonSerializerOptions PrettyJsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true
        };

        // RichTextBox 本身没有内置 JSON 高亮能力，所以这里定义几种固定颜色。
        // 后面会把 JSON 文本切分成多段 Run，不同类型的片段使用不同 Brush。
        private static readonly Brush DefaultJsonBrush = new SolidColorBrush(Color.FromRgb(221, 231, 243));

        private static readonly Brush KeyJsonBrush = new SolidColorBrush(Color.FromRgb(199, 69, 46));
        private static readonly Brush StringJsonBrush = new SolidColorBrush(Color.FromRgb(166, 190, 66));
        private static readonly Brush NumberJsonBrush = new SolidColorBrush(Color.FromRgb(236, 170, 15));
        // private static readonly Brush BracketJsonBrush = new SolidColorBrush(Color.FromRgb(251, 146, 60));

        // 程序主动替换 RichTextBox.Document 时也会触发 TextChanged。
        // 这个标记用来区分“用户输入触发”和“代码刷新高亮触发”，避免循环刷新。
        private bool _isUpdatingEditor;

        // WPF 在 TextChanged 事件内部还处于 BeginChange/EndChange 文本变更块中，
        // 此时不能直接重新设置 RichTextBox.Document，否则会抛 InvalidOperationException。
        // 这两个字段用于把高亮刷新合并并延迟到 Dispatcher 队列里执行。
        private bool _isHighlightRefreshQueued;
        private int _pendingHighlightCaretOffset;

        public JsonFormatterWindow()
        {
            InitializeComponent();
            ResetEditorDocument();
            ValidateJson();
        }

        // 用户每次输入都实时校验 JSON，并排队刷新语法颜色。
        // 注意：这里不能直接设置 Document，只能排队；原因见 QueueHighlightJson 的注释。
        private void JsonEditorTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_isUpdatingEditor)
            {
                return;
            }

            int caretOffset = GetEditorCaretOffset();
            ValidateJson();
            QueueHighlightJson(caretOffset);
        }

        // 输入左括号时自动补齐右括号，并把光标放在左右括号中间。
        // 示例：输入 { 后得到 {}，光标停在两者中间，方便继续输入内容。
        private void JsonEditorTextBox_PreviewTextInput(object sender, TextCompositionEventArgs e)
        {
            if (e.Text == "[")
            {
                InsertJsonPair('[', ']');
                e.Handled = true;
                return;
            }

            if (e.Text == "{")
            {
                InsertJsonPair('{', '}');
                e.Handled = true;
            }

            if (e.Text == "\"")
            {
                InsertJsonPair('\"', '\"');
                e.Handled = true;
            }
        }

        // 这里处理键盘层面的增强编辑行为：
        // 1. 删除空字符串 "" 中任意一个引号时，直接成对删除 ""。
        // 2. 接管回车输入，直接向纯文本插入换行，避免 RichTextBox 默认段落换行和高亮重建互相影响。
        // 3. 只有光标紧贴在 {} 或 [] 中间时，回车才会自动展开为多行结构。
        //    例如 {|} 按回车会变成：
        //    {
        //
        //    }
        //    如果光标左右不是紧贴的一对括号，则只插入普通换行。
        private void JsonEditorTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (HandleQuotePairDelete(e.Key))
            {
                e.Handled = true;
                return;
            }

            if (!IsEnterKey(e.Key))
            {
                return;
            }

            HandleEnterKey();
            e.Handled = true;
        }

        // 统一处理回车。
        // 不依赖 RichTextBox 默认换行，是为了保证后续语法高亮重建 Document 后换行仍然稳定存在。
        private void HandleEnterKey()
        {
            string text = GetEditorText();
            int selectionStart = GetSelectionStartOffset();
            int selectionEnd = GetSelectionEndOffset();
            string currentIndent = GetCurrentLineIndent(text, selectionStart);

            char openingBracket;
            char closingBracket;
            if (selectionStart == selectionEnd && TryGetAdjacentJsonPair(out openingBracket, out closingBracket))
            {
                string innerIndent = string.Format("{0}    ", currentIndent);
                string expandedPairText = string.Format("{0}{1}{0}{2}", Environment.NewLine, innerIndent, currentIndent);
                ReplaceSelectionText(text, selectionStart, selectionEnd, expandedPairText,
                    selectionStart + Environment.NewLine.Length + innerIndent.Length);
                return;
            }

            string normalLineBreakText = string.Format("{0}{1}", Environment.NewLine, currentIndent);
            ReplaceSelectionText(text, selectionStart, selectionEnd, normalLineBreakText,
                selectionStart + normalLineBreakText.Length);
        }

        // 按纯文本 offset 替换当前选区或光标位置的文本。
        // RichTextBox.Selection.Text 在复杂 FlowDocument 中对换行处理不够稳定，所以这里统一走字符串替换。
        private void ReplaceSelectionText(string text, int selectionStart, int selectionEnd, string replacement,
            int newCaretOffset)
        {
            string updatedText = text.Remove(selectionStart, selectionEnd - selectionStart)
                .Insert(selectionStart, replacement);
            SetEditorText(updatedText, newCaretOffset);
        }

        // 一键美化：先解析验证，再把 JSON 重新序列化为缩进格式。
        private void FormatJsonButton_Click(object sender, RoutedEventArgs e)
        {
            string json = GetEditorText();
            JsonDocument document;
            string errorMessage;
            if (!TryParseJson(json, out document, out errorMessage))
            {
                SetInvalidState(errorMessage);
                return;
            }

            JsonDocument parsedDocument = document;
            using (parsedDocument)
            {
                string formattedJson = JsonSerializer.Serialize(parsedDocument.RootElement, PrettyJsonOptions);
                SetEditorText(formattedJson, formattedJson.Length);
            }

            SetValidState();
            JsonEditorTextBox.Focus();
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        // 在当前选择位置插入一对括号。
        // 如果用户选中了文本，会把选中内容包在括号中间；没有选中文本则只插入空括号对。
        private void InsertJsonPair(char openingBracket, char closingBracket)
        {
            int selectionStart = GetSelectionStartOffset();
            string selectedText = JsonEditorTextBox.Selection.Text;
            string replacement = string.Format("{0}{1}{2}", openingBracket, selectedText, closingBracket);

            JsonEditorTextBox.Selection.Text = replacement;
            SetEditorCaretOffset(selectionStart + 1 + selectedText.Length);
            QueueHighlightJson(GetEditorCaretOffset());
            ValidateJson();
        }

        // 处理空字符串 "" 的成对删除。
        // Backspace 场景：光标在两个引号中间，按退格删除左侧引号时，同时删除右侧引号。
        // Delete 场景：光标在第一个引号前面，按 Delete 删除右侧引号时，同时删除第二个引号。
        private bool HandleQuotePairDelete(Key key)
        {
            if (!JsonEditorTextBox.Selection.IsEmpty)
            {
                return false;
            }

            string text = GetEditorText();
            int caretOffset = GetEditorCaretOffset();

            if (key == Key.Back && caretOffset > 0 && caretOffset < text.Length &&
                text[caretOffset - 1] == '"' && text[caretOffset] == '"')
            {
                SetEditorText(text.Remove(caretOffset - 1, 2), caretOffset - 1);
                return true;
            }

            if (key == Key.Delete && caretOffset >= 0 && caretOffset + 1 < text.Length &&
                text[caretOffset] == '"' && text[caretOffset + 1] == '"')
            {
                SetEditorText(text.Remove(caretOffset, 2), caretOffset);
                return true;
            }

            return false;
        }

        // 判断是否为普通回车键。
        // WPF 中不同键盘区域可能上报 Enter 或 Return，这里统一处理。
        private static bool IsEnterKey(Key key)
        {
            return key == Key.Enter || key == Key.Return;
        }

        // 判断光标是否“紧贴”在一对 JSON 括号中间。
        // 这里故意不跳过空格和换行：只有左边紧邻 { 且右边紧邻 }，
        // 或左边紧邻 [ 且右边紧邻 ] 时才返回 true。
        private bool TryGetAdjacentJsonPair(out char openingBracket, out char closingBracket)
        {
            openingBracket = '\0';
            closingBracket = '\0';

            int caretOffset = GetEditorCaretOffset();
            string text = GetEditorText();

            if (caretOffset <= 0 || caretOffset >= text.Length)
            {
                return false;
            }

            openingBracket = text[caretOffset - 1];
            closingBracket = text[caretOffset];

            return (openingBracket == '{' && closingBracket == '}') ||
                   (openingBracket == '[' && closingBracket == ']');
        }

        // 取当前行开头已有的缩进。
        // 自动展开 {} 或 [] 时，内部行会在当前缩进基础上再加 4 个空格。
        private static string GetCurrentLineIndent(string text, int caretOffset)
        {
            string textBeforeCaret = text.Substring(0, caretOffset);
            int lineStart = Math.Max(textBeforeCaret.LastIndexOf('\n') + 1, 0);
            string currentLinePrefix = textBeforeCaret.Substring(lineStart);

            return new string(currentLinePrefix.TakeWhile(char.IsWhiteSpace).ToArray());
        }

        // 实时校验编辑器中的 JSON，并同步更新顶部状态和底部提示。
        // 空内容不算错误，只禁用“一键美化”按钮，避免误点。
        private void ValidateJson()
        {
            string json = GetEditorText();
            if (string.IsNullOrWhiteSpace(json))
            {
                FormatJsonButton.IsEnabled = false;
                JsonStatusTextBlock.Text = "请输入 JSON";
                JsonStatusTextBlock.Foreground = Brushes.Gray;
                JsonDetailTextBlock.Text = "支持对象或数组 JSON。";
                return;
            }

            JsonDocument document;
            string errorMessage;
            if (TryParseJson(json, out document, out errorMessage))
            {
                document.Dispose();
                SetValidState();
            }
            else
            {
                SetInvalidState(errorMessage);
            }
        }

        // JSON 可解析时的 UI 状态。
        private void SetValidState()
        {
            FormatJsonButton.IsEnabled = true;
            JsonStatusTextBlock.Text = "JSON 正确";
            JsonStatusTextBlock.Foreground = new SolidColorBrush(Color.FromRgb(4, 120, 87));
            JsonDetailTextBlock.Text = "当前 JSON 可解析，可以继续编辑或点击一键美化。";
        }

        // JSON 不可解析时的 UI 状态。
        // errorMessage 中包含 System.Text.Json 返回的错误和大致行列位置。
        private void SetInvalidState(string errorMessage)
        {
            FormatJsonButton.IsEnabled = false;
            JsonStatusTextBlock.Text = "JSON 有问题";
            JsonStatusTextBlock.Foreground = new SolidColorBrush(Color.FromRgb(185, 28, 28));
            JsonDetailTextBlock.Text = errorMessage;
        }

        // 立即刷新高亮。调用前必须确认当前不在 RichTextBox 的内部文本变更块里。
        // 用户输入触发的刷新请走 QueueHighlightJson，不要直接调用这个方法。
        private void HighlightJson(int caretOffset)
        {
            SetEditorText(GetEditorText(), caretOffset);
        }

        // 延迟刷新高亮。
        // RichTextBox 在 TextChanged 事件中仍然处于内部变更事务里，直接替换 Document 会崩溃。
        // BeginInvoke 会等本次输入处理结束后再执行；连续快速输入时只保留最后一次光标位置。
        private void QueueHighlightJson(int caretOffset)
        {
            _pendingHighlightCaretOffset = caretOffset;

            if (_isHighlightRefreshQueued)
            {
                return;
            }

            _isHighlightRefreshQueued = true;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                _isHighlightRefreshQueued = false;

                if (_isUpdatingEditor)
                {
                    return;
                }

                HighlightJson(_pendingHighlightCaretOffset);
            }), DispatcherPriority.Background);
        }

        // 用高亮后的 FlowDocument 替换编辑器内容，并尽量恢复原光标位置。
        // 所有程序主动替换 Document 的地方都集中到这里，方便统一维护 _isUpdatingEditor。
        private void SetEditorText(string text, int caretOffset)
        {
            try
            {
                _isUpdatingEditor = true;
                JsonEditorTextBox.Document = BuildHighlightedDocument(text);
                SetEditorCaretOffset(Math.Min(caretOffset, text.Length));
            }
            finally
            {
                _isUpdatingEditor = false;
            }

            ValidateJson();
        }

        // 初始化一个空的高亮文档，避免 RichTextBox 使用默认 FlowDocument 样式。
        private void ResetEditorDocument()
        {
            try
            {
                _isUpdatingEditor = true;
                JsonEditorTextBox.Document = BuildHighlightedDocument(string.Empty);
            }
            finally
            {
                _isUpdatingEditor = false;
            }
        }

        // 根据纯文本 JSON 构建带颜色的 FlowDocument。
        // PageWidth 给大一点，配合 RichTextBox 的水平滚动条，长行不容易被强制折行。
        private FlowDocument BuildHighlightedDocument(string text)
        {
            FlowDocument document = new FlowDocument
            {
                PageWidth = 4096
            };

            Paragraph paragraph = new Paragraph
            {
                Margin = new Thickness(0)
            };

            foreach (JsonColorSegment segment in GetJsonColorSegments(text))
            {
                AddHighlightedInline(paragraph, segment);
            }

            document.Blocks.Add(paragraph);
            return document;
        }

        // 把一个高亮片段加入 Paragraph。
        // RichTextBox 里直接把 \r\n 放进 Run 时，重建 Document 后可能不会按真实换行显示。
        // 所以这里遇到 \r\n、\n、\r 都显式插入 LineBreak，普通文本仍用 Run 保留颜色。
        private static void AddHighlightedInline(Paragraph paragraph, JsonColorSegment segment)
        {
            int index = 0;

            while (index < segment.Text.Length)
            {
                int lineBreakIndex = FindLineBreak(segment.Text, index);
                if (lineBreakIndex < 0)
                {
                    AddRunIfNotEmpty(paragraph, segment.Text.Substring(index), segment.Brush);
                    return;
                }

                AddRunIfNotEmpty(paragraph, segment.Text.Substring(index, lineBreakIndex - index), segment.Brush);
                paragraph.Inlines.Add(new LineBreak());

                index = lineBreakIndex + GetLineBreakLength(segment.Text, lineBreakIndex);
            }
        }

        // 添加非空文本 Run，避免生成大量空 Run 影响后续光标定位。
        private static void AddRunIfNotEmpty(Paragraph paragraph, string text, Brush brush)
        {
            if (text.Length == 0)
            {
                return;
            }

            paragraph.Inlines.Add(new Run(text)
            {
                Foreground = brush
            });
        }

        // 找到下一处换行符位置。
        // Windows 文本一般是 \r\n，但这里也兼容单独的 \n 或 \r。
        private static int FindLineBreak(string text, int start)
        {
            for (int i = start; i < text.Length; i++)
            {
                if (text[i] == '\r' || text[i] == '\n')
                {
                    return i;
                }
            }

            return -1;
        }

        // 返回当前位置换行符的长度。
        // \r\n 要作为一个换行处理，否则会插入两个 LineBreak。
        private static int GetLineBreakLength(string text, int index)
        {
            return text[index] == '\r' && index + 1 < text.Length && text[index + 1] == '\n'
                ? 2
                : 1;
        }

        // 简单 JSON 词法扫描器。
        // 只负责 UI 高亮，不承担 JSON 正确性校验；真正的校验由 JsonDocument.Parse 完成。
        // 扫描结果会把 key、字符串 value、数字、花括号/中括号和普通字符分别切成片段。
        private static IEnumerable<JsonColorSegment> GetJsonColorSegments(string text)
        {
            int index = 0;

            while (index < text.Length)
            {
                if (IsJsonBracket(text[index]))
                {
                    yield return new JsonColorSegment(text[index].ToString(), DefaultJsonBrush);
                    index++;
                    continue;
                }

                if (text[index] == '"')
                {
                    int end = FindStringEnd(text, index);
                    string value = text.Substring(index, end - index);
                    Brush brush = IsJsonKey(text, end) ? KeyJsonBrush : StringJsonBrush;
                    yield return new JsonColorSegment(value, brush);
                    index = end;
                    continue;
                }

                if (IsNumberStart(text, index))
                {
                    int end = FindNumberEnd(text, index);
                    yield return new JsonColorSegment(text.Substring(index, end - index), NumberJsonBrush);
                    index = end;
                    continue;
                }

                int nextSpecial = FindNextSpecial(text, index + 1);
                yield return new JsonColorSegment(text.Substring(index, nextSpecial - index), DefaultJsonBrush);
                index = nextSpecial;
            }

            if (text.Length == 0)
            {
                yield return new JsonColorSegment(string.Empty, DefaultJsonBrush);
            }
        }

        // JSON 结构括号单独着色，方便在复杂对象或数组里快速看清层级边界。
        private static bool IsJsonBracket(char value)
        {
            return value == '{' || value == '}' || value == '[' || value == ']';
        }

        // 找到字符串结束位置。
        // 会识别反斜杠转义，避免把 \" 误判成字符串结束。
        private static int FindStringEnd(string text, int start)
        {
            bool escaped = false;
            for (int i = start + 1; i < text.Length; i++)
            {
                if (escaped)
                {
                    escaped = false;
                    continue;
                }

                if (text[i] == '\\')
                {
                    escaped = true;
                    continue;
                }

                if (text[i] == '"')
                {
                    return i + 1;
                }
            }

            return text.Length;
        }

        // 判断刚扫描到的字符串是不是 JSON key。
        // JSON key 的特征是字符串后面跳过空白后紧跟冒号。
        private static bool IsJsonKey(string text, int stringEnd)
        {
            int index = stringEnd;
            while (index < text.Length && char.IsWhiteSpace(text[index]))
            {
                index++;
            }

            return index < text.Length && text[index] == ':';
        }

        // 判断当前位置是不是数字开头。
        // 支持普通数字和负数；具体数字合法性仍交给 JsonDocument.Parse 校验。
        private static bool IsNumberStart(string text, int index)
        {
            char current = text[index];
            if (char.IsDigit(current))
            {
                return true;
            }

            return current == '-' && index + 1 < text.Length && char.IsDigit(text[index + 1]);
        }

        // 找到数字片段结束位置。
        // 支持整数、小数和科学计数法，如 -12、3.14、1e-5。
        private static int FindNumberEnd(string text, int start)
        {
            int index = start;

            if (index < text.Length && text[index] == '-')
            {
                index++;
            }

            while (index < text.Length && char.IsDigit(text[index]))
            {
                index++;
            }

            if (index < text.Length && text[index] == '.')
            {
                index++;
                while (index < text.Length && char.IsDigit(text[index]))
                {
                    index++;
                }
            }

            if (index < text.Length && (text[index] == 'e' || text[index] == 'E'))
            {
                index++;
                if (index < text.Length && (text[index] == '+' || text[index] == '-'))
                {
                    index++;
                }

                while (index < text.Length && char.IsDigit(text[index]))
                {
                    index++;
                }
            }

            return index;
        }

        // 找到下一个需要特殊着色的起点：字符串引号、数字或 JSON 结构括号。
        // 中间的标点、空白、true/false/null 都使用默认颜色。
        private static int FindNextSpecial(string text, int start)
        {
            for (int i = start; i < text.Length; i++)
            {
                if (text[i] == '"' || IsNumberStart(text, i) || IsJsonBracket(text[i]))
                {
                    return i;
                }
            }

            return text.Length;
        }

        // 从 RichTextBox 读取纯文本。
        // TextRange 会额外带出 FlowDocument 末尾的 \r\n，这里统一去掉，避免校验和光标偏移被扰动。
        private string GetEditorText()
        {
            string text = new TextRange(JsonEditorTextBox.Document.ContentStart, JsonEditorTextBox.Document.ContentEnd)
                .Text;

            return text.EndsWith("\r\n", StringComparison.Ordinal)
                ? text.Substring(0, text.Length - 2)
                : text;
        }

        // 把 RichTextBox 当前光标位置转换成纯文本 offset。
        // 因为每次高亮都会重建 Run，不能直接保存 TextPointer，只能保存文本偏移。
        private int GetEditorCaretOffset()
        {
            string text = new TextRange(JsonEditorTextBox.Document.ContentStart, JsonEditorTextBox.CaretPosition).Text;
            return text.EndsWith("\r\n", StringComparison.Ordinal) ? text.Length - 2 : text.Length;
        }

        // 获取选择区域起点对应的纯文本 offset。
        // 插入括号对时需要用它恢复光标到括号中间。
        private int GetSelectionStartOffset()
        {
            TextPointer start = JsonEditorTextBox.Selection.Start.CompareTo(JsonEditorTextBox.Selection.End) <= 0
                ? JsonEditorTextBox.Selection.Start
                : JsonEditorTextBox.Selection.End;

            string text = new TextRange(JsonEditorTextBox.Document.ContentStart, start).Text;
            return text.EndsWith("\r\n", StringComparison.Ordinal) ? text.Length - 2 : text.Length;
        }

        // 获取选择区域终点对应的纯文本 offset。
        // 回车替换选区时需要同时知道起点和终点，才能从纯文本中删掉被选中的内容。
        private int GetSelectionEndOffset()
        {
            TextPointer end = JsonEditorTextBox.Selection.Start.CompareTo(JsonEditorTextBox.Selection.End) <= 0
                ? JsonEditorTextBox.Selection.End
                : JsonEditorTextBox.Selection.Start;

            string text = new TextRange(JsonEditorTextBox.Document.ContentStart, end).Text;
            return text.EndsWith("\r\n", StringComparison.Ordinal) ? text.Length - 2 : text.Length;
        }

        // 按纯文本 offset 恢复 RichTextBox 光标位置。
        private void SetEditorCaretOffset(int offset)
        {
            TextPointer pointer = GetTextPointerAtOffset(JsonEditorTextBox.Document.ContentStart, Math.Max(offset, 0));
            JsonEditorTextBox.CaretPosition = pointer;
        }

        // 从 FlowDocument 起点开始遍历 TextPointer，找到指定纯文本 offset 对应的位置。
        // RichTextBox 文档里有 Paragraph/Run 等结构，不能简单按字符串索引定位。
        private TextPointer GetTextPointerAtOffset(TextPointer start, int targetOffset)
        {
            TextPointer navigator = start;
            int currentOffset = 0;

            while (navigator.CompareTo(JsonEditorTextBox.Document.ContentEnd) < 0)
            {
                TextPointerContext context = navigator.GetPointerContext(LogicalDirection.Forward);

                if (context == TextPointerContext.Text)
                {
                    string runText = navigator.GetTextInRun(LogicalDirection.Forward);
                    if (currentOffset + runText.Length >= targetOffset)
                    {
                        TextPointer pointer = navigator.GetPositionAtOffset(targetOffset - currentOffset, LogicalDirection.Forward);
                        return pointer ?? navigator;
                    }

                    currentOffset += runText.Length;
                    navigator = navigator.GetPositionAtOffset(runText.Length, LogicalDirection.Forward) ?? navigator;
                    continue;
                }

                if (context == TextPointerContext.ElementStart &&
                    navigator.GetAdjacentElement(LogicalDirection.Forward) is LineBreak)
                {
                    int lineBreakLength = Environment.NewLine.Length;
                    if (currentOffset + lineBreakLength >= targetOffset)
                    {
                        TextPointer pointer = navigator.GetNextContextPosition(LogicalDirection.Forward);
                        return pointer ?? navigator;
                    }

                    currentOffset += lineBreakLength;
                }

                TextPointer next = navigator.GetNextContextPosition(LogicalDirection.Forward);
                if (next == null)
                {
                    break;
                }

                navigator = next;
            }

            return JsonEditorTextBox.Document.ContentEnd;
        }

        // 统一 JSON 解析入口。
        // 成功时返回 JsonDocument；失败时返回适合显示在界面上的中文错误提示。
        private static bool TryParseJson(string json, out JsonDocument document, out string errorMessage)
        {
            document = null;
            errorMessage = string.Empty;

            try
            {
                document = JsonDocument.Parse(json);
                return true;
            }
            catch (JsonException ex)
            {
                errorMessage = string.Format("解析失败：{0}", ex.Message);

                if (ex.LineNumber.HasValue && ex.BytePositionInLine.HasValue)
                {
                    errorMessage = string.Format("{0} 位置：第 {1} 行，第 {2} 列。", errorMessage, ex.LineNumber.Value + 1, ex.BytePositionInLine.Value + 1);
                }

                return false;
            }
            catch (Exception ex)
            {
                errorMessage = string.Format("解析失败：{0}", ex.Message);
                return false;
            }
        }

        // 一段已经确定颜色的 JSON 文本片段。
        // BuildHighlightedDocument 会把每个片段转换成 RichTextBox 里的 Run。
        private sealed class JsonColorSegment
        {
            public JsonColorSegment(string text, Brush brush)
            {
                Text = text;
                Brush = brush;
            }

            public string Text { get; private set; }

            public Brush Brush { get; private set; }
        }
    }
}
