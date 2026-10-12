using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.ServiceProcess;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Win32;

namespace PortForwarder;

public sealed class ForwardRule
{
    public string ListenHost { get; }
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

    public ForwardRule(string listenHost, int listenPort, string targetHost, int targetPort)
    {
        ListenHost = string.IsNullOrWhiteSpace(listenHost) ? "0.0.0.0" : listenHost;
        ListenPort = listenPort;
        TargetHost = targetHost;
        TargetPort = targetPort;
    }

    public ForwardRule(int listenPort, string targetHost, int targetPort)
        : this("0.0.0.0", listenPort, targetHost, targetPort)
    {
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

    public bool ConflictsWith(ForwardRule other)
    {
        if (ListenPort != other.ListenPort)
            return false;

        bool thisIsAll = ListenHost == "0.0.0.0" || ListenHost == "*" || ListenHost == "::";
        bool otherIsAll = other.ListenHost == "0.0.0.0" || other.ListenHost == "*" || other.ListenHost == "::";

        if (thisIsAll || otherIsAll)
            return true;

        return string.Equals(ListenHost, other.ListenHost, StringComparison.OrdinalIgnoreCase);
    }

    public override string ToString() => $"{ListenHost}:{ListenPort} -> {TargetHost}:{TargetPort}";

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
        var tokens = SplitIgnoringBrackets(trimmed, ':');

        string listenHost = "0.0.0.0";
        string listenPortStr;
        string targetHost;
        string targetPortStr;

        if (tokens.Count == 3)
        {
            // Формат по умолчанию: listen_port:target_ip:target_port (интерфейс 0.0.0.0)
            listenPortStr = tokens[0];
            targetHost = tokens[1];
            targetPortStr = tokens[2];
        }
        else if (tokens.Count == 4)
        {
            // Расширенный формат: listen_ip:listen_port:target_ip:target_port
            listenHost = tokens[0];
            listenPortStr = tokens[1];
            targetHost = tokens[2];
            targetPortStr = tokens[3];
        }
        else
        {
            errorMessage = $"Неверный формат '{input}'. Ожидается: [listen_ip:]<listen_port>:<target_ip>:<target_port> (например: 2022:192.168.1.150:22 или 0.0.0.0:2022:192.168.1.150:22)";
            return false;
        }

        if (listenHost.StartsWith('[') && listenHost.EndsWith(']'))
            listenHost = listenHost[1..^1];
        if (string.IsNullOrWhiteSpace(listenHost))
            listenHost = "0.0.0.0";

        if (targetHost.StartsWith('[') && targetHost.EndsWith(']'))
            targetHost = targetHost[1..^1];

        if (!IsValidListenHost(listenHost))
        {
            errorMessage = $"Некорректный интерфейс/IP для прослушивания '{listenHost}'. Допустимы: 0.0.0.0, 127.0.0.1, ::, localhost или конкретный IP-адрес интерфейса.";
            return false;
        }

        if (!int.TryParse(listenPortStr, out int listenPort) || listenPort < 1 || listenPort > 65535)
        {
            errorMessage = $"Некорректный порт для прослушивания '{listenPortStr}'. Допустимый диапазон: 1-65535.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(targetHost))
        {
            errorMessage = "Целевой хост или IP-адрес не может быть пустым.";
            return false;
        }

        if (!int.TryParse(targetPortStr, out int targetPort) || targetPort < 1 || targetPort > 65535)
        {
            errorMessage = $"Некорректный целевой порт '{targetPortStr}'. Допустимый диапазон: 1-65535.";
            return false;
        }

        if (IsSelfLoop(listenHost, listenPort, targetHost, targetPort))
        {
            errorMessage = $"Обнаружена петля проброса (Self-Loop)! Порт {listenHost}:{listenPort} пробрасывается на {targetHost}:{targetPort} (этот же компьютер и порт). Это вызовет бесконечный цикл подключений на самого себя.";
            return false;
        }

        rule = new ForwardRule(listenHost, listenPort, targetHost, targetPort);
        return true;
    }

    private static List<string> SplitIgnoringBrackets(string input, char sep)
    {
        var list = new List<string>();
        int bracketDepth = 0;
        int start = 0;
        for (int i = 0; i < input.Length; i++)
        {
            if (input[i] == '[') bracketDepth++;
            else if (input[i] == ']') bracketDepth = Math.Max(0, bracketDepth - 1);
            else if (input[i] == sep && bracketDepth == 0)
            {
                list.Add(input.Substring(start, i - start));
                start = i + 1;
            }
        }
        list.Add(input.Substring(start));
        return list;
    }

    private static bool IsValidListenHost(string host)
    {
        if (host == "0.0.0.0" || host == "*" || host == "::" || string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
            return true;

        if (IPAddress.TryParse(host, out _))
            return true;

        try
        {
            var addrs = Dns.GetHostAddresses(host);
            return addrs.Length > 0;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsSelfLoop(string listenHost, int listenPort, string targetHost, int targetPort)
    {
        if (listenPort != targetPort)
            return false;

        bool listenIsAll = listenHost == "0.0.0.0" || listenHost == "*" || listenHost == "::";
        bool targetIsLocal = IsLocalHostOrIp(targetHost);

        if (listenIsAll && targetIsLocal)
            return true;

        if (string.Equals(listenHost, targetHost, StringComparison.OrdinalIgnoreCase))
            return true;

        if (IPAddress.TryParse(listenHost, out var lIp) && IPAddress.TryParse(targetHost, out var tIp) && lIp.Equals(tIp))
            return true;

        return false;
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
            Console.WriteLine($"Keep-Alive защита  : TCP Probes (15с/5с), Idle-Timeout: {(idleTimeoutSeconds > 0 ? $"{idleTimeoutSeconds}с" : "выключен")}");
            Console.WriteLine("Защита от петель    : Включена (блокировка Self-Loop и конфликтующих правил)");
            Console.WriteLine("Логирование         : Консоль + Windows EventViewer -> Application (ID: 1001-1005)");
            Console.WriteLine("--------------------------------------------------------------------------------");
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("Активные правила проброса:");
            foreach (var rule in rules)
            {
                string ifaceDesc = (rule.ListenHost == "0.0.0.0" || rule.ListenHost == "*") ? "все интерфейсы (0.0.0.0)" : rule.ListenHost;
                Console.WriteLine($"  * [{ifaceDesc}]:{rule.ListenPort}  ==>  {rule.TargetHost}:{rule.TargetPort}");
            }
            Console.ResetColor();
            Console.WriteLine("--------------------------------------------------------------------------------");
            Console.WriteLine("Управление: Ctrl+C или Q - выход с записью статистики | S - статус | I - статистика по IP");
            Console.WriteLine("================================================================================");
            Console.WriteLine();
        }
    }

    public static string BuildStatusReport(IReadOnlyList<ForwardRule> rules)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("================================================================================");
        sb.AppendLine("           ИТОГОВЫЙ ОТЧЁТ ПО ПРОБРОСАМ ПЕРЕД ЗАКРЫТИЕМ ПРИЛОЖЕНИЯ              ");
        sb.AppendLine("================================================================================");
        sb.AppendLine();
        sb.AppendLine("1. ПРАВИЛА ПРОБРОСА:");
        int totalActive = 0;
        long totalForwarded = 0;
        long totalBytesSent = 0;
        long totalBytesRecv = 0;

        foreach (var r in rules)
        {
            totalActive += r.ActiveConnections;
            totalForwarded += r.TotalConnections;
            totalBytesSent += r.TotalBytesSent;
            totalBytesRecv += r.TotalBytesReceived;

            sb.AppendLine($"  [{r.ListenHost}:{r.ListenPort} -> {r.TargetHost}:{r.TargetPort}] Активных: {r.ActiveConnections} | Всего проброшено: {r.TotalConnections} | Трафик: {FormatBytes(r.TotalBytesSent + r.TotalBytesReceived)} (↑ {FormatBytes(r.TotalBytesSent)}, ↓ {FormatBytes(r.TotalBytesReceived)})");
        }

        sb.AppendLine($"  ИТОГО ПО ПРАВИЛАМ: Активных: {totalActive} | Всего проброшено: {totalForwarded} | Общий трафик: {FormatBytes(totalBytesSent + totalBytesRecv)}");
        sb.AppendLine();

        sb.AppendLine("2. ДЕТАЛИЗАЦИЯ ПО IP-КЛИЕНТАМ И ПОРТАМ:");
        var ipList = IpStatsTracker.GetAll(IpSortMode.Connections);
        if (ipList.Count == 0)
        {
            sb.AppendLine("  Подключений клиентов не зафиксировано.");
        }
        else
        {
            foreach (var ipEntry in ipList)
            {
                sb.AppendLine("  ------------------------------------------------------------------------------");
                sb.AppendLine($"  IP: {ipEntry.Ip} | Всего подключений: {ipEntry.TotalConnections} | Ошибок: {ipEntry.TotalErrors} | Трафик: {FormatBytes(ipEntry.TotalBytes)} (↑ {FormatBytes(ipEntry.TotalBytesSent)}, ↓ {FormatBytes(ipEntry.TotalBytesReceived)}) | Уникальных портов: {ipEntry.UniquePortsCount}");
                sb.AppendLine($"  Сессия: c {ipEntry.FirstSeen:yyyy-MM-dd HH:mm:ss} по {ipEntry.LastSeen:yyyy-MM-dd HH:mm:ss}");

                if (ipEntry.IsFullPortScan)
                {
                    sb.AppendLine("    * ВНИМАНИЕ: Обнаружен полный порт-скан (порты 1-65535).");
                }
                else
                {
                    var sortedPorts = ipEntry.Ports.Values.OrderBy(p => p.Port);
                    foreach (var p in sortedPorts)
                    {
                        sb.AppendLine($"    * Порт {p.Port,5}: Подключений: {p.Connections,5} | Ошибок: {p.Errors,4} | Трафик: {FormatBytes(p.TotalBytes)} (↑ {FormatBytes(p.BytesSent)}, ↓ {FormatBytes(p.BytesReceived)})");
                    }
                }
            }
            sb.AppendLine("  ------------------------------------------------------------------------------");
        }

        return sb.ToString().TrimEnd();
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
                Console.WriteLine($"  [{r.ListenHost}:{r.ListenPort} -> {r.TargetHost}:{r.TargetPort}]");
                Console.WriteLine($"    Активных подключений: {r.ActiveConnections,4} | Всего проброшено: {r.TotalConnections,6} | Трафик: {FormatBytes(r.TotalBytesSent + r.TotalBytesReceived)}");
            }
            Console.WriteLine($"  ИТОГО: Активных: {totalActive} | Всего проброшено: {totalForwarded}");
            Console.WriteLine("--------------------------------------------------------------------------");
            Console.ResetColor();
            Console.WriteLine();
        }
    }

    public static void ShowInteractiveIpStats(CancellationToken ct)
    {
        var sortMode = IpSortMode.Connections;

        while (!ct.IsCancellationRequested)
        {
            var list = IpStatsTracker.GetAll(sortMode);
            if (list.Count == 0)
            {
                lock (ConsoleLock)
                {
                    Console.WriteLine();
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine("--------------------------------------------------------------------------------");
                    Console.WriteLine("                  СТАТИСТИКА ПОДКЛЮЧЕНИЙ ПО IP-АДРЕСАМ                         ");
                    Console.WriteLine("--------------------------------------------------------------------------------");
                    Console.WriteLine("  Подключений пока не зафиксировано.");
                    Console.WriteLine("--------------------------------------------------------------------------------");
                    Console.ResetColor();
                    Console.WriteLine();
                }
                return;
            }

            PrintIpStatsTable(list, sortMode);

            Console.WriteLine();
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.Write($"Введите номер строки [1-{list.Count}] | [T/Tab] - сменить сортировку ({IpStatsTracker.GetSortName(sortMode)}) | Enter/Esc - возврат: ");
            Console.ResetColor();

            string? line = ReadLineWithCancellation(ct);
            if (string.IsNullOrWhiteSpace(line))
            {
                break;
            }

            line = line.Trim();
            if (line.Equals("q", StringComparison.OrdinalIgnoreCase) ||
                line.Equals("exit", StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            if (line.Equals("t", StringComparison.OrdinalIgnoreCase) ||
                line.Equals("s", StringComparison.OrdinalIgnoreCase) ||
                line.Equals("sort", StringComparison.OrdinalIgnoreCase) ||
                line.Equals("tab", StringComparison.OrdinalIgnoreCase))
            {
                sortMode = (IpSortMode)(((int)sortMode + 1) % 4);
                continue;
            }

            if (int.TryParse(line, out int index) && index >= 1 && index <= list.Count)
            {
                ShowInteractiveIpDetails(list[index - 1], ct);
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"Некорректная команда. Введите число от 1 до {list.Count}, 'T' для смены сортировки или Enter для возврата.");
                Console.ResetColor();
            }
        }
    }

    public static void ShowInteractiveIpDetails(IpStatsEntry item, CancellationToken ct)
    {
        var portSortMode = PortSortMode.PortNumber;

        while (!ct.IsCancellationRequested)
        {
            PrintIpDetails(item, portSortMode);

            if (item.IsFullPortScan)
            {
                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.Write("Нажмите Enter или Esc для возврата к списку IP: ");
                Console.ResetColor();
                _ = ReadLineWithCancellation(ct);
                break;
            }

            Console.WriteLine();
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.Write($"[T/Tab] - сменить сортировку портов ({IpStatsTracker.GetPortSortName(portSortMode)}) | Enter/Esc - назад: ");
            Console.ResetColor();

            string? line = ReadLineWithCancellation(ct);
            if (string.IsNullOrWhiteSpace(line))
            {
                break;
            }

            line = line.Trim();
            if (line.Equals("q", StringComparison.OrdinalIgnoreCase) ||
                line.Equals("exit", StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            if (line.Equals("t", StringComparison.OrdinalIgnoreCase) ||
                line.Equals("s", StringComparison.OrdinalIgnoreCase) ||
                line.Equals("sort", StringComparison.OrdinalIgnoreCase) ||
                line.Equals("tab", StringComparison.OrdinalIgnoreCase))
            {
                portSortMode = (PortSortMode)(((int)portSortMode + 1) % 6);
                continue;
            }

            break;
        }
    }

    private static void PrintIpStatsTable(IReadOnlyList<IpStatsEntry> list, IpSortMode sortMode)
    {
        lock (ConsoleLock)
        {
            Console.WriteLine();
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("==================================================================================================================================");
            Console.WriteLine("                                               СТАТИСТИКА ПОДКЛЮЧЕНИЙ ПО IP-АДРЕСАМ                                               ");
            Console.WriteLine($" [Сортировка: {IpStatsTracker.GetSortName(sortMode)}]".PadRight(130));
            Console.WriteLine("==================================================================================================================================");
            Console.ResetColor();

            int ipColWidth = Math.Max(15, list.Max(x => x.Ip.Length));

            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine($" {"#",-4} {"IP адрес".PadRight(ipColWidth)}   {"Всего",-8}   {"Ошибок",-7}   {"Трафик",-12}   {"ТОП-5 портов",-40}   {"Уникальных портов",-17}");
            Console.WriteLine(new string('-', 4 + ipColWidth + 3 + 8 + 3 + 7 + 3 + 12 + 3 + 40 + 3 + 17 + 2));
            Console.ResetColor();

            for (int i = 0; i < list.Count; i++)
            {
                var item = list[i];
                string numStr = $"[{i + 1}]";
                string topFormatted = item.GetTopPortsFormatted(sortMode, 5);
                string trafficStr = FormatBytes(item.TotalBytes);

                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.Write($" {numStr,-4} ");
                Console.ForegroundColor = ConsoleColor.White;
                Console.Write($"{item.Ip.PadRight(ipColWidth)}   ");
                Console.ForegroundColor = ConsoleColor.Green;
                Console.Write($"{item.TotalConnections,-8}   ");
                Console.ForegroundColor = item.TotalErrors > 0 ? ConsoleColor.Red : ConsoleColor.DarkGray;
                Console.Write($"{item.TotalErrors,-7}   ");
                Console.ForegroundColor = ConsoleColor.DarkCyan;
                Console.Write($"{trafficStr,-12}   ");
                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.Write($"{topFormatted,-40}   ");
                Console.ForegroundColor = ConsoleColor.Magenta;
                Console.WriteLine($"{item.UniquePortsCount,-17}");
                Console.ResetColor();
            }

            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine(new string('-', 4 + ipColWidth + 3 + 8 + 3 + 7 + 3 + 12 + 3 + 40 + 3 + 17 + 2));
            Console.ResetColor();
        }
    }

    private static void PrintIpDetails(IpStatsEntry item, PortSortMode sortMode)
    {
        lock (ConsoleLock)
        {
            Console.WriteLine();
            Console.ForegroundColor = ConsoleColor.Magenta;
            Console.WriteLine("--------------------------------------------------------------------------------------------------");
            Console.WriteLine($" ПОЛНЫЙ СПИСОК ПОРТОВ ДЛЯ IP: {item.Ip}");
            Console.WriteLine("--------------------------------------------------------------------------------------------------");
            Console.ResetColor();
            Console.WriteLine($" Всего подключений   : {item.TotalConnections}");
            Console.WriteLine($" Всего ошибок        : {item.TotalErrors}");
            Console.WriteLine($" Общий трафик        : {FormatTraffic(item.TotalBytesSent, item.TotalBytesReceived)}");
            Console.WriteLine($" Уникальных портов   : {item.UniquePortsCount}");
            Console.WriteLine($" Первое подключение  : {item.FirstSeen:yyyy-MM-dd HH:mm:ss}");
            Console.WriteLine($" Крайнее подключение : {item.LastSeen:yyyy-MM-dd HH:mm:ss}");
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($" [Сортировка портов  : {IpStatsTracker.GetPortSortName(sortMode)}]");
            Console.ResetColor();
            Console.WriteLine();

            if (item.IsFullPortScan)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine(" [!] Обнаружены порты 1-65535: это был явный порт скан.");
                Console.ResetColor();
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.WriteLine("  Порт       Подключений   Ошибок   Трафик");
                Console.WriteLine("  ---------------------------------------------------------------------------------");
                Console.ResetColor();

                IEnumerable<IpPortStats> sortedPorts = sortMode switch
                {
                    PortSortMode.PortNumber => item.Ports.Values.OrderBy(p => p.Port),
                    PortSortMode.Connections => item.Ports.Values.OrderByDescending(p => p.Connections).ThenBy(p => p.Port),
                    PortSortMode.TotalTraffic => item.Ports.Values.OrderByDescending(p => p.TotalBytes).ThenBy(p => p.Port),
                    PortSortMode.Incoming => item.Ports.Values.OrderByDescending(p => p.BytesReceived).ThenBy(p => p.Port),
                    PortSortMode.Outgoing => item.Ports.Values.OrderByDescending(p => p.BytesSent).ThenBy(p => p.Port),
                    PortSortMode.Errors => item.Ports.Values.OrderByDescending(p => p.Errors).ThenBy(p => p.Port),
                    _ => item.Ports.Values.OrderBy(p => p.Port)
                };

                foreach (var p in sortedPorts)
                {
                    string errStr = p.Errors > 0 ? $"{p.Errors}" : "0";
                    Console.Write($"  {p.Port,-10} {p.Connections,-13} ");
                    if (p.Errors > 0)
                    {
                        Console.ForegroundColor = ConsoleColor.Red;
                        Console.Write($"{errStr,-8} ");
                        Console.ResetColor();
                    }
                    else
                    {
                        Console.Write($"{errStr,-8} ");
                    }
                    Console.WriteLine($"{FormatTraffic(p.BytesSent, p.BytesReceived)}");
                }
            }

            Console.ForegroundColor = ConsoleColor.Magenta;
            Console.WriteLine("--------------------------------------------------------------------------------------------------");
            Console.ResetColor();
        }
    }

    private static string? ReadLineWithCancellation(CancellationToken ct)
    {
        try
        {
            var sb = new System.Text.StringBuilder();
            while (!ct.IsCancellationRequested)
            {
                if (Console.KeyAvailable)
                {
                    var key = Console.ReadKey(intercept: true);
                    if (key.Key == ConsoleKey.Enter)
                    {
                        Console.WriteLine();
                        return sb.ToString();
                    }
                    if (key.Key == ConsoleKey.Escape)
                    {
                        Console.WriteLine();
                        return null;
                    }
                    if (key.Key == ConsoleKey.Tab)
                    {
                        Console.WriteLine();
                        return "t";
                    }
                    if (key.Key == ConsoleKey.Backspace)
                    {
                        if (sb.Length > 0)
                        {
                            sb.Length--;
                            Console.Write("\b \b");
                        }
                    }
                    else if (!char.IsControl(key.KeyChar))
                    {
                        if (sb.Length == 0 && (key.Key == ConsoleKey.T || key.Key == ConsoleKey.S))
                        {
                            Console.WriteLine(key.KeyChar);
                            return "t";
                        }

                        sb.Append(key.KeyChar);
                        Console.Write(key.KeyChar);
                    }
                }
                else
                {
                    Thread.Sleep(50);
                }
            }
            return null;
        }
        catch (InvalidOperationException)
        {
            return Console.ReadLine();
        }
    }

    public static string FormatTraffic(long sent, long recv)
    {
        long total = sent + recv;
        if (total == 0) return "0 B";
        return $"{FormatBytes(total)} (↑ {FormatBytes(sent)}, ↓ {FormatBytes(recv)})";
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

public enum IpSortMode
{
    Connections,    // Топ по подключениям
    TotalTraffic,   // Суммарный трафик
    Incoming,       // Топ по входящему трафику (↓)
    Outgoing        // Топ по исходящему трафику (↑)
}

public enum PortSortMode
{
    PortNumber,     // По номеру порта (по умолчанию)
    Connections,    // По количеству соединений
    TotalTraffic,   // Суммарный трафик
    Incoming,       // Входящий трафик (↓)
    Outgoing,       // Исходящий трафик (↑)
    Errors          // По количеству ошибок
}

public sealed class IpPortStats
{
    public int Port { get; }
    private long _connections;
    private long _errors;
    private long _bytesSent;
    private long _bytesReceived;

    public long Connections => Volatile.Read(ref _connections);
    public long Errors => Volatile.Read(ref _errors);
    public long BytesSent => Volatile.Read(ref _bytesSent);
    public long BytesReceived => Volatile.Read(ref _bytesReceived);
    public long TotalBytes => BytesSent + BytesReceived;

    public IpPortStats(int port) => Port = port;

    public void IncrementConnection() => Interlocked.Increment(ref _connections);
    public void IncrementError() => Interlocked.Increment(ref _errors);

    public void AddBytes(long sent, long recv)
    {
        Interlocked.Add(ref _bytesSent, sent);
        Interlocked.Add(ref _bytesReceived, recv);
    }
}

public sealed class IpStatsEntry
{
    public string Ip { get; }
    private long _totalConnections;
    private long _totalErrors;
    private long _totalBytesSent;
    private long _totalBytesReceived;

    public long TotalConnections => Volatile.Read(ref _totalConnections);
    public long TotalErrors => Volatile.Read(ref _totalErrors);
    public long TotalBytesSent => Volatile.Read(ref _totalBytesSent);
    public long TotalBytesReceived => Volatile.Read(ref _totalBytesReceived);
    public long TotalBytes => TotalBytesSent + TotalBytesReceived;

    public ConcurrentDictionary<int, IpPortStats> Ports { get; } = new();

    public DateTime FirstSeen { get; }
    private DateTime _lastSeen;
    public DateTime LastSeen
    {
        get => _lastSeen;
        private set => _lastSeen = value;
    }

    public IpStatsEntry(string ip)
    {
        Ip = ip;
        FirstSeen = DateTime.Now;
        _lastSeen = DateTime.Now;
    }

    public void RecordConnection(int port)
    {
        Interlocked.Increment(ref _totalConnections);
        var portStats = Ports.GetOrAdd(port, static p => new IpPortStats(p));
        portStats.IncrementConnection();
        LastSeen = DateTime.Now;
    }

    public void RecordError(int port)
    {
        Interlocked.Increment(ref _totalErrors);
        var portStats = Ports.GetOrAdd(port, static p => new IpPortStats(p));
        portStats.IncrementError();
        LastSeen = DateTime.Now;
    }

    public void RecordBytes(int port, long sent, long recv)
    {
        Interlocked.Add(ref _totalBytesSent, sent);
        Interlocked.Add(ref _totalBytesReceived, recv);
        var portStats = Ports.GetOrAdd(port, static p => new IpPortStats(p));
        portStats.AddBytes(sent, recv);
    }

    public int UniquePortsCount => Ports.Count;

    public bool IsFullPortScan
    {
        get
        {
            if (Ports.Count == 65535) return true;
            if (Ports.Count >= 65530 && Ports.ContainsKey(1) && Ports.ContainsKey(65535))
                return true;
            return false;
        }
    }

    public string GetTopPortsFormatted(IpSortMode sortMode, int topCount = 5)
    {
        if (IsFullPortScan)
        {
            return "1-65535 (явный порт скан)";
        }

        IEnumerable<string> top;
        switch (sortMode)
        {
            case IpSortMode.TotalTraffic:
                top = Ports.Values
                    .OrderByDescending(p => p.TotalBytes)
                    .ThenBy(p => p.Port)
                    .Take(topCount)
                    .Select(p => $"{p.Port}:{ConsoleLogger.FormatBytes(p.TotalBytes)}");
                break;

            case IpSortMode.Incoming:
                top = Ports.Values
                    .OrderByDescending(p => p.BytesReceived)
                    .ThenBy(p => p.Port)
                    .Take(topCount)
                    .Select(p => $"{p.Port}:↓{ConsoleLogger.FormatBytes(p.BytesReceived)}");
                break;

            case IpSortMode.Outgoing:
                top = Ports.Values
                    .OrderByDescending(p => p.BytesSent)
                    .ThenBy(p => p.Port)
                    .Take(topCount)
                    .Select(p => $"{p.Port}:↑{ConsoleLogger.FormatBytes(p.BytesSent)}");
                break;

            case IpSortMode.Connections:
            default:
                top = Ports.Values
                    .OrderByDescending(p => p.Connections)
                    .ThenBy(p => p.Port)
                    .Take(topCount)
                    .Select(p => $"{p.Port}:{p.Connections}");
                break;
        }

        return $"ТОП ({string.Join(", ", top)})";
    }
}

public static class IpStatsTracker
{
    private static readonly ConcurrentDictionary<string, IpStatsEntry> _stats = new();

    public static string GetSortName(IpSortMode mode) => mode switch
    {
        IpSortMode.Connections => "По подключениям",
        IpSortMode.TotalTraffic => "По суммарному трафику",
        IpSortMode.Incoming => "По входящему трафику (↓)",
        IpSortMode.Outgoing => "По исходящему трафику (↑)",
        _ => mode.ToString()
    };

    public static string GetPortSortName(PortSortMode mode) => mode switch
    {
        PortSortMode.PortNumber => "По номеру порта (1..65535)",
        PortSortMode.Connections => "По подключениям",
        PortSortMode.TotalTraffic => "По суммарному трафику",
        PortSortMode.Incoming => "По входящему трафику (↓)",
        PortSortMode.Outgoing => "По исходящему трафику (↑)",
        PortSortMode.Errors => "По количеству ошибок",
        _ => mode.ToString()
    };

    public static void RecordConnection(string ip, int port)
    {
        if (string.IsNullOrWhiteSpace(ip) || ip == "Unknown") return;
        var entry = _stats.GetOrAdd(ip, static key => new IpStatsEntry(key));
        entry.RecordConnection(port);
    }

    public static void RecordError(string ip, int port)
    {
        if (string.IsNullOrWhiteSpace(ip) || ip == "Unknown") return;
        var entry = _stats.GetOrAdd(ip, static key => new IpStatsEntry(key));
        entry.RecordError(port);
    }

    public static void RecordBytes(string ip, int port, long sent, long recv)
    {
        if (string.IsNullOrWhiteSpace(ip) || ip == "Unknown") return;
        if (_stats.TryGetValue(ip, out var entry))
        {
            entry.RecordBytes(port, sent, recv);
        }
    }

    public static IReadOnlyList<IpStatsEntry> GetAll(IpSortMode sortMode = IpSortMode.Connections)
    {
        var values = _stats.Values;
        return sortMode switch
        {
            IpSortMode.TotalTraffic => values.OrderByDescending(e => e.TotalBytes).ThenByDescending(e => e.TotalConnections).ThenBy(e => e.Ip).ToList(),
            IpSortMode.Incoming => values.OrderByDescending(e => e.TotalBytesReceived).ThenByDescending(e => e.TotalConnections).ThenBy(e => e.Ip).ToList(),
            IpSortMode.Outgoing => values.OrderByDescending(e => e.TotalBytesSent).ThenByDescending(e => e.TotalConnections).ThenBy(e => e.Ip).ToList(),
            IpSortMode.Connections or _ => values.OrderByDescending(e => e.TotalConnections).ThenByDescending(e => e.TotalBytes).ThenBy(e => e.Ip).ToList(),
        };
    }

    public static void Clear()
    {
        _stats.Clear();
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
        string host = _rule.ListenHost;
        string startMsg;

        if (host == "0.0.0.0" || host == "*" || string.IsNullOrWhiteSpace(host))
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
            startMsg = $"Служба запущена: порт {_rule.ListenPort} на всех интерфейсах (0.0.0.0) -> {_rule.TargetHost}:{_rule.TargetPort}";
        }
        else if (host == "::" || host == "[::]")
        {
            _listener = new TcpListener(IPAddress.IPv6Any, _rule.ListenPort);
            _listener.Start();
            startMsg = $"Служба запущена: порт {_rule.ListenPort} на интерфейсе [::] -> {_rule.TargetHost}:{_rule.TargetPort}";
        }
        else
        {
            IPAddress bindIp;
            if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
            {
                bindIp = IPAddress.Loopback;
            }
            else if (!IPAddress.TryParse(host, out bindIp!))
            {
                var addrs = Dns.GetHostAddresses(host);
                if (addrs.Length == 0) throw new InvalidOperationException($"Не удалось определить IP-адрес для интерфейса '{host}'.");
                bindIp = addrs[0];
            }

            _listener = new TcpListener(bindIp, _rule.ListenPort);
            _listener.Start();
            startMsg = $"Служба запущена: {bindIp}:{_rule.ListenPort} -> {_rule.TargetHost}:{_rule.TargetPort}";
        }

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
                    string err = $"Ошибка приема подключения на порту {_rule.ListenHost}:{_rule.ListenPort}: {ex.Message}";
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
            socket.NoDelay = true;
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
            socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime, 15);
            socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval, 5);
            socket.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveRetryCount, 3);
        }
        catch { }
    }

    private async Task HandleClientAsync(TcpClient client, long connId, CancellationToken globalCt)
    {
        _rule.IncrementConnections();
        var sw = Stopwatch.StartNew();
        string clientEp = client.Client.RemoteEndPoint?.ToString() ?? "Unknown";
        string clientIp = "Unknown";
        if (client.Client.RemoteEndPoint is IPEndPoint ipEp)
        {
            var addr = ipEp.Address;
            if (addr.IsIPv4MappedToIPv6)
            {
                addr = addr.MapToIPv4();
            }
            clientIp = addr.ToString();
        }
        else if (!string.IsNullOrEmpty(clientEp))
        {
            int lastColon = clientEp.LastIndexOf(':');
            if (lastColon > 0)
            {
                clientIp = clientEp.Substring(0, lastColon).Trim('[', ']');
            }
        }

        IpStatsTracker.RecordConnection(clientIp, _rule.ListenPort);

        string connectMsg = $"[#{connId}] Подключен клиент: {clientEp} -> {_rule.TargetHost}:{_rule.TargetPort} (Слушаем {_rule.ListenHost}:{_rule.ListenPort}) | Активных: {_rule.ActiveConnections} | Всего: {_rule.TotalConnections}";
        ConsoleLogger.Log(ConsoleColor.Green, "CONNECT", connectMsg);
        EventViewerLogger.LogInformation(connectMsg, 1002);

        using (client)
        using (var targetClient = new TcpClient())
        using (var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(globalCt))
        {
            ConfigureSocketOptions(client.Client);

            try
            {
                using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(sessionCts.Token);
                connectCts.CancelAfter(TimeSpan.FromSeconds(10));

                await targetClient.ConnectAsync(_rule.TargetHost, _rule.TargetPort, connectCts.Token);
                ConfigureSocketOptions(targetClient.Client);

                if (targetClient.Client.RemoteEndPoint is IPEndPoint targetRemoteEp &&
                    targetRemoteEp.Port == _rule.ListenPort &&
                    ForwardRule.IsLocalHostOrIp(targetRemoteEp.Address.ToString()))
                {
                    throw new InvalidOperationException("Обнаружена динамическая петля соединения (Self-loop connection)! Разрыв соединения.");
                }

                using var clientStream = client.GetStream();
                using var targetStream = targetClient.GetStream();

                long lastActivityTicks = DateTime.UtcNow.Ticks;
                void UpdateActivity() => Interlocked.Exchange(ref lastActivityTicks, DateTime.UtcNow.Ticks);

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
                                sessionCts.Cancel();
                                break;
                            }
                        }
                    }, sessionCts.Token);
                }

                var clientToTarget = RelayTrafficAsync(clientStream, targetStream, sessionCts.Token, UpdateActivity);
                var targetToClient = RelayTrafficAsync(targetStream, clientStream, sessionCts.Token, UpdateActivity);

                var completedTask = await Task.WhenAny(clientToTarget, targetToClient);

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
                IpStatsTracker.RecordBytes(clientIp, _rule.ListenPort, sent, received);
                sw.Stop();
                _rule.DecrementConnections();

                string disconnectMsg = $"[#{connId}] Отключен клиент: {clientEp} (Правило {_rule.ListenHost}:{_rule.ListenPort} -> {_rule.TargetHost}:{_rule.TargetPort}) | Длительность: {sw.Elapsed.TotalSeconds:F1}с | Трафик: ↑ {ConsoleLogger.FormatBytes(sent)}, ↓ {ConsoleLogger.FormatBytes(received)} | Активных: {_rule.ActiveConnections}";
                ConsoleLogger.Log(ConsoleColor.Yellow, "DISCONNECT", disconnectMsg);
                EventViewerLogger.LogInformation(disconnectMsg, 1003);
            }
            catch (Exception ex)
            {
                sw.Stop();
                _rule.DecrementConnections();
                IpStatsTracker.RecordError(clientIp, _rule.ListenPort);

                string errMsg = $"[#{connId}] Ошибка/таймаут сессии {clientEp} -> {_rule.TargetHost}:{_rule.TargetPort}: {ex.Message} | Активных: {_rule.ActiveConnections}";
                ConsoleLogger.Log(ConsoleColor.Red, "ERROR", errMsg);
                EventViewerLogger.LogError(errMsg, 2002);
            }
        }
    }

    private static async Task<long> RelayTrafficAsync(NetworkStream source, NetworkStream destination, CancellationToken ct, Action onActivity)
    {
        byte[] buffer = new byte[65536];
        long totalBytes = 0;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                int bytesRead = await source.ReadAsync(buffer, ct);
                if (bytesRead == 0) break;

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

        string stopMsg = $"Служба {_rule.ListenHost}:{_rule.ListenPort} остановлена. Всего обработано подключений: {_rule.TotalConnections}";
        ConsoleLogger.Log(ConsoleColor.DarkYellow, "STOP", stopMsg);
        EventViewerLogger.LogInformation(stopMsg, 1004);

        _cts.Dispose();
    }
}

public static class PortRegistry
{
    private const string SubKeyPath = @"Software\PortForwarder";
    private const string RulesValueName = "Rules";
    private const string IdleTimeoutValueName = "IdleTimeout";

    public static bool SaveRules(IEnumerable<ForwardRule> rules, int idleTimeoutSeconds, out string errorMessage)
    {
        errorMessage = string.Empty;
        var ruleList = rules.ToList();
        string[] ruleStrings = ruleList.Select(r =>
        {
            if (r.ListenHost == "0.0.0.0" || r.ListenHost == "*")
                return $"{r.ListenPort}:{r.TargetHost}:{r.TargetPort}";
            return $"{r.ListenHost}:{r.ListenPort}:{r.TargetHost}:{r.TargetPort}";
        }).ToArray();

        // 1. Попытка записать в HKLM
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = baseKey.CreateSubKey(SubKeyPath, true);
            if (key != null)
            {
                key.SetValue(RulesValueName, ruleStrings, RegistryValueKind.MultiString);
                key.SetValue(IdleTimeoutValueName, idleTimeoutSeconds, RegistryValueKind.DWord);
                return true;
            }
        }
        catch (Exception exHklm)
        {
            // Если нет прав на HKLM, пробуем HKCU
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
                using var key = baseKey.CreateSubKey(SubKeyPath, true);
                if (key != null)
                {
                    key.SetValue(RulesValueName, ruleStrings, RegistryValueKind.MultiString);
                    key.SetValue(IdleTimeoutValueName, idleTimeoutSeconds, RegistryValueKind.DWord);
                    return true;
                }
            }
            catch (Exception exHkcu)
            {
                errorMessage = $"Не удалось сохранить правила в реестр (HKLM: {exHklm.Message}; HKCU: {exHkcu.Message})";
                return false;
            }
        }

        errorMessage = "Не удалось открыть ветку реестра для записи.";
        return false;
    }

    public static bool LoadRules(out List<ForwardRule> rules, out int idleTimeoutSeconds, out string location)
    {
        rules = new List<ForwardRule>();
        idleTimeoutSeconds = 120;
        location = string.Empty;

        // 1. Проверяем HKLM
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = baseKey.OpenSubKey(SubKeyPath, false);
            if (key != null)
            {
                if (TryReadFromKey(key, out rules, out idleTimeoutSeconds))
                {
                    location = @"HKLM\Software\PortForwarder";
                    return true;
                }
            }
        }
        catch { }

        // 2. Проверяем HKCU
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
            using var key = baseKey.OpenSubKey(SubKeyPath, false);
            if (key != null)
            {
                if (TryReadFromKey(key, out rules, out idleTimeoutSeconds))
                {
                    location = @"HKCU\Software\PortForwarder";
                    return true;
                }
            }
        }
        catch { }

        return false;
    }

    private static bool TryReadFromKey(RegistryKey key, out List<ForwardRule> rules, out int idleTimeoutSeconds)
    {
        rules = new List<ForwardRule>();
        idleTimeoutSeconds = 120;

        object? timeoutObj = key.GetValue(IdleTimeoutValueName);
        if (timeoutObj is int tVal)
        {
            idleTimeoutSeconds = tVal;
        }

        object? rulesObj = key.GetValue(RulesValueName);
        if (rulesObj is string[] ruleArr)
        {
            foreach (var rStr in ruleArr)
            {
                if (ForwardRule.TryParse(rStr, out var rule, out _))
                {
                    if (rule != null && !rules.Any(r => r.ConflictsWith(rule)))
                        rules.Add(rule);
                }
            }
            return rules.Count > 0;
        }
        else if (rulesObj is string singleStr && !string.IsNullOrWhiteSpace(singleStr))
        {
            if (ForwardRule.TryParse(singleStr, out var rule, out _))
            {
                if (rule != null)
                    rules.Add(rule);
            }
            return rules.Count > 0;
        }

        return false;
    }

    public static bool ClearRules(out string message)
    {
        bool clearedAny = false;
        var details = new List<string>();

        // Удаляем из HKLM
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = baseKey.OpenSubKey(SubKeyPath, true);
            if (key != null)
            {
                key.DeleteValue(RulesValueName, false);
                key.DeleteValue(IdleTimeoutValueName, false);
                clearedAny = true;
                details.Add(@"HKLM\Software\PortForwarder");
            }
        }
        catch { }

        // Удаляем из HKCU
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
            using var key = baseKey.OpenSubKey(SubKeyPath, true);
            if (key != null)
            {
                key.DeleteValue(RulesValueName, false);
                key.DeleteValue(IdleTimeoutValueName, false);
                clearedAny = true;
                details.Add(@"HKCU\Software\PortForwarder");
            }
        }
        catch { }

        if (clearedAny)
        {
            message = $"Параметры проброса портов удалены из реестра ({string.Join(", ", details)}).";
            return true;
        }

        message = "Параметры проброса портов в реестре не найдены.";
        return false;
    }
}

public static class ServiceManager
{
    public const string ServiceName = "PortForwarder";
    public const string DisplayName = "TCP Port Forwarder Service";
    public const string Description = "Служба проброса портов TCP (Port Forwarder) на базе правил из реестра Windows.";

    public static bool IsInstalled()
    {
        try
        {
            using var sc = new ServiceController(ServiceName);
            _ = sc.Status;
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static int InstallService(string[] forwarderArgs)
    {
        string? exePath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exePath))
        {
            ConsoleLogger.Log(ConsoleColor.Red, "SERVICE", "Не удалось определить путь к исполняемому файлу.");
            return 1;
        }

        // Аргументы для работы службы: служба запускается с флагом -service -last (или указанными правилами)
        string serviceBinArgs = "-service run";
        if (forwarderArgs.Length > 0)
        {
            var cleanArgs = forwarderArgs.Where(a => !a.Equals("-service", StringComparison.OrdinalIgnoreCase) &&
                                                     !a.Equals("install", StringComparison.OrdinalIgnoreCase) &&
                                                     !a.Equals("-install", StringComparison.OrdinalIgnoreCase) &&
                                                     !a.Equals("--install", StringComparison.OrdinalIgnoreCase)).ToArray();
            if (cleanArgs.Length > 0)
            {
                serviceBinArgs = "-service run " + string.Join(" ", cleanArgs.Select(a => $"\"{a}\""));
            }
        }
        else
        {
            serviceBinArgs = "-service run -last";
        }

        string fullBinPath = $"\"{exePath}\" {serviceBinArgs}";

        if (IsInstalled())
        {
            ConsoleLogger.Log(ConsoleColor.Yellow, "SERVICE", $"Служба '{ServiceName}' уже установлена. Обновление конфигурации...");
            int stopResult = RunSc($"stop \"{ServiceName}\"");
            int configResult = RunSc($"config \"{ServiceName}\" binPath= \"{fullBinPath}\" start= auto DisplayName= \"{DisplayName}\"");
            RunSc($"description \"{ServiceName}\" \"{Description}\"");
            if (configResult == 0)
            {
                ConsoleLogger.Log(ConsoleColor.Green, "SERVICE", $"Конфигурация службы '{ServiceName}' успешно обновлена (binPath: {fullBinPath}).");
                return 0;
            }
            return configResult;
        }

        ConsoleLogger.Log(ConsoleColor.Cyan, "SERVICE", $"Регистрация службы '{ServiceName}' в системе...");
        int createResult = RunSc($"create \"{ServiceName}\" binPath= \"{fullBinPath}\" start= auto DisplayName= \"{DisplayName}\"");
        if (createResult != 0)
        {
            ConsoleLogger.Log(ConsoleColor.Red, "SERVICE", $"Ошибка при создании службы через sc.exe (Код ошибки: {createResult}). Запустите консоль от имени Администратора.");
            return createResult;
        }

        RunSc($"description \"{ServiceName}\" \"{Description}\"");
        ConsoleLogger.Log(ConsoleColor.Green, "SERVICE", $"Служба '{ServiceName}' успешно установлена с автозапуском (binPath: {fullBinPath}).");
        return 0;
    }

    public static int UninstallService()
    {
        if (!IsInstalled())
        {
            ConsoleLogger.Log(ConsoleColor.Yellow, "SERVICE", $"Служба '{ServiceName}' не установлена в системе.");
            return 0;
        }

        ConsoleLogger.Log(ConsoleColor.Cyan, "SERVICE", $"Остановка и удаление службы '{ServiceName}'...");
        RunSc($"stop \"{ServiceName}\"");
        int delResult = RunSc($"delete \"{ServiceName}\"");
        if (delResult == 0)
        {
            ConsoleLogger.Log(ConsoleColor.Green, "SERVICE", $"Служба '{ServiceName}' успешно удалена из системы.");
            return 0;
        }

        ConsoleLogger.Log(ConsoleColor.Red, "SERVICE", $"Не удалось удалить службу '{ServiceName}' (Код: {delResult}). Запустите консоль от имени Администратора.");
        return delResult;
    }

    public static int StartService()
    {
        if (!IsInstalled())
        {
            ConsoleLogger.Log(ConsoleColor.Red, "SERVICE", $"Служба '{ServiceName}' не установлена.");
            return 1;
        }

        int res = RunSc($"start \"{ServiceName}\"");
        if (res == 0)
            ConsoleLogger.Log(ConsoleColor.Green, "SERVICE", $"Служба '{ServiceName}' запущена.");
        return res;
    }

    public static int StopService()
    {
        if (!IsInstalled())
        {
            ConsoleLogger.Log(ConsoleColor.Yellow, "SERVICE", $"Служба '{ServiceName}' не установлена.");
            return 0;
        }

        int res = RunSc($"stop \"{ServiceName}\"");
        if (res == 0)
            ConsoleLogger.Log(ConsoleColor.Green, "SERVICE", $"Служба '{ServiceName}' остановлена.");
        return res;
    }

    private static int RunSc(string arguments)
    {
        try
        {
            var psi = new ProcessStartInfo("sc.exe", arguments)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            if (proc == null) return -1;
            proc.WaitForExit();
            return proc.ExitCode;
        }
        catch (Exception ex)
        {
            ConsoleLogger.Log(ConsoleColor.Red, "SERVICE", $"Исключение при вызове sc.exe: {ex.Message}");
            return -1;
        }
    }
}

public sealed class ForwarderBackgroundService : BackgroundService
{
    private readonly IReadOnlyList<ForwardRule> _rules;
    private readonly int _idleTimeoutSeconds;

    public ForwarderBackgroundService(IReadOnlyList<ForwardRule> rules, int idleTimeoutSeconds)
    {
        _rules = rules;
        _idleTimeoutSeconds = idleTimeoutSeconds;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        EventViewerLogger.LogInformation($"Фоновая служба Windows Service запущена. Правил: {_rules.Count}", 1001);

        var services = new List<ForwardingService>();
        try
        {
            foreach (var rule in _rules)
            {
                var service = new ForwardingService(rule, _idleTimeoutSeconds);
                service.Start();
                services.Add(service);
            }

            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            // Корректная остановка
        }
        finally
        {
            string statusReport = ConsoleLogger.BuildStatusReport(_rules);
            EventViewerLogger.LogInformation(statusReport, 1005);

            foreach (var svc in services)
            {
                await svc.DisposeAsync();
            }

            EventViewerLogger.LogInformation("Фоновая служба Windows Service корректно остановлена.", 1004);
        }
    }
}

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        // 1. Проверка на режим фоновой службы Windows Service
        if (WindowsServiceHelpers.IsWindowsService())
        {
            return await RunAsWindowsServiceAsync(args);
        }

        // 2. Если аргументы не переданы вообще — выводим справку и текущие значения из реестра
        if (args.Length == 0)
        {
            PrintHelp();
            Console.WriteLine();
            PrintRegistryValues();
            return 0;
        }

        // 3. Разбор аргументов командной строки
        bool isServiceCommand = false;
        string? serviceSubCommand = null;

        bool useLastConfig = false;
        bool saveToRegistry = false;
        bool clearRegistry = false;
        bool showRegistryOnly = false;

        int idleTimeoutSeconds = 120;
        var rawRules = new List<string>();

        for (int i = 0; i < args.Length; i++)
        {
            var arg = args[i];

            if (arg.Equals("--help", StringComparison.OrdinalIgnoreCase) ||
                arg.Equals("-h", StringComparison.OrdinalIgnoreCase) ||
                arg.Equals("/?", StringComparison.OrdinalIgnoreCase))
            {
                PrintHelp();
                Console.WriteLine();
                PrintRegistryValues();
                return 0;
            }

            if (arg.Equals("show", StringComparison.OrdinalIgnoreCase) ||
                arg.Equals("-show", StringComparison.OrdinalIgnoreCase) ||
                arg.Equals("--show", StringComparison.OrdinalIgnoreCase))
            {
                showRegistryOnly = true;
                continue;
            }

            if (arg.Equals("-service", StringComparison.OrdinalIgnoreCase) ||
                arg.Equals("--service", StringComparison.OrdinalIgnoreCase))
            {
                isServiceCommand = true;
                if (i + 1 < args.Length && !args[i + 1].StartsWith('-'))
                {
                    serviceSubCommand = args[++i].ToLowerInvariant();
                }
                continue;
            }

            if (arg.Equals("-last", StringComparison.OrdinalIgnoreCase) ||
                arg.Equals("--last", StringComparison.OrdinalIgnoreCase))
            {
                useLastConfig = true;
                continue;
            }

            if (arg.Equals("-save", StringComparison.OrdinalIgnoreCase) ||
                arg.Equals("--save", StringComparison.OrdinalIgnoreCase))
            {
                saveToRegistry = true;
                continue;
            }

            if (arg.Equals("-clear", StringComparison.OrdinalIgnoreCase) ||
                arg.Equals("--clear", StringComparison.OrdinalIgnoreCase))
            {
                clearRegistry = true;
                continue;
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

        // Обработка просмотра сохраненных в реестре значений (show / -show)
        if (showRegistryOnly)
        {
            PrintRegistryValues();
            return 0;
        }

        // Обработка очистки реестра (-clear)
        if (clearRegistry)
        {
            PortRegistry.ClearRules(out string msg);
            ConsoleLogger.Log(ConsoleColor.Yellow, "REGISTRY", msg);
            return 0;
        }

        // 4. Обработка команд управления службой (-service install / uninstall / start / stop / status / run)
        if (isServiceCommand)
        {
            return await HandleServiceCommandAsync(serviceSubCommand, rawRules, useLastConfig, idleTimeoutSeconds);
        }

        // Обработка сохранения правил в реестр (-save) без запуска проброса
        if (saveToRegistry)
        {
            var rulesToSave = new List<ForwardRule>();

            if (rawRules.Count == 0)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("Ошибка: Для ключа -save необходимо указать хотя бы одно правило проброса (например: -save 2022:192.168.1.150:22).");
                Console.ResetColor();
                return 1;
            }

            foreach (var ruleStr in rawRules)
            {
                if (!ForwardRule.TryParse(ruleStr, out var rule, out string error))
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine($"Ошибка в параметре '{ruleStr}': {error}");
                    Console.ResetColor();
                    return 1;
                }

                if (rulesToSave.Any(r => r.ConflictsWith(rule!)))
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine($"Ошибка: Конфликт правил прослушивания! Правило '{rule}' конфликтует с уже добавленным правилом.");
                    Console.ResetColor();
                    return 1;
                }

                rulesToSave.Add(rule!);
            }

            if (PortRegistry.SaveRules(rulesToSave, idleTimeoutSeconds, out string saveErr))
            {
                ConsoleLogger.Log(ConsoleColor.Green, "REGISTRY", $"Конфигурация пробросов ({rulesToSave.Count} правил) успешно сохранена в реестр.");
                return 0;
            }
            else
            {
                ConsoleLogger.Log(ConsoleColor.Red, "REGISTRY", $"Ошибка сохранения в реестр: {saveErr}");
                return 1;
            }
        }

        // 4. Обычный запуск приложения (консольный режим)
        var rules = new List<ForwardRule>();

        if (useLastConfig)
        {
            if (PortRegistry.LoadRules(out var loadedRules, out int loadedTimeout, out string location))
            {
                rules.AddRange(loadedRules);
                if (idleTimeoutSeconds == 120 && loadedTimeout != 120)
                    idleTimeoutSeconds = loadedTimeout;
                ConsoleLogger.Log(ConsoleColor.Cyan, "REGISTRY", $"Загружено {rules.Count} правил проброса из реестра ({location}).");
            }
            else
            {
                ConsoleLogger.Log(ConsoleColor.Yellow, "REGISTRY", "Сохраненные правила проброса в реестре не найдены. Будут применены параметры из командной строки / по умолчанию.");
            }
        }

        // Добавляем правила из командной строки
        if (rawRules.Count > 0)
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

                if (rules.Any(r => r.ConflictsWith(rule!)))
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine($"Ошибка: Конфликт правил прослушивания! Правило '{rule}' конфликтует с уже добавленным правилом.");
                    Console.ResetColor();
                    return 1;
                }

                rules.Add(rule!);
            }
        }

        // Если правил всё ещё нет — используем правило по умолчанию
        if (rules.Count == 0)
        {
            string defaultArg = "0.0.0.0:2022:192.168.1.150:22";
            if (ForwardRule.TryParse(defaultArg, out var defaultRule, out _))
            {
                rules.Add(defaultRule!);
            }
        }

        // При запуске с явными новыми правилами сохраняем их в реестр как последнее активное состояние
        if (rawRules.Count > 0 && !useLastConfig)
        {
            if (PortRegistry.SaveRules(rules, idleTimeoutSeconds, out string saveErr))
            {
                ConsoleLogger.Log(ConsoleColor.Green, "REGISTRY", $"Конфигурация пробросов ({rules.Count} правил) сохранена в реестр для последующего запуска с -last.");
            }
            else
            {
                ConsoleLogger.Log(ConsoleColor.Yellow, "REGISTRY", $"Предупреждение при сохранении в реестр: {saveErr}");
            }
        }

        // Запуск прослушивания в консольном интерактивном режиме
        return await RunConsoleInteractiveAsync(rules, idleTimeoutSeconds);
    }

    private static async Task<int> HandleServiceCommandAsync(string? subCommand, List<string> rawRules, bool useLastConfig, int idleTimeoutSeconds)
    {
        switch (subCommand)
        {
            case "install":
            case "-install":
            case "--install":
            case "i":
            {
                // При установке службы, если указаны правила, сохраняем их в реестр
                var rulesToInstall = new List<ForwardRule>();
                foreach (var rStr in rawRules)
                {
                    if (ForwardRule.TryParse(rStr, out var r, out string err))
                    {
                        if (r != null && !rulesToInstall.Any(x => x.ConflictsWith(r)))
                            rulesToInstall.Add(r);
                    }
                    else
                    {
                        ConsoleLogger.Log(ConsoleColor.Red, "SERVICE", $"Некорректное правило '{rStr}': {err}");
                        return 1;
                    }
                }

                if (rulesToInstall.Count > 0)
                {
                    if (PortRegistry.SaveRules(rulesToInstall, idleTimeoutSeconds, out string saveErr))
                    {
                        ConsoleLogger.Log(ConsoleColor.Green, "REGISTRY", $"Правила для службы сохранены в реестр ({rulesToInstall.Count} правил).");
                    }
                    else
                    {
                        ConsoleLogger.Log(ConsoleColor.Yellow, "REGISTRY", $"Предупреждение: {saveErr}");
                    }
                }

                int code = ServiceManager.InstallService(rawRules.ToArray());
                if (code == 0)
                {
                    Console.WriteLine();
                    Console.ForegroundColor = ConsoleColor.Cyan;
                    Console.WriteLine($"Для запуска службы используйте: PortForwarder -service start   (или: sc start {ServiceManager.ServiceName})");
                    Console.WriteLine($"Для удаления службы используйте: PortForwarder -service uninstall (или: sc delete {ServiceManager.ServiceName})");
                    Console.ResetColor();
                }
                return code;
            }

            case "uninstall":
            case "-uninstall":
            case "--uninstall":
            case "remove":
            case "u":
            {
                return ServiceManager.UninstallService();
            }

            case "start":
            {
                return ServiceManager.StartService();
            }

            case "stop":
            {
                return ServiceManager.StopService();
            }

            case "status":
            {
                bool installed = ServiceManager.IsInstalled();
                Console.WriteLine($"Статус службы '{ServiceManager.ServiceName}': {(installed ? "Установлена" : "Не установлена")}");
                if (PortRegistry.LoadRules(out var regRules, out int to, out string loc))
                {
                    Console.WriteLine($"Правила в реестре ({loc}, Idle-Timeout: {to}с):");
                    foreach (var r in regRules)
                    {
                        Console.WriteLine($"  * {r}");
                    }
                }
                else
                {
                    Console.WriteLine("Правила в реестре: не найдены.");
                }
                return 0;
            }

            case "run":
            {
                // Запуск службы Service Control Manager
                var rules = new List<ForwardRule>();
                if (useLastConfig || rawRules.Count == 0)
                {
                    if (PortRegistry.LoadRules(out var loadedRules, out int to, out _))
                    {
                        rules.AddRange(loadedRules);
                        idleTimeoutSeconds = to;
                    }
                }

                foreach (var rStr in rawRules)
                {
                    if (ForwardRule.TryParse(rStr, out var r, out _) && r != null && !rules.Any(x => x.ConflictsWith(r)))
                        rules.Add(r);
                }

                if (rules.Count == 0)
                {
                    rules.Add(new ForwardRule("0.0.0.0", 2022, "192.168.1.150", 22));
                }

                return await RunHostServiceAsync(rules, idleTimeoutSeconds);
            }

            default:
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"Неизвестная подкоманда для -service: '{subCommand}'. Допустимы: install, uninstall, start, stop, status, run.");
                Console.ResetColor();
                return 1;
            }
        }
    }

    private static async Task<int> RunAsWindowsServiceAsync(string[] args)
    {
        var rules = new List<ForwardRule>();
        int idleTimeoutSeconds = 120;

        // Если служба стартовала от SCM, читаем правила из реестра
        if (PortRegistry.LoadRules(out var regRules, out int regTimeout, out _))
        {
            rules.AddRange(regRules);
            idleTimeoutSeconds = regTimeout;
        }

        // Также учитываем аргументы, если они были переданы в binPath
        for (int i = 0; i < args.Length; i++)
        {
            var a = args[i];
            if (a.Equals("-service", StringComparison.OrdinalIgnoreCase) || a.Equals("run", StringComparison.OrdinalIgnoreCase) || a.Equals("-last", StringComparison.OrdinalIgnoreCase))
                continue;

            if (a.StartsWith("--idle-timeout=", StringComparison.OrdinalIgnoreCase))
            {
                if (int.TryParse(a["--idle-timeout=".Length..], out int t)) idleTimeoutSeconds = t;
                continue;
            }

            if (a.Equals("--idle-timeout", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                if (int.TryParse(args[++i], out int t)) idleTimeoutSeconds = t;
                continue;
            }

            if (ForwardRule.TryParse(a, out var r, out _) && r != null && !rules.Any(x => x.ConflictsWith(r)))
            {
                rules.Add(r);
            }
        }

        if (rules.Count == 0)
        {
            rules.Add(new ForwardRule("0.0.0.0", 2022, "192.168.1.150", 22));
        }

        return await RunHostServiceAsync(rules, idleTimeoutSeconds);
    }

    private static async Task<int> RunHostServiceAsync(IReadOnlyList<ForwardRule> rules, int idleTimeoutSeconds)
    {
        try
        {
            var builder = Host.CreateDefaultBuilder();
            builder.UseWindowsService(options =>
            {
                options.ServiceName = ServiceManager.ServiceName;
            });
            builder.ConfigureServices(services =>
            {
                services.AddHostedService(_ => new ForwarderBackgroundService(rules, idleTimeoutSeconds));
            });

            var host = builder.Build();
            await host.RunAsync();
            return 0;
        }
        catch (Exception ex)
        {
            EventViewerLogger.LogError($"Критический сбой службы Windows: {ex.Message}", 3001);
            return 1;
        }
    }

    private static async Task<int> RunConsoleInteractiveAsync(IReadOnlyList<ForwardRule> rules, int idleTimeoutSeconds)
    {
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
                        else if (key.Key == ConsoleKey.I)
                        {
                            ConsoleLogger.ShowInteractiveIpStats(cts.Token);
                        }
                        else if (key.Key == ConsoleKey.Q || key.Key == ConsoleKey.X)
                        {
                            cts.Cancel();
                            break;
                        }
                    }
                    await Task.Delay(200, cts.Token);
                }
                catch (InvalidOperationException)
                {
                    try
                    {
                        string? line = await Console.In.ReadLineAsync(cts.Token);
                        if (line == null || line.Trim().Equals("q", StringComparison.OrdinalIgnoreCase) || line.Trim().Equals("exit", StringComparison.OrdinalIgnoreCase))
                        {
                            cts.Cancel();
                            break;
                        }
                        else if (line.Trim().Equals("s", StringComparison.OrdinalIgnoreCase))
                        {
                            ConsoleLogger.PrintStatus(rules);
                        }
                        else if (line.Trim().Equals("i", StringComparison.OrdinalIgnoreCase))
                        {
                            ConsoleLogger.ShowInteractiveIpStats(cts.Token);
                        }
                    }
                    catch
                    {
                        break;
                    }
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

        int statsLogged = 0;
        void LogFinalStats()
        {
            if (Interlocked.Exchange(ref statsLogged, 1) == 0)
            {
                string statusReport = ConsoleLogger.BuildStatusReport(rules);
                ConsoleLogger.PrintStatus(rules);
                EventViewerLogger.LogInformation(statusReport, 1005);
            }
        }

        AppDomain.CurrentDomain.ProcessExit += (s, e) => LogFinalStats();
        LogFinalStats();

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
        Console.WriteLine("  PortForwarder [опции] [[listen_ip:]listen_port:target_ip:target_port] ...");
        Console.WriteLine("  PortForwarder -service [install|uninstall|start|stop|status] [правила...]");
        Console.WriteLine("  PortForwarder -last");
        Console.WriteLine();
        Console.WriteLine("Формат правил:");
        Console.WriteLine("  *([listen_ip][:])!([listen_port][:])!([target_ip][:])!([target_port])");
        Console.WriteLine("  где listen_ip опционален (по умолчанию 0.0.0.0 - все интерфейсы).");
        Console.WriteLine();
        Console.WriteLine("Опции и команды:");
        Console.WriteLine("  -service install [правила]   Установить приложение как службу Windows (с автозапуском).");
        Console.WriteLine("  -service uninstall           Остановить и удалить службу Windows.");
        Console.WriteLine("  -service start / stop        Запустить или остановить установленную службу.");
        Console.WriteLine("  -service status              Проверить статус службы и правила в реестре.");
        Console.WriteLine("  -last                        Запустить приложение (или службу) с правилами из реестра.");
        Console.WriteLine("  show / -show                 Отобразить текущие значения и правила из реестра Windows.");
        Console.WriteLine("  -save                        Сохранить указанные правила в реестр Windows и выйти (без запуска проброса).");
        Console.WriteLine("  -clear                       Очистить сохраненные правила проброса из реестра и выйти (без запуска проброса).");
        Console.WriteLine("  --idle-timeout <сек>         Таймаут простоя неактивного соединения (по умолчанию 120с). 0 = выключить.");
        Console.WriteLine();
        Console.WriteLine("Раздел реестра:");
        Console.WriteLine(@"  HKLM\Software\PortForwarder или HKCU\Software\PortForwarder");
        Console.WriteLine("  Параметры: Rules (REG_MULTI_SZ), IdleTimeout (REG_DWORD)");
        Console.WriteLine();
        Console.WriteLine("Примеры:");
        Console.WriteLine("  PortForwarder                       # Показать справку и текущие значения в реестре");
        Console.WriteLine("  PortForwarder show                  # Показать сохраненные значения в реестре");
        Console.WriteLine("  PortForwarder 2022:192.168.1.150:22");
        Console.WriteLine("  PortForwarder -last");
        Console.WriteLine("  PortForwarder -service install 2022:192.168.1.150:22 8080:192.168.1.150:80");
        Console.WriteLine("  PortForwarder -service uninstall");
        Console.WriteLine("  PortForwarder -clear");
        Console.WriteLine();
        Console.WriteLine("Управление в консоли:");
        Console.WriteLine("  Ctrl+C или Q   - Корректная остановка служб и запись финальной статистики в EventViewer");
        Console.WriteLine("  S              - Вывод текущей статистики всех соединений на экран");
        Console.WriteLine("  I              - Статистика подключений по IP-адресам (с детализацией портов и детектом порт-скана)");
    }

    public static void PrintRegistryValues()
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("--------------------------------------------------------------------------------");
        Console.WriteLine("                   ТЕКУЩИЕ ЗНАЧЕНИЯ В РЕЕСТРЕ WINDOWS                           ");
        Console.WriteLine("--------------------------------------------------------------------------------");
        Console.ResetColor();

        if (PortRegistry.LoadRules(out var regRules, out int regTimeout, out string location))
        {
            Console.WriteLine($"Раздел реестра    : {location}");
            Console.WriteLine($"Keep-Alive Timeout: {regTimeout} сек.");
            Console.WriteLine($"Количество правил : {regRules.Count}");
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("Сохраненные правила проброса:");
            for (int i = 0; i < regRules.Count; i++)
            {
                var r = regRules[i];
                string ifaceDesc = (r.ListenHost == "0.0.0.0" || r.ListenHost == "*") ? "все интерфейсы (0.0.0.0)" : r.ListenHost;
                Console.WriteLine($"  [{i + 1}] [{ifaceDesc}]:{r.ListenPort}  ==>  {r.TargetHost}:{r.TargetPort}");
            }
            Console.ResetColor();
        }
        else
        {
            Console.ForegroundColor = ConsoleColor.DarkYellow;
            Console.WriteLine("  В реестре Windows (HKLM/HKCU\\Software\\PortForwarder) правила не найдены.");
            Console.ResetColor();
        }
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("--------------------------------------------------------------------------------");
        Console.ResetColor();
    }
}
