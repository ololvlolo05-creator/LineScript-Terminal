using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace LineScriptTerminal
{
    public enum ShellMode
    {
        PowerShell,
        Cmd
    }

    public class CustomCommand
    {
        public string Name { get; set; } = "";
        public string Template { get; set; } = "{args}";
        public string Environment { get; set; } = "PS";
        public string Executable { get; set; } = "";
        public string Install { get; set; } = "";
    }

    public class ParsedLineScriptCommand
    {
        public int Order { get; set; } = int.MaxValue;
        public string Type { get; set; } = "";
        public string Body { get; set; } = "";
        public string Raw { get; set; } = "";
    }

    public class LocalExecutor
    {
        public static readonly Color ColorDefault = Color.FromArgb(220, 220, 220);
        public static readonly Color ColorInfo    = Color.FromArgb(120, 180, 255);
        public static readonly Color ColorSuccess = Color.FromArgb(120, 225, 120);
        public static readonly Color ColorWarn    = Color.FromArgb(240, 200, 100);
        public static readonly Color ColorError   = Color.FromArgb(255, 120, 120);

        public string CurrentDir { get; private set; } = "";
        public List<string> Workspaces { get; private set; } = new();
        public Dictionary<string, CustomCommand> Commands { get; private set; } = new(StringComparer.OrdinalIgnoreCase);
        public string AppDir { get; }
        public int SessionCommands { get; private set; }
        public DateTime StartTime { get; } = DateTime.Now;
        public int LastExitCode { get; private set; }
        public ShellMode DefaultShell { get; set; } = ShellMode.PowerShell;

        private Process? _activeProcess;
        private readonly object _processLock = new();

        public bool HasActiveProcess
        {
            get
            {
                lock (_processLock)
                {
                    return _activeProcess != null && !_activeProcess.HasExited;
                }
            }
        }

        private string CommandsFile => Path.Combine(AppDir, "commands.json");
        private string WorkspacesFile => Path.Combine(AppDir, "workspaces.json");
        public string ProfileFile => Path.Combine(AppDir, "profile.ls");

        public LocalExecutor()
        {
            AppDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LineScriptTerminal");
            Directory.CreateDirectory(AppDir);

            CurrentDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (string.IsNullOrEmpty(CurrentDir) || !Directory.Exists(CurrentDir))
                CurrentDir = AppDomain.CurrentDomain.BaseDirectory;

            LoadWorkspaces();
            LoadCommands();

            if (!Workspaces.Contains(CurrentDir, StringComparer.OrdinalIgnoreCase))
            {
                Workspaces.Insert(0, CurrentDir);
                SaveWorkspaces();
            }

            if (!File.Exists(ProfileFile))
            {
                try
                {
                    File.WriteAllText(ProfileFile,
                        "# LineScript Profile — автозагрузка алиасов и параметров\r\n" +
                        "Set-Alias ll Get-ChildItem\r\n" +
                        "Set-Alias ls Get-ChildItem\r\n" +
                        "Set-Alias cat Get-Content\r\n" +
                        "Set-Alias rm Remove-Item\r\n",
                        new UTF8Encoding(false));
                }
                catch { }
            }
        }

        public void SetInitialDirectory(string path)
        {
            if (Directory.Exists(path))
            {
                CurrentDir = Path.GetFullPath(path);
                if (!Workspaces.Contains(CurrentDir, StringComparer.OrdinalIgnoreCase))
                {
                    Workspaces.Insert(0, CurrentDir);
                    SaveWorkspaces();
                }
            }
        }

        public string Execute(string input)
        {
            var sb = new StringBuilder();
            try
            {
                ExecuteAsync(input, (chunk, _) => sb.Append(chunk), CancellationToken.None).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                sb.AppendLine("Ошибка: " + ex.Message);
            }
            return sb.ToString();
        }

        public void SendInput(string text)
        {
            lock (_processLock)
            {
                if (_activeProcess != null && !_activeProcess.HasExited)
                {
                    try
                    {
                        _activeProcess.StandardInput.WriteLine(text);
                        _activeProcess.StandardInput.Flush();
                    }
                    catch { }
                }
            }
        }

        public void KillActiveProcess()
        {
            lock (_processLock)
            {
                if (_activeProcess != null && !_activeProcess.HasExited)
                {
                    try
                    {
                        _activeProcess.Kill(entireProcessTree: true);
                    }
                    catch { }
                }
                _activeProcess = null;
            }
        }

        private void LoadWorkspaces()
        {
            try
            {
                if (File.Exists(WorkspacesFile))
                    Workspaces = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(WorkspacesFile, Encoding.UTF8)) ?? new();
            }
            catch { }
        }

        private void SaveWorkspaces()
        {
            try
            {
                File.WriteAllText(WorkspacesFile, JsonSerializer.Serialize(Workspaces, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
            }
            catch { }
        }

        private void LoadCommands()
        {
            try
            {
                if (File.Exists(CommandsFile))
                {
                    var l = JsonSerializer.Deserialize<List<CustomCommand>>(File.ReadAllText(CommandsFile, Encoding.UTF8));
                    if (l != null)
                        foreach (var c in l) Commands[c.Name] = c;
                }
            }
            catch { }
        }

        private void SaveCommands()
        {
            try
            {
                File.WriteAllText(CommandsFile, JsonSerializer.Serialize(Commands.Values.ToList(), new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
            }
            catch { }
        }

        public bool IsLineScript(string input)
        {
            return Regex.IsMatch(input.Trim(), @"^(?:\[\d+\]\s*)?-[a-zA-Z][\w-]*-(?:[\s\S]*?)-/-", RegexOptions.Multiline);
        }

        public async Task ExecuteAsync(string rawInput, Action<string, Color> emit, CancellationToken ct)
        {
            SessionCommands++;
            string input = rawInput.Trim();
            if (string.IsNullOrEmpty(input)) return;

            // 1. Проверка на смену режима шелла: mode ps / mode cmd
            var modeMatch = Regex.Match(input, @"^(?:mode|shell)\s+(ps|powershell|cmd)$", RegexOptions.IgnoreCase);
            if (modeMatch.Success)
            {
                string targetMode = modeMatch.Groups[1].Value.ToLowerInvariant();
                DefaultShell = targetMode == "cmd" ? ShellMode.Cmd : ShellMode.PowerShell;
                emit($"[OK] Режим оболочки переключён на: {DefaultShell}\r\n", ColorSuccess);
                return;
            }

            // 2. Проверка на команды навигации: cd, chdir, Set-Location, sl
            var cdMatch = Regex.Match(input, @"^(?:cd|chdir|Set-Location|sl)\s*(.*)$", RegexOptions.IgnoreCase);
            if (cdMatch.Success)
            {
                string target = cdMatch.Groups[1].Value.Trim().Trim('"', '\'');
                if (string.IsNullOrEmpty(target))
                {
                    emit(CurrentDir + "\r\n", ColorDefault);
                    return;
                }
                ChangeDirectory(target, emit);
                return;
            }

            if (Regex.IsMatch(input, @"^(?:pwd|Get-Location)$", RegexOptions.IgnoreCase))
            {
                emit(CurrentDir + "\r\n", ColorDefault);
                return;
            }

            // 3. Если это блок или набор команд LineScript
            if (IsLineScript(input))
            {
                var commands = ParseLineScriptCommands(input);
                if (commands.Count == 0)
                {
                    emit("Ошибка: синтаксис LineScript: -тип- тело -/-\r\n", ColorError);
                    return;
                }

                // Сортировка по порядку [N]
                var ordered = commands.OrderBy(c => c.Order).ToList();
                for (int i = 0; i < ordered.Count; i++)
                {
                    if (ct.IsCancellationRequested) break;
                    var cmd = ordered[i];
                    if (ordered.Count > 1)
                        emit($"\r\n>>> [{i + 1}/{ordered.Count}] -{cmd.Type}- ...\r\n", ColorInfo);

                    await ExecuteSingleLineScriptAsync(cmd.Type, cmd.Body, emit, ct);
                }
                return;
            }

            // 4. Если введен многострочный скрипт без тегов LineScript
            if (input.Contains('\n'))
            {
                if (DefaultShell == ShellMode.PowerShell)
                    await RunPowerShellAsync(input, emit, ct);
                else
                    await RunCmdAsync(input, emit, ct);
                return;
            }

            // 5. Однострочная нативная команда
            if (DefaultShell == ShellMode.PowerShell)
                await RunPowerShellAsync(input, emit, ct);
            else
                await RunCmdAsync(input, emit, ct);
        }

        private List<ParsedLineScriptCommand> ParseLineScriptCommands(string input)
        {
            var list = new List<ParsedLineScriptCommand>();
            var regex = new Regex(@"(?:\[(?<order>\d+)\]\s*)?-?(?<type>[a-zA-Z][\w-]*)-(?<body>[\s\S]*?)-/-", RegexOptions.Multiline);
            int seq = 0;
            foreach (Match m in regex.Matches(input))
            {
                int order = seq++;
                if (m.Groups["order"].Success && int.TryParse(m.Groups["order"].Value, out int o))
                    order = o;

                string type = m.Groups["type"].Value.ToLowerInvariant();
                string body = m.Groups["body"].Value;

                // Разэкранирование \--/-\- -> -/-
                body = body.Replace("\\--/-\\--", "-/-").Replace("\\--/-\\-", "-/-");

                list.Add(new ParsedLineScriptCommand
                {
                    Order = order,
                    Type = type,
                    Body = body.Trim(),
                    Raw = m.Value
                });
            }
            return list;
        }

        private async Task ExecuteSingleLineScriptAsync(string type, string body, Action<string, Color> emit, CancellationToken ct)
        {
            try
            {
                switch (type)
                {
                    case "cmd":
                        await RunCmdAsync(body, emit, ct);
                        break;
                    case "ps":
                        await RunPowerShellAsync(body, emit, ct);
                        break;
                    case "git":
                        await RunProcessStreamingAsync("git.exe", body, emit, ct);
                        break;
                    case "file":
                        ExecuteReadFile(body, emit);
                        break;
                    case "file-up":
                        ExecuteWriteFile(body, emit);
                        break;
                    case "file-bin":
                        ExecuteBinaryFile(body, emit);
                        break;
                    case "del-file":
                        ExecuteDeleteFile(body, emit);
                        break;
                    case "patch":
                        ExecutePatch(body, emit);
                        break;
                    case "grep":
                        ExecuteGrep(body, emit);
                        break;
                    case "undo":
                        ExecuteUndo(body, emit);
                        break;
                    case "add-command":
                        ExecuteAddCommand(body, emit);
                        break;
                    case "cd":
                        ChangeDirectory(body.Trim().Trim('"'), emit);
                        break;
                    case "list-workspaces":
                        ExecuteListWorkspaces(emit);
                        break;
                    case "server":
                        ExecuteServerStatus(emit);
                        break;
                    case "help":
                        ExecuteHelp(emit);
                        break;
                    default:
                        if (Commands.TryGetValue(type, out var cc))
                        {
                            string line = cc.Template.Replace("{args}", body.Trim());
                            if (string.Equals(cc.Environment, "CMD", StringComparison.OrdinalIgnoreCase))
                                await RunCmdAsync(line, emit, ct);
                            else
                                await RunPowerShellAsync(line, emit, ct);
                        }
                        else
                        {
                            emit($"[STATUS: ERROR: ERR_UNKNOWN_TYPE] Неизвестный тип команды '{type}'\r\n", ColorError);
                        }
                        break;
                }
            }
            catch (Exception ex)
            {
                emit($"[STATUS: ERROR] Исключение при выполнении: {ex.Message}\r\n", ColorError);
            }
        }

        public async Task RunPowerShellAsync(string command, Action<string, Color> emit, CancellationToken ct)
        {
            string prelude =
                "[Console]::OutputEncoding = [Text.Encoding]::UTF8; " +
                "$OutputEncoding = [Text.Encoding]::UTF8; " +
                "$ProgressPreference = 'SilentlyContinue'; " +
                "if (Test-Path '" + ProfileFile.Replace("'", "''") + "') { . '" + ProfileFile.Replace("'", "''") + "' }; ";

            string full = prelude + command;
            string b64 = Convert.ToBase64String(Encoding.Unicode.GetBytes(full));
            string args = "-NoLogo -NoProfile -ExecutionPolicy Bypass -EncodedCommand " + b64;

            await RunProcessStreamingAsync("powershell.exe", args, emit, ct);
        }

        public async Task RunCmdAsync(string command, Action<string, Color> emit, CancellationToken ct)
        {
            string bat = Path.Combine(Path.GetTempPath(), "ls_" + Guid.NewGuid().ToString("N") + ".bat");
            try
            {
                File.WriteAllText(bat, "@echo off\r\nchcp 65001 >nul\r\n" + command + "\r\n", new UTF8Encoding(false));
                await RunProcessStreamingAsync("cmd.exe", "/c \"" + bat + "\"", emit, ct);
            }
            finally
            {
                try { if (File.Exists(bat)) File.Delete(bat); } catch { }
            }
        }

        public async Task RunProcessStreamingAsync(string file, string args, Action<string, Color> emit, CancellationToken ct)
        {
            var psi = new ProcessStartInfo
            {
                FileName = file,
                Arguments = args,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = CurrentDir
            };

            Process proc;
            try
            {
                proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
            }
            catch (Exception ex)
            {
                emit($"Ошибка инициализации процесса '{file}': {ex.Message}\r\n", ColorError);
                return;
            }

            var tcs = new TaskCompletionSource<bool>();

            proc.OutputDataReceived += (_, e) =>
            {
                if (e.Data != null) emit(e.Data + "\r\n", ColorDefault);
            };

            proc.ErrorDataReceived += (_, e) =>
            {
                if (e.Data != null) emit(e.Data + "\r\n", ColorError);
            };

            proc.Exited += (_, _) => tcs.TrySetResult(true);

            lock (_processLock)
            {
                _activeProcess = proc;
            }

            using var reg = ct.Register(() =>
            {
                KillActiveProcess();
                tcs.TrySetCanceled();
            });

            try
            {
                if (!proc.Start())
                {
                    emit($"Не удалось запустить процесс '{file}'\r\n", ColorError);
                    return;
                }

                proc.BeginOutputReadLine();
                proc.BeginErrorReadLine();

                await tcs.Task;
                LastExitCode = proc.ExitCode;
            }
            catch (OperationCanceledException)
            {
                emit("\r\n^C [Процесс прерван пользователем]\r\n", ColorWarn);
                LastExitCode = -1;
            }
            catch (Exception ex)
            {
                emit($"[Ошибка исполнения '{file}']: {ex.Message}\r\n", ColorError);
                LastExitCode = -1;
            }
            finally
            {
                lock (_processLock)
                {
                    if (_activeProcess == proc) _activeProcess = null;
                }
                proc.Dispose();
            }
        }

        private void ChangeDirectory(string target, Action<string, Color> emit)
        {
            target = target.Trim().Trim('"', '\'');
            string path;
            if (target == "..")
                path = Path.GetDirectoryName(CurrentDir) ?? CurrentDir;
            else if (target == "." || string.IsNullOrEmpty(target))
                path = CurrentDir;
            else if (Path.IsPathRooted(target))
                path = target;
            else
                path = Path.GetFullPath(Path.Combine(CurrentDir, target));

            if (Directory.Exists(path))
            {
                CurrentDir = path;
                if (!Workspaces.Contains(path, StringComparer.OrdinalIgnoreCase))
                {
                    Workspaces.Add(path);
                    SaveWorkspaces();
                }
                emit($"Рабочая директория: {CurrentDir}\r\n", ColorInfo);
            }
            else
            {
                emit($"cd: путь не найден: {target}\r\n", ColorError);
            }
        }

        private void ExecuteReadFile(string body, Action<string, Color> emit)
        {
            int nl = body.IndexOf('\n');
            string first = nl >= 0 ? body.Substring(0, nl).Trim() : body.Trim();
            string rest = nl >= 0 ? body.Substring(nl + 1).Trim() : "";
            string path = ResolvePath(first);

            if (!File.Exists(path))
            {
                emit($"[STATUS: ERROR: ERR_FILE_NOT_FOUND] Файл не найден: {path}\r\n", ColorError);
                return;
            }

            var range = Regex.Match(rest, @"^(\d*)-(\d*)$");
            int start = 1, end = int.MaxValue;
            if (range.Success)
            {
                if (range.Groups[1].Value.Length > 0) start = int.Parse(range.Groups[1].Value);
                if (range.Groups[2].Value.Length > 0) end = int.Parse(range.Groups[2].Value);
            }

            var lines = File.ReadAllLines(path);
            var sb = new StringBuilder($"[STATUS: OK] Файл: {path} (всего строк: {lines.Length})\r\n");
            int last = Math.Min(end, lines.Length);
            for (int i = start; i <= last; i++)
                sb.AppendLine($"{i}: {lines[i - 1]}");

            emit(sb.ToString(), ColorDefault);
        }

        private void ExecuteWriteFile(string body, Action<string, Color> emit)
        {
            int nl = body.IndexOf('\n');
            if (nl < 0)
            {
                emit("[STATUS: ERROR: ERR_SYNTAX] Для записи укажите путь на 1-й строке, а содержимое ниже.\r\n", ColorError);
                return;
            }

            string path = ResolvePath(body.Substring(0, nl).Trim());
            string content = body.Substring(nl + 1);

            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            string bak = "";
            if (File.Exists(path)) bak = CreateBackup(path);

            File.WriteAllText(path, content, new UTF8Encoding(false));
            emit($"[STATUS: OK] Сохранено: {path} ({content.Length} симв.)" + (string.IsNullOrEmpty(bak) ? "" : $" (бэкап: {Path.GetFileName(bak)})") + "\r\n", ColorSuccess);
        }

        private void ExecuteBinaryFile(string body, Action<string, Color> emit)
        {
            var lines = body.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length < 2)
            {
                emit("[STATUS: ERROR: ERR_SYNTAX] Формат: write\\n<путь>\\n<base64> или read\\n<путь>\r\n", ColorError);
                return;
            }

            string op = lines[0].Trim().ToLowerInvariant();
            string path = ResolvePath(lines[1].Trim());

            if (op == "write")
            {
                if (lines.Length < 3)
                {
                    emit("[STATUS: ERROR: ERR_SYNTAX] Отсутствуют base64 данные для записи.\r\n", ColorError);
                    return;
                }
                string b64 = string.Concat(lines.Skip(2)).Trim();
                byte[] bytes = Convert.FromBase64String(b64);

                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                string bak = "";
                if (File.Exists(path)) bak = CreateBackup(path);

                File.WriteAllBytes(path, bytes);
                emit($"[STATUS: OK] Бинарный файл сохранён: {path} ({bytes.Length} байт)" + (string.IsNullOrEmpty(bak) ? "" : $" (бэкап: {Path.GetFileName(bak)})") + "\r\n", ColorSuccess);
            }
            else if (op == "read")
            {
                if (!File.Exists(path))
                {
                    emit($"[STATUS: ERROR: ERR_FILE_NOT_FOUND] Файл не найден: {path}\r\n", ColorError);
                    return;
                }
                byte[] bytes = File.ReadAllBytes(path);
                string b64 = Convert.ToBase64String(bytes);
                emit($"[STATUS: OK] Размер: {bytes.Length} байт\r\n{b64}\r\n", ColorDefault);
            }
            else
            {
                emit($"[STATUS: ERROR: ERR_SYNTAX] Неизвестная операция '{op}'. Ожидается 'read' или 'write'.\r\n", ColorError);
            }
        }

        private void ExecuteDeleteFile(string body, Action<string, Color> emit)
        {
            string path = ResolvePath(body.Trim().Split('\n')[0].Trim());
            if (!File.Exists(path))
            {
                emit($"[STATUS: ERROR: ERR_FILE_NOT_FOUND] Файл не найден: {path}\r\n", ColorError);
                return;
            }
            string bak = CreateBackup(path);
            File.Delete(path);
            emit($"[STATUS: OK] Файл удалён: {path} (сохранён бэкап: {Path.GetFileName(bak)})\r\n", ColorWarn);
        }

        private void ExecutePatch(string body, Action<string, Color> emit)
        {
            int nl = body.IndexOf('\n');
            if (nl < 0)
            {
                emit("[STATUS: ERROR: ERR_SYNTAX] Укажите путь к файлу и блоки SEARCH/REPLACE.\r\n", ColorError);
                return;
            }

            string path = ResolvePath(body.Substring(0, nl).Trim());
            string rest = body.Substring(nl + 1);

            if (!File.Exists(path))
            {
                emit($"[STATUS: ERROR: ERR_FILE_NOT_FOUND] Файл не найден: {path}\r\n", ColorError);
                return;
            }

            string text = File.ReadAllText(path).Replace("\r\n", "\n");
            var matches = Regex.Matches(rest, @"<{3,10}\s*SEARCH\r?\n([\s\S]*?)\r?\n={3,10}\r?\n([\s\S]*?)\r?\n>{0,10}\s*REPLACE", RegexOptions.Multiline);
            if (matches.Count == 0)
            {
                emit("[STATUS: ERROR: ERR_SYNTAX] Блоки SEARCH/REPLACE не обнаружены.\r\n", ColorError);
                return;
            }

            string bak = CreateBackup(path);
            int count = 0;
            foreach (Match m in matches)
            {
                string search = m.Groups[1].Value.Replace("\r\n", "\n");
                string replace = m.Groups[2].Value.Replace("\r\n", "\n");

                if (!text.Contains(search))
                {
                    emit($"[STATUS: ERROR: ERR_PATCH_MISMATCH] Блок SEARCH не найден в {Path.GetFileName(path)}:\r\n{search}\r\n", ColorError);
                    return;
                }

                text = text.Replace(search, replace);
                count++;
            }

            File.WriteAllText(path, text.Replace("\n", "\r\n"), new UTF8Encoding(false));
            emit($"[STATUS: OK] Применено {count} изменений в {path} (бэкап: {Path.GetFileName(bak)})\r\n", ColorSuccess);
        }

        private void ExecuteGrep(string body, Action<string, Color> emit)
        {
            var lines = body.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length == 0)
            {
                emit("[STATUS: ERROR: ERR_SYNTAX] Укажите строку для поиска.\r\n", ColorError);
                return;
            }

            string pattern, root;
            if (lines.Length >= 2 && (Directory.Exists(ResolvePath(lines[0])) || File.Exists(ResolvePath(lines[0]))))
            {
                root = ResolvePath(lines[0]);
                pattern = lines[1];
            }
            else
            {
                pattern = lines[0];
                root = CurrentDir;
            }

            var sb = new StringBuilder($"[STATUS: OK] Поиск '{pattern}' в {Path.GetFileName(root)}:\r\n");
            int total = 0;

            if (File.Exists(root))
            {
                SearchFile(root, pattern, sb, ref total, 50);
            }
            else if (Directory.Exists(root))
            {
                foreach (var f in Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories))
                {
                    if (f.Contains("\\bin\\") || f.Contains("\\obj\\") || f.Contains("\\.ls_backup\\") || f.Contains("\\.git\\"))
                        continue;
                    if (f.EndsWith(".exe") || f.EndsWith(".dll") || f.EndsWith(".pdb"))
                        continue;

                    SearchFile(f, pattern, sb, ref total, 50);
                    if (total >= 50)
                    {
                        sb.AppendLine("...(достигнут лимит первых 50 совпадений)");
                        break;
                    }
                }
            }

            emit(total == 0 ? $"Ничего не найдено по '{pattern}'.\r\n" : sb.ToString(), ColorDefault);
        }

        private void SearchFile(string f, string pattern, StringBuilder sb, ref int total, int max)
        {
            try
            {
                var lines = File.ReadAllLines(f);
                for (int i = 0; i < lines.Length; i++)
                {
                    if (lines[i].IndexOf(pattern, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        sb.AppendLine($"[{Path.GetFileName(f)}:{i + 1}] {lines[i].Trim()}");
                        if (++total >= max) return;
                    }
                }
            }
            catch { }
        }

        private void ExecuteUndo(string body, Action<string, Color> emit)
        {
            string path = ResolvePath(body.Trim().Split('\n')[0].Trim());
            var dir = Path.GetDirectoryName(path) ?? CurrentDir;
            var bakDir = Path.Combine(dir, ".ls_backup");

            if (!Directory.Exists(bakDir))
            {
                emit($"[STATUS: ERROR] Папка бэкапов не найдена: {bakDir}\r\n", ColorError);
                return;
            }

            string name = Path.GetFileName(path);
            var files = new DirectoryInfo(bakDir).GetFiles($"{name}.*.bak");
            if (files.Length == 0)
            {
                emit($"[STATUS: ERROR] Резервные копии для '{name}' отсутствуют.\r\n", ColorError);
                return;
            }

            Array.Sort(files, (a, b) => b.CreationTime.CompareTo(a.CreationTime));
            var latest = files[0];

            if (File.Exists(path))
                File.Copy(path, Path.Combine(bakDir, $"{name}.pre_undo_{DateTime.Now:yyyyMMdd_HHmmss}.bak"), true);

            File.Copy(latest.FullName, path, true);
            emit($"[STATUS: OK] Восстановлен {path} из резервной копии: {latest.Name}\r\n", ColorSuccess);
        }

        private void ExecuteAddCommand(string body, Action<string, Color> emit)
        {
            int nl = body.IndexOf('\n');
            string name = (nl < 0 ? body : body.Substring(0, nl)).Trim();
            string rest = nl < 0 ? "" : body.Substring(nl + 1);

            if (!Regex.IsMatch(name, @"^[a-zA-Z][\w-]*$"))
            {
                emit($"[STATUS: ERROR: ERR_SYNTAX] Недопустимое имя команды '{name}'. Разрешены латинские буквы, цифры, дефис.\r\n", ColorError);
                return;
            }

            var cc = new CustomCommand { Name = name };
            bool hasTpl = false;

            foreach (var line in rest.Split('\n'))
            {
                var m = Regex.Match(line.Trim(), @"^(executable|template|install|environment)\s*:\s*""?([^""\r\n]+?)""?\s*$", RegexOptions.IgnoreCase);
                if (!m.Success) continue;

                switch (m.Groups[1].Value.ToLowerInvariant())
                {
                    case "template":
                        cc.Template = m.Groups[2].Value.Trim();
                        hasTpl = true;
                        break;
                    case "environment":
                        cc.Environment = m.Groups[2].Value.Trim().ToUpperInvariant();
                        break;
                    case "executable":
                        cc.Executable = m.Groups[2].Value.Trim();
                        break;
                    case "install":
                        cc.Install = m.Groups[2].Value.Trim();
                        break;
                }
            }

            if (!hasTpl && string.IsNullOrEmpty(cc.Executable))
            {
                emit($"[STATUS: ERROR: ERR_SYNTAX] Для команды '{name}' укажите template или executable.\r\n", ColorError);
                return;
            }

            Commands[name] = cc;
            SaveCommands();
            emit($"[STATUS: OK] Зарегистрирована команда '-{name}- ... -/-' (среда: {cc.Environment})\r\n", ColorSuccess);
        }

        private void ExecuteListWorkspaces(Action<string, Color> emit)
        {
            var sb = new StringBuilder("[STATUS: OK] Список рабочих папок:\r\n");
            foreach (var w in Workspaces)
                sb.AppendLine("  " + w + (string.Equals(w, CurrentDir, StringComparison.OrdinalIgnoreCase) ? "  [активная]" : ""));
            emit(sb.ToString(), ColorDefault);
        }

        private void ExecuteServerStatus(Action<string, Color> emit)
        {
            var up = DateTime.Now - StartTime;
            string text = string.Join("\r\n", new[]
            {
                "[STATUS: OK] LineScript Terminal v1.1",
                $"  Рабочая папка   : {CurrentDir}",
                $"  Оболочка по умол: {DefaultShell}",
                $"  Время работы    : {up:hh\\:mm\\:ss}",
                $"  Команд за сеанс : {SessionCommands}",
                $"  Кастомных команд: {Commands.Count}",
                $"  Каталог конфига : {AppDir}"
            }) + "\r\n";
            emit(text, ColorInfo);
        }

        private void ExecuteHelp(Action<string, Color> emit)
        {
            string text = string.Join("\r\n", new[]
            {
                "=== LineScript Terminal v1.1 Help ===",
                "Режимы оболочки:",
                "  mode ps                           - переключить оболочку по умолчанию на PowerShell",
                "  mode cmd                          - переключить оболочку по умолчанию на CMD",
                "",
                "Стандартные команды LineScript:",
                "  -cmd- <команда> -/-               - выполнить в CMD",
                "  -ps-  <скрипт> -/-                - выполнить в PowerShell",
                "  -git- <аргументы> -/-             - выполнить Git",
                "  -file- <путь>[\\nдиапазон] -/-     - прочитать файл",
                "  -file-up- <путь>\\n<текст> -/-     - записать файл (с созданием бэкапа)",
                "  -file-bin- <read|write>\\n... -/-   - чтение/запись бинарных файлов (Base64)",
                "  -del-file- <путь> -/-             - безопасное удаление (в .ls_backup)",
                "  -patch- <путь>\\nSEARCH/REPLACE -/- - контекстный патч",
                "  -grep- [путь\\n]<текст> -/-        - поиск по файлам",
                "  -undo- <путь> -/-                 - откат файла из .ls_backup",
                "  -add-command- <имя>\\n... -/-      - регистрация своей команды",
                "  -cd- <путь> -/-                   - смена директории",
                "  -list-workspaces- -/-             - список рабочих папок",
                "  -server- -/-                      - диагностика и статус",
                "  -help- -/-                        - эта справка",
                "",
                "Нативный ввод: любые команды CMD / PowerShell (dir, ls, Get-Process, npm, dotnet и т.д.)",
                "Горячие клавиши:",
                "  Ctrl+C - мгновенное прерывание запущенного процесса и возврат промпта",
                "  Ctrl+L - очистка экрана",
                "  Ctrl+R - поиск по истории команд",
                "  Tab    - автодополнение команд и путей к файлам",
                "  Перетаскивание файлов мышью в окно (Drag & Drop) вставляет путь"
            }) + "\r\n";
            emit(text, ColorDefault);
        }

        public string CreateBackup(string file)
        {
            try
            {
                if (!File.Exists(file)) return "";
                var dir = Path.GetDirectoryName(file) ?? CurrentDir;
                var bak = Path.Combine(dir, ".ls_backup");
                Directory.CreateDirectory(bak);
                string name = Path.GetFileName(file);
                string ts = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                string target = Path.Combine(bak, $"{name}.{ts}.bak");
                File.Copy(file, target, true);

                var files = new DirectoryInfo(bak).GetFiles("*.bak");
                if (files.Length > 30)
                {
                    Array.Sort(files, (a, b) => a.CreationTime.CompareTo(b.CreationTime));
                    for (int i = 0; i < files.Length - 30; i++)
                    {
                        try { files[i].Delete(); } catch { }
                    }
                }
                return target;
            }
            catch { return ""; }
        }

        public string ResolvePath(string path)
        {
            path = (path ?? "").Trim().Trim('"');
            if (Path.IsPathRooted(path)) return path;
            return Path.GetFullPath(Path.Combine(CurrentDir, path));
        }
    }
}
