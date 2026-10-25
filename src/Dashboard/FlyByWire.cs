using System;
using UnityEngine;

namespace ReDefinition.Dashboard
{
    /// <summary>
    /// Steering from a dashboard on the active vessel, in two ways:
    /// <list type="bullet">
    /// <item>Rates: pitch, yaw and roll added to the player's own input, as keys would.</item>
    /// <item>A target attitude, direction and roll: the vessel turns there and stays (dragging the
    /// navball; the roll buttons turn the target about its own axis). With a target altitude the
    /// turn is spread over the climb or descent to that altitude, linearly, as an S-curve, or (auto)
    /// as MechJeb's classic gravity turn.</item>
    /// </list>
    /// Rates have to be repeated while a finger is down and run out after <see cref="HoldSeconds"/>,
    /// so a dropped connection never leaves the controls deflected. A target is held until the
    /// player steers with the keyboard or a joystick, the vessel changes, or the dashboard resets it.
    /// </summary>
    internal static class FlyByWire
    {
        // How long a rate or a roll-button press holds without being repeated.
        public const float HoldSeconds = 0.3f;

        // KSP's pitch axis, checked in the game: positive raises the nose, like a nose-up input.
        private const float PitchSign = 1f;

        // The turn to a target, in two loops (as MechJeb and KSP's SAS do it in principle):
        // - The nose turns along the shortest way to the target's (a great circle, about the axis
        //   square to both), the vessel rolls about the nose by itself; the one never bends the
        //   other's way. The wanted turning speed is what the vessel can still brake from before
        //   the target: sqrt(2 * BRAKE * alpha * error), alpha being how fast the vessel can change
        //   its turning speed about that axis (its torque over its moment of inertia). Near the
        //   target it is error / SETTLE instead, at most MAX_RATE.
        // - Per axis of the vessel, the input brings its turning speed (Vessel.angularVelocityD) to
        //   the wanted one within RESPONSE seconds, at the vessel's alpha; an integral part (over
        //   INTEGRAL seconds) grows while the vessel lags behind, so steady drag or an off-centre
        //   thrust cannot hold it short of the target.
        private const double MAX_RATE = 20 * Math.PI / 180;
        private const double BRAKE = 0.5;       // share of the vessel's alpha planned for braking
        private const double SETTLE = 0.8;      // seconds: near the target the error is closed over this time
        private const double RESPONSE = 0.25;   // seconds in which the input brings the turning speed to the wanted one
        private const double INTEGRAL = 1.5;    // seconds in which a steady lag adds as much again through the integral part
        private const double MIN_ALPHA = 0.02;  // radians per second squared, when nothing is known
        private const float ALPHA_EVERY = 0.5f; // seconds between readings of the vessel's torque
        // How fast the roll buttons turn the target about its axis, in radians per second.
        private const double ROLL_RATE = 45 * Math.PI / 180;
        // Further than this from the aim, the nose keeps the way round it has taken.
        private const double KEEP_WAY_ROUND = 150 * Math.PI / 180;
        // Player input above this on pitch, yaw or roll takes the target away (below it is a
        // joystick's resting noise).
        private const float PLAYER_INPUT = 0.05f;

        // The rates last set and when they run out (Time.realtimeSinceStartup), and the vessel whose
        // OnFlyByWire carries Apply.
        private static float pitch, yaw, roll, until;
        private static Vessel attached;

        // Whether a target attitude is held (target, targetTop), with the roll buttons' direction and their
        // end, the turn's state, the vessel's alpha and when it was read.
        private static bool steering;
        // Paused, a target can be set and adjusted but is not steered to; play starts the turn.
        private static bool armed = true;
        private static Vector3d target, targetTop; // north, east, up: the target's nose and its top
        private static double rollDir;
        private static float rollUntil;
        private static readonly Turn turn = new Turn();
        private static Vector3d alpha = new Vector3d(MIN_ALPHA, MIN_ALPHA, MIN_ALPHA); // pitch, roll, yaw: as Alpha last read it, every ALPHA_EVERY
        private static float alphaAt = -1f;

        // The target altitude, and the turn spread over the way there: it started at startAttitude
        // (north, east, up) and at startAltitude.
        private static bool altitudeSet, spreading;
        private static double targetAltitude, startAltitude;
        private static Quaternion startAttitude;

        // How the turn is spread over the way to the target altitude. Auto climbs as MechJeb's classic
        // ascent with its automatic turn does: straight up until 5 % of the atmosphere's height or about
        // 99 m/s, then the pitch falls with ((altitude - turn start) / (turn end - turn start))^0.4,
        // the turn ending at 85 % of the atmosphere's height or the target altitude if lower; the nose
        // stays within 2000 Pa*rad / dynamic pressure of the surface prograde (MechJeb's Q-alpha limit).
        // Going down it is the S-curve.
        public enum Curve { Linear, S, Auto } // evenly, slow-faster-slow, or MechJeb's classic ascent
        private static Curve curve = Curve.S; // the shape chosen, the S-curve until the dashboard picks another
        private const double AUTO_SHAPE = 0.4; // the power the share of the way climbed is raised to
        private const double AUTO_TURN_START_SHARE = 0.05, AUTO_TURN_START_SPEED = 98.9; // m/s, MechJeb's 18.5^3 / 64
        private const double AUTO_TURN_END_SHARE = 0.85;
        private const double AUTO_VACUUM_START = 25; // metres climbed first on an airless body
        private const double Q_ALPHA = 2000;          // Pa * rad, MechJeb's limit on dynamic pressure times angle of attack
        // Above the prograde the nose may go this many times further: a climb that has gone flatter
        // than the curve has to be pulled up again, or it would follow its prograde to the horizon.
        private const double AOA_UP = 3;
        // Where the turn left the straight climb, or NaN while still climbing straight.
        private static double autoTurnStart = double.NaN;

        /// <summary>Values from -1 to 1. Pitch up, yaw right and roll right are positive.</summary>
        public static void Set(float p, float y, float r)
        {
            pitch = Mathf.Clamp(p, -1f, 1f);
            yaw = Mathf.Clamp(y, -1f, 1f);
            roll = Mathf.Clamp(r, -1f, 1f);
            until = Time.realtimeSinceStartup + HoldSeconds;
        }

        // Ends the rates at once (v.fbwOff); a target attitude stays.
        public static void Release()
        {
            until = 0f;
        }

        /// <summary>Turn to this attitude: the nose to t, the top to top (both north, east, up). With a
        /// target altitude set, the turn is spread from where the steering points now (or, without a
        /// target, from the vessel's attitude) over the way from the present altitude.</summary>
        public static void SteerTo(Vector3d t, Vector3d top)
        {
            if (t.magnitude < 1e-6) return;
            t = t.normalized;
            top = Vector3d.Exclude(t, top);
            if (top.magnitude < 1e-6) return;
            Restart();
            if (!steering) turn.Clear();
            target = t;
            targetTop = top.normalized;
            steering = true;
        }

        // The spread turn starts again here: from the attitude aimed at now, at the present altitude.
        // Called before the target or the target altitude changes, so the turn goes on from where it
        // is instead of from where an earlier one began.
        private static void Restart()
        {
            Vessel v = attached;
            if (curve == Curve.Auto && v != null && !altitudeSet) AutoAltitude(v);
            if (!altitudeSet || v == null || Math.Abs(targetAltitude - v.altitude) <= 1) { spreading = false; autoTurnStart = double.NaN; return; }
            Vector3d nose, top;
            if (steering && armed) Aim(v, out nose, out top); // with the turn as it is, before it starts again
            else Present(v, out nose, out top);
            autoTurnStart = double.NaN;
            startAttitude = Quaternion.LookRotation((Vector3)nose, (Vector3)top);
            startAltitude = v.altitude;
            spreading = true;
        }

        // What the steering points at now (north, east, up): the target, or with a target altitude the
        // part of the turn that belongs to the altitude reached.
        private static void Aim(Vessel v, out Vector3d aim, out Vector3d aimTop)
        {
            aim = target; aimTop = targetTop;
            if (!spreading || !altitudeSet) return;
            double way = targetAltitude - startAltitude;
            double f = Math.Abs(way) < 1 ? 1 : Math.Max(0, Math.Min(1, (v.altitude - startAltitude) / way));
            bool climbing = curve == Curve.Auto && way > 0;
            if (climbing) f = AutoTurn(v);
            else if (curve != Curve.Linear) f = f * f * (3 - 2 * f);
            Quaternion q = Quaternion.Slerp(startAttitude, Quaternion.LookRotation((Vector3)target, (Vector3)targetTop), (float)f);
            aim = (Vector3d)(q * Vector3.forward);
            aimTop = (Vector3d)(q * Vector3.up);
            if (climbing && f < 1) KeepNearPrograde(v, ref aim, ref aimTop);
        }

        // Auto's share of the turn done: none while climbing straight, then MechJeb's curve.
        private static double AutoTurn(Vessel v)
        {
            CelestialBody body = v.mainBody;
            if (double.IsNaN(autoTurnStart))
            {
                bool turn = body.atmosphere
                    ? v.altitude >= AUTO_TURN_START_SHARE * body.atmosphereDepth || v.srfSpeed >= AUTO_TURN_START_SPEED
                    : v.altitude >= startAltitude + AUTO_VACUUM_START;
                if (!turn) return 0;
                autoTurnStart = v.altitude;
            }
            double end = body.atmosphere ? Math.Min(targetAltitude, AUTO_TURN_END_SHARE * body.atmosphereDepth) : targetAltitude;
            if (end - autoTurnStart < 1) return 1;
            return Math.Pow(Math.Max(0, Math.Min(1, (v.altitude - autoTurnStart) / (end - autoTurnStart))), AUTO_SHAPE);
        }

        // Auto's target altitude where none is set, when the vessel is below it (in an atmosphere) or
        // on the ground (without one); higher up the turn is done at once, as without an altitude.
        private static void AutoAltitude(Vessel v)
        {
            CelestialBody body = v.mainBody;
            double end;
            if (body.atmosphere) end = AUTO_TURN_END_SHARE * body.atmosphereDepth;
            else if (v.LandedOrSplashed || v.situation == Vessel.Situations.PRELAUNCH) end = v.altitude + Math.Max(1500, Math.Min(10000, 0.02 * body.Radius));
            else return;
            if (v.altitude >= end - 500) return;
            targetAltitude = end;
            altitudeSet = true;
        }

        // In the atmosphere a nose far off the airflow costs speed and can flip the vessel: the aim
        // stays within Q_ALPHA / dynamic pressure of the surface prograde, as MechJeb keeps it. Above
        // the prograde it may go further, so a climb gone too flat is pulled up again.
        private static void KeepNearPrograde(Vessel v, ref Vector3d aim, ref Vector3d aimTop)
        {
            double q = v.dynamicPressurekPa * 1000;
            if (q < 1 || v.srfSpeed < 30 || v.altitude >= v.mainBody.atmosphereDepth) return;
            double limit = Math.Min(180, Q_ALPHA / q * 180 / Math.PI);
            Vector3d north, east, up;
            Telemetry.Frame(v, out north, out east, out up);
            Vector3d w = v.srf_velocity;
            var prograde = new Vector3d(Vector3d.Dot(w, north), Vector3d.Dot(w, east), Vector3d.Dot(w, up)).normalized;
            double off = Vector3d.Angle(prograde, aim);
            if (aim.z > prograde.z) limit *= AOA_UP;
            if (off <= limit) return;
            aim = (Vector3d)Vector3.Slerp((Vector3)prograde, (Vector3)aim, (float)(limit / off));
            aimTop = Vector3d.Exclude(aim, aimTop).normalized;
        }

        /// <summary>While repeated, turn the target about its axis: 1 rolls right, -1 left. Without a
        /// target the vessel's present attitude becomes one.</summary>
        public static void SteerRoll(double dir)
        {
            Vessel v = attached;
            if (v == null) return;
            if (!steering)
            {
                Vector3d nose, top;
                Present(v, out nose, out top);
                SteerTo(nose, top);
            }
            rollDir = Math.Sign(dir);
            rollUntil = Time.realtimeSinceStartup + HoldSeconds;
        }

        /// <summary>The finger is off the ball. The target stays: nothing to do.</summary>
        public static void SteerLetGo()
        {
        }

        /// <summary>The altitude above sea level by which the turn to the target is to be done.</summary>
        public static void SetAltitude(double metres)
        {
            metres = Math.Max(0, metres);
            if (altitudeSet && Math.Abs(metres - targetAltitude) < 0.5) return;
            if (steering && altitudeSet) Restart(); // from where the turn is now, before the old altitude goes
            targetAltitude = metres;
            altitudeSet = true;
            if (steering) Restart();
        }

        /// <summary>The turn is spread as an S-curve (slow, faster, slow) or linearly.</summary>
        public static void SetSCurve(bool on)
        {
            SetCurve(on ? Curve.S : Curve.Linear);
        }

        /// <summary>"linear", "s" or "auto".</summary>
        public static void SetCurve(string name)
        {
            SetCurve(name == "auto" ? Curve.Auto : name == "linear" ? Curve.Linear : Curve.S);
        }

        // Changes the shape; a turn under way starts again from where it is, so it does not jump.
        private static void SetCurve(Curve c)
        {
            if (c == curve) return;
            if (steering) Restart(); // the new shape goes on from where the turn is
            curve = c;
            if (steering && c == Curve.Auto) Restart(); // auto may bring its own altitude
        }

        /// <summary>Play (true) steers to the target; pause (false) only holds it, to be adjusted.</summary>
        public static void SetArmed(bool on)
        {
            if (on == armed) return;
            armed = on;
            turn.HaveLast = false;
            if (on && steering) Restart(); // the spread turn begins from where the vessel points now
        }

        // n.steerArmed: whether play is on, the vessel steered to the target rather than it only held.
        public static bool Armed { get { return armed; } }

        // Drops the target and the target altitude (v.steerReset, and on a change of vessel).
        public static void Reset()
        {
            steering = false;
            altitudeSet = false;
            spreading = false;
        }

        /// <summary>The direction the vessel is turning to (north, east, up), or null.</summary>
        public static double[] Target
        {
            get { return steering ? new[] { target.x, target.y, target.z } : null; }
        }

        /// <summary>Where the vessel's top will point there (north, east, up), or null.</summary>
        public static double[] TargetTop
        {
            get { return steering ? new[] { targetTop.x, targetTop.y, targetTop.z } : null; }
        }

        /// <summary>The target altitude in metres, or null.</summary>
        public static object TargetAltitude
        {
            get { return altitudeSet ? (object)targetAltitude : null; }
        }

        // n.steerSCurve: whether the turn is spread as an S-curve.
        public static bool SCurve { get { return curve == Curve.S; } }

        // n.steerCurve: the shape's name as SetCurve(string) takes it, "linear", "s" or "auto".
        public static string CurveName { get { return curve == Curve.Auto ? "auto" : curve == Curve.Linear ? "linear" : "s"; } }

        /// <summary>Keeps the callback on the vessel the player flies. Called every frame.</summary>
        public static void Follow(Vessel active)
        {
            if (active == attached) return;
            if (attached != null) attached.OnFlyByWire -= Apply;
            if (active != null)
            {
                active.OnFlyByWire -= Apply;
                active.OnFlyByWire += Apply;
            }
            attached = active;
            Release();
            Reset();
        }

        // The vessel's OnFlyByWire, each physics step: steers to the target, then adds the rates while they hold.
        private static void Apply(FlightCtrlState s)
        {
            float now = Time.realtimeSinceStartup;
            if (steering) Steer(s);
            if (now >= until) return;
            s.pitch = Mathf.Clamp(s.pitch + PitchSign * pitch, -1f, 1f);
            s.yaw = Mathf.Clamp(s.yaw + yaw, -1f, 1f);
            s.roll = Mathf.Clamp(s.roll + roll, -1f, 1f);
        }

        // The vessel's nose and top as north, east, up.
        private static void Present(Vessel v, out Vector3d nose, out Vector3d top)
        {
            Vector3d north, east, up;
            Telemetry.Frame(v, out north, out east, out up);
            Transform rt = v.ReferenceTransform;
            Vector3d n = rt.up, t = -rt.forward;
            nose = new Vector3d(Vector3d.Dot(n, north), Vector3d.Dot(n, east), Vector3d.Dot(n, up));
            top = new Vector3d(Vector3d.Dot(t, north), Vector3d.Dot(t, east), Vector3d.Dot(t, up));
        }

        // One step of the turn to the target: rolls the target while a roll button holds, gives the target up
        // where the player steers, and otherwise sets pitch, yaw and roll from Inputs.
        private static void Steer(FlightCtrlState s)
        {
            Vessel v = attached;
            if (v == null) { steering = false; return; }
            double dt = Math.Max(TimeWarp.fixedDeltaTime, 1e-3);

            // The roll buttons turn the target's top about its nose; rolling right moves the top
            // towards the target's right. This works paused too, to set the manoeuvre up.
            if (Time.realtimeSinceStartup < rollUntil)
            {
                double a = rollDir * ROLL_RATE * dt;
                Vector3d targetRight = Vector3d.Cross(targetTop, target);
                targetTop = (targetTop * Math.Cos(a) + targetRight * Math.Sin(a)).normalized;
            }
            if (!armed) return; // paused: the player flies, the target waits

            // The player's own keys and joystick, before this mod adds anything, give the target up.
            FlightCtrlState player = FlightInputHandler.state;
            if (Mathf.Abs(player.pitch) > PLAYER_INPUT || Mathf.Abs(player.yaw) > PLAYER_INPUT || Mathf.Abs(player.roll) > PLAYER_INPUT)
            {
                steering = false;
                spreading = false;
                return;
            }

            Vector3d aim, aimTop;
            Aim(v, out aim, out aimTop);

            Vector3d north, east, up;
            Telemetry.Frame(v, out north, out east, out up);
            Vector3d t = aim.x * north + aim.y * east + aim.z * up;
            Vector3d tTop = aimTop.x * north + aimTop.y * east + aimTop.z * up;
            Transform rt = v.ReferenceTransform;
            float now = Time.realtimeSinceStartup;
            if (now - alphaAt >= ALPHA_EVERY || alphaAt < 0) { alphaAt = now; alpha = Alpha(v); }

            // The steering replaces the pitch, yaw and roll input; SAS is told to hold where the
            // vessel points now, so it neither fights the turn nor turns back afterwards.
            Vector3d input = Inputs(turn, rt.up, rt.right, -rt.forward, t, tTop, alpha, dt);
            s.pitch = PitchSign * (float)input.x;
            s.yaw = (float)input.y;
            s.roll = (float)input.z;
            if (v.Autopilot.Enabled && v.Autopilot.Mode == VesselAutopilot.AutopilotMode.StabilityAssist)
                v.Autopilot.SAS.LockRotation(rt.rotation);
        }

        // What the turn keeps from frame to frame: the vessel's axes and its turning speed worked out
        // from them, the axis the nose turns about, the roll error, the integral parts.
        internal sealed class Turn
        {
            public bool HaveLast; // whether the fields below hold a previous frame
            public Vector3d Nose, Right, Top, Spin, Axis; // the axes last frame, the turning speed, the nose's axis
            public double Roll, PitchI, YawI, RollI; // the roll error last frame and each axis's integral part

            // Forgets the last frame, the turning speed and the integral parts, for a turn that starts anew.
            public void Clear()
            {
                HaveLast = false;
                Spin = Vector3d.zero;
                PitchI = YawI = RollI = 0;
            }
        }

        // The input on pitch, yaw and roll (x, y, z), each -1 to 1, that turns a vessel with these axes
        // (nose, right, top) to the aim and its top, all in one frame; `alpha` per axis as Alpha gives
        // it (pitch, roll, yaw). Positive pitch raises the nose towards the top, positive yaw turns it
        // right, positive roll turns the top right. Free of Unity, tested.
        internal static Vector3d Inputs(Turn turn, Vector3d nose, Vector3d right, Vector3d top, Vector3d aim, Vector3d aimTop, Vector3d alpha,
                                        double dt)
        {
            // How the vessel turns: from how its axes moved since the last frame (for a small
            // rotation, the sum of each axis before crossed with it after is twice the rotation),
            // smoothed a little against the jitter of single frames. A point of it moves at
            // spin x its position.
            if (turn.HaveLast)
            {
                Vector3d moved = (Vector3d.Cross(turn.Nose, nose) + Vector3d.Cross(turn.Right, right) + Vector3d.Cross(turn.Top, top)) / (2 * dt);
                turn.Spin += 0.4 * (moved - turn.Spin);
            }
            else turn.Spin = Vector3d.zero;
            turn.Nose = nose; turn.Right = right; turn.Top = top;

            // Turning about these moves the nose towards the top, the nose to the right, the top to
            // the right.
            Vector3d pitchAxis = Vector3d.Cross(nose, top), yawAxis = Vector3d.Cross(nose, right), rollAxis = Vector3d.Cross(top, right);

            // The nose's way: about the axis square to it and the aim. Nearly straight away from the
            // aim that axis turns with every small movement, so the way round once taken is kept.
            Vector3d side = Vector3d.Cross(nose, aim);
            double angle = Math.Atan2(side.magnitude, Vector3d.Dot(nose, aim));
            Vector3d axis = side.magnitude > 1e-9 ? side / side.magnitude : Vector3d.zero;
            if (angle > KEEP_WAY_ROUND)
            {
                Vector3d kept = turn.HaveLast ? Vector3d.Exclude(nose, turn.Axis) : Vector3d.zero;
                if (kept.magnitude > 1e-6) axis = kept.normalized;
                else if (axis.magnitude < 0.5) axis = pitchAxis;
            }
            turn.Axis = axis;
            double p = Vector3d.Dot(axis, pitchAxis), y = Vector3d.Dot(axis, yawAxis);
            double noseAlpha = Math.Max(MIN_ALPHA, p * p * alpha.x + y * y * alpha.z);

            double rollError = Math.Atan2(Vector3d.Dot(aimTop, right), Vector3d.Dot(aimTop, top));
            if (turn.HaveLast) rollError = Continue(rollError, turn.Roll);
            turn.Roll = rollError;
            turn.HaveLast = true;

            Vector3d wanted = axis * Wanted(angle, noseAlpha) + rollAxis * Wanted(rollError, alpha.y);
            // Rolling about a nose that turns: the roll's own axis turns with it, which takes an input
            // square to the nose that the vessel is given ahead rather than left to fall behind.
            Vector3d keep = Vector3d.Cross(turn.Spin, rollAxis) * Vector3d.Dot(turn.Spin, rollAxis);
            return new Vector3d(
                Command(Vector3d.Dot(wanted, pitchAxis), Vector3d.Dot(turn.Spin, pitchAxis), Vector3d.Dot(keep, pitchAxis), alpha.x, ref turn.PitchI, dt),
                Command(Vector3d.Dot(wanted, yawAxis), Vector3d.Dot(turn.Spin, yawAxis), Vector3d.Dot(keep, yawAxis), alpha.z, ref turn.YawI, dt),
                Command(Vector3d.Dot(wanted, rollAxis), Vector3d.Dot(turn.Spin, rollAxis), 0, alpha.y, ref turn.RollI, dt));
        }

        // The turning speed wanted towards the target from an error (radians) at the vessel's alpha
        // about that axis: what it can still brake from, near the target error / SETTLE.
        private static double Wanted(double error, double a)
        {
            double size = Math.Abs(error);
            return Math.Sign(error) * Math.Min(MAX_RATE, Math.Min(Math.Sqrt(2 * BRAKE * a * size), size / SETTLE));
        }

        // Input for one axis from the turning speed wanted about it and the one it has (radians per
        // second), at the vessel's alpha about it, and the change of turning speed it needs besides
        // (radians per second squared). Positive input speeds the turn up.
        private static double Command(double wanted, double turning, double ahead, double a, ref double integral, double dt)
        {
            double shortfall = wanted - turning;
            double p = shortfall / (RESPONSE * a);
            // The integral only grows while it can help, and never beyond full input.
            double next = integral + shortfall * dt / (INTEGRAL * RESPONSE * a);
            if (Math.Abs(p + next) < 1 || Math.Abs(next) < Math.Abs(integral)) integral = Clamp(next);
            return Clamp(p + integral + ahead / a);
        }

        // How fast the vessel can change its turning speed on each axis (pitch, roll, yaw), from the
        // torque its parts can give (reaction wheels, gimbals, RCS, control surfaces) over its moment
        // of inertia. Half the average of the two directions, as aerodynamics and thrust vary.
        private static Vector3d Alpha(Vessel v)
        {
            var torque = new Vector3d();
            foreach (Part p in v.parts)
                foreach (PartModule m in p.Modules)
                {
                    var provider = m as ITorqueProvider;
                    if (provider == null) continue;
                    Vector3 pos, neg;
                    try { provider.GetPotentialTorque(out pos, out neg); }
                    catch { continue; }
                    torque.x += (Mathf.Abs(pos.x) + Mathf.Abs(neg.x)) / 2;
                    torque.y += (Mathf.Abs(pos.y) + Mathf.Abs(neg.y)) / 2;
                    torque.z += (Mathf.Abs(pos.z) + Mathf.Abs(neg.z)) / 2;
                }
            Vector3 moi = v.MOI;
            Func<double, double, double> per = (t, i) => i > 1e-6 ? Math.Max(MIN_ALPHA, t / i) : MIN_ALPHA;
            return new Vector3d(per(torque.x, moi.x), per(torque.y, moi.y), per(torque.z, moi.z));
        }

        // An error near 180 degrees would flip between turning one way and the other with every small
        // movement, so the vessel hardly starts. The error is kept continuous instead: once a way round
        // is taken it is kept, up to 270 degrees, and only beyond that measured afresh.
        private static double Continue(double raw, double last)
        {
            double e = last + Wrap(raw - last);
            return Math.Abs(e) > 1.5 * Math.PI ? raw : e;
        }

        // An angle difference brought into -pi..pi.
        private static double Wrap(double a)
        {
            while (a > Math.PI) a -= 2 * Math.PI;
            while (a < -Math.PI) a += 2 * Math.PI;
            return a;
        }

        // A control input held within -1 to 1.
        private static double Clamp(double x)
        {
            return x < -1 ? -1 : x > 1 ? 1 : x;
        }
    }
}
