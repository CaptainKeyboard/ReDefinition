using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using ReDefinition.Core;
using ReDefinition.Modules;
using UnityEngine;

namespace ReDefinition.Dashboard
{
    // The dashboard's server: telemetry out and commands in over a WebSocket, and the dashboard's
    // own files (GameData/ReDefinition/Dashboard) for a phone's browser. It listens on this computer
    // always, and on the network where the settings say so (OwnSettings.DashboardNetwork); a change
    // there starts it again. The network threads only queue messages; everything that touches the
    // game runs here in Update.
    [KSPAddon(KSPAddon.Startup.Instantly, true)]
    internal sealed class DashboardServer : MonoBehaviour
    {
        // Fixed on purpose: a dashboard's manifest, the iCUE widget's, names the port it may talk to.
        public const int Port = 8765;

        // The letters a new key is drawn from: lower case and digits without l, o, 0 and 1, which read alike.
        private const string KeyLetters = "abcdefghijkmnpqrstuvwxyz23456789";

        // One client's state (WebSocketServer.Client.State): what it reads, how often, and what it was sent.
        private sealed class Subscriptions
        {
            // The keys sent with every update, the keys read once with the next one, the time between
            // updates, and when the next one is due (Time.realtimeSinceStartup).
            public readonly HashSet<string> Keys = new HashSet<string>();
            public readonly List<string> OneShot = new List<string>();
            public float IntervalSeconds = 0.5f;
            public float NextSend;
            // What each key was last sent as: a key goes out again only when that changes.
            public readonly Dictionary<string, SentValue> Sent = new Dictionary<string, SentValue>();
        }

        // What a key was last sent as: its JSON text and the rounded value that text was made of.
        private sealed class SentValue
        {
            public string Text;
            public object Value;
        }

        // A message from a network thread, waiting for Update: the client that sent it and its text.
        private struct Incoming
        {
            // The sender and the message's JSON text.
            public WebSocketServer.Client Client;
            public string Text;
        }

        // The running server, null while none listens (not started yet, or the port was taken).
        private WebSocketServer server;
        // What the server was last started with, null for not yet; it is started again where the
        // settings differ.
        private bool listensOnNetwork;
        private string listensWithKey;
        private Telemetry telemetry;
        private readonly ConcurrentQueue<Incoming> incoming = new ConcurrentQueue<Incoming>();
        // What Collect works in, reused by every client's update: it runs on the main thread and its result is sent before the next call.
        private readonly Dictionary<string, object> collected = new Dictionary<string, object>();
        private readonly List<string> unknownKeys = new List<string>();
        private readonly List<string> readKeys = new List<string>();
        private readonly HashSet<string> readOnce = new HashSet<string>();

        // The folder the dashboard's pages, scripts and pictures are served from.
        private static string WebRoot
        {
            get { return KSPUtil.ApplicationRootPath + "GameData/ReDefinition/Dashboard"; }
        }

        // The dashboard's address for a phone on the same network, with its key; null where it may
        // not be opened from the network, or this computer has no address there.
        internal static string PhoneUrl
        {
            get
            {
                OwnSettings settings = Settings;
                string ip = LanAddress();
                return settings != null && settings.DashboardNetwork && ip != null && settings.DashboardKey != ""
                    ? "http://" + ip + ":" + Port + "/?key=" + settings.DashboardKey
                    : null;
            }
        }

        // ReDefinition's settings once the add-on has loaded them.
        internal static OwnSettings Settings
        {
            get
            {
                ReDefinitionAddon addon = ReDefinitionAddon.Instance;
                return addon == null ? null : addon.Live;
            }
        }

        // Kept across scenes; the telemetry's readers and commands are built once.
        private void Awake()
        {
            DontDestroyOnLoad(gameObject);
            telemetry = new Telemetry();
        }

        // Closes the listener and every client as the game quits.
        private void OnDestroy()
        {
            if (server != null) server.Stop();
        }

        // Each frame: (re)starts the server where the settings changed, feeds the fly-by-wire the active vessel,
        // handles the queued messages, and sends each client whose interval is up the keys that changed.
        private void Update()
        {
            OwnSettings settings = Settings;
            if (settings == null) return;
            if (settings.DashboardKey == "") settings.DashboardKey = NewKey();
            if (listensWithKey == null || listensOnNetwork != settings.DashboardNetwork || listensWithKey != settings.DashboardKey)
                Listen(settings.DashboardNetwork, settings.DashboardKey);
            if (server == null) return;

            FlyByWire.Follow(HighLogic.LoadedSceneIsFlight ? FlightGlobals.ActiveVessel : null);

            Incoming msg;
            while (incoming.TryDequeue(out msg)) Handle(msg.Client, msg.Text);

            float now = Time.realtimeSinceStartup;
            foreach (var client in server.Snapshot())
            {
                var subs = client.State as Subscriptions;
                if (subs == null || now < subs.NextSend) continue;
                if (subs.Keys.Count == 0 && subs.OneShot.Count == 0) continue;
                subs.NextSend = now + subs.IntervalSeconds;
                // A dropped frame may have held changes: everything goes out again.
                if (client.Dropped) { client.Dropped = false; subs.Sent.Clear(); }
                var update = Collect(subs);
                if (update.Count > 0) client.SendText(Json.Encode(update));
            }
        }

        // Tried once per setting: a port another program holds is not tried every frame.
        private void Listen(bool network, string key)
        {
            if (server != null) server.Stop();
            server = null;
            listensOnNetwork = network;
            listensWithKey = key;
            WebSocketServer started = new WebSocketServer(Port, LogLine, network, key, WebRoot);
            started.MessageReceived += (c, text) => incoming.Enqueue(new Incoming { Client = c, Text = text });
            started.ClientConnected += c => { c.State = new Subscriptions(); LogLine("client connected, origin " + (c.Origin ?? "none")); };
            started.ClientDisconnected += c => LogLine("client disconnected");
            try
            {
                started.Start();
                server = started;
                LogLine(network ? "listening on the network, port " + Port : "listening on 127.0.0.1:" + Port);
            }
            catch (Exception e)
            {
                LogLine("could not listen on port " + Port + ": " + e.Message);
            }
        }

        // A random key of eight letters for the settings, made where the file has none.
        private static string NewKey()
        {
            var random = new System.Random();
            StringBuilder key = new StringBuilder();
            for (int i = 0; i < 8; i++) key.Append(KeyLetters[random.Next(KeyLetters.Length)]);
            return key.ToString();
        }

        // One update for a client: its subscribed keys whose value changed since the last send, its one-shot
        // reads whatever their value, and under "unknown" the keys the telemetry has no reader for.
        // The result is the server's scratch dictionary, emptied by the next call.
        private Dictionary<string, object> Collect(Subscriptions subs)
        {
            var result = collected;
            var unknown = unknownKeys;
            var keys = readKeys;
            var oneShot = readOnce;
            result.Clear();
            unknown.Clear();
            keys.Clear();
            oneShot.Clear();
            keys.AddRange(subs.Keys);
            keys.AddRange(subs.OneShot);
            foreach (string once in subs.OneShot) oneShot.Add(once);
            subs.OneShot.Clear();
            foreach (string key in keys)
            {
                try
                {
                    object value;
                    if (telemetry.TryRead(key, out value))
                    {
                        object rounded = telemetry.RoundRead(key, value);
                        SentValue last;
                        bool sent = subs.Sent.TryGetValue(key, out last);
                        bool again = sent && !oneShot.Contains(key);
                        // The same scalar as last time gives the same text, which is not worth writing out to find that.
                        if (again && Same(last.Value, rounded)) continue;
                        string text = Json.Value(rounded);
                        if (again && last.Text == text) { last.Value = rounded; continue; }
                        if (subs.Keys.Contains(key))
                        {
                            if (sent) { last.Text = text; last.Value = rounded; }
                            else subs.Sent[key] = new SentValue { Text = text, Value = rounded };
                        }
                        result[key] = new Json.Raw(text);
                    }
                    else if (!telemetry.IsKnown(key)) unknown.Add(key);
                }
                catch (Exception e)
                {
                    // One broken key (a part that vanished mid-frame, say) must not silence the rest.
                    LogLine("reading " + key + " failed: " + e.Message);
                    subs.Keys.Remove(key);
                }
            }
            if (unknown.Count > 0) result["unknown"] = unknown;
            return result;
        }

        // Whether two rounded values certainly write the same JSON: both null, the same number to the bit, the same
        // bool, an equal string, or one Json.Raw. Anything else (a list, a dictionary) is compared by its text.
        internal static bool Same(object a, object b)
        {
            if (a == null || b == null) return a == null && b == null;
            if (a is double) return b is double && BitConverter.DoubleToInt64Bits((double)a) == BitConverter.DoubleToInt64Bits((double)b);
            if (a is bool) return b is bool && (bool)a == (bool)b;
            string text = a as string;
            if (text != null) return text == b as string;
            return a is Json.Raw && ReferenceEquals(a, b);
        }

        // One message from a client, a JSON object: "+" and "-" add and remove subscribed keys, "run" lists
        // commands (or one-shot reads), "rate" sets the milliseconds between updates.
        private void Handle(WebSocketServer.Client client, string text)
        {
            var subs = client.State as Subscriptions;
            if (subs == null) return;
            Dictionary<string, object> json;
            try { json = Json.DecodeObject(text); }
            catch (Exception e) { LogLine("bad message: " + e.Message); return; }

            foreach (var entry in json)
            {
                var list = entry.Value as List<object>;
                switch (entry.Key)
                {
                    case "+":
                        if (list != null) foreach (var k in list) subs.Keys.Add(Convert.ToString(k));
                        break;
                    case "-":
                        if (list != null) foreach (var k in list) { subs.Keys.Remove(Convert.ToString(k)); subs.Sent.Remove(Convert.ToString(k)); }
                        break;
                    case "run":
                        if (list != null) foreach (var k in list) Run(Convert.ToString(k), subs);
                        break;
                    case "rate":
                        double ms;
                        // Down to 16 ms, one update per frame of a 60 Hz screen; KSP's own frame rate
                        // caps it anyway, as updates go out from Update().
                        if (entry.Value is double && (ms = (double)entry.Value) >= 16 && ms <= 5000) subs.IntervalSeconds = (float)(ms / 1000.0);
                        break;
                }
            }
        }

        // Runs one entry of a "run" list; a failing command is logged and the rest of the list goes on.
        private void Run(string command, Subscriptions subs)
        {
            try
            {
                if (telemetry.TryRun(command)) return;
                // Not a command: a one-shot read, answered with the next update.
                if (telemetry.IsKnown(command)) subs.OneShot.Add(command);
            }
            catch (Exception e)
            {
                LogLine("command " + command + " failed: " + e.Message);
            }
        }

        // This computer's address on the local network: an interface that is up, not loopback or a
        // tunnel, with a gateway (the one towards the router).
        private static string LanAddress()
        {
            try
            {
                foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback
                        || ni.NetworkInterfaceType == NetworkInterfaceType.Tunnel) continue;
                    IPInterfaceProperties p = ni.GetIPProperties();
                    if (p.GatewayAddresses.Count == 0) continue;
                    foreach (UnicastIPAddressInformation a in p.UnicastAddresses)
                        if (a.Address.AddressFamily == AddressFamily.InterNetwork) return a.Address.ToString();
                }
            }
            catch (Exception)
            {
            }
            return null;
        }

        // Called from the network threads too: Unity's log takes lines from any thread.
        private static void LogLine(string message)
        {
            Debug.Log(Log.Tag + " Dashboard: " + message);
        }
    }
}
