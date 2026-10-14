using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace Skald
{
    /// <summary>One thing that happened on the server.</summary>
    public sealed class SkaldEvent
    {
        public DateTime Time;
        public string Type;      // join, leave, chest_take, chest_add, death, grave_take, built, removed, destroyed, boss_defeated, global_key
        public string Actor;     // player name, or "world"
        public string ActorId;   // platform/host id when known
        public string Target;    // prefab or object name
        public string Detail;    // human-readable detail (items moved, owner, ...)
        public float X, Y, Z;

        public string ToJson()
        {
            var sb = new StringBuilder(160);
            sb.Append('{');
            Str(sb, "t", Time.ToString("o", CultureInfo.InvariantCulture)); sb.Append(',');
            Str(sb, "type", Type); sb.Append(',');
            Str(sb, "actor", Actor); sb.Append(',');
            Str(sb, "actorId", ActorId); sb.Append(',');
            Str(sb, "target", Target); sb.Append(',');
            Str(sb, "detail", Detail); sb.Append(',');
            sb.Append("\"pos\":[")
              .Append(X.ToString("0.0", CultureInfo.InvariantCulture)).Append(',')
              .Append(Y.ToString("0.0", CultureInfo.InvariantCulture)).Append(',')
              .Append(Z.ToString("0.0", CultureInfo.InvariantCulture)).Append(']');
            sb.Append('}');
            return sb.ToString();
        }

        static void Str(StringBuilder sb, string key, string value)
        {
            sb.Append('"').Append(key).Append("\":");
            if (value == null) { sb.Append("null"); return; }
            sb.Append('"');
            foreach (char c in value)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }
    }

    /// <summary>
    /// Thread-safe in-memory ring of recent events plus an append-only JSONL file.
    /// Written from the game thread, read from the HTTP thread.
    /// </summary>
    public static class EventLog
    {
        const int MaxInMemory = 2000;
        static readonly object Gate = new object();
        static readonly LinkedList<SkaldEvent> Recent = new LinkedList<SkaldEvent>();
        static readonly Queue<string> PendingLines = new Queue<string>();
        static string _filePath;

        public static void Init(string dataDir)
        {
            Directory.CreateDirectory(dataDir);
            _filePath = Path.Combine(dataDir, "events.jsonl");
        }

        public static void Add(SkaldEvent e)
        {
            lock (Gate)
            {
                Recent.AddLast(e);
                if (Recent.Count > MaxInMemory) Recent.RemoveFirst();
                PendingLines.Enqueue(e.ToJson());
            }
            Plugin.Log.LogInfo($"[{e.Type}] {e.Actor}: {e.Target} {e.Detail}");
        }

        /// <summary>Append queued events to disk; called periodically from the game thread.</summary>
        public static void Flush()
        {
            if (_filePath == null) return;
            string[] lines;
            lock (Gate)
            {
                if (PendingLines.Count == 0) return;
                lines = PendingLines.ToArray();
                PendingLines.Clear();
            }
            try { File.AppendAllLines(_filePath, lines); }
            catch (Exception ex) { Plugin.Log.LogWarning("Could not write events file: " + ex.Message); }
        }

        public static string RecentJson(int max)
        {
            var sb = new StringBuilder();
            sb.Append('[');
            lock (Gate)
            {
                int skip = Math.Max(0, Recent.Count - max);
                bool first = true;
                // newest first
                var node = Recent.Last;
                int taken = 0;
                while (node != null && taken < Recent.Count - skip)
                {
                    if (!first) sb.Append(',');
                    sb.Append(node.Value.ToJson());
                    first = false;
                    taken++;
                    node = node.Previous;
                }
            }
            sb.Append(']');
            return sb.ToString();
        }
    }
}
