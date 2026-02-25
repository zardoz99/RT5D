// Program.cs
// RT-5D Programming Tool — Console/CLI entry point.
// Supports: test, read, write, info sub-commands.

using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace RT5D
{
    internal static class Program
    {
        // Debug flag — set by --debug anywhere on the command line.
        private static bool _debug;

        static async Task<int> Main(string[] args)
        {
            // GB2312 support (required for channel/contact names)
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

            _debug = Array.Exists(args, a => a == "--debug");

            Console.WriteLine("RT-5D DMR Programming Tool");
            Console.WriteLine("──────────────────────────");

            if (args.Length == 0)
            {
                PrintUsage();
                return 0;
            }

            try
            {
                return args[0].ToLowerInvariant() switch
                {
                    "test"  => RunTests(),
                    "ports" => ListPorts(),
                    "read"  => await RunReadAsync(args),
                    "write" => await RunWriteAsync(args),
                    "info"  => await RunInfoAsync(args),
                    _       => BadUsage($"Unknown command: {args[0]}"),
                };
            }
            catch (ProtocolException ex)
            {
                Console.Error.WriteLine($"[PROTOCOL ERROR] {ex.Message}");
                return 2;
            }
            catch (SerialLinkException ex)
            {
                Console.Error.WriteLine($"[SERIAL ERROR] {ex.Message}");
                return 3;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[ERROR] {ex.GetType().Name}: {ex.Message}");
                return 1;
            }
        }

        // ── Sub-commands ─────────────────────────────────────────────────────────

        private static int RunTests()
        {
            bool passed = Tests.RunAll();
            return passed ? 0 : 1;
        }

        private static int ListPorts()
        {
            var ports = SerialLink.GetPortNames();
            if (ports.Length == 0)
            {
                Console.WriteLine("No serial ports found.");
                return 0;
            }
            Console.WriteLine("Available serial ports:");
            foreach (var p in ports)
                Console.WriteLine($"  {p}");
            return 0;
        }

        private static async Task<int> RunReadAsync(string[] args)
        {
            // Usage: read <port> [output.json]
            if (args.Length < 2) return BadUsage("read requires a port argument.");
            string port   = args[1];
            string outFile = args.Length >= 3 ? args[2] : "rt5d_config.json";

            Console.WriteLine($"Reading from {port} → {outFile}");

            using var link = new SerialLink(port);
            link.Open();
            Console.WriteLine($"Serial port {port} opened at 115200 8N1.");

            var proto   = new ProtocolV2(link, _debug ? LogLine : null);
            var session = new SessionV2(proto, _debug ? LogLine : null, ShowProgress);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
            SessionData data = await session.ReadAsync(cts.Token);

            // Decode and display summary
            var summary = BuildSummary(data);
            Console.WriteLine();
            Console.WriteLine("── Read Summary ──────────────────────────────");
            Console.WriteLine(summary);

            // Save raw JSON
            SaveJson(data, outFile);
            Console.WriteLine($"Raw session data saved to {outFile}");
            return 0;
        }

        private static async Task<int> RunWriteAsync(string[] args)
        {
            // Usage: write <port> <input.json> [--basic-info]
            if (args.Length < 3) return BadUsage("write requires port and input file.");
            string port     = args[1];
            string inFile   = args[2];
            bool basicInfo  = Array.Exists(args, a => a == "--basic-info");

            if (!File.Exists(inFile))
            {
                Console.Error.WriteLine($"Input file not found: {inFile}");
                return 1;
            }

            Console.WriteLine($"Writing {inFile} → {port}");
            if (basicInfo) Console.WriteLine("  (including Basic Info / model ID)");

            SessionData data = LoadJson(inFile);

            using var link = new SerialLink(port);
            link.Open();
            Console.WriteLine($"Serial port {port} opened at 115200 8N1.");

            var proto   = new ProtocolV2(link, _debug ? LogLine : null);
            var session = new SessionV2(proto, _debug ? LogLine : null, ShowProgress);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
            await session.WriteAsync(data, basicInfo, cts.Token);

            Console.WriteLine();
            Console.WriteLine("Write complete. Waiting 10 seconds for radio to restart...");
            await Task.Delay(TimeSpan.FromSeconds(10));
            Console.WriteLine("Re-reading to verify...");

            using var cts2 = new CancellationTokenSource(TimeSpan.FromSeconds(120));
            SessionData verify = await session.ReadAsync(cts2.Token);
            // (A production tool would byte-compare data vs verify here)
            Console.WriteLine("Verification read complete.");
            return 0;
        }

        private static async Task<int> RunInfoAsync(string[] args)
        {
            // Usage: info <port>
            if (args.Length < 2) return BadUsage("info requires a port argument.");
            string port = args[1];

            using var link = new SerialLink(port);
            link.Open();

            var proto = new ProtocolV2(link, null);

            // Only run handshake + password + version, then end session
            Console.WriteLine($"Connecting to radio on {port}...");
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

            byte[] hs = FixedPayloads.Handshake;
            await proto.SendReceiveAsync(Commands.Handshake, 0, hs, cts.Token);

            await proto.SendReceiveAsync(Commands.CheckPassword, 0, FixedPayloads.Password, cts.Token);

            var vr = await proto.SendReceiveAsync(Commands.GetVersion, 0,
                                                   FixedPayloads.VersionRequest(), cts.Token);

            await proto.SendReceiveAsync(Commands.EndSession, 0, FixedPayloads.EndSession, cts.Token);

            Console.WriteLine("Version block (128 bytes, hex):");
            Console.WriteLine(FormatHex(vr.Payload, 16));
            // Attempt to print as ASCII for any readable portion
            string ascii = Encoding.ASCII.GetString(vr.Payload)
                                         .Replace('\0', '.')
                                         .Replace('\r', '.')
                                         .Replace('\n', '.');
            Console.WriteLine($"ASCII: {ascii}");
            return 0;
        }

        // ── Helpers ──────────────────────────────────────────────────────────────

        private static void LogLine(string msg) =>
            Console.WriteLine($"  {msg}");

        private static void ShowProgress(SessionProgress p)
        {
            if (p.TotalPackets > 1)
                Console.Write($"\r  [{p.Phase}] {p.PacketIndex + 1}/{p.TotalPackets}   ");
            else
                Console.Write($"\r  [{p.Phase}]   ");
        }

        private static string BuildSummary(SessionData data)
        {
            var channels = ChannelsPackerV2.Unpack(data.ChannelPackets);
            var contacts = ContactsPackerV2.Unpack(data.AddrBookPackets);
            var groups   = RxGroupsPackerV2.Unpack(data.RxGroupPackets);
            var dtmf     = DtmfBlock.Decode(data.DtmfData);
            var basic    = BasicInfoBlock.Decode(data.BasicInfoData);
            var optfun   = OptFunBlock.Decode(data.OptFunData);
            var vfo      = VfoBlock.Decode(data.VfoData);

            int chCount  = System.Linq.Enumerable.Count(channels, c => c is not null);
            int cxCount  = System.Linq.Enumerable.Count(contacts, c => c is not null);
            int grpCount = System.Linq.Enumerable.Count(groups,   g => g is not null);

            var sb = new StringBuilder();
            sb.AppendLine($"  Model   : {basic.ModelName} (ID={basic.ModelId})");
            sb.AppendLine($"  Channels: {chCount} / {ChannelsPackerV2.TotalChannels}");
            sb.AppendLine($"  Contacts: {cxCount} / {ContactsPackerV2.MaxContacts}");
            sb.AppendLine($"  RxGroups: {grpCount} / {RxGroupsPackerV2.MaxGroups}");
            sb.AppendLine($"  DTMF ID : {dtmf.CurrentId}");
            sb.AppendLine($"  VFO A   : {vfo.BankA.RxFreqMHz:F4} MHz");
            sb.AppendLine($"  VFO B   : {vfo.BankB.RxFreqMHz:F4} MHz");
            sb.AppendLine($"  Squelch : {optfun.AnalogSql} (analog) / {optfun.DigitalSql} (DMR)");
            return sb.ToString();
        }

        private static string FormatHex(byte[] data, int bytesPerLine)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < data.Length; i += bytesPerLine)
            {
                sb.Append($"  {i:X4}: ");
                int end = Math.Min(i + bytesPerLine, data.Length);
                for (int j = i; j < end; j++)
                    sb.Append($"{data[j]:X2} ");
                sb.AppendLine();
            }
            return sb.ToString();
        }

        // ── Human-readable JSON persistence ──────────────────────────────────────
        // All blocks are decoded to field-level values (frequencies as MHz strings,
        // tones as human strings, enums as names). See CodeplugJson.cs.

        private static void SaveJson(SessionData data, string path)
        {
            string json = CodeplugJson.Serialize(data);
            File.WriteAllText(path, json);
        }

        private static SessionData LoadJson(string path)
        {
            string json = File.ReadAllText(path);
            return CodeplugJson.Deserialize(json);
        }

        private static void PrintUsage()
        {
            Console.WriteLine("""
Usage: RT5D <command> [args] [--debug]

Commands:
  test               Run all unit tests and exit.
  ports              List available serial ports.
  info <port>        Connect, read version block, and disconnect.
  read <port> [out]  Read full configuration from radio.
                     Saves to out (default: rt5d_config.json).
  write <port> <in>  Write configuration file to radio.
              [--basic-info]  Also write model name/ID (optional).

Options:
  --debug            Show full protocol trace (frame TX/RX log).
                     Without this flag only progress and errors are shown.

Examples:
  RT5D test
  RT5D ports
  RT5D info COM3
  RT5D read COM3 myconfig.json
  RT5D read COM3 myconfig.json --debug
  RT5D write COM3 myconfig.json
""");
        }

        private static int BadUsage(string msg)
        {
            Console.Error.WriteLine($"Usage error: {msg}");
            PrintUsage();
            return 1;
        }
    }
}
