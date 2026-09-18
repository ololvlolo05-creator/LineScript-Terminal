using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace LineScriptTerminal
{
    public class MainForm : Form
    {
        private static readonly Color BgDark       = Color.FromArgb(14, 14, 18);
        private static readonly Color BgMenu       = Color.FromArgb(24, 24, 30);
        private static readonly Color FgDefault    = Color.FromArgb(220, 220, 225);
        private static readonly Color FgPrompt     = Color.FromArgb(120, 220, 160);
        private static readonly Color FgPromptMode = Color.FromArgb(100, 190, 255);
        private static readonly Color FgInfo       = Color.FromArgb(120, 180, 255);
        private static readonly Color FgError      = Color.FromArgb(255, 110, 110);
        private static readonly Color FgWarn       = Color.FromArgb(245, 205, 95);
        private static readonly Color FgSuccess    = Color.FromArgb(120, 225, 120);
        private static readonly Color FgInput      = Color.White;
        private static readonly Color FgDim        = Color.FromArgb(150, 150, 160);

        private static readonly string[] LineScriptTypes =
        {
            "cmd", "ps", "git", "file", "file-up", "file-bin", "del-file",
            "patch", "grep", "undo", "add-command", "cd",
            "list-workspaces", "server", "help"
        };

        private RichTextBox _console = null!;
        private Label _status = null!;
        private ToolStripMenuItem _mPs = null!;
        private ToolStripMenuItem _mCmd = null!;

        private int _promptEnd;
        private volatile bool _isBusy;
        private bool _suppressHighlight;
        private readonly LocalExecutor _ex;
        private CancellationTokenSource? _cts;
        private bool _autoScroll = true;

        private readonly List<string> _history = new();
        private int _historyIndex = -1;
        private string _draft = "";

        private List<string> _autoCandidates = new();
        private int _autoIndex;
        private int _autoStart;
        private string _autoPrefix = "";

        public MainForm(LocalExecutor ex)
        {
            _ex = ex;
            Text = "LineScript Universal Terminal v1.1";
            Size = new Size(1080, 700);
            MinimumSize = new Size(680, 420);
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = BgDark;
            ForeColor = FgDefault;
            Font = new Font("Consolas", 11);

            BuildUi();
            InitConsole();
            UpdateStatus();
        }

        private void BuildUi()
        {
            var menu = new MenuStrip { BackColor = BgMenu, ForeColor = FgDefault };

            // Меню "Файл"
            var mFile = new ToolStripMenuItem("Файл");
            mFile.DropDownItems.Add("Очистить экран (Ctrl+L)", null, (s, e) => ClearConsole());
            mFile.DropDownItems.Add("Открыть профиль (profile.ls)", null, (s, e) =>
            {
                try { Process.Start(new ProcessStartInfo("notepad.exe", _ex.ProfileFile) { UseShellExecute = true }); } catch { }
            });
            mFile.DropDownItems.Add("Открыть папку настроек", null, (s, e) =>
            {
                try { Process.Start(new ProcessStartInfo("explorer.exe", _ex.AppDir) { UseShellExecute = true }); } catch { }
            });
            mFile.DropDownItems.Add(new ToolStripSeparator());
            mFile.DropDownItems.Add("Выход", null, (s, e) => Close());
            menu.Items.Add(mFile);

            // Меню "Режим оболочки"
            var mMode = new ToolStripMenuItem("Оболочка");
            _mPs = new ToolStripMenuItem("PowerShell (по умолчанию)", null, (s, e) => SetShellMode(ShellMode.PowerShell)) { Checked = true };
            _mCmd = new ToolStripMenuItem("Command Prompt (CMD)", null, (s, e) => SetShellMode(ShellMode.Cmd));
            mMode.DropDownItems.Add(_mPs);
            mMode.DropDownItems.Add(_mCmd);
            menu.Items.Add(mMode);

            // Меню "Помощь"
            var mHelp = new ToolStripMenuItem("Справка");
            mHelp.DropDownItems.Add("Справка по LineScript (-help-)", null, (s, e) => _ = ExecuteInternalCommandAsync("-help- -/-"));
            mHelp.DropDownItems.Add("Документация LineScript (E:\\LineScript)", null, (s, e) =>
            {
                try { Process.Start(new ProcessStartInfo("explorer.exe", "E:\\LineScript") { UseShellExecute = true }); } catch { }
            });
            menu.Items.Add(mHelp);

            Controls.Add(menu);
            MainMenuStrip = menu;

            // Статус-бар внизу
            _status = new Label
            {
                Dock = DockStyle.Bottom,
                Height = 26,
                BackColor = BgMenu,
                ForeColor = FgDim,
                Font = new Font("Consolas", 9),
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(10, 0, 0, 0)
            };
            Controls.Add(_status);

            // Консольное поле вывода/ввода
            _console = new RichTextBox
            {
                Dock = DockStyle.Fill,
                BackColor = BgDark,
                ForeColor = FgDefault,
                BorderStyle = BorderStyle.None,
                Font = new Font("Consolas", 11),
                WordWrap = true,
                ScrollBars = RichTextBoxScrollBars.Vertical,
                DetectUrls = false,
                AcceptsTab = true,
                ShortcutsEnabled = true,
                AllowDrop = true
            };

            // Контекстное меню
            var ctx = new ContextMenuStrip();
            ctx.Items.Add("Копировать", null, (s, e) => CopySelection());
            ctx.Items.Add("Вставить", null, (s, e) => PasteClipboard());
            ctx.Items.Add(new ToolStripSeparator());
            ctx.Items.Add("Очистить экран (Ctrl+L)", null, (s, e) => ClearConsole());
            ctx.Items.Add("Прервать процесс (Ctrl+C)", null, (s, e) => InterruptExecution());
            _console.ContextMenuStrip = ctx;

            // Drag & Drop файлов и папок
            _console.DragEnter += (s, e) =>
            {
                if (e.Data != null && e.Data.GetDataPresent(DataFormats.FileDrop))
                    e.Effect = DragDropEffects.Copy;
            };
            _console.DragDrop += (s, e) =>
            {
                if (e.Data != null && e.Data.GetDataPresent(DataFormats.FileDrop))
                {
                    string[]? files = (string[]?)e.Data.GetData(DataFormats.FileDrop);
                    if (files != null && files.Length > 0)
                    {
                        string insert = string.Join(" ", files.Select(f => f.Contains(' ') ? $"\"{f}\"" : f));
                        InsertAtCursor(insert);
                    }
                }
            };

            Controls.Add(_console);
            _console.BringToFront();
        }

        private void SetShellMode(ShellMode mode)
        {
            _ex.DefaultShell = mode;
            _mPs.Checked = mode == ShellMode.PowerShell;
            _mCmd.Checked = mode == ShellMode.Cmd;
            UpdateStatus();
        }

        private void InitConsole()
        {
            _console.KeyDown += Console_KeyDown;
            _console.TextChanged += (_, _) => { if (!_suppressHighlight) ApplyHighlighting(); };
            _console.VScroll += (_, _) => DetectAutoScroll();

            AppendInfo("===============================================================================");
            AppendInfo("LineScript Universal Terminal v1.1  [Потоковый вывод | Интерактивный Shell]");
            AppendInfo("Поддерживает: любые команды PowerShell, CMD, Git, пакеты LineScript v1.1");
            AppendInfo("Горячие клавиши: Ctrl+C (прервать), Ctrl+L (очистить), Ctrl+R (поиск), Tab (автодополнение)");
            AppendInfo("Перетаскивание файлов из Проводника (Drag & Drop) вставляет путь в консоль.");
            AppendInfo("===============================================================================\r\n");

            AppendPrompt();
        }

        // ---------- Вывод текста и ANSI ----------

        private void Append(string text, Color color)
        {
            if (_console.IsDisposed) return;
            if (_console.InvokeRequired)
            {
                _console.BeginInvoke(new Action(() => Append(text, color)));
                return;
            }

            _suppressHighlight = true;
            _console.SelectionStart = _console.TextLength;
            _console.SelectionLength = 0;
            _console.SelectionColor = color;
            _console.AppendText(text);
            _console.SelectionColor = _console.ForeColor;

            if (_autoScroll)
            {
                _console.SelectionStart = _console.TextLength;
                _console.ScrollToCaret();
            }
            _suppressHighlight = false;
        }

        private void AppendDefault(string t) => Append(t, FgDefault);
        private void AppendInfo(string t)    => Append(t + "\r\n", FgInfo);
        private void AppendError(string t)   => Append(t + "\r\n", FgError);

        private void AppendWithAnsi(string text, Color baseColor)
        {
            if (string.IsNullOrEmpty(text)) return;
            var rx = new Regex(@"\x1b\[([0-9;]*)m");
            int pos = 0;
            Color current = baseColor;
            foreach (Match m in rx.Matches(text))
            {
                if (m.Index > pos)
                    Append(text.Substring(pos, m.Index - pos), current);

                string code = m.Groups[1].Value;
                current = ParseAnsiColor(code, baseColor);
                pos = m.Index + m.Length;
            }
            if (pos < text.Length)
                Append(text.Substring(pos), current);
        }

        private static Color ParseAnsiColor(string code, Color fallback)
        {
            if (string.IsNullOrEmpty(code) || code == "0") return FgDefault;
            var parts = code.Split(';');
            foreach (var p in parts)
            {
                switch (p)
                {
                    case "30": return Color.FromArgb(90, 90, 90);
                    case "31": return Color.FromArgb(255, 95, 95);
                    case "32": return Color.FromArgb(120, 225, 120);
                    case "33": return Color.FromArgb(245, 205, 95);
                    case "34": return Color.FromArgb(120, 180, 255);
                    case "35": return Color.FromArgb(210, 130, 255);
                    case "36": return Color.FromArgb(95, 225, 225);
                    case "37": return Color.FromArgb(235, 235, 235);
                    case "90": return Color.FromArgb(130, 130, 130);
                    case "91": return Color.FromArgb(255, 130, 130);
                    case "92": return Color.FromArgb(150, 245, 150);
                    case "93": return Color.FromArgb(255, 235, 130);
                    case "94": return Color.FromArgb(145, 195, 255);
                    case "95": return Color.FromArgb(225, 155, 255);
                    case "96": return Color.FromArgb(130, 245, 245);
                    case "97": return Color.FromArgb(255, 255, 255);
                }
            }
            return fallback;
        }

        private void AppendPrompt()
        {
            _suppressHighlight = true;
            _console.SelectionStart = _console.TextLength;
            _console.SelectionColor = FgPrompt;
            _console.AppendText("LS [");
            _console.SelectionColor = FgPromptMode;
            _console.AppendText(_ex.DefaultShell == ShellMode.PowerShell ? "PS" : "CMD");
            _console.SelectionColor = FgPrompt;
            _console.AppendText("] " + _ex.CurrentDir + "> ");
            _console.SelectionColor = _console.ForeColor;

            _promptEnd = _console.TextLength;
            _console.SelectionStart = _console.TextLength;
            _console.ScrollToCaret();
            _suppressHighlight = false;
        }

        private void UpdateStatus()
        {
            string state = _isBusy ? (_ex.HasActiveProcess ? "Исполняется процесс..." : "Обработка...") : "Ожидание команды";
            _status.Text = $"  LineScript v1.1  |  Оболочка: {_ex.DefaultShell}  |  {state}  |  Код: {_ex.LastExitCode}  |  {_ex.CurrentDir}";
        }

        private void DetectAutoScroll()
        {
            if (_console.TextLength == 0) return;
            var p = _console.GetPositionFromCharIndex(_console.TextLength - 1);
            _autoScroll = p.Y < _console.ClientSize.Height + 50;
        }

        // ---------- Обработка клавиатуры ----------

        private void Console_KeyDown(object? sender, KeyEventArgs e)
        {
            // Ctrl+C — отмена процесса или копирование текста
            if (e.Control && e.KeyCode == Keys.C)
            {
                e.SuppressKeyPress = true;
                if (_console.SelectionLength > 0)
                {
                    CopySelection();
                    return;
                }
                InterruptExecution();
                return;
            }

            // Ctrl+Shift+C / Ctrl+Shift+V
            if (e.Control && e.Shift && e.KeyCode == Keys.C)
            {
                e.SuppressKeyPress = true;
                CopySelection();
                return;
            }
            if (e.Control && e.Shift && e.KeyCode == Keys.V)
            {
                e.SuppressKeyPress = true;
                PasteClipboard();
                return;
            }

            // Ctrl+L — очистка консоли
            if (e.Control && e.KeyCode == Keys.L)
            {
                e.SuppressKeyPress = true;
                ClearConsole();
                return;
            }

            // Ctrl+R — поиск по истории
            if (e.Control && e.KeyCode == Keys.R)
            {
                e.SuppressKeyPress = true;
                SearchHistory();
                return;
            }

            // Zoom Ctrl+= / Ctrl+-
            if (e.Control && e.KeyCode == Keys.Oemplus) { e.SuppressKeyPress = true; Zoom(+1); return; }
            if (e.Control && e.KeyCode == Keys.OemMinus) { e.SuppressKeyPress = true; Zoom(-1); return; }

            // Если процесс сейчас работает в интерактивном режиме:
            if (_isBusy && _ex.HasActiveProcess)
            {
                if (e.KeyCode == Keys.Enter)
                {
                    e.SuppressKeyPress = true;
                    string stdinText = _console.Text.Substring(Math.Min(_promptEnd, _console.TextLength));
                    Append("\r\n", FgDefault);
                    _promptEnd = _console.TextLength;
                    _ex.SendInput(stdinText);
                    return;
                }
                if (e.KeyCode == Keys.Back && _console.SelectionStart <= _promptEnd && _console.SelectionLength == 0)
                {
                    e.SuppressKeyPress = true;
                    return;
                }
                return;
            }

            // Если система занята внутренней обработкой
            if (_isBusy)
            {
                if (e.KeyCode == Keys.Enter || e.KeyCode == Keys.Tab) e.SuppressKeyPress = true;
                return;
            }

            // Навигация по истории команд
            if (e.KeyCode == Keys.Up && !e.Shift)   { e.SuppressKeyPress = true; NavigateHistory(-1); return; }
            if (e.KeyCode == Keys.Down && !e.Shift) { e.SuppressKeyPress = true; NavigateHistory(1);  return; }
            if (e.KeyCode == Keys.Tab)              { e.SuppressKeyPress = true; DoTabComplete(e.Shift); return; }

            // Защита от редактирования текста до промпта
            if ((e.KeyCode == Keys.Left || e.KeyCode == Keys.Home) && _console.SelectionStart <= _promptEnd)
            {
                e.SuppressKeyPress = true;
                _console.SelectionStart = _promptEnd;
                return;
            }
            if (e.KeyCode == Keys.Back && _console.SelectionStart <= _promptEnd && _console.SelectionLength == 0)
            {
                e.SuppressKeyPress = true;
                return;
            }

            if (e.KeyCode == Keys.Enter)
            {
                if (e.Shift) return; // Shift+Enter позволяет вводить многострочные блоки
                e.SuppressKeyPress = true;
                _ = SubmitAsync();
            }
        }

        private void InterruptExecution()
        {
            _cts?.Cancel();
            _ex.KillActiveProcess();
            Append("\r\n^C\r\n", FgWarn);
            _isBusy = false;
            UpdateStatus();
            AppendPrompt();
        }

        private void ClearConsole()
        {
            _console.Clear();
            AppendPrompt();
        }

        private void CopySelection()
        {
            if (_console.SelectionLength > 0)
            {
                try { Clipboard.SetText(_console.SelectedText); } catch { }
            }
        }

        private void PasteClipboard()
        {
            if (!Clipboard.ContainsText()) return;
            string text = Clipboard.GetText();
            InsertAtCursor(text);
        }

        private void InsertAtCursor(string text)
        {
            if (_console.SelectionStart < _promptEnd)
                _console.SelectionStart = _console.TextLength;
            _console.SelectedText = text;
        }

        private void Zoom(int delta)
        {
            float size = _console.Font.Size + delta;
            if (size < 8) size = 8;
            if (size > 26) size = 26;
            _console.Font = new Font("Consolas", size);
        }

        private void NavigateHistory(int delta)
        {
            if (_history.Count == 0) return;
            if (_historyIndex == -1 && delta < 0)
                _draft = _console.Text.Substring(Math.Min(_promptEnd, _console.TextLength));

            int next = _historyIndex + delta;
            if (next < -1) next = -1;
            if (next >= _history.Count) next = _history.Count - 1;
            if (next == _historyIndex) return;

            _historyIndex = next;
            ReplaceInput(_historyIndex == -1 ? _draft : _history[_historyIndex]);
        }

        private void SearchHistory()
        {
            if (_history.Count == 0) return;
            string current = _console.Text.Substring(Math.Min(_promptEnd, _console.TextLength)).Trim();
            var matches = _history.Where(h => h.IndexOf(current, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            if (matches.Count > 0)
            {
                string pick = matches.Last();
                ReplaceInput(pick);
            }
        }

        private void ReplaceInput(string text)
        {
            _suppressHighlight = true;
            _console.Select(_promptEnd, _console.TextLength - _promptEnd);
            _console.SelectedText = text;
            _console.SelectionStart = _console.TextLength;
            _console.SelectionLength = 0;
            _suppressHighlight = false;
            ApplyHighlighting();
        }

        // ---------- Tab Completion ----------

        private void DoTabComplete(bool reverse)
        {
            string input = _console.Text.Substring(Math.Min(_promptEnd, _console.TextLength));
            int cursor = _console.SelectionStart - _promptEnd;
            string before = cursor <= input.Length ? input.Substring(0, cursor) : input;

            if (_autoCandidates.Count == 0 || _autoPrefix != before)
            {
                _autoCandidates.Clear();
                _autoIndex = 0;
                _autoStart = 0;
                _autoPrefix = before;

                // Типы LineScript: -xxx
                var mType = Regex.Match(before, @"(?:^|\s)-(\w*)$");
                if (mType.Success)
                {
                    _autoStart = mType.Index + 1;
                    string prefix = mType.Groups[1].Value;
                    _autoCandidates.AddRange(LineScriptTypes
                        .Where(t => t.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                        .Select(t => t + "-"));
                }
                else
                {
                    var mToken = Regex.Match(before, @"([^\s]*)$");
                    string token = mToken.Value;
                    _autoStart = mToken.Index;

                    if (token.Contains('\\') || token.Contains('/'))
                    {
                        int idx = token.LastIndexOfAny(new[] { '\\', '/' });
                        string dirPart = token.Substring(0, idx + 1);
                        string filePrefix = token.Substring(idx + 1);
                        string fullDir = _ex.ResolvePath(dirPart);

                        if (Directory.Exists(fullDir))
                        {
                            foreach (var d in Directory.GetDirectories(fullDir))
                                _autoCandidates.Add(dirPart + Path.GetFileName(d) + "\\");
                            foreach (var f in Directory.GetFiles(fullDir))
                                _autoCandidates.Add(dirPart + Path.GetFileName(f));

                            _autoCandidates = _autoCandidates
                                .Where(c => Path.GetFileName(c.TrimEnd('\\')).StartsWith(filePrefix, StringComparison.OrdinalIgnoreCase))
                                .ToList();
                        }
                    }
                    else if (token.Length >= 1)
                    {
                        var standardCommands = new[]
                        {
                            "Get-Date", "Get-Process", "Get-ChildItem", "Get-Content", "Get-Location",
                            "Set-Location", "Set-Content", "Copy-Item", "Move-Item", "Remove-Item",
                            "New-Item", "Start-Process", "Stop-Process", "Invoke-WebRequest",
                            "dotnet", "git", "npm", "node", "python", "pip", "code", "notepad",
                            "dir", "cls", "cd", "clear", "help", "mode"
                        };
                        _autoCandidates.AddRange(standardCommands.Where(c => c.StartsWith(token, StringComparison.OrdinalIgnoreCase)));
                    }
                }
            }

            if (_autoCandidates.Count == 0) return;

            if (reverse)
                _autoIndex = (_autoIndex - 1 + _autoCandidates.Count) % _autoCandidates.Count;
            else
                _autoIndex = (_autoIndex + 1) % _autoCandidates.Count;

            string chosen = _autoCandidates[_autoIndex];

            _suppressHighlight = true;
            _console.Select(_promptEnd + _autoStart, (_console.TextLength - _promptEnd) - _autoStart);
            _console.SelectedText = chosen;
            _console.SelectionStart = _promptEnd + _autoStart + chosen.Length;
            _console.SelectionLength = 0;
            _suppressHighlight = false;
            ApplyHighlighting();
        }

        // ---------- Отправка и исполнение команд ----------

        private async Task SubmitAsync()
        {
            string input = _console.Text.Substring(Math.Min(_promptEnd, _console.TextLength));
            _historyIndex = -1;
            _draft = "";
            _autoCandidates.Clear();
            _autoPrefix = "";

            Append("\r\n", FgDefault);

            if (!string.IsNullOrWhiteSpace(input))
            {
                _history.Add(input);
                if (_history.Count > 300) _history.RemoveAt(0);

                string trimmed = input.Trim();

                if (string.Equals(trimmed, "cls", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(trimmed, "clear", StringComparison.OrdinalIgnoreCase))
                {
                    ClearConsole();
                    return;
                }

                if (string.Equals(trimmed, "exit", StringComparison.OrdinalIgnoreCase))
                {
                    Close();
                    return;
                }

                if (string.Equals(trimmed, "history", StringComparison.OrdinalIgnoreCase))
                {
                    for (int i = 0; i < _history.Count; i++)
                        AppendDefault($"  {i + 1,3}  {_history[i]}\r\n");
                    AppendPrompt();
                    return;
                }

                _isBusy = true;
                _cts = new CancellationTokenSource();
                UpdateStatus();

                try
                {
                    await _ex.ExecuteAsync(trimmed, (chunk, color) => AppendWithAnsi(chunk, color), _cts.Token);
                }
                catch (OperationCanceledException)
                {
                    Append("\r\n^C\r\n", FgWarn);
                }
                catch (Exception ex)
                {
                    AppendError("Исключение: " + ex.Message);
                }
                finally
                {
                    _isBusy = false;
                    _cts = null;
                }
            }

            _autoScroll = true;
            UpdateStatus();
            AppendPrompt();
        }

        private async Task ExecuteInternalCommandAsync(string cmd)
        {
            AppendPrompt();
            InsertAtCursor(cmd);
            await SubmitAsync();
        }

        // ---------- Подсветка синтаксиса ----------

        private void ApplyHighlighting()
        {
            if (_console.IsDisposed) return;
            int selStart = _console.SelectionStart, selLen = _console.SelectionLength;
            _suppressHighlight = true;
            _console.SuspendLayout();

            int userStart = Math.Min(_promptEnd, _console.TextLength);
            int userLen = _console.TextLength - userStart;
            if (userLen > 0)
            {
                _console.Select(userStart, userLen);
                _console.SelectionColor = FgInput;
                _console.SelectionBackColor = BgDark;
            }

            string userText = userLen > 0 ? _console.Text.Substring(userStart, userLen) : "";
            var regex = new Regex(@"(?<type>-?(?:cmd|ps|git|file|file-up|file-bin|del-file|patch|grep|undo|add-command|cd|list-workspaces|server|help)-)(?<body>[\s\S]*?)(?<end>-?/-)", RegexOptions.IgnoreCase);
            var bold = new Font(_console.Font, FontStyle.Bold);
            var norm = new Font(_console.Font, FontStyle.Regular);

            foreach (Match m in regex.Matches(userText))
            {
                string type = m.Groups["type"].Value.Trim('-', ' ').ToLowerInvariant();
                Color c = TypeColor(type);
                _console.Select(userStart + m.Groups["type"].Index, m.Groups["type"].Length);
                _console.SelectionColor = c;
                _console.SelectionFont = bold;

                _console.Select(userStart + m.Groups["body"].Index, m.Groups["body"].Length);
                _console.SelectionColor = Color.FromArgb(220, 220, 220);
                _console.SelectionFont = norm;

                _console.Select(userStart + m.Groups["end"].Index, m.Groups["end"].Length);
                _console.SelectionColor = Color.FromArgb(140, 140, 150);
                _console.SelectionFont = norm;
            }

            bold.Dispose();
            norm.Dispose();
            _console.Select(selStart, selLen);
            _console.ResumeLayout();
            _suppressHighlight = false;
        }

        private static Color TypeColor(string type) => type switch
        {
            "cmd" => Color.FromArgb(255, 110, 110),
            "ps" => Color.FromArgb(80, 210, 200),
            "git" => Color.FromArgb(255, 160, 70),
            "file" or "file-up" => Color.FromArgb(255, 220, 70),
            "file-bin" => Color.FromArgb(255, 180, 220),
            "del-file" => Color.FromArgb(245, 105, 150),
            "patch" => Color.FromArgb(195, 140, 255),
            "grep" => Color.FromArgb(120, 205, 255),
            "undo" => Color.FromArgb(255, 185, 125),
            "add-command" => Color.FromArgb(165, 160, 255),
            "cd" or "list-workspaces" => Color.FromArgb(0, 210, 205),
            "server" => Color.FromArgb(255, 205, 115),
            "help" => Color.FromArgb(130, 240, 240),
            _ => Color.FromArgb(200, 200, 200)
        };
    }
}
