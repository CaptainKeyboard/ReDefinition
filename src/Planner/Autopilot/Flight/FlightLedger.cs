using System.Collections.Generic;
using System.Text;
using ReDefinition.Planner.Autopilot.Planning;

namespace ReDefinition.Planner.Autopilot.Flight
{
    // What a flight has burned and how the vessel behaved between the burns: the count of the burns by what they are for, one line for each
    // burn, and the fastest turn of the vessel while it coasted. The probes check these against what a route may take.
    internal sealed class FlightLedger
    {
        private readonly int[] counts = new int[6];
        private readonly List<string> lines = new List<string>();

        // How fast the vessel turned at most while it coasted or warped, in rad/s, and the longest coast that was watched, in s of the game's time.
        internal double CoastRate { get; private set; }
        internal double CoastSeconds { get; private set; }

        internal int Total
        {
            get
            {
                int sum = 0;
                foreach (int count in counts) sum += count;
                return sum;
            }
        }

        internal int Count(NodePlanner.BurnPurpose purpose)
        {
            return counts[(int)purpose];
        }

        // The burns that bring the vessel to the target body: the transfer and its corrections, before the capture.
        internal int UntilEncounter
        {
            get { return Count(NodePlanner.BurnPurpose.Transfer) + Count(NodePlanner.BurnPurpose.Correction); }
        }

        internal IList<string> Lines
        {
            get { return lines; }
        }

        internal void Add(NodePlanner.BurnPurpose purpose, string line)
        {
            counts[(int)purpose]++;
            lines.Add(line);
        }

        // One coast watched: its length and the fastest turn in it.
        internal void Coasted(double seconds, double maxRate)
        {
            if (maxRate > CoastRate) CoastRate = maxRate;
            if (seconds > CoastSeconds) CoastSeconds = seconds;
        }

        internal string Summary()
        {
            var text = new StringBuilder();
            text.Append(Total).Append(" burns: ");
            text.Append(Count(NodePlanner.BurnPurpose.Transfer)).Append(" transfer, ");
            text.Append(Count(NodePlanner.BurnPurpose.Correction)).Append(" correction, ");
            text.Append(Count(NodePlanner.BurnPurpose.Capture)).Append(" capture, ");
            text.Append(Count(NodePlanner.BurnPurpose.Orbit)).Append(" orbit, ");
            text.Append(Count(NodePlanner.BurnPurpose.Deorbit)).Append(" deorbit, ");
            text.Append(Count(NodePlanner.BurnPurpose.Other)).Append(" other; fastest turn while coasting ").Append(CoastRate.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture));
            text.Append(" rad/s.");
            return text.ToString();
        }
    }
}
