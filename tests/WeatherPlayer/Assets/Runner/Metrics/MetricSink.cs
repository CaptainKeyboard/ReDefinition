using System;
using System.Globalization;
using UnityEngine;

namespace ReDefinition.WeatherPlayer
{
    // Writes the metric lines of a run: WEATHERPLAYER {name,value,limit,pass} in the player's log, which tools/build_weather_player.py
    // reads (tools/weather_metrics, Context.player_metrics). The limit is text ("<= 1.5", ">= 0.9", "0.215 +- 0.02", "-"), pass is
    // true, false or info. A value that is not a finite number fails every verdict but info.
    public sealed class MetricSink
    {
        public int Failed { get; private set; }
        public int Count { get; private set; }

        public static void Line(string text)
        {
            Debug.Log("WEATHERPLAYER " + text);
        }

        private void Write(string name, double value, string limit, string pass)
        {
            Count++;
            if (pass == "false") Failed++;
            Line("{" + name + "," + Number(value) + "," + limit + "," + pass + "}");
        }

        private static string Number(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value)) return "nan";
            return value.ToString("0.#####", CultureInfo.InvariantCulture);
        }

        private static bool Finite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }

        public void Info(string name, double value)
        {
            Write(name, value, "-", "info");
        }

        public void AtMost(string name, double value, double limit)
        {
            Write(name, value, "<=" + Number(limit), Finite(value) && value <= limit ? "true" : "false");
        }

        public void AtLeast(string name, double value, double limit)
        {
            Write(name, value, ">=" + Number(limit), Finite(value) && value >= limit ? "true" : "false");
        }

        // Within rel of the known value, or within floor where that is larger.
        public void Reference(string name, double value, double known, double rel, double floor)
        {
            double tolerance = Math.Max(rel * Math.Abs(known), floor);
            Write(name, value, Number(known) + "+-" + Number(tolerance), Finite(value) && Math.Abs(value - known) <= tolerance ? "true" : "false");
        }

        public void Flag(string name, bool ok)
        {
            Write(name, ok ? 1 : 0, "==1", ok ? "true" : "false");
        }

        // The end of a run: the line the runner waits for.
        public void Finish()
        {
            Line(Failed == 0 ? "all checks passed" : "FAIL  " + Failed + " of " + Count + " metrics failed");
        }
    }
}
