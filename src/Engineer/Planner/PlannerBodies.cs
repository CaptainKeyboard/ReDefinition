using System.Collections.Generic;
using System;
using ReDefinition.Core;
using UnityEngine;

namespace ReDefinition.Engineer
{
    // The bodies of the loaded system as the planner needs them (PlannerBody),
    // from FlightGlobals.Bodies: the stock system or whatever Kopernicus
    // loads. The order the planner lists them in: the star, then each body
    // followed by its moons, each level by the size of its orbit.
    internal sealed class PlannerBodies
    {
        // The bodies in the game's order (FlightGlobals.Bodies), their indices in the planner's order, and the home body's.
        public readonly List<PlannerBody> Bodies = new List<PlannerBody>();
        public readonly List<int> Order = new List<int>();
        public int Home;

        // In metres: a low orbit's height over the atmosphere, and over the highest ground of a body without air.
        private const double LowOrbitMargin = 10000.0;
        private const double TerrainMargin = 5000.0;
        // Whether the low orbits went to the log yet: once per game.
        private static bool logged;

        // The bodies of the system loaded now; none where the game has none yet.
        internal static PlannerBodies Read()
        {
            PlannerBodies read = new PlannerBodies();
            List<CelestialBody> bodies = FlightGlobals.Bodies;
            if (bodies == null) return read;

            Dictionary<CelestialBody, int> index = new Dictionary<CelestialBody, int>();
            for (int i = 0; i < bodies.Count; i++) index[bodies[i]] = i;
            for (int i = 0; i < bodies.Count; i++)
            {
                CelestialBody body = bodies[i];
                int parent;
                if (body.orbit == null || body.referenceBody == null || body.referenceBody == body
                    || !index.TryGetValue(body.referenceBody, out parent))
                    parent = -1;
                read.Bodies.Add(new PlannerBody
                {
                    Name = body.displayName.LocalizeRemoveGender(),
                    Id = body.bodyName,
                    Parent = parent,
                    Mu = body.gravParameter,
                    Radius = body.Radius,
                    RotationPeriod = body.rotationPeriod,
                    AtmosphereDepth = body.atmosphere ? body.atmosphereDepth : 0.0,
                    SurfacePressure = body.atmosphere ? body.atmospherePressureSeaLevel / 101.325 : 0.0,
                    SemiMajorAxis = body.orbit != null ? body.orbit.semiMajorAxis : 0.0,
                    Inclination = body.orbit != null ? body.orbit.inclination : 0.0,
                    SphereOfInfluence = body.sphereOfInfluence > 0.0 ? body.sphereOfInfluence : double.PositiveInfinity,
                    LowOrbitAltitude = LowOrbit(body),
                    Oxygen = body.atmosphere && body.atmosphereContainsOxygen,
                    SpeedOfSound = HighSpeedOfSound(body),
                });
            }

            CelestialBody home = FlightGlobals.GetHomeBody();
            read.Home = home != null && index.ContainsKey(home) ? index[home] : 0;
            for (int i = 0; i < read.Bodies.Count; i++)
                if (read.Bodies[i].Parent < 0) read.AddWithMoons(i);
            if (!logged)
            {
                logged = true;
                List<string> orbits = new List<string>();
                foreach (int i in read.Order)
                    orbits.Add(read.Bodies[i].Id + " " + RoutePlanner.Km(read.Bodies[i].LowOrbitAltitude)
                               + (read.Bodies[i].Oxygen ? " (oxygen, sound " + read.Bodies[i].SpeedOfSound.ToString("0") + " m/s high up)" : ""));
                Debug.Log(Log.Tag + " Planner low orbits: " + string.Join(", ", orbits.ToArray()));
            }
            return read;
        }

        // The speed of sound where the pressure has fallen to AirLaunch.HighAir of
        // the sea level's (CelestialBody.GetPressure, GetTemperature, GetDensity,
        // GetSpeedOfSound); 0 without air.
        private static double HighSpeedOfSound(CelestialBody body)
        {
            if (!body.atmosphere || body.atmosphereDepth <= 0.0) return 0.0;
            double sea = body.GetPressure(0.0);
            if (sea <= 0.0) return 0.0;
            double low = 0.0, high = body.atmosphereDepth;
            for (int i = 0; i < 40; i++)
            {
                double middle = (low + high) / 2.0;
                if (body.GetPressure(middle) > sea * AirLaunch.HighAir) low = middle;
                else high = middle;
            }
            double pressure = body.GetPressure(low);
            double density = body.GetDensity(pressure, body.GetTemperature(low));
            return density > 0.0 ? body.GetSpeedOfSound(pressure, density) : 0.0;
        }

        // The body, then each of its moons with their own moons, nearest orbit first.
        private void AddWithMoons(int body)
        {
            Order.Add(body);
            List<int> moons = new List<int>();
            for (int i = 0; i < Bodies.Count; i++)
                if (Bodies[i].Parent == body && i != body) moons.Add(i);
            moons.Sort((a, b) => Bodies[a].SemiMajorAxis.CompareTo(Bodies[b].SemiMajorAxis));
            foreach (int moon in moons) AddWithMoons(moon);
        }

        // Above the atmosphere, or above the highest ground where the terrain is
        // known; a twentieth of the radius and the same margin otherwise. In
        // whole kilometres.
        private static double LowOrbit(CelestialBody body)
        {
            double altitude;
            if (body.atmosphere)
            {
                altitude = body.atmosphereDepth + LowOrbitMargin;
            }
            else
            {
                double highest = body.pqsController != null ? body.pqsController.radiusMax - body.Radius : 0.0;
                altitude = highest > 0.0 ? highest + TerrainMargin : body.Radius * 0.05 + TerrainMargin;
            }
            if (body.sphereOfInfluence > 0.0 && body.Radius + altitude > body.sphereOfInfluence)
                altitude = Math.Max(1000.0, (body.sphereOfInfluence - body.Radius) / 2.0);
            return Math.Ceiling(altitude / 1000.0) * 1000.0;
        }

        // By the name the game knows the body by, whatever the language.
        internal int IndexOf(string id)
        {
            for (int i = 0; i < Bodies.Count; i++)
                if (Bodies[i].Id == id) return i;
            return -1;
        }

        // How high an orbit may be: inside the body's sphere of influence.
        internal double MaxAltitude(int body)
        {
            PlannerBody planner = Bodies[body];
            if (double.IsInfinity(planner.SphereOfInfluence)) return planner.Radius * 100.0;
            return Math.Max(planner.LowOrbitAltitude, planner.SphereOfInfluence - planner.Radius - 1000.0);
        }
    }
}
