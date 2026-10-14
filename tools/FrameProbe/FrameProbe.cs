using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using BepInEx;
using UnityEngine;

namespace SkaldTools
{
    /// <summary>
    /// TEST-ONLY client plugin (never shipped with Skald): logs frames that take much longer than usual, with a UTC
    /// timestamp and whether a GC ran, so hitches can be lined up with server/dashboard activity.
    /// Output: BepInEx/frameprobe.csv. Disk writes happen on a background thread.
    /// </summary>
    [BepInPlugin("skald.tools.frameprobe", "Skald FrameProbe (test only)", "0.1.0")]
    public class FrameProbe : BaseUnityPlugin
    {
        readonly float[] _window = new float[240];
        int _n, _i;
        int _gc0, _gc1, _gc2;
        float _nextSummary;
        readonly List<float> _second = new List<float>(400);
        readonly ConcurrentQueue<string> _lines = new ConcurrentQueue<string>();
        Thread _writer;
        volatile bool _run = true;
        string _file;

        void Awake()
        {
            _file = Path.Combine(Paths.BepInExRootPath, "frameprobe.csv");
            File.WriteAllText(_file, "utc,kind,frame_ms,median_ms,gc0,gc1,gc2,detail\n");
            _gc0 = GC.CollectionCount(0); _gc1 = GC.CollectionCount(1); _gc2 = GC.CollectionCount(2);
            _writer = new Thread(WriteLoop) { IsBackground = true, Name = "FrameProbe-writer" };
            _writer.Start();
            Logger.LogInfo("FrameProbe writing to " + _file);
        }

        void Update()
        {
            float ms = Time.unscaledDeltaTime * 1000f;
            int g0 = GC.CollectionCount(0), g1 = GC.CollectionCount(1), g2 = GC.CollectionCount(2);
            float median = Median();
            // A hitch: clearly longer than this player's normal frame.
            if (_n >= 60 && ms > Math.Max(median * 2f, median + 8f))
                _lines.Enqueue($"{DateTime.UtcNow:HH:mm:ss.fff},hitch,{ms:0.0},{median:0.0},{g0 - _gc0},{g1 - _gc1},{g2 - _gc2},");
            _gc0 = g0; _gc1 = g1; _gc2 = g2;

            _window[_i] = ms; _i = (_i + 1) % _window.Length; if (_n < _window.Length) _n++;
            _second.Add(ms);
            if (Time.unscaledTime >= _nextSummary)
            {
                _nextSummary = Time.unscaledTime + 10f;
                if (_second.Count > 0)
                {
                    _second.Sort();
                    float p50 = _second[_second.Count / 2], p99 = _second[Math.Min(_second.Count - 1, (int)(_second.Count * 0.99f))], max = _second[_second.Count - 1];
                    _lines.Enqueue($"{DateTime.UtcNow:HH:mm:ss.fff},summary10s,{max:0.0},{p50:0.0},,,,frames={_second.Count} p99={p99:0.0}");
                    _second.Clear();
                }
            }
        }

        readonly float[] _sortBuf = new float[240];
        float _cachedMedian = 16f;
        int _medianAge;

        // Recomputed every 30 frames into a reused buffer (no per-frame allocation, so the probe adds no GC itself).
        float Median()
        {
            if (_n == 0) return 16f;
            if (_medianAge-- > 0) return _cachedMedian;
            _medianAge = 30;
            Array.Copy(_window, _sortBuf, _n);
            Array.Sort(_sortBuf, 0, _n);
            return _cachedMedian = _sortBuf[_n / 2];
        }

        void WriteLoop()
        {
            while (_run)
            {
                Thread.Sleep(1000);
                if (_lines.IsEmpty) continue;
                using (var w = File.AppendText(_file))
                    while (_lines.TryDequeue(out var l)) w.WriteLine(l);
            }
        }

        void OnDestroy() => _run = false;
    }
}
