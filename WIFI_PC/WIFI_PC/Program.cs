using System;
using System.IO;
using System.Text.Json;
using System.Diagnostics;
using System.Net.Sockets;
using System.Threading;

class Program
{
    const string ConfigFile = "config.json";

    // ---------- ЛОГИРОВАНИЕ ----------
    static void Log(string msg, string level = "INFO")
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
        Console.WriteLine($"[{ts}] [{level}] {msg}");
        Console.ResetColor();
    }

    // ---------- ЗАГРУЗКА КОНФИГА ----------
    static dynamic LoadConfig()
    {
        try
        {
            string json = File.ReadAllText(ConfigFile);
            return JsonSerializer.Deserialize<dynamic>(json);
        }
        catch (Exception e)
        {
            Log($"Ошибка при загрузке конфига: {e}", "ERROR");
            Environment.Exit(1);
            return null;
        }
    }

    // ---------- ПОДКЛЮЧЕНИЕ К Wi-Fi ----------
    static void ConnectToNetwork(string ssid)
    {
        Log($"Попытка подключения к сети {ssid}...", "INFO");
        try
        {
            Process.Start("netsh", $"wlan connect ssid=\"{ssid}\" name=\"{ssid}\"")?.WaitForExit();
            Thread.Sleep(5000);
            Log($"Подключение к {ssid} выполнено (или в процессе)...", "SUCCESS");
        }
        catch (Exception e)
        {
            Log($"Ошибка подключения: {e}", "ERROR");
        }
    }

    // ---------- ОТПРАВКА ДАННЫХ НА ESP ----------
    static void SendWifiCredentialsToEsp(string espIp, int espPort, string ssid, string pass)
    {
        Log("Отправка данных на ESP...", "INFO");
        try
        {
            using (TcpClient client = new TcpClient())
            {
                client.Connect(espIp, espPort);
                using (NetworkStream stream = client.GetStream())
                {
                    string data = $"SET\n{ssid}\n{pass}\n";
                    byte[] bytes = System.Text.Encoding.UTF8.GetBytes(data);
                    stream.Write(bytes, 0, bytes.Length);

                    // Чтение ответа ESP
                    StreamReader reader = new StreamReader(stream);
                    string response = reader.ReadLine();
                    Log($"Ответ от ESP: {response?.Trim()}", "SUCCESS");
                }
            }
        }
        catch (Exception e)
        {
            Log($"Ошибка при отправке данных на ESP: {e}", "ERROR");
        }
    }

    // ---------- ПРИЁМ МАССИВА С ESP ----------
    static void ReceiveArrayFromEsp()
    {
        string espApIp = "192.168.4.1";
        int espPort = 8888;

        Log("Подключение к ESP AP для получения массива...", "INFO");

        // Подключаемся к AP ESP32
        ConnectToNetwork("karch_eeg_88005553535");

        Thread.Sleep(5000); // ждём пока соединение стабилизируется

        try
        {
            using (TcpClient client = new TcpClient())
            {
                client.Connect(espApIp, espPort);
                using (NetworkStream stream = client.GetStream())
                using (StreamReader reader = new StreamReader(stream))
                {
                    // Отправляем команду GET_ARRAY
                    byte[] cmdBytes = System.Text.Encoding.UTF8.GetBytes("GET_ARRAY\n");
                    stream.Write(cmdBytes, 0, cmdBytes.Length);

                    // Чтение всего потока до конца
                    string data = reader.ReadToEnd();

                    Log("Массив получен от ESP32:", "SUCCESS");
                    Console.WriteLine(data);

                    // Разбор CSV в массив 5x5
                    string[] lines = data.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
                    int[,] arr = new int[5, 5];
                    for (int i = 0; i < lines.Length && i < 5; i++)
                    {
                        string[] nums = lines[i].Split(',');
                        for (int j = 0; j < nums.Length && j < 5; j++)
                            arr[i, j] = int.Parse(nums[j]);
                    }

                    // Печать массива
                    Console.WriteLine("Массив 5x5:");
                    for (int i = 0; i < 5; i++)
                    {
                        for (int j = 0; j < 5; j++)
                            Console.Write(arr[i, j] + "\t");
                        Console.WriteLine();
                    }
                }
            }
        }
        catch (Exception e)
        {
            Log($"Ошибка при получении массива: {e}", "ERROR");
        }
    }

    // ---------- ОСНОВНОЙ ЦИКЛ ----------
    static void Main()
    {
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);

        var config = LoadConfig();
        string espName = config.GetProperty("esp_network_name").GetString();
        string espPass = config.GetProperty("esp_network_password").GetString();
        string pcSsid = config.GetProperty("pc_wifi_ssid").GetString();
        string pcPass = config.GetProperty("pc_wifi_password").GetString();

        Log("=== ESP32 Auto-Connector ===", "INFO");

        // 1️⃣ Отправляем данные домашней сети на ESP
        SendWifiCredentialsToEsp("192.168.4.1", 8888, pcSsid, pcPass);

        // 2️⃣ Ждём немного, пока ESP настроится
        Log("Ждём 10 секунд, пока ESP применит настройки...", "INFO");
        Thread.Sleep(10000);

        // 3️⃣ Подключаемся к точке AP ESP и получаем массив
        ReceiveArrayFromEsp();

        // 4️⃣ После получения массива можно вернуть ПК в домашнюю сеть
        Log("Возврат к домашней сети...", "INFO");
        ConnectToNetwork(pcSsid);
        Log("Готово.", "SUCCESS");
    }
}
