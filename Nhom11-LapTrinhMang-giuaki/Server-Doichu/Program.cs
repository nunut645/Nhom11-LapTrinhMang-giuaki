using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace RpsServer
{
    class Player
    {
        public int Id { get; }
        public string Name { get; set; } = "Guest";
        public TcpClient Tcp { get; }
        public StreamReader Reader { get; }
        public StreamWriter Writer { get; }
        public string? Choice { get; set; } // keo/bua/bao
        public int Score { get; set; }

        public Player(int id, TcpClient c)
        {
            Id = id;
            Tcp = c;
            Reader = new StreamReader(c.GetStream(), new UTF8Encoding(false));
            Writer = new StreamWriter(c.GetStream(), new UTF8Encoding(false)) { AutoFlush = true };
        }
    }

    class Program
    {
        static readonly object _lock = new();
        static readonly List<Player> _waiting = new();   // hàng chờ
        static readonly List<Player> _room = new();      // tối đa 2
        static int _nextId = 1;
        static bool _roundRunning = false;

        static void Main()
        {
            Console.OutputEncoding = Encoding.UTF8;
            var listener = new TcpListener(IPAddress.Any, 5555);
            listener.Start();
            Console.WriteLine("🎮 RPS Server listening on 0.0.0.0:5555");

            new Thread(MatchLoop) { IsBackground = true }.Start();

            while (true)
            {
                var tcp = listener.AcceptTcpClient();
                var p = new Player(_nextId++, tcp);
                lock (_lock) _waiting.Add(p);
                Console.WriteLine($"👤 Client #{p.Id} connected");

                new Thread(() => HandleClient(p)) { IsBackground = true }.Start();
            }
        }

        static void HandleClient(Player p)
        {
            try
            {
                Send(p, "WELCOME|Nhập tên của bạn rồi bấm Enter:");
                p.Name = p.Reader.ReadLine()?.Trim() ?? $"Player{p.Id}";
                Send(p, $"INFO|Xin chào, {p.Name}! Hãy chờ vào phòng...");

                lock (_lock)
                {
                    if (_room.Count < 2) { _room.Add(p); BroadcastRoom($"INFO|{p.Name} đã vào phòng ({_room.Count}/2)."); }
                    else BroadcastWaiting($"INFO|{p.Name} đang chờ phòng trống.");
                }

                while (p.Tcp.Connected)
                {
                    var line = p.Reader.ReadLine();
                    if (line == null) break;
                    var norm = NormalizeChoice(line);
                    if (norm == null)
                    {
                        Send(p, "ERROR|Lựa chọn không hợp lệ. Hãy nhập: keo / bua / bao.");
                        continue;
                    }
                    lock (_lock)
                    {
                        if (_room.Contains(p))
                        {
                            p.Choice = norm;
                            Send(p, $"OK|Bạn đã chọn: {ToDisplay(norm)}");
                        }
                        else
                        {
                            Send(p, "INFO|Bạn chưa ở trong phòng, vui lòng chờ.");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"⚠️ Client #{p.Id} error: {ex.Message}");
            }
            finally
            {
                lock (_lock)
                {
                    _waiting.Remove(p);
                    if (_room.Remove(p))
                    {
                        BroadcastRoom($"INFO|{p.Name} đã rời phòng.");
                        // đẩy 1 người từ hàng chờ vào phòng nếu có
                        if (_waiting.Count > 0 && _room.Count < 2)
                        {
                            var nxt = _waiting[0]; _waiting.RemoveAt(0); _room.Add(nxt);
                            Send(nxt, "INFO|Bạn đã vào phòng!");
                            BroadcastRoom($"INFO|{nxt.Name} đã vào phòng ({_room.Count}/2).");
                        }
                    }
                }
                try { p.Tcp.Close(); } catch { }
                Console.WriteLine($"👋 Client #{p.Id} disconnected");
            }
        }

        static void MatchLoop()
        {
            while (true)
            {
                Thread.Sleep(150); // nhẹ CPU
                Player a, b;
                lock (_lock)
                {
                    if (_roundRunning) continue;
                    if (_room.Count < 2) continue;
                    a = _room[0]; b = _room[1];
                    _roundRunning = true;
                }

                // reset lựa chọn + mời chơi
                lock (_lock) { a.Choice = null; b.Choice = null; }
                BroadcastRoom("ROUND|Vòng mới! Nhập lựa chọn (keo/bua/bao).");
                // đếm ngược nhỏ
                BroadcastRoom("TIMER|Bạn có 15 giây để chọn.");
                var start = DateTime.UtcNow;

                // đợi hai người chọn hoặc hết thời gian
                while (true)
                {
                    Thread.Sleep(100);
                    bool both;
                    lock (_lock) both = a.Choice != null && b.Choice != null;
                    if (both) break;
                    if ((DateTime.UtcNow - start).TotalSeconds >= 15) break;
                }

                string? ca, cb;
                lock (_lock) { ca = a.Choice; cb = b.Choice; }

                // xử lý kết quả
                if (ca == null && cb == null)
                {
                    BroadcastRoom("RESULT|Cả hai không chọn. Hòa!");
                }
                else if (ca == null || cb == null)
                {
                    var winner = ca != null ? a : b;
                    winner.Score++;
                    BroadcastRoom($"RESULT|{(ca != null ? a.Name : b.Name)} thắng vì đối thủ không chọn.");
                }
                else
                {
                    var res = Judge(ca!, cb!); // -1: a thua, 0: hòa, 1: a thắng
                    if (res == 0)
                        BroadcastRoom($"RESULT|Hòa: {a.Name}({ToDisplay(ca!)}) vs {b.Name}({ToDisplay(cb!)})");
                    else if (res > 0)
                    {
                        a.Score++;
                        BroadcastRoom($"RESULT|{a.Name} thắng! {ToDisplay(ca!)} ăn {ToDisplay(cb!)}");
                    }
                    else
                    {
                        b.Score++;
                        BroadcastRoom($"RESULT|{b.Name} thắng! {ToDisplay(cb!)} ăn {ToDisplay(ca!)}");
                    }
                }

                BroadcastRoom($"SCORE|Điểm số: {a.Name}={a.Score} | {b.Name}={b.Score}");
                BroadcastRoom("----");

                lock (_lock) _roundRunning = false;
            }
        }

        // keo < bao, bua < keo, bao < bua
        static int Judge(string a, string b)
        {
            if (a == b) return 0;
            return (a, b) switch
            {
                ("keo", "bao") => 1,
                ("keo", "bua") => -1,
                ("bua", "keo") => 1,
                ("bua", "bao") => -1,
                ("bao", "bua") => 1,
                ("bao", "keo") => -1,
                _ => 0
            };
        }

        static string? NormalizeChoice(string input)
        {
            if (string.IsNullOrWhiteSpace(input)) return null;
            var s = RemoveDiacritics(input.Trim().ToLowerInvariant());
            // chấp nhận tiếng Anh
            if (s is "rock") s = "keo";
            if (s is "paper") s = "bao";
            if (s is "scissors" or "scissor") s = "bua"; // quy ước: scissors = búa
            return s is "keo" or "bua" or "bao" ? s : null;
        }

        static string ToDisplay(string norm) => norm switch
        {
            "keo" => "Kéo",
            "bua" => "Búa",
            "bao" => "Bao",
            _ => norm
        };

        static string RemoveDiacritics(string text)
        {
            var norm = text.Normalize(NormalizationForm.FormD);
            var sb = new StringBuilder(capacity: norm.Length);
            foreach (var ch in norm)
            {
                var uc = System.Globalization.CharUnicodeInfo.GetUnicodeCategory(ch);
                if (uc != System.Globalization.UnicodeCategory.NonSpacingMark)
                    sb.Append(ch);
            }
            return sb.ToString().Normalize(NormalizationForm.FormC);
        }

        static void Send(Player p, string line)
        {
            try { p.Writer.WriteLine(line); } catch { }
        }

        static void BroadcastRoom(string line)
        {
            lock (_lock)
            {
                foreach (var p in _room.ToArray())
                {
                    try { p.Writer.WriteLine(line); } catch { }
                }
                Console.WriteLine(line);
            }
        }

        static void BroadcastWaiting(string line)
        {
            lock (_lock)
            {
                foreach (var p in _waiting.ToArray())
                {
                    try { p.Writer.WriteLine(line); } catch { }
                }
            }
        }
    }
}
