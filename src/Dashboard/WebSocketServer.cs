using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace ReDefinition.Dashboard
{
    /// <summary>
    /// A WebSocket server (RFC 6455) for text frames, which also serves the dashboard's own files
    /// over plain HTTP so a phone's browser can open it. It does what a dashboard needs and nothing
    /// more: no extensions, no fragmentation of what it sends, no binary frames. Every client has a
    /// reader thread; frames go out through a bounded queue so a stuck client can never stall the
    /// game's main thread. On the network (not only this computer) it listens when asked to, and
    /// then lets a WebSocket in only with the key from the dashboard's address.
    /// </summary>
    internal sealed class WebSocketServer
    {
        private const string Guid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";
        private readonly TcpListener listener;
        private readonly Action<string> log;
        private readonly object clientsLock = new object();
        private readonly List<Client> clients = new List<Client>();
        private Thread acceptThread;
        private volatile bool running;

        /// <summary>Raised on a network thread when a client sent a text message.</summary>
        public event Action<Client, string> MessageReceived;
        public event Action<Client> ClientConnected;
        public event Action<Client> ClientDisconnected;

        private readonly string key, webRoot;

        public WebSocketServer(int port, Action<string> log, bool network, string key, string webRoot)
        {
            this.log = log;
            this.key = key;
            this.webRoot = webRoot;
            listener = new TcpListener(network ? IPAddress.Any : IPAddress.Loopback, port);
        }

        public void Start()
        {
            listener.Start();
            running = true;
            acceptThread = new Thread(AcceptLoop) { IsBackground = true, Name = "ReDefinition dashboard accept" };
            acceptThread.Start();
        }

        public void Stop()
        {
            running = false;
            try { listener.Stop(); } catch { }
            lock (clientsLock)
                foreach (var c in clients) c.Close();
        }

        public Client[] Snapshot()
        {
            lock (clientsLock) return clients.ToArray();
        }

        private void AcceptLoop()
        {
            while (running)
            {
                TcpClient tcp;
                try { tcp = listener.AcceptTcpClient(); }
                catch { break; }
                var t = new Thread(() => Serve(tcp)) { IsBackground = true, Name = "ReDefinition dashboard client" };
                t.Start();
            }
        }

        private void Serve(TcpClient tcp)
        {
            Client client = null;
            try
            {
                tcp.NoDelay = true;
                tcp.SendTimeout = 1000;
                var stream = tcp.GetStream();
                stream.ReadTimeout = 5000;
                string origin;
                bool local = IPAddress.IsLoopback(((IPEndPoint)tcp.Client.RemoteEndPoint).Address);
                if (!Handshake(stream, local, out origin)) { tcp.Close(); return; }
                stream.ReadTimeout = Timeout.Infinite;

                client = new Client(tcp, stream, origin);
                lock (clientsLock) clients.Add(client);
                var handler = ClientConnected; if (handler != null) handler(client);
                client.StartWriter();
                ReadLoop(client);
            }
            catch (Exception e)
            {
                if (running) log("client ended: " + e.Message);
            }
            finally
            {
                if (client != null)
                {
                    lock (clientsLock) clients.Remove(client);
                    client.Close();
                    var handler = ClientDisconnected; if (handler != null) handler(client);
                }
                else tcp.Close();
            }
        }

        // Only pages the player's own software serves may talk to the game: iCUE widgets (file:// or
        // an opaque origin) and no origin at all (tools). A website open in the browser must not
        // be able to press Stage, which is the reason for the origin check.
        private static bool OriginAllowed(string origin)
        {
            if (string.IsNullOrEmpty(origin)) return true;
            if (origin == "null") return true;
            if (origin.StartsWith("file://", StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private bool Handshake(System.IO.Stream stream, bool local, out string origin)
        {
            origin = null;
            var header = new StringBuilder();
            var one = new byte[1];
            while (header.Length < 8192)
            {
                if (stream.Read(one, 0, 1) != 1) return false;
                header.Append((char)one[0]);
                if (header.Length >= 4 && header.ToString(header.Length - 4, 4) == "\r\n\r\n") break;
            }
            string key = null;
            bool upgrade = false;
            string[] lines = header.ToString().Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries);
            string[] request = lines.Length > 0 ? lines[0].Split(' ') : new string[0];
            string target = request.Length > 1 ? request[1] : "/";
            foreach (string line in lines)
            {
                int colon = line.IndexOf(':');
                if (colon < 0) continue;
                string name = line.Substring(0, colon).Trim().ToLowerInvariant();
                string value = line.Substring(colon + 1).Trim();
                if (name == "sec-websocket-key") key = value;
                else if (name == "origin") origin = value;
                else if (name == "upgrade" && value.ToLowerInvariant() == "websocket") upgrade = true;
            }
            if (!upgrade)
            {
                if (request.Length > 0 && request[0] == "GET") ServeFile(stream, target);
                else Reply(stream, "400 Bad Request");
                return false;
            }
            if (key == null)
            {
                Reply(stream, "400 Bad Request");
                return false;
            }
            // With the dashboard's key anyone may connect (the phone, a browser); without it only
            // the iCUE widget on this computer.
            bool keyed = !string.IsNullOrEmpty(this.key) && QueryValue(target, "key") == this.key;
            if (!keyed && !(local && OriginAllowed(origin)))
            {
                log("refused origin " + origin);
                Reply(stream, "403 Forbidden");
                return false;
            }
            string accept;
            using (var sha1 = SHA1.Create())
                accept = Convert.ToBase64String(sha1.ComputeHash(Encoding.ASCII.GetBytes(key + Guid)));
            string response = "HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n" +
                              "Sec-WebSocket-Accept: " + accept + "\r\n\r\n";
            var bytes = Encoding.ASCII.GetBytes(response);
            stream.Write(bytes, 0, bytes.Length);
            return true;
        }

        private static string QueryValue(string target, string name)
        {
            int q = target.IndexOf('?');
            if (q < 0) return null;
            foreach (string pair in target.Substring(q + 1).Split('&'))
            {
                int eq = pair.IndexOf('=');
                if (eq > 0 && pair.Substring(0, eq) == name) return Uri.UnescapeDataString(pair.Substring(eq + 1));
            }
            return null;
        }

        private static readonly Dictionary<string, string> ContentTypes = new Dictionary<string, string>
        {
            { ".html", "text/html; charset=utf-8" }, { ".js", "text/javascript; charset=utf-8" },
            { ".css", "text/css; charset=utf-8" }, { ".svg", "image/svg+xml" }, { ".png", "image/png" },
            { ".json", "application/json" },
        };

        // The dashboard's files, from the web folder only: "/" is index.html, nothing outside the folder.
        private void ServeFile(System.IO.Stream stream, string target)
        {
            string path = target.Split('?')[0];
            if (path == "/" || path == "") path = "/index.html";
            try
            {
                // With its separator, so that a folder beside it whose name begins the same is outside.
                string root = System.IO.Path.GetFullPath(webRoot).TrimEnd(System.IO.Path.DirectorySeparatorChar)
                              + System.IO.Path.DirectorySeparatorChar;
                string full = System.IO.Path.GetFullPath(System.IO.Path.Combine(root, Uri.UnescapeDataString(path.TrimStart('/'))));
                string type;
                if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !System.IO.File.Exists(full)
                    || !ContentTypes.TryGetValue(System.IO.Path.GetExtension(full).ToLowerInvariant(), out type))
                {
                    Reply(stream, "404 Not Found");
                    return;
                }
                byte[] body = System.IO.File.ReadAllBytes(full);
                byte[] head = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: " + type + "\r\nContent-Length: " + body.Length +
                                                      "\r\nCache-Control: no-cache\r\nConnection: close\r\n\r\n");
                stream.Write(head, 0, head.Length);
                stream.Write(body, 0, body.Length);
            }
            catch (Exception e)
            {
                log("serving " + path + " failed: " + e.Message);
                Reply(stream, "500 Internal Server Error");
            }
        }

        private static void Reply(System.IO.Stream stream, string status)
        {
            var b = Encoding.ASCII.GetBytes("HTTP/1.1 " + status + "\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
            try { stream.Write(b, 0, b.Length); } catch { }
        }

        private void ReadLoop(Client client)
        {
            var stream = client.Stream;
            var message = new List<byte>();
            while (running)
            {
                int b0 = stream.ReadByte(), b1 = stream.ReadByte();
                if (b0 < 0 || b1 < 0) return;
                int opcode = b0 & 0x0F;
                bool fin = (b0 & 0x80) != 0;
                bool masked = (b1 & 0x80) != 0;
                long length = b1 & 0x7F;
                if (length == 126)
                {
                    var l = ReadExact(stream, 2);
                    length = (l[0] << 8) | l[1];
                }
                else if (length == 127)
                {
                    var l = ReadExact(stream, 8);
                    length = 0;
                    for (int i = 0; i < 8; i++) length = (length << 8) | l[i];
                }
                if (length > 1 << 20 || !masked) return; // a client frame is always masked; 1 MB is far more than any command
                var mask = ReadExact(stream, 4);
                var payload = ReadExact(stream, (int)length);
                for (int i = 0; i < payload.Length; i++) payload[i] ^= mask[i & 3];

                if (opcode == 0x8) return;                          // close
                if (opcode == 0x9) { client.EnqueueFrame(0xA, payload); continue; } // ping -> pong
                if (opcode != 0x1 && opcode != 0x0) continue;       // only text (and its continuations)
                message.AddRange(payload);
                if (!fin) continue;
                string text = Encoding.UTF8.GetString(message.ToArray());
                message.Clear();
                var handler = MessageReceived; if (handler != null) handler(client, text);
            }
        }

        private static byte[] ReadExact(System.IO.Stream s, int count)
        {
            var buf = new byte[count];
            int got = 0;
            while (got < count)
            {
                int n = s.Read(buf, got, count - got);
                if (n <= 0) throw new System.IO.EndOfStreamException();
                got += n;
            }
            return buf;
        }

        internal sealed class Client
        {
            private readonly TcpClient tcp;
            private readonly object queueLock = new object();
            private readonly Queue<byte[]> queue = new Queue<byte[]>();
            private readonly AutoResetEvent wake = new AutoResetEvent(false);
            private volatile bool closed;

            public System.IO.Stream Stream { get; private set; }
            public string Origin { get; private set; }
            /// <summary>State the dashboard keeps per client (its subscriptions); owned by the caller.</summary>
            public object State;

            public Client(TcpClient tcp, System.IO.Stream stream, string origin)
            {
                this.tcp = tcp; Stream = stream; Origin = origin;
            }

            public void StartWriter()
            {
                new Thread(WriteLoop) { IsBackground = true, Name = "ReDefinition dashboard writer" }.Start();
            }

            /// <summary>Set when a frame was dropped for a slow reader; whoever sends changes only resends all.</summary>
            public volatile bool Dropped;

            public void SendText(string text)
            {
                EnqueueFrame(0x1, Encoding.UTF8.GetBytes(text));
            }

            public void EnqueueFrame(int opcode, byte[] payload)
            {
                var frame = new List<byte>(payload.Length + 10) { (byte)(0x80 | opcode) };
                if (payload.Length < 126) frame.Add((byte)payload.Length);
                else if (payload.Length <= 0xFFFF) { frame.Add(126); frame.Add((byte)(payload.Length >> 8)); frame.Add((byte)payload.Length); }
                else { frame.Add(127); for (int i = 7; i >= 0; i--) frame.Add((byte)((long)payload.Length >> (8 * i))); }
                frame.AddRange(payload);
                lock (queueLock)
                {
                    // A reader that cannot keep up gets the newest data, not a growing backlog.
                    if (opcode == 0x1) while (queue.Count >= 2) { queue.Dequeue(); Dropped = true; }
                    queue.Enqueue(frame.ToArray());
                }
                wake.Set();
            }

            private void WriteLoop()
            {
                try
                {
                    while (!closed)
                    {
                        wake.WaitOne(500);
                        while (true)
                        {
                            byte[] frame;
                            lock (queueLock) frame = queue.Count > 0 ? queue.Dequeue() : null;
                            if (frame == null) break;
                            Stream.Write(frame, 0, frame.Length);
                        }
                    }
                }
                catch { Close(); }
            }

            public void Close()
            {
                closed = true;
                wake.Set();
                try { tcp.Close(); } catch { }
            }
        }
    }
}
