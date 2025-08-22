using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace RpsClient
{
    class Program
    {
        static void Main(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;
            Console.InputEncoding = Encoding.UTF8;

            var host = args.Length > 0 ? args[0] : "127.0.0.1";
            var port = args.Length > 1 ? int.Parse(args[1]) : 5555;

            try
            {
                var tcp = new TcpClient();
                Console.WriteLine($"🔗 Đang kết nối {host}:{port} ...");
                tcp.Connect(host, port);

                using var reader = new StreamReader(tcp.GetStream(), new UTF8Encoding(false));
                using var writer = new StreamWriter(tcp.GetStream(), new UTF8Encoding(false)) { AutoFlush = true };

                // receiver
                var recv = new Thread(() =>
                {
                    try
                    {
                        string? line;
                        while ((line = reader.ReadLine()) != null)
                        {
                            var parts = line.Split('|', 2);
                            var kind = parts[0];
                            var msg = parts.Length > 1 ? parts[1] : "";
                            switch (kind)
                            {
                                case "WELCOME":
                                    Console.WriteLine(msg);
                                    Console.Write("> ");
                                    break;
                                case "INFO":
                                case "ROUND":
                                case "TIMER":
                                case "RESULT":
                                case "SCORE":
                                    Console.WriteLine(msg);
                                    if (kind is "ROUND") Console.Write("Nhập (kéo/búa/bao): ");
                                    break;
                                case "OK":
                                    Console.WriteLine(msg);
                                    break;
                                case "ERROR":
                                    Console.WriteLine("⚠️ " + msg);
                                    Console.Write("Nhập lại (kéo/búa/bao): ");
                                    break;
                                default:
                                    Console.WriteLine(line);
                                    break;
                            }
                        }
                    }
                    catch { }
                    finally { try { tcp.Close(); } catch { } }
                })
                { IsBackground = true };
                recv.Start();

                // sender (stdin)
                while (tcp.Connected)
                {
                    var input = Console.ReadLine();
                    if (input == null) break;
                    writer.WriteLineAsync(input);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("❌ Lỗi: " + ex.Message);
            }
        }
    }
}
