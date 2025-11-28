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

    // ---------- СТРИМИНГ ДАННЫХ С ESP ----------
    static void StreamDataFromEsp()
    {
        string espApIp = "192.168.4.1";
        int espPort = 8888;

        Log("Подключение к ESP AP для стриминга данных...", "INFO");
        ConnectToNetwork("karch_eeg_88005553535");
        Thread.Sleep(5000);

        try
        {
            using (TcpClient client = new TcpClient())
            {
                client.Connect(espApIp, espPort);
                using (NetworkStream stream = client.GetStream())
                using (StreamReader reader = new StreamReader(stream))
                {
                    // Отправляем команду START_STREAM
                    byte[] cmdBytes = System.Text.Encoding.UTF8.GetBytes("START_STREAM\n");
                    stream.Write(cmdBytes, 0, cmdBytes.Length);

                    Log("Начало стриминга данных...", "SUCCESS");
                    Log("Нажмите любую клавишу для остановки...", "INFO");

                    // Чтение данных в реальном времени
                    while (!Console.KeyAvailable)
                    {
                        if (stream.DataAvailable)
                        {
                            string data = reader.ReadLine();
                            if (!string.IsNullOrEmpty(data))
                            {
                                Console.WriteLine($"Данные: {data}");
                            }
                        }
                        Thread.Sleep(10); // Небольшая задержка для снижения нагрузки
                    }

                    // Отправляем команду остановки
                    byte[] stopBytes = System.Text.Encoding.UTF8.GetBytes("STOP_STREAM\n");
                    stream.Write(stopBytes, 0, stopBytes.Length);

                    Log("Стриминг остановлен.", "INFO");
                }
            }
        }
        catch (Exception e)
        {
            Log($"Ошибка при стриминге данных: {e}", "ERROR");
        }
    }

    static void Main()
    {
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);

        string espName = "karch_eeg_88005553535";
        string espPass = "mypassword122";
        string pcSsid = "MyHomeWiFi";
        string pcPass = "12345678";

        Log("=== ESP32 Data Streamer ===", "INFO");

        // 1️⃣ Отправляем данные домашней сети на ESP
        SendWifiCredentialsToEsp("192.168.4.1", 8888, pcSsid, pcPass);

        // 2️⃣ Ждём немного, пока ESP настроится
        Log("Ждём 10 секунд, пока ESP применит настройки...", "INFO");
        Thread.Sleep(10000);

        // 3️⃣ Подключаемся к точке AP ESP и начинаем стриминг
        StreamDataFromEsp();

        // 4️⃣ Возврат к домашней сети
        Log("Возврат к домашней сети...", "INFO");
        ConnectToNetwork(pcSsid);
        Log("Готово.", "SUCCESS");
    }
}