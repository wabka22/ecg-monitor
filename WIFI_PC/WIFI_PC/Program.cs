using System;
using System.IO;
using System.Text.Json;
using System.Diagnostics;
using System.Net.Sockets;
using System.Threading;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;

class Program
{
    const string ConfigFile = "config.json";

    // Глобальный список активных работников
    private static List<StreamWorker> _activeWorkers = new List<StreamWorker>();

    // Класс для конфигурации ОДНОГО ESP устройства
    class EspDeviceConfig
    {
        public string Name { get; set; } = "";
        public string ApSsid { get; set; } = "";
        public string ApPassword { get; set; } = "";
        public string ApIp { get; set; } = "192.168.4.1";
        public int Port { get; set; } = 8888;
        public string? HomeIp { get; set; }
    }

    // Главный класс конфигурации
    class Config
    {
        public string PcWifiSsid { get; set; } = "";
        public string PcWifiPassword { get; set; } = "";
        public string Esp1NetworkName { get; set; } = "ESP32_Cos_Streamer";
        public string Esp2NetworkName { get; set; } = "ESP32_Sin_Streamer";
        public string EspNetworkPassword { get; set; } = "12345678";
        public List<EspDeviceConfig> EspDevices { get; set; } = new List<EspDeviceConfig>();
    }

    // ---------- ЛОГИРОВАНИЕ ----------
    static void Log(string msg, string level = "INFO", string deviceTag = "")
    {
        string ts = DateTime.Now.ToString("HH:mm:ss");
        ConsoleColor color = level switch
        {
            "INFO" => ConsoleColor.Cyan,
            "SUCCESS" => ConsoleColor.Green,
            "WARN" => ConsoleColor.Yellow,
            "ERROR" => ConsoleColor.Red,
            _ => ConsoleColor.White
        };
        Console.ForegroundColor = color;
        string tag = string.IsNullOrEmpty(deviceTag) ? "" : $"[{deviceTag}] ";
        Console.WriteLine($"[{ts}] [{level}] {tag}{msg}");
        Console.ResetColor();
    }

    // ---------- ПРИНУДИТЕЛЬНАЯ ОСТАНОВКА ВСЕХ АКТИВНЫХ СТРИМОВ ----------
    static void StopAllActiveStreams()
    {
        if (_activeWorkers.Count == 0)
        {
            Log("Нет активных стримов для остановки", "INFO");
            return;
        }

        Log($"Останавливаю {_activeWorkers.Count} активных стрима(ов)...", "INFO");

        foreach (var worker in _activeWorkers.ToList())
        {
            try
            {
                worker.Stop();
                Log($"Стрим для {worker.Device.Name} остановлен", "SUCCESS", worker.Device.Name);
            }
            catch (Exception ex)
            {
                Log($"Ошибка при остановке стрима {worker.Device.Name}: {ex.Message}", "ERROR");
            }
        }

        _activeWorkers.Clear();
        Thread.Sleep(1000);
        Log("Все активные стримы остановлены", "SUCCESS");
    }

    // ---------- ПРОВЕРКА ПОДКЛЮЧЕНИЯ К СЕТИ ----------
    static bool IsConnectedToNetwork(string networkName)
    {
        try
        {
            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "netsh",
                    Arguments = "wlan show interfaces",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.GetEncoding(866)
                }
            };

            process.Start();
            string output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();

            if (output.Contains(networkName))
            {
                string[] connectedMarkers = { "подключено", "Connected" };
                foreach (string marker in connectedMarkers)
                {
                    int idx = output.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
                    if (idx >= 0)
                    {
                        string context = output.Substring(Math.Max(0, idx - 200),
                                                         Math.Min(400, output.Length - Math.Max(0, idx - 200)));
                        if (context.Contains(networkName))
                        {
                            return true;
                        }
                    }
                }
            }
            return false;
        }
        catch (Exception e)
        {
            Log($"Ошибка при проверке подключения: {e.Message}", "ERROR");
            return false;
        }
    }

    // ---------- ПОДКЛЮЧЕНИЕ К СЕТИ ----------
    static bool ConnectToNetwork(string networkName, string password, bool forceReconnect = false)
    {
        Log($"Подключение к сети {networkName}...", "INFO");

        try
        {
            // Если уже подключены к этой сети и не требуется переподключение
            if (IsConnectedToNetwork(networkName) && !forceReconnect)
            {
                Log($"Уже подключен к сети {networkName}", "SUCCESS");
                return true;
            }

            // Сначала отключаемся от текущей сети
            RunNetshCommand("wlan disconnect", false);
            Thread.Sleep(2000);

            // Создаем профиль WiFi
            string profileXml = $@"<?xml version=""1.0""?>
<WLANProfile xmlns=""http://www.microsoft.com/networking/WLAN/profile/v1"">
    <name>{networkName}</name>
    <SSIDConfig>
        <SSID>
            <name>{networkName}</name>
        </SSID>
    </SSIDConfig>
    <connectionType>ESS</connectionType>
    <connectionMode>manual</connectionMode>
    <MSM>
        <security>
            <authEncryption>
                <authentication>WPA2PSK</authentication>
                <encryption>AES</encryption>
                <useOneX>false</useOneX>
            </authEncryption>
            <sharedKey>
                <keyType>passPhrase</keyType>
                <protected>false</protected>
                <keyMaterial>{password}</keyMaterial>
            </sharedKey>
        </security>
    </MSM>
</WLANProfile>";

            string tempFile = Path.Combine(Path.GetTempPath(), $"wifi_{Guid.NewGuid()}.xml");
            File.WriteAllText(tempFile, profileXml, Encoding.UTF8);

            try
            {
                // Удаляем старый профиль, добавляем новый
                RunNetshCommand($"wlan delete profile name=\"{networkName}\"", false);
                Thread.Sleep(1000);

                string addResult = RunNetshCommand($"wlan add profile filename=\"{tempFile}\"");

                if (addResult.Contains("added") || addResult.Contains("успешно") || addResult.Contains("добавлен"))
                {
                    // Подключаемся
                    string connectResult = RunNetshCommand($"wlan connect name=\"{networkName}\"");

                    if (connectResult.Contains("completed") || connectResult.Contains("успешно") || connectResult.Contains("connected"))
                    {
                        // Ждем подключения
                        for (int i = 0; i < 15; i++)
                        {
                            Thread.Sleep(1000);
                            if (IsConnectedToNetwork(networkName))
                            {
                                Log($"Подключение к {networkName} установлено!", "SUCCESS");
                                Thread.Sleep(3000); // Даем время на полное установление соединения
                                return true;
                            }
                            Console.Write(".");
                        }
                        Console.WriteLine();
                        Log("Таймаут подключения", "ERROR");
                    }
                }
            }
            finally
            {
                if (File.Exists(tempFile)) File.Delete(tempFile);
            }
        }
        catch (Exception e)
        {
            Log($"Ошибка при подключении: {e.Message}", "ERROR");
        }

        return false;
    }

    // ---------- ВЫПОЛНЕНИЕ КОМАНД netsh ----------
    static string RunNetshCommand(string arguments, bool showErrors = true)
    {
        try
        {
            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "netsh",
                    Arguments = arguments,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.GetEncoding(866),
                    StandardErrorEncoding = Encoding.GetEncoding(866)
                }
            };

            process.Start();
            string output = process.StandardOutput.ReadToEnd();
            string error = process.StandardError.ReadToEnd();
            process.WaitForExit();

            if (!string.IsNullOrEmpty(error) && showErrors)
            {
                Log($"netsh error: {error}", "WARN");
            }

            return output + error;
        }
        catch (Exception e)
        {
            Log($"Ошибка выполнения netsh: {e.Message}", "ERROR");
            return "";
        }
    }

    // ---------- ПРОВЕРКА ДОСТУПНОСТИ ESP ----------
    static bool CheckEspAvailability(string ip, int port, int timeout = 1000)
    {
        try
        {
            using (TcpClient client = new TcpClient())
            {
                IAsyncResult result = client.BeginConnect(ip, port, null, null);
                bool success = result.AsyncWaitHandle.WaitOne(timeout);
                if (!success) return false;
                client.EndConnect(result);

                using (NetworkStream stream = client.GetStream())
                {
                    stream.WriteTimeout = timeout;
                    stream.ReadTimeout = timeout;

                    // Отправляем простую команду PING
                    byte[] ping = Encoding.UTF8.GetBytes("PING\n");
                    stream.Write(ping, 0, ping.Length);

                    byte[] buffer = new byte[1024];
                    int bytesRead = stream.Read(buffer, 0, buffer.Length);
                    string response = Encoding.UTF8.GetString(buffer, 0, bytesRead);

                    return !string.IsNullOrEmpty(response);
                }
            }
        }
        catch { return false; }
    }

    // ---------- ОТПРАВКА КОМАНДЫ НА ESP ----------
    static string SendCommandToEsp(string ip, int port, string command, int timeout = 3000)
    {
        try
        {
            using (TcpClient client = new TcpClient())
            {
                IAsyncResult result = client.BeginConnect(ip, port, null, null);
                bool success = result.AsyncWaitHandle.WaitOne(timeout);
                if (!success) return "ERROR: Connection failed";
                client.EndConnect(result);

                using (NetworkStream stream = client.GetStream())
                {
                    stream.WriteTimeout = timeout;
                    stream.ReadTimeout = timeout;
                    byte[] cmdBytes = Encoding.UTF8.GetBytes(command + "\n");
                    stream.Write(cmdBytes, 0, cmdBytes.Length);

                    byte[] buffer = new byte[4096];
                    int bytesRead = stream.Read(buffer, 0, buffer.Length);
                    return Encoding.UTF8.GetString(buffer, 0, bytesRead);
                }
            }
        }
        catch (Exception e) { return $"ERROR: {e.Message}"; }
    }

    // ---------- НОВЫЙ МЕТОД: БЕЗОПАСНАЯ ПРОВЕРКА И ПОДКЛЮЧЕНИЕ ----------
    static string SafelyConnectToEsp(EspDeviceConfig device, Config globalConfig, string operationName = "операции")
    {
        Console.Clear();
        Log($"=== {operationName.ToUpper()} для {device.Name} ===", "INFO");

        // Проверяем, подключены ли уже к нужной сети
        bool isConnected = IsConnectedToNetwork(device.ApSsid);

        if (!isConnected)
        {
            Console.WriteLine($"\nДля работы с {device.Name} необходимо подключиться к его WiFi сети.");
            Console.WriteLine($"\nСеть: {device.ApSsid}");
            Console.WriteLine($"Пароль: {device.ApPassword}");

            Console.WriteLine("\nВыберите способ подключения:");
            Console.WriteLine("1. Автоматическое подключение");
            Console.WriteLine("2. Показать инструкцию для ручного подключения");
            Console.WriteLine("3. Отмена");

            Console.Write("\nВаш выбор (1-3): ");
            string choice = Console.ReadLine();

            switch (choice)
            {
                case "1":
                    if (!ConnectToNetwork(device.ApSsid, device.ApPassword))
                    {
                        Log($"Не удалось подключиться к WiFi сети {device.ApSsid}", "ERROR");
                        return null;
                    }
                    break;
                case "2":
                    ShowManualConnectionInstructions(device.ApSsid, device.ApPassword);
                    if (!IsConnectedToNetwork(device.ApSsid))
                    {
                        Log($"Не удалось подключиться к WiFi сети {device.ApSsid}", "ERROR");
                        return null;
                    }
                    break;
                default:
                    return null;
            }
        }
        else
        {
            Log($"Уже подключен к сети {device.ApSsid}", "INFO");
        }

        // Ждем, чтобы ESP могла принять соединение
        Log("Ожидание готовности ESP...", "INFO");
        Thread.Sleep(3000);

        // Пробуем подключиться к ESP с несколькими попытками
        int maxAttempts = 5;
        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            Log($"Попытка подключения {attempt}/{maxAttempts}...", "INFO");

            if (CheckEspAvailability(device.ApIp, device.Port, 2000))
            {
                Log($"{device.Name} доступен по адресу {device.ApIp}", "SUCCESS", device.Name);
                return device.ApIp;
            }

            if (attempt < maxAttempts)
            {
                Log("ESP не отвечает, жду 2 секунды...", "WARN");
                Thread.Sleep(2000);
            }
        }

        // Если ESP не отвечает
        Log($"Не удалось подключиться к {device.Name}", "ERROR", device.Name);
        Console.WriteLine($"\nВозможные причины:");
        Console.WriteLine($"1. ESP не включена или не раздает WiFi");
        Console.WriteLine($"2. ESP уже стримит и не принимает новые соединения");
        Console.WriteLine($"3. Неправильные настройки IP/порта");

        Console.WriteLine($"\nРекомендуемые действия:");
        Console.WriteLine($"1. Нажмите кнопку RESET на {device.Name}");
        Console.WriteLine($"2. Подождите 10 секунд после перезагрузки");
        Console.WriteLine($"3. Попробуйте снова");

        Console.WriteLine($"\nДля принудительного переподключения нажмите 'R'");
        Console.WriteLine($"Для отмены нажмите любую другую клавишу...");

        if (Console.ReadKey(true).Key == ConsoleKey.R)
        {
            // Принудительное переподключение
            Log("Принудительное переподключение...", "INFO");
            RunNetshCommand("wlan disconnect", false);
            Thread.Sleep(3000);

            if (ConnectToNetwork(device.ApSsid, device.ApPassword, true))
            {
                Thread.Sleep(5000); // Даем ESP время на инициализацию

                if (CheckEspAvailability(device.ApIp, device.Port, 3000))
                {
                    Log($"{device.Name} теперь доступен!", "SUCCESS", device.Name);
                    return device.ApIp;
                }
            }
        }

        return null;
    }

    // ---------- ИНСТРУКЦИЯ ДЛЯ РУЧНОГО ПОДКЛЮЧЕНИЯ ----------
    static void ShowManualConnectionInstructions(string networkName, string password)
    {
        Console.Clear();
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("╔══════════════════════════════════════════════════════════════╗");
        Console.WriteLine("║               РУЧНОЕ ПОДКЛЮЧЕНИЕ К ESP                      ║");
        Console.WriteLine("╚══════════════════════════════════════════════════════════════╝");
        Console.ResetColor();
        Console.WriteLine($"\n1. Подключитесь к WiFi сети: {networkName}");
        Console.WriteLine($"2. Используйте пароль: {password}");
        Console.WriteLine("\n3. Вернитесь в это окно и нажмите Enter");
        Console.Write("\nНажмите Enter, когда подключитесь...");
        Console.ReadLine();
    }

    // ---------- ОТПРАВКА ДАННЫХ WIFI НА ESP ----------
    static bool SendWifiCredentialsToEsp(string espIp, int espPort, string ssid, string pass, string deviceTag)
    {
        Log($"Отправка данных WiFi на {deviceTag}...", "INFO", deviceTag);
        try
        {
            using (TcpClient client = new TcpClient())
            {
                client.Connect(espIp, espPort);
                using (NetworkStream stream = client.GetStream())
                {
                    string data = $"SET\n{ssid}\n{pass}\n";
                    byte[] bytes = Encoding.UTF8.GetBytes(data);
                    stream.Write(bytes, 0, bytes.Length);

                    byte[] buffer = new byte[1024];
                    int bytesRead = stream.Read(buffer, 0, buffer.Length);
                    string response = Encoding.UTF8.GetString(buffer, 0, bytesRead);

                    if (response.Contains("OK") || response.Contains("Success"))
                    {
                        Log($"Данные WiFi отправлены успешно", "SUCCESS", deviceTag);
                        return true;
                    }
                    else
                    {
                        Log($"Ошибка: {response.Trim()}", "WARN", deviceTag);
                        return false;
                    }
                }
            }
        }
        catch (Exception e)
        {
            Log($"Ошибка: {e.Message}", "ERROR", deviceTag);
            return false;
        }
    }

    // ---------- ЧТЕНИЕ И СОХРАНЕНИЕ КОНФИГУРАЦИИ ----------
    static Config LoadConfig()
    {
        try
        {
            if (File.Exists(ConfigFile))
            {
                string json = File.ReadAllText(ConfigFile);
                var config = JsonSerializer.Deserialize<Config>(json);

                if (config.EspDevices == null || config.EspDevices.Count == 0)
                {
                    Log("Обнаружен старый формат config.json. Выполняю автоматическое обновление...", "INFO");
                    config.EspDevices = new List<EspDeviceConfig>();

                    config.EspDevices.Add(new EspDeviceConfig
                    {
                        Name = "ESP32_Cos",
                        ApSsid = config.Esp1NetworkName,
                        ApPassword = config.EspNetworkPassword,
                        ApIp = "192.168.4.1",
                        Port = 8888
                    });

                    config.EspDevices.Add(new EspDeviceConfig
                    {
                        Name = "ESP32_Sin",
                        ApSsid = config.Esp2NetworkName,
                        ApPassword = config.EspNetworkPassword,
                        ApIp = "192.168.4.1",
                        Port = 8888
                    });

                    SaveConfig(config);
                    Log("Конфигурация успешно обновлена до нового формата с двумя устройствами.", "SUCCESS");
                }
                return config;
            }
            else
            {
                Config defaultConfig = new Config
                {
                    PcWifiSsid = "MyHomeWiFi",
                    PcWifiPassword = "mypassword122",
                    Esp1NetworkName = "ESP32_Cos_Streamer",
                    Esp2NetworkName = "ESP32_Sin_Streamer",
                    EspNetworkPassword = "12345678",
                    EspDevices = new List<EspDeviceConfig>
                    {
                        new EspDeviceConfig
                        {
                            Name = "ESP32_Cos",
                            ApSsid = "ESP32_Cos_Streamer",
                            ApPassword = "12345678",
                            ApIp = "192.168.4.1",
                            Port = 8888
                        },
                        new EspDeviceConfig
                        {
                            Name = "ESP32_Sin",
                            ApSsid = "ESP32_Sin_Streamer",
                            ApPassword = "12345678",
                            ApIp = "192.168.4.1",
                            Port = 8888
                        }
                    }
                };

                SaveConfig(defaultConfig);
                Log($"Создан новый файл конфигурации: {ConfigFile}", "INFO");
                return defaultConfig;
            }
        }
        catch (Exception e)
        {
            Log($"Ошибка загрузки конфигурации: {e.Message}", "ERROR");
            return new Config();
        }
    }

    static void SaveConfig(Config config)
    {
        try
        {
            string json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(ConfigFile, json);
        }
        catch (Exception e)
        {
            Log($"Ошибка сохранения конфигурации: {e.Message}", "ERROR");
        }
    }

    // ---------- КЛАСС ДЛЯ ПАРАЛЛЕЛЬНОГО СТРИМИНГА ----------
    class StreamWorker
    {
        public EspDeviceConfig Device { get; }
        public string Ip { get; }
        private volatile bool _running = false;
        private TcpClient _client;
        private NetworkStream _stream;
        private StreamReader _reader;
        private readonly Queue<string> _dataQueue = new Queue<string>();
        private readonly object _lock = new object();

        public StreamWorker(EspDeviceConfig device, string ip)
        {
            Device = device;
            Ip = ip;
        }

        public bool StartStreaming()
        {
            try
            {
                _client = new TcpClient();
                _client.Connect(Ip, Device.Port);
                _stream = _client.GetStream();
                _reader = new StreamReader(_stream);

                byte[] cmdBytes = Encoding.UTF8.GetBytes("START_STREAM\n");
                _stream.Write(cmdBytes, 0, cmdBytes.Length);

                _running = true;

                lock (_activeWorkers)
                {
                    _activeWorkers.Add(this);
                }

                Log($"Работник для {Device.Name} добавлен в активные", "INFO", Device.Name);

                // Чтение данных
                while (_running && _client.Connected)
                {
                    if (_stream.DataAvailable)
                    {
                        string data = _reader.ReadLine();
                        if (!string.IsNullOrEmpty(data))
                        {
                            lock (_lock)
                            {
                                if (_dataQueue.Count > 100) _dataQueue.Dequeue();
                                _dataQueue.Enqueue(data);
                            }
                        }
                    }
                    Thread.Sleep(10);
                }

                return true;
            }
            catch (Exception ex)
            {
                Log($"Ошибка в потоке для {Device.Name}: {ex.Message}", "ERROR", Device.Name);
                return false;
            }
        }

        public List<string> GetRecentData()
        {
            lock (_lock)
            {
                return _dataQueue.ToList();
            }
        }

        public void Stop()
        {
            _running = false;
            try
            {
                if (_stream != null)
                {
                    byte[] stopBytes = Encoding.UTF8.GetBytes("STOP_STREAM\n");
                    _stream.Write(stopBytes, 0, stopBytes.Length);
                    Thread.Sleep(100);
                }
                _client?.Close();

                lock (_activeWorkers)
                {
                    _activeWorkers.Remove(this);
                }
            }
            catch { }
        }
    }

    // ---------- ОТОБРАЖЕНИЕ ПАРАЛЛЕЛЬНЫХ ДАННЫХ ----------
    static void DisplayParallelStreams(List<StreamWorker> workers)
    {
        Console.WriteLine("\n" + new string('=', 80));
        Console.WriteLine("ПАРАЛЛЕЛЬНЫЙ СТРИМИНГ | ЛЕВО: КОСИНУС (ESP1) | ПРАВО: СИНУС (ESP2)");
        Console.WriteLine(new string('=', 80));

        try
        {
            while (true)
            {
                Console.SetCursorPosition(0, Console.CursorTop);

                if (workers.Count >= 2 && workers[0] != null && workers[1] != null)
                {
                    var data1 = workers[0].GetRecentData();
                    var data2 = workers[1].GetRecentData();

                    int maxLines = Math.Max(data1.Count, data2.Count);
                    int startIdx = Math.Max(0, maxLines - 10);

                    for (int i = startIdx; i < maxLines; i++)
                    {
                        string left = i < data1.Count ? data1[i] : "";
                        string right = i < data2.Count ? data2[i] : "";

                        string leftFormatted = string.IsNullOrEmpty(left) ? "" : $"{left,-35}";
                        string rightFormatted = string.IsNullOrEmpty(right) ? "" : $"{right,-35}";

                        Console.WriteLine($"[{workers[0].Device.Name}] {leftFormatted} | [{workers[1].Device.Name}] {rightFormatted}");
                    }
                }

                Thread.Sleep(500);
            }
        }
        catch (Exception ex)
        {
            Log($"Ошибка отображения: {ex.Message}", "ERROR");
        }
    }

    // ---------- ГЛАВНОЕ МЕНЮ ----------
    static string GetMenuChoice()
    {
        Console.WriteLine("\nГЛАВНОЕ МЕНЮ:");
        Console.WriteLine("--- ОДИНОЧНЫЕ ОПЕРАЦИИ ---");
        Console.WriteLine("1. Настроить ESP на домашнюю WiFi");
        Console.WriteLine("2. Начать стриминг с одной ESP");
        Console.WriteLine("3. Изменить конфигурацию");
        Console.WriteLine("4. Найти ESP в домашней сети");
        Console.WriteLine("5. Показать текущую конфигурацию");

        Console.WriteLine("\n--- ПАРАЛЛЕЛЬНЫЕ ОПЕРАЦИИ ---");
        Console.WriteLine("6. Параллельная настройка двух ESP");
        Console.WriteLine("7. Параллельный стриминг с двух ESP");

        Console.WriteLine("\n--- СЛУЖЕБНЫЕ ---");
        Console.WriteLine("8. Остановить все активные стримы");
        Console.WriteLine("9. Проверить доступность ESP");
        Console.WriteLine("10. Выход");
        Console.WriteLine("11. Тестирование команд ESP");

        Console.WriteLine("\n[h] Помощь по командам ESP");

        Console.Write("\nВаш выбор (1-11): ");
        return Console.ReadLine()?.ToLower().Trim() ?? "";
    }

    // ---------- ОТОБРАЖЕНИЕ ЗАГОЛОВКА ----------
    static void ShowHeader(Config config)
    {
        Console.ForegroundColor = ConsoleColor.Magenta;
        Console.WriteLine("╔══════════════════════════════════════════════════════════════╗");
        Console.WriteLine("║       ESP32 Dual Stream Manager - Параллельный стриминг     ║");
        Console.WriteLine("╚══════════════════════════════════════════════════════════════╝");
        Console.ResetColor();

        Console.WriteLine($"\nЗарегистрированные устройства: {config.EspDevices.Count}");

        foreach (var device in config.EspDevices)
        {
            bool isConnected = IsConnectedToNetwork(device.ApSsid);
            Console.ForegroundColor = isConnected ? ConsoleColor.Green : ConsoleColor.Yellow;
            Console.WriteLine($"  {(isConnected ? "✓" : "✗")} {device.Name}: {device.ApSsid}");
            Console.ResetColor();
        }

        Console.WriteLine($"\nАктивных стримов: {_activeWorkers.Count}");
        if (_activeWorkers.Count > 0)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("  Внимание: активные стримы занимают соединение с ESP!");
            Console.ResetColor();
        }

        Console.WriteLine(new string('-', 60));
    }

    // ==================== ОСНОВНЫЕ ФУНКЦИИ ====================

    // ---------- 1. НАСТРОЙКА ОДНОЙ ESP ----------
    static void ConfigureSingleEsp(Config config)
    {
        if (config.EspDevices.Count == 0)
        {
            Log("Нет настроенных устройств.", "ERROR");
            WaitForKey();
            return;
        }

        Console.WriteLine("\nВыберите устройство для настройки:");
        for (int i = 0; i < config.EspDevices.Count; i++)
        {
            Console.WriteLine($"{i + 1}. {config.EspDevices[i].Name} ({config.EspDevices[i].ApSsid})");
        }
        Console.Write($"\nВаш выбор (1-{config.EspDevices.Count}): ");

        if (!int.TryParse(Console.ReadLine(), out int choice) || choice < 1 || choice > config.EspDevices.Count)
        {
            Log("Неверный выбор.", "ERROR");
            return;
        }

        var device = config.EspDevices[choice - 1];

        // Останавливаем все активные стримы ПЕРЕД настройкой
        StopAllActiveStreams();

        string espIp = SafelyConnectToEsp(device, config, "НАСТРОЙКА WIFI");

        if (string.IsNullOrEmpty(espIp)) return;

        Console.WriteLine($"\n=== НАСТРОЙКА ДОМАШНЕЙ WIFI ДЛЯ {device.Name} ===");
        Console.WriteLine($"\nТекущие данные домашней WiFi:");
        Console.WriteLine($"SSID: {config.PcWifiSsid}");
        Console.WriteLine($"Пароль: {new string('*', config.PcWifiPassword.Length)}");

        Console.Write("\nИспользовать эти данные? (y/n): ");
        if (Console.ReadLine()?.ToLower() != "y")
        {
            Console.Write("Введите SSID вашей домашней WiFi: ");
            config.PcWifiSsid = Console.ReadLine();
            Console.Write("Введите пароль: ");
            config.PcWifiPassword = Console.ReadLine();
            SaveConfig(config);
        }

        bool success = SendWifiCredentialsToEsp(espIp, device.Port, config.PcWifiSsid, config.PcWifiPassword, device.Name);
        if (success)
        {
            Log($"\n{device.Name} перезагрузится для подключения к домашней сети.", "INFO");
            for (int i = 10; i > 0; i--)
            {
                Console.Write($"\rПерезагрузка... {i} секунд ");
                Thread.Sleep(1000);
            }
            Console.WriteLine();
        }
        WaitForKey();
    }

    // ---------- 2. СТРИМИНГ С ОДНОЙ ESP ----------
    static void StreamFromSingleEsp(Config config)
    {
        if (config.EspDevices.Count == 0)
        {
            Log("Нет настроенных устройств.", "ERROR");
            WaitForKey();
            return;
        }

        Console.WriteLine("\nВыберите устройство для стриминга:");
        for (int i = 0; i < config.EspDevices.Count; i++)
        {
            Console.WriteLine($"{i + 1}. {config.EspDevices[i].Name} ({config.EspDevices[i].ApSsid})");
        }
        Console.Write($"\nВаш выбор (1-{config.EspDevices.Count}): ");

        if (!int.TryParse(Console.ReadLine(), out int choice) || choice < 1 || choice > config.EspDevices.Count)
        {
            Log("Неверный выбор.", "ERROR");
            return;
        }

        var device = config.EspDevices[choice - 1];

        // Останавливаем все активные стримы перед новым стримингом
        StopAllActiveStreams();

        string espIp = SafelyConnectToEsp(device, config, "СТРИМИНГ ДАННЫХ");
        if (string.IsNullOrEmpty(espIp)) return;

        try
        {
            using (TcpClient client = new TcpClient())
            {
                client.Connect(espIp, device.Port);
                using (NetworkStream stream = client.GetStream())
                using (StreamReader reader = new StreamReader(stream))
                {
                    byte[] cmdBytes = Encoding.UTF8.GetBytes("START_STREAM\n");
                    stream.Write(cmdBytes, 0, cmdBytes.Length);

                    string response = reader.ReadLine();
                    Log($"Ответ: {response}", "INFO", device.Name);
                    Log("Стриминг начат. Нажмите 'Q' для остановки...", "SUCCESS", device.Name);

                    DateTime lastDataTime = DateTime.Now;
                    while (true)
                    {
                        if (Console.KeyAvailable && Console.ReadKey(true).Key == ConsoleKey.Q) break;

                        if (stream.DataAvailable)
                        {
                            string data = reader.ReadLine();
                            if (!string.IsNullOrEmpty(data))
                            {
                                lastDataTime = DateTime.Now;
                                Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] [{device.Name}] {data}");
                            }
                        }
                        else if ((DateTime.Now - lastDataTime).TotalSeconds > 5)
                        {
                            try
                            {
                                byte[] ping = Encoding.UTF8.GetBytes("PING\n");
                                stream.Write(ping, 0, ping.Length);
                            }
                            catch
                            {
                                Log("Потеряно соединение", "ERROR", device.Name);
                                break;
                            }
                        }
                        Thread.Sleep(10);
                    }

                    try
                    {
                        byte[] stopBytes = Encoding.UTF8.GetBytes("STOP_STREAM\n");
                        stream.Write(stopBytes, 0, stopBytes.Length);
                        Log("Стриминг остановлен.", "INFO", device.Name);
                    }
                    catch { }
                }
            }
        }
        catch (Exception e)
        {
            Log($"Ошибка: {e.Message}", "ERROR", device.Name);
        }
        WaitForKey();
    }

    // ---------- 6. ПАРАЛЛЕЛЬНАЯ НАСТРОЙКА ДВУХ ESP ----------
    static void ConfigureTwoEspParallel(Config config)
    {
        if (config.EspDevices.Count < 2)
        {
            Log("Требуется минимум 2 устройства.", "ERROR");
            WaitForKey();
            return;
        }

        Console.WriteLine("\n=== ПАРАЛЛЕЛЬНАЯ НАСТРОЙКА ДВУХ ESP ===");
        Console.WriteLine($"\nБудет выполнена настройка для:");
        Console.WriteLine($"1. {config.EspDevices[0].Name}");
        Console.WriteLine($"2. {config.EspDevices[1].Name}");

        Console.Write("\nИспользовать текущие данные WiFi? (y/n): ");
        if (Console.ReadLine()?.ToLower() != "y")
        {
            Console.Write("Введите SSID домашней WiFi: ");
            config.PcWifiSsid = Console.ReadLine();
            Console.Write("Введите пароль: ");
            config.PcWifiPassword = Console.ReadLine();
            SaveConfig(config);
        }

        bool allSuccess = true;

        // Настройка первой ESP
        Log("\n=== НАСТРОЙКА ПЕРВОЙ ESP ===", "INFO");
        StopAllActiveStreams(); // Останавливаем все стримы перед настройкой

        string ip1 = SafelyConnectToEsp(config.EspDevices[0], config, "НАСТРОЙКА ESP1");
        if (!string.IsNullOrEmpty(ip1))
        {
            bool success1 = SendWifiCredentialsToEsp(ip1, config.EspDevices[0].Port,
                config.PcWifiSsid, config.PcWifiPassword, config.EspDevices[0].Name);
            allSuccess &= success1;

            if (success1)
            {
                Log("Ожидание 10 секунд...", "INFO");
                Thread.Sleep(10000);
            }
        }
        else allSuccess = false;

        // Настройка второй ESP
        Log("\n=== НАСТРОЙКА ВТОРОЙ ESP ===", "INFO");
        StopAllActiveStreams(); // Снова останавливаем

        string ip2 = SafelyConnectToEsp(config.EspDevices[1], config, "НАСТРОЙКА ESP2");
        if (!string.IsNullOrEmpty(ip2))
        {
            bool success2 = SendWifiCredentialsToEsp(ip2, config.EspDevices[1].Port,
                config.PcWifiSsid, config.PcWifiPassword, config.EspDevices[1].Name);
            allSuccess &= success2;
        }
        else allSuccess = false;

        if (allSuccess)
        {
            Log("\nОбе ESP настроены и перезагружаются.", "SUCCESS");
            Log("Через 20 секунд используйте 'Найти ESP в домашней сети'.", "INFO");
            Thread.Sleep(20000);
        }
        WaitForKey();
    }

    // ---------- 7. ПАРАЛЛЕЛЬНЫЙ СТРИМИНГ С ДВУХ ESP ----------
    static void StreamFromTwoEspParallel(Config config)
    {
        if (config.EspDevices.Count < 2)
        {
            Log("Требуется минимум 2 устройства.", "ERROR");
            WaitForKey();
            return;
        }

        Console.WriteLine("\n=== ПАРАЛЛЕЛЬНЫЙ СТРИМИНГ С ДВУХ ESP ===");
        Log("Эта операция выполняется в ДВА ЭТАПА:", "INFO");
        Log("1. Сначала подключимся к первой ESP и начнем стриминг", "INFO");
        Log("2. Затем подключимся ко второй ESP и начнем стриминг", "INFO");
        Log("3. Данные с обоих устройств будут отображаться параллельно", "INFO");

        Console.Write("\nНажмите Enter чтобы начать...");
        Console.ReadLine();

        // Останавливаем все активные стримы перед началом
        StopAllActiveStreams();

        var streamTasks = new List<Task<bool>>();
        var workers = new List<StreamWorker>();

        // Этап 1: Первое устройство
        Log($"\n=== ЭТАП 1: ПОДКЛЮЧЕНИЕ К {config.EspDevices[0].Name} ===", "INFO");
        string ip1 = SafelyConnectToEsp(config.EspDevices[0], config, "СТРИМИНГ ESP1");
        if (string.IsNullOrEmpty(ip1))
        {
            Log("Не удалось подключиться к первой ESP. Прерывание.", "ERROR");
            WaitForKey();
            return;
        }

        var worker1 = new StreamWorker(config.EspDevices[0], ip1);
        workers.Add(worker1);
        streamTasks.Add(Task.Run(() => worker1.StartStreaming()));

        Log($"Стриминг с {config.EspDevices[0].Name} запущен в фоне.", "SUCCESS");
        Thread.Sleep(3000);

        // Этап 2: Второе устройство
        Log($"\n=== ЭТАП 2: ПОДКЛЮЧЕНИЕ К {config.EspDevices[1].Name} ===", "INFO");

        Log("Отключаемся от первой сети WiFi...", "INFO");
        RunNetshCommand("wlan disconnect", false);
        Thread.Sleep(3000);

        string ip2 = SafelyConnectToEsp(config.EspDevices[1], config, "СТРИМИНГ ESP2");
        if (string.IsNullOrEmpty(ip2))
        {
            Log("Не удалось подключиться ко второй ESP. Останавливаем первый стрим.", "ERROR");
            StopAllActiveStreams();
            WaitForKey();
            return;
        }

        var worker2 = new StreamWorker(config.EspDevices[1], ip2);
        workers.Add(worker2);
        streamTasks.Add(Task.Run(() => worker2.StartStreaming()));

        Log($"Стриминг с {config.EspDevices[1].Name} запущен в фоне.", "SUCCESS");
        Log("\n=== ПАРАЛЛЕЛЬНЫЙ СТРИМИНГ АКТИВЕН ===", "SUCCESS");
        Log("Данные с двух ESP:", "INFO");
        Log($"  СЛЕВА:  {config.EspDevices[0].Name} (косинус)", "INFO");
        Log($"  СПРАВА: {config.EspDevices[1].Name} (синус)", "INFO");
        Log("\nНажмите 'Q' чтобы остановить оба стрима...", "INFO");

        var displayTask = Task.Run(() => DisplayParallelStreams(workers));

        while (true)
        {
            if (Console.KeyAvailable && Console.ReadKey(true).Key == ConsoleKey.Q)
            {
                Log("\nОстанавливаю оба стрима...", "INFO");
                break;
            }
            Thread.Sleep(100);
        }

        StopAllActiveStreams();

        try
        {
            Task.WaitAll(streamTasks.ToArray(), 2000);
        }
        catch (Exception ex)
        {
            Log($"Ошибка при остановке: {ex.Message}", "ERROR");
        }

        try
        {
            if (displayTask.Status == TaskStatus.Running)
            {
                var cts = new CancellationTokenSource();
                cts.CancelAfter(1000);
                displayTask.Wait(cts.Token);
            }
        }
        catch { }

        Log("Параллельный стриминг остановлен.", "SUCCESS");
        WaitForKey();
    }

    // ---------- 8. ОСТАНОВИТЬ ВСЕ АКТИВНЫЕ СТРИМЫ ----------
    static void StopAllStreamsMenu()
    {
        Console.Clear();
        Log("=== ОСТАНОВКА ВСЕХ АКТИВНЫХ СТРИМОВ ===", "INFO");
        StopAllActiveStreams();
        WaitForKey();
    }

    // ---------- 9. ПРОВЕРИТЬ ДОСТУПНОСТЬ ESP ----------
    static void CheckEspAvailabilityMenu(Config config)
    {
        Console.Clear();
        Log("=== ПРОВЕРКА ДОСТУПНОСТИ ESP ===", "INFO");

        if (config.EspDevices.Count == 0)
        {
            Log("Нет настроенных устройств.", "ERROR");
            WaitForKey();
            return;
        }

        Console.WriteLine("\nВыберите устройство для проверки:");
        for (int i = 0; i < config.EspDevices.Count; i++)
        {
            Console.WriteLine($"{i + 1}. {config.EspDevices[i].Name} ({config.EspDevices[i].ApSsid})");
        }
        Console.WriteLine($"{config.EspDevices.Count + 1}. Все устройства");

        Console.Write($"\nВаш выбор (1-{config.EspDevices.Count + 1}): ");

        if (!int.TryParse(Console.ReadLine(), out int choice) || choice < 1 || choice > config.EspDevices.Count + 1)
        {
            Log("Неверный выбор.", "ERROR");
            WaitForKey();
            return;
        }

        if (choice == config.EspDevices.Count + 1)
        {
            foreach (var device in config.EspDevices)
            {
                Console.WriteLine($"\nПроверка {device.Name}...");
                bool isConnected = IsConnectedToNetwork(device.ApSsid);

                if (isConnected)
                {
                    Console.Write($"  WiFi: ✓ Подключен");

                    bool isAvailable = CheckEspAvailability(device.ApIp, device.Port, 2000);
                    if (isAvailable)
                    {
                        Console.WriteLine($", ESP: ✓ Отвечает");
                        Log($"{device.Name} доступен", "SUCCESS", device.Name);
                    }
                    else
                    {
                        Console.WriteLine($", ESP: ✗ Не отвечает");
                        Log($"{device.Name} не отвечает (возможно, занят стримингом)", "WARN", device.Name);
                    }
                }
                else
                {
                    Console.WriteLine($"  WiFi: ✗ Не подключен");
                    Log($"{device.Name}: не подключен к WiFi", "WARN", device.Name);
                }
            }
        }
        else
        {
            var device = config.EspDevices[choice - 1];
            Console.WriteLine($"\nПроверка {device.Name}...");

            bool isConnected = IsConnectedToNetwork(device.ApSsid);

            if (isConnected)
            {
                Console.Write($"  WiFi: ✓ Подключен");

                bool isAvailable = CheckEspAvailability(device.ApIp, device.Port, 2000);
                if (isAvailable)
                {
                    Console.WriteLine($", ESP: ✓ Отвечает");

                    // Пробуем получить статус
                    try
                    {
                        string status = SendCommandToEsp(device.ApIp, device.Port, "STATUS", 3000);
                        Console.WriteLine($"\nСтатус ESP:\n{status}");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"\nНе удалось получить статус: {ex.Message}");
                    }
                }
                else
                {
                    Console.WriteLine($", ESP: ✗ Не отвечает");
                    Log("ESP не отвечает (возможно, занят стримингом)", "WARN", device.Name);
                }
            }
            else
            {
                Console.WriteLine($"  WiFi: ✗ Не подключен");
                Log("Не подключен к WiFi сети ESP", "WARN", device.Name);
            }
        }

        WaitForKey();
    }

    // ---------- 3. РЕДАКТИРОВАНИЕ КОНФИГУРАЦИИ ----------
    static void EditConfiguration(Config config)
    {
        Console.Clear();
        Console.WriteLine("=== РЕДАКТИРОВАНИЕ КОНФИГУРАЦИИ ===\n");

        Console.WriteLine("ГЛОБАЛЬНЫЕ НАСТРОЙКИ:");
        Console.Write($"SSID домашней WiFi [{config.PcWifiSsid}]: ");
        string input = Console.ReadLine();
        if (!string.IsNullOrEmpty(input)) config.PcWifiSsid = input;

        Console.Write($"Пароль домашней WiFi [***]: ");
        input = Console.ReadLine();
        if (!string.IsNullOrEmpty(input)) config.PcWifiPassword = input;

        Console.WriteLine($"\nРЕДАКТИРОВАНИЕ УСТРОЙСТВ (всего: {config.EspDevices.Count}):");

        for (int i = 0; i < config.EspDevices.Count; i++)
        {
            var device = config.EspDevices[i];
            Console.WriteLine($"\n--- Устройство {i + 1}: {device.Name} ---");

            Console.Write($"  Имя [{device.Name}]: ");
            input = Console.ReadLine();
            if (!string.IsNullOrEmpty(input)) device.Name = input;

            Console.Write($"  SSID сети (AP) [{device.ApSsid}]: ");
            input = Console.ReadLine();
            if (!string.IsNullOrEmpty(input)) device.ApSsid = input;

            Console.Write($"  Пароль сети [***]: ");
            input = Console.ReadLine();
            if (!string.IsNullOrEmpty(input)) device.ApPassword = input;

            Console.Write($"  IP в AP режиме [{device.ApIp}]: ");
            input = Console.ReadLine();
            if (!string.IsNullOrEmpty(input)) device.ApIp = input;

            Console.Write($"  Порт [{device.Port}]: ");
            input = Console.ReadLine();
            if (!string.IsNullOrEmpty(input) && int.TryParse(input, out int port))
                device.Port = port;
        }

        SaveConfig(config);
        Log("Конфигурация сохранена", "SUCCESS");
        WaitForKey();
    }

    // ---------- 4. ПОИСК ESP В СЕТИ ----------
    static void FindEspInHomeNetwork(Config config)
    {
        Console.Clear();
        Log("=== ПОИСК ESP В ДОМАШНЕЙ СЕТИ ===", "INFO");

        if (string.IsNullOrEmpty(config.PcWifiSsid))
        {
            Log("Сначала настройте домашнюю WiFi", "ERROR");
            WaitForKey();
            return;
        }

        Console.WriteLine($"\nПоиск всех ESP в сети...");

        string[] commonSubnets = { "192.168.1.", "192.168.0.", "192.168.100.", "192.168.137." };
        int foundCount = 0;

        foreach (var device in config.EspDevices)
        {
            Log($"\nПоиск {device.Name}...", "INFO");
            bool found = false;

            foreach (string subnet in commonSubnets)
            {
                for (int i = 2; i <= 30; i++)
                {
                    string testIp = subnet + i;
                    Console.Write($"\rПроверка {testIp}...");

                    if (CheckEspAvailability(testIp, device.Port, 150))
                    {
                        device.HomeIp = testIp;
                        Log($"Найдена {device.Name}: {testIp}", "SUCCESS", device.Name);
                        found = true;
                        foundCount++;
                        break;
                    }
                }
                if (found) break;
                Console.WriteLine();
            }
            if (!found) Log($"{device.Name} не найдена", "WARN", device.Name);
        }

        if (foundCount > 0)
        {
            SaveConfig(config);
            Log($"\nНайдено устройств: {foundCount}. Конфигурация сохранена.", "SUCCESS");
        }
        WaitForKey();
    }

    // ---------- 5. ПОКАЗАТЬ КОНФИГУРАЦИЮ ----------
    static void ShowCurrentConfig(Config config)
    {
        Console.Clear();
        Console.WriteLine("=== ТЕКУЩАЯ КОНФИГУРАЦИЯ ===");
        Console.WriteLine($"\nДомашняя WiFi:");
        Console.WriteLine($"  SSID: {config.PcWifiSsid}");
        Console.WriteLine($"  Пароль: {new string('*', config.PcWifiPassword.Length)}");

        Console.WriteLine($"\nУстройств: {config.EspDevices.Count}");
        foreach (var device in config.EspDevices)
        {
            Console.WriteLine($"\n--- {device.Name} ---");
            Console.WriteLine($"  Сеть AP: {device.ApSsid}");
            Console.WriteLine($"  Пароль AP: {new string('*', device.ApPassword.Length)}");
            Console.WriteLine($"  IP (AP): {device.ApIp}:{device.Port}");
            Console.WriteLine($"  IP (домашняя): {device.HomeIp ?? "не найден"}");
        }

        Console.WriteLine($"\nАктивных стримов: {_activeWorkers.Count}");
        if (_activeWorkers.Count > 0)
        {
            Console.WriteLine("  Активные устройства:");
            foreach (var worker in _activeWorkers)
            {
                Console.WriteLine($"    - {worker.Device.Name} ({worker.Ip})");
            }
        }

        Console.WriteLine($"\nФайл: {Path.GetFullPath(ConfigFile)}");
        WaitForKey();
    }

    // ---------- 11. ТЕСТИРОВАНИЕ КОМАНД ----------
    static void TestAllCommands(Config config)
    {
        Console.Clear();
        Log("=== ТЕСТИРОВАНИЕ КОМАНД ESP ===", "INFO");

        if (config.EspDevices.Count == 0)
        {
            Log("Нет устройств для тестирования.", "ERROR");
            WaitForKey();
            return;
        }

        Console.WriteLine("\nВыберите устройство для тестирования:");
        for (int i = 0; i < config.EspDevices.Count; i++)
        {
            Console.WriteLine($"{i + 1}. {config.EspDevices[i].Name}");
        }
        Console.Write($"\nВаш выбор (1-{config.EspDevices.Count}): ");

        if (!int.TryParse(Console.ReadLine(), out int choice) || choice < 1 || choice > config.EspDevices.Count)
        {
            Log("Неверный выбор.", "ERROR");
            return;
        }

        var device = config.EspDevices[choice - 1];

        // Останавливаем все активные стримы перед тестированием
        StopAllActiveStreams();

        string espIp = SafelyConnectToEsp(device, config, "ТЕСТИРОВАНИЕ");
        if (string.IsNullOrEmpty(espIp)) return;

        string[] testCommands = { "STATUS", "CLEAR", "START_STREAM" };
        foreach (var cmd in testCommands)
        {
            Console.Write($"\nТест команды '{cmd}' (Enter для пропуска): ");
            if (Console.ReadLine() == "")
            {
                string response = SendCommandToEsp(espIp, device.Port, cmd);
                Console.WriteLine($"Ответ: {response}");
                Thread.Sleep(1000);
            }
        }
        WaitForKey();
    }

    // ---------- ВСПОМОГАТЕЛЬНЫЕ МЕТОДЫ ----------
    static void WaitForKey(string message = "Нажмите любую клавишу для продолжения...")
    {
        Console.WriteLine($"\n{message}");
        Console.ReadKey(true);
    }

    static void ShowHelp()
    {
        Console.Clear();
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("=== СПРАВКА ПО КОМАНДАМ ESP32 ===");
        Console.ResetColor();
        Console.WriteLine("\nПоддерживаемые команды:");
        Console.WriteLine("  SET - настройка WiFi");
        Console.WriteLine("  STATUS - информация об устройстве");
        Console.WriteLine("  START_STREAM - начать стриминг");
        Console.WriteLine("  STOP_STREAM - остановить стриминг");
        Console.WriteLine("  CLEAR - очистить настройки WiFi");
        Console.WriteLine("\nВажная информация:");
        Console.WriteLine("  • ESP принимает только ОДНО TCP-соединение за раз");
        Console.WriteLine("  • Если ESP уже стримит, она не ответит на другие команды");
        Console.WriteLine("  • Перед любой операцией останавливайте активные стримы (меню 8)");
        Console.WriteLine("  • Если ESP не отвечает, перезагрузите ее кнопкой RESET");
        WaitForKey();
    }

    // ---------- ГЛАВНЫЙ ЦИКЛ ----------
    static void Main()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Console.Title = "ESP32 Dual Stream Manager - Параллельный стриминг";
        Console.OutputEncoding = Encoding.UTF8;

        Config config = LoadConfig();

        bool running = true;
        while (running)
        {
            Console.Clear();
            ShowHeader(config);
            string choice = GetMenuChoice();

            switch (choice)
            {
                case "1": ConfigureSingleEsp(config); break;
                case "2": StreamFromSingleEsp(config); break;
                case "3": EditConfiguration(config); break;
                case "4": FindEspInHomeNetwork(config); break;
                case "5": ShowCurrentConfig(config); break;
                case "6": ConfigureTwoEspParallel(config); break;
                case "7": StreamFromTwoEspParallel(config); break;
                case "8": StopAllStreamsMenu(); break;
                case "9": CheckEspAvailabilityMenu(config); break;
                case "10":
                    StopAllActiveStreams();
                    running = false;
                    Log("Выход...", "INFO");
                    break;
                case "11": TestAllCommands(config); break;
                case "h": ShowHelp(); break;
                default: Log("Неверный выбор. Нажмите 'h' для помощи.", "WARN"); WaitForKey(); break;
            }
        }
    }
}