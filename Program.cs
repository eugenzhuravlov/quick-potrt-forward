using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace PortForwarder;

public sealed class ForwardRule
{
    public int ListenPort { get; }
    public string TargetHost { get; }
    public int TargetPort { get; }

    private int _activeConnections;
    private long _totalConnections;
    private long _totalBytesSent;
    private long _totalBytesReceived;

    public int ActiveConnections => Volatile.Read(ref _activeConnections);
    public long TotalConnections => Volatile.Read(ref _totalConnections);
    public long TotalBytesSent => Volatile.Read(ref _totalBytesSent);
    public long TotalBytesReceived => Volatile.Read(ref _totalBytesReceived);

    public ForwardRule(int listenPort, string targetHost, int targetPort)
    {
        ListenPort = listenPort;
        TargetHost = targetHost;
        TargetPort = targetPort;
    }

    public void IncrementConnections()
    {
        Interlocked.Increment(ref _activeConnections);
        Interlocked.Increment(ref _totalConnections);
    }

    public void DecrementConnections()
    {
        Interlocked.Decrement(ref _activeConnections);
    }

    public void AddBytes(long sent, long received)
    {
        Interlocked.Add(ref _totalBytesSent, sent);
        Interlocked.Add(ref _totalBytesReceived, received);
    }

    public override string ToString() => $"{ListenPort} -> {TargetHost}:{TargetPort}";

    public static bool TryParse(string input, out ForwardRule? rule, out string errorMessage)
    {
        rule = null;
        errorMessage = string.Empty;

        if (string.IsNullOrWhiteSpace(input))
        {
            errorMessage = "Строка правила не может быть пустой.";
            return false;
        }

        var trimmed = input.Trim().Trim('"', '\'');
        int firstColon = trimmed.IndexOf(':');
        int lastColon = trimmed.LastIndexOf(':');

        if (firstColon <= 0 || lastColon <= firstColon)
        {
            errorMessage = $"Неверный формат '{input}'. Ожидается: <listen_port>:<target_host>:<target_port> (например: 2022:192.168.1.150:22)";
            return false;
        }

        string listenPortStr = trimmed[..firstColon];
        string targetHostStr = trimmed.Substring(firstColon + 1, lastColon - firstColon - 1);
        string targetPortStr = trimmed[(lastColon + 1)..];

        if (!int.TryParse(listenPortStr, out int listenPort) || listenPort < 1 || listenPort > 65535)
        {
            errorMessage = $"Некорректный порт для прослушивания '{listenPortStr}'. Допустимый диапазон: 1-65535.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(targetHostStr))
        {
            errorMessage = "Целевой хост или IP-адрес не может быть пустым.";
            return false;
        }

        if (targetHostStr.StartsWith('[') && targetHostStr.EndsWith(']'))
        {
            targetHostStr = targetHostStr[1..^1];
        }

        if (!int.TryParse(targetPortStr, out int targetPort) || targetPort < 1 || targetPort > 65535)
        {
            errorMessage = $"Некорректный целевой порт '{targetPortStr}'. Допустимый диапазон: 1-65535.";
            return false;
        }

        // Проверка на петлю проброса (Self-Loop) на этапе парсинга
        if (listenPort == targetPort && IsLocalHostOrIp(targetHostStr))
        {
            errorMessage = $"Обнаружена петля проброса (Self-Loop)! Порт {listenPort} пробрасывается на {targetHostStr}:{targetPort} (этот же компьютер). Это вызовет бесконечный цикл подключений на самого себя.";
            return false;
        }

        rule = new ForwardRule(listenPort, targetHostStr, targetPort);
        return true;
    }

    public static bool IsLocalHostOrIp(string host)
    {
        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) ||
            host == "127.0.0.1" || host == "::1" || host == "0.0.0.0" || host == "::")
        {
            return true;
        }

        try
        {
            if (IPAddress.TryParse(host, out var ip))
            {
                if (IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any))
                    return true;

                // Проверяем локальные IP-адреса сетевых интерфейсов машины
                foreach (var iface in NetworkInterface.GetAllNetworkInterfaces())
                {
                    var props = iface.GetIPProperties();
                    foreach (var addr in props.UnicastAddresses)
                    {
                        if (addr.Address.Equals(ip))
                            return true;
                    }
                }
            }
        }
        catch { }

        return false;
    }
}

public static class EventViewerLogger
{
    private const string LogName = "Application";
    private static readonly string EventSource = "PortForwarder";
    private static readonly bool HasCustomSource = false;
    private static readonly object LogLock = new();

    static EventViewerLogger()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\EventLog\{LogName}\{EventSource}");
                if (key != null)
                {
                    HasCustomSource = true;
                }
                else
                {
                    try
                    {
                        EventLog.CreateEventSource(new EventSourceCreationData(EventSource, LogName));
                        HasCustomSource = true;
                    }
                    catch
                    {
                        HasCustomSource = false;
                    }
                }
            }
        }
        catch
        {
            HasCustomSource = false;
        }
    }

    public static void LogInformation(string message, int eventId = 1000) => WriteToEventLog(message, EventLogEntryType.Information, eventId);
    public static void LogWarning(string message, int eventId = 2000) => WriteToEventLog(message, EventLogEntryType.Warning, eventId);
    public static void LogError(string message, int eventId = 3000) => WriteToEventLog(message, EventLogEntryType.Error, eventId);

    private static void WriteToEventLog(string message, EventLogEntryType entryType, int eventId)
    {
        if (!OperatingSystem.IsWindows()) return;

        lock (LogLock)
        {
            try
            {
                string source = HasCustomSource ? EventSource : LogName;
                string formattedMessage = HasCustomSource ? message : $"[PortForwarder] {message}";
                EventLog.WriteEntry(source, formattedMessage, entryType, eventId);
            }
            catch (Exception ex)
            {
                ConsoleLogger.LogConsole(ConsoleColor.DarkGray, "EVENTLOG-ERR", $"Не удалось записать в EventViewer: {ex.Message}");
            }
        }
    }
}

public static class ConsoleLogger
{
    private static readonly object ConsoleLock = new();

    public static void Log(ConsoleColor color, string tag, string message)
    {
        lock (ConsoleLock)
        {
            string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.Write($"[{timestamp}] ");
            Console.ForegroundColor = color;
            Console.Write($"[{tag,-10}] ");
            Console.ResetColor();
            Console.WriteLine(message);
        }
    }

    public static void LogConsole(ConsoleColor color, string tag, string message) => Log(color, tag, message);

    public static void PrintHeader(IEnumerable<ForwardRule> rules, int idleTimeoutSeconds)
    {
        lock (ConsoleLock)
        {
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("================================================================================");
            Console.WriteLine("                     TCP PORT FORWARDER (ПРОБРОС ПОРТОВ)                       ");
            Console.WriteLine("================================================================================");
            Console.ResetColor();
            Console.WriteLine("Режим прослушивания : Все сетевые интерфейсы (0.0.0.0 / Dual-Stack IPv4+IPv6)");
            Console.WriteLine($"Keep-Alive защита  : TCP Probes (15с/5с), Idle-Timeout: {(idleTimeoutSeconds > 0 ? $"{idleTimeoutSeconds}с" : "выключен")}");
            Console.WriteLine("Защита от петель    : Включена (блокировка Self-Loop и повторных перенаправлений)");
            Console.WriteLine("Логирование         : Консоль + Windows EventViewer -> Application");
            Console.WriteLine("--------------------------------------------------------------------------------");
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("Активные правила проброса:");
            foreach (var rule in rules)
            {
                Console.WriteLine($"  * Порт {rule.ListenPort}  ==>  {rule.TargetHost}:{rule.TargetPort}");
            }
            Console.ResetColor();
            Console.WriteLine("--------------------------------------------------------------------------------");
            Console.WriteLine("Нажмите Ctrl+C для остановки. Нажмите 'S' для вывода текущей статистики.");
            Console.WriteLine("================================================================================");
            Console.WriteLine();
        }
    }

    public static void PrintStatus(IReadOnlyList<ForwardRule> rules)
    {
        lock (ConsoleLock)
        {
            Console.WriteLine();
            Console.ForegroundColor = ConsoleColor.Magenta;
            Console.WriteLine("----------------------------- ТЕКУЩИЙ СТАТУС -----------------------------");
            int totalActive = 0;
            long totalForwarded = 0;
            foreach (var r in rules)
            {
                totalActive += r.ActiveConnections;
                totalForwarded += r.TotalConnections;
                Console.WriteLine($"  [{r.ListenPort} -> {r.TargetHost}:{r.TargetPort}]");
                Console.WriteLine($"    Активных подключений: {r.ActiveConnections,4} | Всего проброшено: {r.TotalConnections,6} | Трафик: {FormatBytes(r.TotalBytesSent + r.TotalBytesReceived)}");
            }
            Console.WriteLine($"  ИТОГО: Активных: {totalActive} | Всего проброшено: {totalForwarded}");
            Console.WriteLine("--------------------------------------------------------------------------");
            Console.ResetColor();
            Console.WriteLine();
        }
    }

    public static string FormatBytes(long bytes)
    {
        string[] sizes = { "B", "KB", "MB", "GB", "TB" };
        double len = bytes;
        int order = 0;
        while (len >= 1024 && order < sizes.Length - 1)
        {
            order++;
            len /= 1024;
        }
        return $"{len:0.##} {sizes[order]}";
    }
}

public sealed class ForwardingService : IAsyncDisposable
{
    private readonly ForwardRule _rule;
    private readonly int _idleTimeoutSeconds;
    private readonly CancellationTokenSource _cts = new();
    private TcpListener? _listener;
    private Task? _listenerTask;
    private long _connectionCounter;

    public ForwardRule Rule => _rule;

    public ForwardingService(ForwardRule rule, int idleTimeoutSeconds = 120)
    {
        _rule = rule;
        _idleTimeoutSeconds = idleTimeoutSeconds;
    }

    public void Start()
    {
        try
        {
            _listener = new TcpListener(IPAddress.IPv6Any, _rule.ListenPort);
            _listener.Server.DualMode = true;
            _listener.Start();
        }
        catch
        {
            _listener = new TcpListener(IPAddress.Any, _rule.ListenPort);
            _listener.Start();
        }

        string startMsg = $"Служба запущена: порт {_rule.ListenPort} на всех интерфейсах -> {_rule.TargetHost}:{_rule.TargetPort}";
        ConsoleLogger.Log(ConsoleColor.Green, "LISTENER", startMsg);
        EventViewerLogger.LogInformation(startMsg, 1001);

        _listenerTask = Task.Run(ListenLoopAsync);
    }

    private async Task ListenLoopAsync()
    {
        if (_listener == null) return;

        while (!_cts.IsCancellationRequested)
        {
            try
            {
                var client = await _listener.AcceptTcpClientAsync(_cts.Token);
                long connId = Interlocked.Increment(ref _connectionCounter);
                _ = Task.Run(() => HandleClientAsync(client, connId, _cts.Token));
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                if (!_cts.IsCancellationRequested)
                {
                    string err = $"Ошибка приема подключения на порту {_rule.ListenPort}: {ex.Message}";
                    ConsoleLogger.Log(ConsoleColor.Red, "ERROR", err);
                    EventViewerLogger.LogError(err, 2001);
                }
            }
        }
    }

    private void ConfigureSocketOptions(Socket socket)
    {
        try
        {
            socket.NoDelay = true; // Отключаем алгоритм Nagle для отзывчивости

            // Включаем TCP Keep-Alive зонды
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);

            // Настройка таймингов Keep-Alive в Windows (.NET 9)
            socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime, 15);      // Первый зонд через 15 секунд простоя
            socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval, 5);  // Повторные зонды каждые 5 секунд
            socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveRetryCount, 3); // 3 попытки, затем разрыв мертвого сокета
        }
        catch { }
    }

    private async Task HandleClientAsync(TcpClient client, long connId, CancellationToken globalCt)
    {
        _rule.IncrementConnections();
        var sw = Stopwatch.StartNew();
        string clientEp = client.Client.RemoteEndPoint?.ToString() ?? "Unknown";

        string connectMsg = $"[#{connId}] Подключен клиент: {clientEp} -> {_rule.TargetHost}:{_rule.TargetPort} (Слушаем :{_rule.ListenPort}) | Активных: {_rule.ActiveConnections} | Всего: {_rule.TotalConnections}";
        ConsoleLogger.Log(ConsoleColor.Green, "CONNECT", connectMsg);
        EventViewerLogger.LogInformation(connectMsg, 1002);

        using (client)
        using (var targetClient = new TcpClient())
        using (var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(globalCt))
        {
            ConfigureSocketOptions(client.Client);

            try
            {
                // Подключение к целевому серверу с таймаутом
                using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(sessionCts.Token);
                connectCts.CancelAfter(TimeSpan.FromSeconds(10));

                await targetClient.ConnectAsync(_rule.TargetHost, _rule.TargetPort, connectCts.Token);
                ConfigureSocketOptions(targetClient.Client);

                // Защита во время выполнения: если целевой клиент подключился обратно к нашему же листенеpy
                if (targetClient.Client.RemoteEndPoint is IPEndPoint targetRemoteEp &&
                    targetRemoteEp.Port == _rule.ListenPort &&
                    ForwardRule.IsLocalHostOrIp(targetRemoteEp.Address.ToString()))
                {
                    throw new InvalidOperationException("Обнаружена петля соединения (Self-loop connection loop)! Разрыв соединения.");
                }

                using var clientStream = client.GetStream();
                using var targetStream = targetClient.GetStream();

                long lastActivityTicks = DateTime.UtcNow.Ticks;
                void UpdateActivity() => Interlocked.Exchange(ref lastActivityTicks, DateTime.UtcNow.Ticks);

                // Фоновый сторож неактивности (Idle Timeout Watchdog)
                Task? idleWatchdogTask = null;
                if (_idleTimeoutSeconds > 0)
                {
                    idleWatchdogTask = Task.Run(async () =>
                    {
                        var timeoutTicks = TimeSpan.FromSeconds(_idleTimeoutSeconds).Ticks;
                        while (!sessionCts.Token.IsCancellationRequested)
                        {
                            await Task.Delay(1000, sessionCts.Token);
                            long idleTicks = DateTime.UtcNow.Ticks - Interlocked.Read(ref lastActivityTicks);
                            if (idleTicks > timeoutTicks)
                            {
                                // Закрываем зависшее keep-alive соединение по таймауту неактивности
                                sessionCts.Cancel();
                                break;
                            }
                        }
                    }, sessionCts.Token);
                }

                var clientToTarget = RelayTrafficAsync(clientStream, targetStream, sessionCts.Token, UpdateActivity);
                var targetToClient = RelayTrafficAsync(targetStream, clientStream, sessionCts.Token, UpdateActivity);

                // Ждем завершения передачи в любую из сторон
                var completedTask = await Task.WhenAny(clientToTarget, targetToClient);

                // КРИТИЧЕСКИЙ FIX ДЛЯ KEEP-ALIVE:
                // Как только одна сторона завершила передачу (или разорвала связь), немедленно отменяем токен сессии
                // и принудительно гасим сокеты, чтобы вторая сторона не висела вечно!
                sessionCts.Cancel();

                try { client.Client.Shutdown(SocketShutdown.Both); } catch { }
                try { targetClient.Client.Shutdown(SocketShutdown.Both); } catch { }

                long sent = 0;
                long received = 0;
                try { sent = await clientToTarget; } catch { }
                try { received = await targetToClient; } catch { }

                if (idleWatchdogTask != null)
                {
                    try { await idleWatchdogTask; } catch { }
                }

                _rule.AddBytes(sent, received);
                sw.Stop();
                _rule.DecrementConnections();

                string disconnectMsg = $"[#{connId}] Отключен клиент: {clientEp} (Правило :{_rule.ListenPort} -> {_rule.TargetHost}:{_rule.TargetPort}) | Длительность: {sw.Elapsed.TotalSeconds:F1}с | Трафик: ↑ {ConsoleLogger.FormatBytes(sent)}, ↓ {ConsoleLogger.FormatBytes(received)} | Активных: {_rule.ActiveConnections}";
                ConsoleLogger.Log(ConsoleColor.Yellow, "DISCONNECT", disconnectMsg);
                EventViewerLogger.LogInformation(disconnectMsg, 1003);
            }
            catch (Exception ex)
            {
                sw.Stop();
                _rule.DecrementConnections();

                string errMsg = $"[#{connId}] Ошибка/таймаут сессии {clientEp} -> {_rule.TargetHost}:{_rule.TargetPort}: {ex.Message} | Активных: {_rule.ActiveConnections}";
                ConsoleLogger.Log(ConsoleColor.Red, "ERROR", errMsg);
                EventViewerLogger.LogError(errMsg, 2002);
            }
        }
    }

    private static async Task<long> RelayTrafficAsync(NetworkStream source, NetworkStream destination, CancellationToken ct, Action onActivity)
    {
        byte[] buffer = new byte[65536]; // 64 KB буфер
        long totalBytes = 0;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                int bytesRead = await source.ReadAsync(buffer, ct);
                if (bytesRead == 0) break; // Нормальный EOF / закрытие половины сокета

                onActivity();
                await destination.WriteAsync(buffer.AsMemory(0, bytesRead), ct);
                onActivity();
                totalBytes += bytesRead;
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
        catch (SocketException) { }

        return totalBytes;
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try
        {
            _listener?.Stop();
        }
        catch { }

        if (_listenerTask != null)
        {
            try
            {
                await _listenerTask;
            }
            catch { }
        }

        string stopMsg = $"Служба на порту {_rule.ListenPort} остановлена. Всего обработано подключений: {_rule.TotalConnections}";
        ConsoleLogger.Log(ConsoleColor.DarkYellow, "STOP", stopMsg);
        EventViewerLogger.LogInformation(stopMsg, 1004);

        _cts.Dispose();
    }
}

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        var rules = new List<ForwardRule>();
        int idleTimeoutSeconds = 120; // 2 минуты таймаут простоя для Keep-Alive по умолчанию

        var rawRules = new List<string>();
        for (int i = 0; i < args.Length; i++)
        {
            var arg = args[i];

            if (arg.Equals("--help", StringComparison.OrdinalIgnoreCase) ||
                arg.Equals("-h", StringComparison.OrdinalIgnoreCase) ||
                arg.Equals("/?", StringComparison.OrdinalIgnoreCase))
            {
                PrintHelp();
                return 0;
            }

            if (arg.StartsWith("--idle-timeout=", StringComparison.OrdinalIgnoreCase))
            {
                if (int.TryParse(arg["--idle-timeout=".Length..], out int t))
                    idleTimeoutSeconds = t;
                continue;
            }

            if (arg.Equals("--idle-timeout", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                if (int.TryParse(args[++i], out int t))
                    idleTimeoutSeconds = t;
                continue;
            }

            rawRules.Add(arg);
        }

        if (rawRules.Count == 0)
        {
            string defaultArg = "2022:192.168.1.150:22";
            if (ForwardRule.TryParse(defaultArg, out var defaultRule, out _))
            {
                rules.Add(defaultRule!);
            }
        }
        else
        {
            foreach (var ruleStr in rawRules)
            {
                if (!ForwardRule.TryParse(ruleStr, out var rule, out string error))
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine($"Ошибка в параметре '{ruleStr}': {error}");
                    Console.ResetColor();
                    Console.WriteLine();
                    PrintHelp();
                    return 1;
                }

                if (rules.Any(r => r.ListenPort == rule!.ListenPort))
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine($"Ошибка: Порт прослушивания {rule!.ListenPort} указан несколько раз!");
                    Console.ResetColor();
                    return 1;
                }

                rules.Add(rule!);
            }
        }

        ConsoleLogger.PrintHeader(rules, idleTimeoutSeconds);

        var services = new List<ForwardingService>();
        try
        {
            foreach (var rule in rules)
            {
                var service = new ForwardingService(rule, idleTimeoutSeconds);
                service.Start();
                services.Add(service);
            }
        }
        catch (Exception ex)
        {
            ConsoleLogger.Log(ConsoleColor.Red, "FATAL", $"Критическая ошибка запуска прокси: {ex.Message}");
            EventViewerLogger.LogError($"Критическая ошибка запуска прокси: {ex.Message}", 3001);
            foreach (var svc in services)
            {
                await svc.DisposeAsync();
            }
            return 1;
        }

        var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (s, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        var monitorTask = Task.Run(async () =>
        {
            while (!cts.Token.IsCancellationRequested)
            {
                try
                {
                    if (Console.KeyAvailable)
                    {
                        var key = Console.ReadKey(intercept: true);
                        if (key.Key == ConsoleKey.S)
                        {
                            ConsoleLogger.PrintStatus(rules);
                        }
                    }
                    await Task.Delay(200, cts.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch
                {
                    await Task.Delay(1000, cts.Token);
                }
            }
        }, cts.Token);

        try
        {
            await Task.Delay(Timeout.Infinite, cts.Token);
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine();
            ConsoleLogger.Log(ConsoleColor.Cyan, "SHUTDOWN", "Получен сигнал завершения. Остановка всех служб...");
        }

        try
        {
            await monitorTask;
        }
        catch { }

        foreach (var svc in services)
        {
            await svc.DisposeAsync();
        }

        ConsoleLogger.Log(ConsoleColor.Green, "FINISHED", "Все службы успешно остановлены.");
        return 0;
    }

    private static void PrintHelp()
    {
        Console.WriteLine("TCP Port Forwarder - Проброс портов на внешние и внутренние адреса.");
        Console.WriteLine();
        Console.WriteLine("Использование:");
        Console.WriteLine("  PortForwarder [опции] [listen_port:target_ip_or_host:target_port] ...");
        Console.WriteLine();
        Console.WriteLine("Опции:");
        Console.WriteLine("  --idle-timeout <сек>   Таймаут простоя неактивного соединения (по умолчанию 120с). 0 = выключить.");
        Console.WriteLine();
        Console.WriteLine("Примеры:");
        Console.WriteLine("  PortForwarder 2022:192.168.1.150:22");
        Console.WriteLine("  PortForwarder 2022:192.168.1.150:22 8088:192.168.1.150:8080");
        Console.WriteLine("  PortForwarder --idle-timeout 60 2022:192.168.1.150:22");
        Console.WriteLine();
        Console.WriteLine("Внимание: Запрещен Self-Loop (например: 8080:127.0.0.1:8080) во избежание бесконечной петли!");
    }
}
