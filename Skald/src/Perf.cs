using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Skald
{
    /// <summary>
    /// Self-measurement: time spent in each Skald hook/job on the game thread, slow server frames, and dashboard
    /// requests. Exposed at GET /api/perf so overhead can be checked on any server, not just ours.
    /// </summary>
    internal static class Perf
    {
        sealed class Stat { public long Count; public double TotalMs, MaxMs; }

        static readonly object Gate = new object();
        static readonly Dictionary<string, Stat> Stats = new Dictionary<string, Stat>();
        static readonly Queue<string> SlowFrames = new Queue<string>();
        static readonly Queue<string> Requests = new Queue<string>();
        static readonly double TickMs = 1000.0 / Stopwatch.Frequency;
        static readonly DateTime Started = DateTime.UtcNow;
        static long _frames;
        static double _frameMsTotal;

        public static long Now() => Stopwatch.GetTimestamp();

        public static void Add(string name, long startTicks)
        {
            double ms = (Stopwatch.GetTimestamp() - startTicks) * TickMs;
            lock (Gate)
            {
                if (!Stats.TryGetValue(name, out var s)) Stats[name] = s = new Stat();
                s.Count++;
                s.TotalMs += ms;
                if (ms > s.MaxMs) s.MaxMs = ms;
            }
        }

        /// <summary>Called once per server frame with Time.unscaledDeltaTime.</summary>
        public static void Frame(float dt)
        {
            _frames++;
            _frameMsTotal += dt * 1000.0;
            if (dt < 0.04f) return;
            lock (Gate)
            {
                SlowFrames.Enqueue($"{DateTime.UtcNow:HH:mm:ss.fff} {dt * 1000f:0} ms");
                while (SlowFrames.Count > 100) SlowFrames.Dequeue();
            }
        }

        public static void Request(string path, long startTicks, int bytes)
        {
            double ms = (Stopwatch.GetTimestamp() - startTicks) * TickMs;
            lock (Gate)
            {
                Requests.Enqueue($"{DateTime.UtcNow:HH:mm:ss.fff} {path} {ms:0.00} ms {bytes} B");
                while (Requests.Count > 300) Requests.Dequeue();
            }
        }

        public static string Json()
        {
            var sb = new StringBuilder("{");
            lock (Gate)
            {
                double up = (DateTime.UtcNow - Started).TotalSeconds;
                sb.Append("\"uptimeSec\":").Append(up.ToString("0", CultureInfo.InvariantCulture));
                sb.Append(",\"serverFrames\":").Append(_frames)
                  .Append(",\"avgServerFrameMs\":").Append((_frames > 0 ? _frameMsTotal / _frames : 0).ToString("0.00", CultureInfo.InvariantCulture));
                sb.Append(",\"stats\":[");
                sb.Append(string.Join(",", Stats.OrderByDescending(kv => kv.Value.TotalMs).Select(kv =>
                    "{\"name\":\"" + kv.Key + "\",\"count\":" + kv.Value.Count +
                    ",\"totalMs\":" + kv.Value.TotalMs.ToString("0.0", CultureInfo.InvariantCulture) +
                    ",\"avgUs\":" + (kv.Value.Count > 0 ? kv.Value.TotalMs * 1000 / kv.Value.Count : 0).ToString("0.0", CultureInfo.InvariantCulture) +
                    ",\"maxMs\":" + kv.Value.MaxMs.ToString("0.00", CultureInfo.InvariantCulture) +
                    ",\"msPerSec\":" + (up > 0 ? kv.Value.TotalMs / up : 0).ToString("0.000", CultureInfo.InvariantCulture) + "}")));
                sb.Append("],\"slowServerFrames\":[").Append(string.Join(",", SlowFrames.Select(s => "\"" + s + "\""))).Append(']');
                sb.Append(",\"requests\":[").Append(string.Join(",", Requests.Select(s => "\"" + s + "\""))).Append(']');
            }
            return sb.Append('}').ToString();
        }
    }
}
