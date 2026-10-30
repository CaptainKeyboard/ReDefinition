// Adapted from MechJeb 2 (MechJeb2/ReentrySimulation.cs), GPL-3.0: the simulation of a descent to the ground, with its drag, parachutes and the ground's height.
// The names and the numbers are MechJeb's; what differs is the namespace and the reading of the vessel's state, which comes from KSP here.
using System;
using System.Collections.Generic;
using UnityEngine;

namespace ReDefinition.Landing
{
    public partial class ReentrySimulation
    {
        private Orbit _inputInitialOrbit;

        private double _inputUT;

        private IDescentSpeedPolicy _inputDescentSpeedPolicy;
        private double _inputDecelEndAltitudeASL;
        private double _inputMaxThrustAccel;
        private double _inputParachuteSemiDeployMultiplier;
        private double _inputProbableLandingSiteASL;
        private bool _inputMultiplierHasError;
        private double _inputDT;

        private readonly Orbit _initialOrbit = new Orbit();

        private bool _bodyHasAtmosphere;

        private double _bodyRadius;
        private double _gravParameter;

        private SimulatedVessel _vessel;
        private Vector3d _bodyAngularVelocity;
        private IDescentSpeedPolicy _descentSpeedPolicy;
        private double _decelRadius;
        private double _aerobrakedRadius;
        private double _startUT;

        private CelestialBody _mainBody;

        private double _maxThrustAccel;

        private double _probableLandingSiteASL;

        private double _probableLandingSiteRadius;

        private QuaternionD _attitude;

        private bool _orbitReenters;
        private readonly ReferenceFrame _referenceFrame = new ReferenceFrame();

        private double _dt;

        private double _maxDT;

        public double MinDT;

        private double _maxSimulatedTime;

        private double _maxOrbits;

        private bool _noSKiptoFreefall;

        private double _parachuteSemiDeployMultiplier;
        private bool _multiplierHasError;

        private Vector3d _x;
        private Vector3d _startX;
        private Vector3d _v;
        private double _t;

        private Vector3 _lastRecordedDrag;

        private double _maxDragGees;
        private double _maxDynamicPressurekPa;
        private double _maxConvectiveFluxKW;
        private double _deltaVExpended;
        private List<AbsoluteVector> _trajectory;

        private int _steps;

        public static int ActiveStep;
        public static double ActiveDt;

        private SimCurves _simCurves;

        private static bool _once = true;

        private static readonly Pool<ReentrySimulation> _pool = new Pool<ReentrySimulation>(Create, Reset);

        public static int PoolSize => _pool.Size;

        private static ReentrySimulation Create() => new ReentrySimulation();

        public void Release() => _pool.Release(this);

        private static void Reset(ReentrySimulation obj)
        {
        }

        public static ReentrySimulation Borrow(Orbit initialOrbit, double ut, SimulatedVessel vessel, SimCurves simcurves,
            IDescentSpeedPolicy descentSpeedPolicy, double decelEndAltitudeASL, double maxThrustAccel, double parachuteSemiDeployMultiplier,
            double probableLandingSiteASL, bool multiplierHasError, double dt, double minDT, double maxOrbits, bool noSKiptoFreefall)
        {
            ReentrySimulation sim = _pool.Borrow();
            sim.Init(initialOrbit, ut, vessel, simcurves, descentSpeedPolicy, decelEndAltitudeASL, maxThrustAccel,
                parachuteSemiDeployMultiplier, probableLandingSiteASL, multiplierHasError, dt, minDT, maxOrbits, noSKiptoFreefall);
            return sim;
        }

        private void Init(Orbit initialOrbit, double ut, SimulatedVessel vessel, SimCurves simcurves, IDescentSpeedPolicy descentSpeedPolicy,
            double decelEndAltitudeASL, double maxThrustAccel, double parachuteSemiDeployMultiplier, double probableLandingSiteASL,
            bool multiplierHasError, double dt, double minDT, double maxOrbits, bool noSKiptoFreefall)
        {
            _inputInitialOrbit = initialOrbit;
            _inputUT = ut;

            _vessel = vessel;
            _inputDescentSpeedPolicy = descentSpeedPolicy;
            _inputDecelEndAltitudeASL = decelEndAltitudeASL;
            _inputMaxThrustAccel = maxThrustAccel;
            _inputParachuteSemiDeployMultiplier = parachuteSemiDeployMultiplier;
            _inputProbableLandingSiteASL = probableLandingSiteASL;
            _inputMultiplierHasError = multiplierHasError;
            _inputDT = dt;
            _attitude = Quaternion.Euler(180, 0, 0);

            MinDT = minDT;
            _maxDT = dt;
            _dt = _maxDT;
            _steps = 0;

            _maxOrbits = maxOrbits;

            _noSKiptoFreefall = noSKiptoFreefall;

            _initialOrbit.UpdateFromOrbitAtUT(initialOrbit, ut, initialOrbit.referenceBody);

            CelestialBody body = initialOrbit.referenceBody;
            _bodyHasAtmosphere = body.atmosphere;
            _bodyRadius = body.Radius;
            _gravParameter = body.gravParameter;

            _parachuteSemiDeployMultiplier = parachuteSemiDeployMultiplier;
            _multiplierHasError = multiplierHasError;

            _bodyAngularVelocity = body.angularVelocity;
            _descentSpeedPolicy = descentSpeedPolicy;
            _decelRadius = _bodyRadius + decelEndAltitudeASL;
            _aerobrakedRadius = _bodyRadius + body.RealMaxAtmosphereAltitude();
            _mainBody = body;
            _maxThrustAccel = maxThrustAccel;
            _probableLandingSiteASL = probableLandingSiteASL;
            _probableLandingSiteRadius = probableLandingSiteASL + _bodyRadius;
            _referenceFrame.UpdateAtCurrentTime(initialOrbit.referenceBody);
            _orbitReenters = OrbitReenters(initialOrbit);

            _startX = _initialOrbit.WorldBCIPositionAtUT(_startUT);
            if (_orbitReenters)
            {
                _startUT = ut;
                _t = _startUT;
                AdvanceToFreefallEnd(_initialOrbit);
            }

            _maxDragGees = 0;
            _maxDynamicPressurekPa = 0;
            _maxConvectiveFluxKW = 0;
            _deltaVExpended = 0;
            _trajectory = ListPool<AbsoluteVector>.Instance.Borrow();

            _simCurves = simcurves;

            _once = true;
        }

        private Result _result;
        private static ulong _resultId;

        public Result RunSimulation()
        {
            _result = Result.Borrow();
            try
            {
                _result.InputInitialOrbit = _inputInitialOrbit;
                _result.InputUT = _inputUT;
                _result.InputDescentSpeedPolicy = _inputDescentSpeedPolicy;
                _result.InputDecelEndAltitudeASL = _inputDecelEndAltitudeASL;
                _result.InputMaxThrustAccel = _inputMaxThrustAccel;
                _result.InputParachuteSemiDeployMultiplier = _inputParachuteSemiDeployMultiplier;
                _result.InputProbableLandingSiteASL = _inputProbableLandingSiteASL;
                _result.InputMultiplierHasError = _inputMultiplierHasError;
                _result.InputDT = _inputDT;


                if (!_orbitReenters)
                {
                    _result.Outcome = Outcome.NO_REENTRY;
                    return _result;
                }

                _result.StartPosition = _referenceFrame.ToAbsolute(_x, _t);

                _maxSimulatedTime = _maxOrbits * 2.0 * Math.PI * Math.Sqrt(Math.Pow(Math.Abs(_x.magnitude), 3.0) / _gravParameter);

                RecordTrajectory();

                double maxT = _t + _maxSimulatedTime;
                while (true)
                {
                    if (Landed())
                    {
                        _result.Outcome = Outcome.LANDED;
                        break;
                    }

                    if (!_result.AeroBrake && Aerobraked())
                    {
                        _result.AeroBrake = true;
                        _result.AeroBrakeUT = _t;
                        _result.AeroBrakePosition = _referenceFrame.ToAbsolute(_x, _t);
                        _result.AeroBrakeVelocity = _referenceFrame.ToAbsolute(_v, _t);
                    }

                    if (_t > maxT || Escaping() || _steps > 50000)
                    {
                        _result.Outcome = _result.AeroBrake ? Outcome.AEROBRAKED : Outcome.TIMED_OUT;
                        break;
                    }

                    BS34Step();
                    LimitSpeed();
                    RecordTrajectory();
                }

                _result.ID = _resultId++;
                _result.Body = _mainBody;
                _result.ReferenceFrame = _referenceFrame;
                _result.EndUT = _t;
                _result.TimeToComplete = _t - _inputUT;
                _result.MaxDragGees = _maxDragGees;
                _result.MaxDynamicPressurekPa = _maxDynamicPressurekPa;
                _result.MaxConvectiveFluxKW = _maxConvectiveFluxKW;
                _result.DeltaVExpended = _deltaVExpended;
                _result.EndPosition = _referenceFrame.ToAbsolute(_x, _t);
                _result.EndVelocity = _referenceFrame.ToAbsolute(_v, _t);
                _result.Trajectory = _trajectory;
                _result.ParachuteMultiplier = _parachuteSemiDeployMultiplier;
                _result.MultiplierHasError = _multiplierHasError;
                _result.Maxdt = _maxDT;
                _result.Steps = _steps;
            }
            catch (Exception ex)
            {
                _result.Exception = ex;
                _result.Outcome = Outcome.ERROR;
            }
            finally
            {
                if (_trajectory != _result.Trajectory)
                    ListPool<AbsoluteVector>.Instance.Release(_trajectory);
                _vessel.Release();
                _simCurves.Release();
            }

            return _result;
        }

        private bool OrbitReenters(Orbit initialOrbit) => initialOrbit.PeR < _decelRadius || initialOrbit.PeR < _aerobrakedRadius;

        private bool Landed() => _x.magnitude < _probableLandingSiteRadius;

        private bool Aerobraked() => _bodyHasAtmosphere && _x.magnitude > _aerobrakedRadius && Vector3d.Dot(_x, _v) > 0;

        private bool Escaping()
        {
            double escapeVel = Math.Sqrt(2 * _gravParameter / _x.magnitude);
            return _bodyHasAtmosphere && _v.magnitude > escapeVel && Vector3d.Dot(_x, _v) > 0;
        }

        private void AdvanceToFreefallEnd(Orbit initialOrbit)
        {
            _t = FindFreefallEndTime(initialOrbit);

            _x = initialOrbit.WorldBCIPositionAtUT(_t);
            _v = initialOrbit.WorldOrbitalVelocityAtUT(_t);

            if (double.IsNaN(_v.magnitude))
            {

                double mu = initialOrbit.referenceBody.gravParameter;
                double e = -mu / (2 * initialOrbit.semiMajorAxis);
                _v = Math.Sqrt(Math.Abs(2 * (e + mu / _x.magnitude))) * _x.normalized;
                if (initialOrbit.MeanAnomalyAtUT(_t) > Math.PI) _v *= -1;
            }
        }

        private double FindFreefallEndTime(Orbit initialOrbit)
        {
            if (_noSKiptoFreefall || FreefallEnded(initialOrbit, _t))
            {
                return _t;
            }

            double lowerUT = _t;
            double upperUT = initialOrbit.NextPeriapsisTime(_t);

            const double PRECISION = 1.0;
            while (upperUT - lowerUT > PRECISION)
            {
                double testUT = (upperUT + lowerUT) / 2;
                if (FreefallEnded(initialOrbit, testUT)) upperUT = testUT;
                else lowerUT = testUT;
            }

            return (upperUT + lowerUT) / 2;
        }

        private bool FreefallEnded(Orbit initialOrbit, double ut)
        {
            Vector3d pos = initialOrbit.WorldBCIPositionAtUT(ut);
            Vector3d surfaceVelocity = SurfaceVelocity(pos, initialOrbit.WorldOrbitalVelocityAtUT(ut));

            if (pos.magnitude < _aerobrakedRadius) return true;
            if (Vector3d.Dot(surfaceVelocity, initialOrbit.Up(ut)) > 0) return false;
            if (pos.magnitude < _decelRadius) return true;
            if (_descentSpeedPolicy != null && surfaceVelocity.magnitude > _descentSpeedPolicy.MaxAllowedSpeed(pos, surfaceVelocity)) return true;
            return false;
        }

        private void BS34Step()
        {
            bool repeatWithSmallerStep;

            const double TOL = 0.01;
            const double D9 = 1d / 9d;
            const double D24 = 1d / 24d;

            do
            {
                _steps++;
                ActiveStep = _steps;
                ActiveDt = _dt;
                repeatWithSmallerStep = false;
                Vector3d errorv;
                Vector3d dx;
                Vector3d dv;
                {
                    Vector3d dv1 = _dt * TotalAccel(_x, _v, true);
                    Vector3d dx1 = _dt * _v;

                    Vector3d dv2 = _dt * TotalAccel(_x + 0.5 * dx1, _v + 0.5 * dv1);
                    Vector3d dx2 = _dt * (_v + 0.5 * dv1);

                    Vector3d dv3 = _dt * TotalAccel(_x + 0.75 * dx2, _v + 0.75 * dv2);
                    Vector3d dx3 = _dt * (_v + 0.75 * dv2);

                    Vector3d dv4 = _dt * TotalAccel(_x + 2 * D9 * dx1 + 3 * D9 * dx2 + 4 * D9 * dx3, _v + 2 * D9 * dv1 + 3 * D9 * dv2 + 4 * D9 * dv3);

                    dx = (2 * dx1 + 3 * dx2 + 4 * dx3) * D9;
                    dv = (2 * dv1 + 3 * dv2 + 4 * dv3) * D9;

                    Vector3d zv = (7 * dv1 + 6 * dv2 + 8 * dv3 + 3 * dv4) * D24;
                    errorv = zv - dv;
                }


                Vector3 xForChuteSim = _x + dx;
                double altASL = xForChuteSim.magnitude - _bodyRadius;
                double altAGL = altASL - _probableLandingSiteASL;
                double pressure = Pressure(xForChuteSim);

                Vector3d airVel = SurfaceVelocity(xForChuteSim, _v + dv);
                double airDensity = AirDensity(xForChuteSim, altASL);
                double speedOfSound = _mainBody.GetSpeedOfSound(Pressure(xForChuteSim), airDensity);
                double velocity = airVel.magnitude;
                double mach = Math.Min(velocity / speedOfSound, 50f);
                double shockTemp = ShockTemperature(velocity, mach);

                bool willChutesOpen = altASL < _probableLandingSiteASL || _vessel.WillChutesDeploy(altAGL, altASL, _probableLandingSiteASL, pressure,
                    shockTemp, _t, _parachuteSemiDeployMultiplier);

                double nextDT;
                double errorMagnitude = Math.Max(errorv.magnitude, 10e-6);
                if (!willChutesOpen)
                {
                    nextDT = _dt * 0.9 * Math.Pow(TOL / errorMagnitude, 1 / 3d);
                }
                else
                {
                    nextDT = _dt * 0.5;
                }

                if (double.IsNaN(nextDT))
                    nextDT = MinDT;

                nextDT = Math.Max(nextDT, MinDT);
                nextDT = Math.Min(nextDT, 10);

                double sqrStartDist = (_x - _startX).sqrMagnitude;
                if (sqrStartDist < 1000 * 1000)
                    nextDT = Math.Min(nextDT, 0.02);
                else if (sqrStartDist < 5000 * 5000)
                    nextDT = Math.Min(nextDT, 0.5);
                else if (sqrStartDist < 10000 * 10000)
                    nextDT = Math.Min(nextDT, 1);

                if ((errorMagnitude > TOL || willChutesOpen) && _dt > MinDT)
                {
                    _dt = nextDT;
                    repeatWithSmallerStep = true;
                }
                else
                {
                    _maxDragGees = Math.Max(_maxDragGees, _lastRecordedDrag.magnitude / 9.81f);
                    bool parachutesDeploying = _vessel.Simulate(altAGL, altASL, _probableLandingSiteASL, pressure, shockTemp, _t,
                        _parachuteSemiDeployMultiplier);
                    _x += dx;
                    _v += dv;
                    _t += _dt;
                    _dt = parachutesDeploying ? MinDT : nextDT;
                }
            } while (repeatWithSmallerStep);
        }

        private void LimitSpeed()
        {
            if (_descentSpeedPolicy == null) return;

            Vector3d surfaceVel = SurfaceVelocity(_x, _v);
            double maxAllowedSpeed = _descentSpeedPolicy.MaxAllowedSpeed(_x, surfaceVel);
            if (surfaceVel.magnitude > maxAllowedSpeed)
            {
                double dV = Math.Min(surfaceVel.magnitude - maxAllowedSpeed, _dt * _maxThrustAccel);
                surfaceVel -= dV * surfaceVel.normalized;
                _deltaVExpended += dV;
                _v = surfaceVel + Vector3d.Cross(_bodyAngularVelocity, _x);
            }
        }

        private void RecordTrajectory() => _trajectory.Add(_referenceFrame.ToAbsolute(_x, _t));

        private Vector3d TotalAccel(Vector3d pos, Vector3d vel, bool record = false)
        {
            Vector3d airVel = SurfaceVelocity(pos, vel);
            double altitude = pos.magnitude - _bodyRadius;

            double airDensity = AirDensity(pos, altitude);
            double pressure = Pressure(pos);
            double speedOfSound = _mainBody.GetSpeedOfSound(pressure, airDensity);
            float mach = Mathf.Min((float)(airVel.magnitude / speedOfSound), 50f);

            float dynamicPressurekPa = (float)(0.0005 * airDensity * airVel.sqrMagnitude);
            _maxDynamicPressurekPa = Math.Max(_maxDynamicPressurekPa, dynamicPressurekPa);
            _maxConvectiveFluxKW = Math.Max(_maxConvectiveFluxKW, ConvectiveFluxBoundKW(airDensity, airVel.magnitude, mach));

            double pseudoReynolds = airDensity * airVel.magnitude;
            double pseudoReDragMult = _simCurves.DragCurvePseudoReynolds.Evaluate((float)pseudoReynolds);

            if (_once)
            {
                _result.Prediction.FirstDrag = DragForce(pos, vel, dynamicPressurekPa, mach).magnitude / 9.81;
                _result.Prediction.FirstLift = LiftForce(pos, vel, dynamicPressurekPa, mach).magnitude / 9.81;
                _result.Prediction.Mach = mach;
                _result.Prediction.SpeedOfSound = speedOfSound;
                _result.Prediction.DynamicPressurekPa = dynamicPressurekPa;
            }

            Vector3d dragAccel = DragForce(pos, vel, dynamicPressurekPa, mach) * pseudoReDragMult / _vessel.totalMass;

            if (record)
                _lastRecordedDrag = dragAccel;

            Vector3d gravAccel = GravAccel(pos);
            Vector3d liftAccel = LiftForce(pos, vel, dynamicPressurekPa, mach) / _vessel.totalMass;

            Vector3d totalAccel = gravAccel + dragAccel + liftAccel;

            if (_once)
                _once = false;

            return totalAccel;
        }

        private Vector3d GravAccel(Vector3d pos) => -(_gravParameter / pos.sqrMagnitude) * pos.normalized;

        private Vector3d DragForce(Vector3d pos, Vector3d vel, float dynamicPressurekPa, float mach)
        {
            if (!_bodyHasAtmosphere) return Vector3d.zero;

            Vector3d airVel = SurfaceVelocity(pos, vel);

            Vector3d localVel = _attitude * Vector3d.up * airVel.magnitude;

            Vector3d shipDrag = _vessel.Drag(localVel, dynamicPressurekPa, mach);



            return -airVel.normalized * shipDrag.magnitude;
        }

        private Vector3d LiftForce(Vector3d pos, Vector3d vel, float dynamicPressurekPa, float mach)
        {
            if (!_bodyHasAtmosphere) return Vector3d.zero;

            Vector3d airVel = SurfaceVelocity(pos, vel);

            Vector3d localVel = _attitude * Vector3d.up * airVel.magnitude;

            Vector3d localLift = _vessel.Lift(localVel, dynamicPressurekPa, mach);

            QuaternionD vesselToWorld = Quaternion.FromToRotation(localVel, airVel);

            return vesselToWorld * localLift;
        }

        private double Pressure(Vector3d pos)
        {
            double altitude = pos.magnitude - _bodyRadius;
            return StaticPressure(altitude);
        }

        private Vector3d SurfaceVelocity(Vector3d pos, Vector3d vel) =>
            vel - Vector3d.Cross(_bodyAngularVelocity, pos);

        private double AirDensity(Vector3d pos, double altitude)
        {
            double pressure = StaticPressure(altitude);
            double temp = GetTemperature(pos, altitude);

            return FlightGlobals.getAtmDensity(pressure, temp, _mainBody);
        }

        private double StaticPressure(double altitude)
        {
            if (!_mainBody.atmosphere)
                return 0;

            if (altitude >= _mainBody.atmosphereDepth)
                return 0;

            if (_mainBody.atmosphereUsePressureCurve)
            {
                if (_mainBody.atmospherePressureCurveIsNormalized)
                    return Mathf.Lerp(0f, (float)_mainBody.atmospherePressureSeaLevel,
                        _simCurves.AtmospherePressureCurve.Evaluate((float)(altitude / _mainBody.atmosphereDepth)));

                return _simCurves.AtmospherePressureCurve.Evaluate((float)altitude);
            }

            return _mainBody.atmospherePressureSeaLevel *
                Math.Pow(1 - _mainBody.atmosphereTemperatureLapseRate * altitude / _mainBody.atmosphereTemperatureSeaLevel,
                    _mainBody.atmosphereGasMassLapseRate);
        }

        private double GetTemperature(Vector3d position, double altitude)
        {
            if (!_mainBody.atmosphere)
                return PhysicsGlobals.SpaceTemperature;

            if (altitude > _mainBody.atmosphereDepth)
                return PhysicsGlobals.SpaceTemperature;

            Vector3 up = position.normalized;
            float polarAngle = Mathf.Acos(Vector3.Dot(_mainBody.bodyTransform.up, up));
            if (polarAngle > Mathf.PI * 0.5f)
            {
                polarAngle = Mathf.PI - polarAngle;
            }

            float time = (Mathf.PI * 0.5f - polarAngle) * Mathf.Rad2Deg;

            Vector3 sunVector = (FlightGlobals.Bodies[0].position - position + _mainBody.position).normalized;
            Vector3 bodyUp = _mainBody.bodyTransform.up;
            float sunAxialDot = Vector3.Dot(sunVector, bodyUp);
            float bodyPolarAngle = Mathf.Acos(Vector3.Dot(bodyUp, up));
            float sunPolarAngle = Mathf.Acos(sunAxialDot);
            float sunBodyMaxDot = (1.0f + Mathf.Cos(sunPolarAngle - bodyPolarAngle)) * 0.5f;
            float sunBodyMinDot = (1.0f + Mathf.Cos(sunPolarAngle + bodyPolarAngle)) * 0.5f;
            float sunDotCorrected = (1.0f + Vector3.Dot(sunVector,
                Quaternion.AngleAxis(45f * Mathf.Sign((float)_mainBody.rotationPeriod), bodyUp) * up)) * 0.5f;
            float sunDotNormalized = (sunDotCorrected - sunBodyMinDot) / (sunBodyMaxDot - sunBodyMinDot);
            double atmosphereTemperatureOffset = _simCurves.LatitudeTemperatureBiasCurve.Evaluate(time) +
                (double)_simCurves.LatitudeTemperatureSunMultCurve.Evaluate(time) * sunDotNormalized +
                _simCurves.AxialTemperatureSunMultCurve.Evaluate(sunAxialDot);

            double temperature;
            if (!_mainBody.atmosphereUseTemperatureCurve)
            {
                temperature = _mainBody.atmosphereTemperatureSeaLevel - _mainBody.atmosphereTemperatureLapseRate * altitude;
            }
            else
            {
                temperature = !_mainBody.atmosphereTemperatureCurveIsNormalized
                    ? _simCurves.AtmosphereTemperatureCurve.Evaluate((float)altitude)
                    : UtilMath.Lerp(_simCurves.SpaceTemperature, _mainBody.atmosphereTemperatureSeaLevel,
                        _simCurves.AtmosphereTemperatureCurve.Evaluate((float)(altitude / _mainBody.atmosphereDepth)));
            }

            temperature += _simCurves.AtmosphereTemperatureSunMultCurve.Evaluate((float)altitude) * atmosphereTemperatureOffset;

            return temperature;
        }

        // The game's heating constants, as the bound of the flux takes them.
        private static HeatConstants HeatConstantsNow()
        {
            return new HeatConstants
            {
                MachFactor = PhysicsGlobals.MachConvectionFactor,
                MachDensityExponent = PhysicsGlobals.MachConvectionDensityExponent,
                MachVelocityExponent = PhysicsGlobals.MachConvectionVelocityExponent,
                TurbulentStart = PhysicsGlobals.TurbulentConvectionStart,
                TurbulentEnd = PhysicsGlobals.TurbulentConvectionEnd,
                TurbulentMultiplier = PhysicsGlobals.TurbulentConvectionMult,
                LerpStartMach = PhysicsGlobals.NewtonianMachTempLerpStartMach,
                LerpEndMach = PhysicsGlobals.NewtonianMachTempLerpEndMach,
                LerpExponent = PhysicsGlobals.NewtonianMachTempLerpExponent,
                LowMachTemperatureFactor = PhysicsGlobals.NewtonianTemperatureFactor,
                HighMachTemperatureScalar = PhysicsGlobals.MachTemperatureScalar,
                HighMachTemperatureExponent = PhysicsGlobals.MachTemperatureVelocityExponent,
            };
        }

        private double ConvectiveFluxBoundKW(double airDensity, double speed, double mach)
        {
            return HeatBound.FluxKW(airDensity, speed, mach, HighLogic.CurrentGame.Parameters.Difficulty.ReentryHeatScale * _mainBody.shockTemperatureMultiplier, HeatConstantsNow());
        }

        private double ShockTemperature(double velocity, double mach)
        {
            double newtonianTemperatureFactor = velocity * PhysicsGlobals.NewtonianTemperatureFactor;
            double convectiveMachLerp =
                Math.Pow(
                    UtilMath.Clamp01((mach - PhysicsGlobals.NewtonianMachTempLerpStartMach) /
                        (PhysicsGlobals.NewtonianMachTempLerpEndMach - PhysicsGlobals.NewtonianMachTempLerpStartMach)),
                    PhysicsGlobals.NewtonianMachTempLerpExponent);
            if (convectiveMachLerp > 0)
            {
                double machTemperatureScalar =
                    PhysicsGlobals.MachTemperatureScalar * Math.Pow(velocity, PhysicsGlobals.MachTemperatureVelocityExponent);
                newtonianTemperatureFactor = UtilMath.LerpUnclamped(newtonianTemperatureFactor, machTemperatureScalar, convectiveMachLerp);
            }

            return newtonianTemperatureFactor * HighLogic.CurrentGame.Parameters.Difficulty.ReentryHeatScale * _mainBody.shockTemperatureMultiplier;
        }

        private double _nextLog;

        public void Log(Vector3d pos, Vector3d vel)
        {
            if (_t > _nextLog)
            {
                _nextLog = _t + 10;

                double altitude = pos.magnitude - _bodyRadius;
                Vector3d airVel = SurfaceVelocity(pos, vel);

                double atmosphericTemperature = GetTemperature(pos, altitude);

                double speedOfSound = _mainBody.GetSpeedOfSound(Pressure(pos), AirDensity(pos, altitude));
                float mach = Mathf.Min((float)(airVel.magnitude / speedOfSound), 50f);

                float dynamicPressurekPa = (float)(0.0005 * AirDensity(pos, altitude) * airVel.sqrMagnitude);


                _result.DebugLog += "\n "
                    + (_t - _startUT).ToString("F2").PadLeft(8)
                    + " Alt:" + altitude.ToString("F0").PadLeft(6)
                    + " Vel:" + vel.magnitude.ToString("F2").PadLeft(8)
                    + " AirVel:" + airVel.magnitude.ToString("F2").PadLeft(8)
                    + " SoS:" + speedOfSound.ToString("F2").PadLeft(6)
                    + " mach:" + mach.ToString("F2").PadLeft(6)
                    + " dynP:" + dynamicPressurekPa.ToString("F5").PadLeft(9)
                    + " Temp:" + atmosphericTemperature.ToString("F2").PadLeft(8)
                    + " Lat:" + _referenceFrame.Latitude(pos).ToString("F2").PadLeft(6);
            }
        }

        public enum Outcome { LANDED, AEROBRAKED, TIMED_OUT, NO_REENTRY, ERROR }

        public struct Prediction
        {
            public double FirstDrag;
            public double FirstLift;
            public double SpeedOfSound;
            public double Mach;
            public double DynamicPressurekPa;
        }

        public class Result
        {
            public double Maxdt;
            public int Steps;

            public double TimeToComplete;

            public ulong ID;
            public Outcome Outcome;
            public Exception Exception;

            public CelestialBody Body;
            public ReferenceFrame ReferenceFrame;
            public double EndUT;

            public AbsoluteVector StartPosition;
            public AbsoluteVector EndPosition;
            public AbsoluteVector EndVelocity;

            public bool AeroBrake;
            public double AeroBrakeUT;
            public AbsoluteVector AeroBrakePosition;
            public AbsoluteVector AeroBrakeVelocity;

            public double EndASL;
            public List<AbsoluteVector> Trajectory;

            public double MaxDragGees;
            public double MaxDynamicPressurekPa;
            public double MaxConvectiveFluxKW;
            public double DeltaVExpended;

            public bool MultiplierHasError;
            public double ParachuteMultiplier;

            public Orbit InputInitialOrbit;

            public double InputUT;

            public List<SimulatedParachute> InputParachuteList;
            public IDescentSpeedPolicy InputDescentSpeedPolicy;
            public double InputDecelEndAltitudeASL;
            public double InputMaxThrustAccel;
            public double InputParachuteSemiDeployMultiplier;
            public double InputProbableLandingSiteASL;
            public bool InputMultiplierHasError;
            public double InputDT;

            public string DebugLog;

            private static readonly Pool<Result> _pool = new Pool<Result>(Create, Reset);

            private static Result Create() => new Result();

            public void Release()
            {
                if (Trajectory != null)
                    ListPool<AbsoluteVector>.Instance.Release(Trajectory);
                Exception = null;
                _pool.Release(this);
            }

            private static void Reset(Result obj) => obj.AeroBrake = false;

            public static Result Borrow()
            {
                Result result = _pool.Borrow();
                return result;
            }

            public Prediction Prediction;

            public Vector3d RelativeEndPosition() => WorldEndPosition() - Body.position;

            public Vector3d WorldEndPosition() => ReferenceFrame.WorldPositionAtCurrentTime(EndPosition);

            private Vector3d WorldEndVelocity() => ReferenceFrame.WorldVelocityAtCurrentTime(EndVelocity);

            public Orbit EndOrbit() => Compat.OrbitFromStateVectors(WorldEndPosition(), WorldEndVelocity(), Body, EndUT);

            public Vector3d WorldAeroBrakePosition() => ReferenceFrame.WorldPositionAtCurrentTime(AeroBrakePosition);

            public Vector3d WorldAeroBrakeVelocity() => ReferenceFrame.WorldVelocityAtCurrentTime(AeroBrakeVelocity);

            public Orbit AeroBrakeOrbit() => Compat.OrbitFromStateVectors(WorldAeroBrakePosition(), WorldAeroBrakeVelocity(), Body, EndUT);

            public Disposable<List<Vector3d>> WorldTrajectory(double timeStep, bool world = true)
            {
                Disposable<List<Vector3d>> ret = ListPool<Vector3d>.Instance.BorrowDisposable();

                if (Trajectory.Count == 0) return ret;

                ret.value.Add(world
                    ? ReferenceFrame.WorldPositionAtCurrentTime(Trajectory[0])
                    : ReferenceFrame.BodyPositionAtCurrentTime(Trajectory[0]));
                double lastTime = Trajectory[0].UT;
                for (int i = 0; i < Trajectory.Count; i++)
                {
                    AbsoluteVector absolute = Trajectory[i];
                    if (absolute.UT > lastTime + timeStep)
                    {
                        ret.value.Add(
                            world ? ReferenceFrame.WorldPositionAtCurrentTime(absolute) : ReferenceFrame.BodyPositionAtCurrentTime(absolute));
                        lastTime = absolute.UT;
                    }
                }

                return ret;
            }

            public override string ToString()
            {
                string resultText = "Simulation result\n{";

                resultText += "Inputs:\n{";
                if (null != InputInitialOrbit) { resultText += "\n input_initialOrbit: " + InputInitialOrbit; }

                resultText += "\n input_UT: " + InputUT;
                if (null != InputDescentSpeedPolicy) { resultText += "\n input_descentSpeedPolicy: " + InputDescentSpeedPolicy; }

                resultText += "\n input_decelEndAltitudeASL: " + InputDecelEndAltitudeASL;
                resultText += "\n input_maxThrustAccel: " + InputMaxThrustAccel;
                resultText += "\n input_parachuteSemiDeployMultiplier: " + InputParachuteSemiDeployMultiplier;
                resultText += "\n input_probableLandingSiteASL: " + InputProbableLandingSiteASL;
                resultText += "\n input_multiplierHasError: " + InputMultiplierHasError;
                resultText += "\n input_dt: " + InputDT;
                resultText += "\n}";
                resultText += "\nid: " + ID;
                resultText += "\noutcome: " + Outcome;
                resultText += "\nmaxdt: " + Maxdt;
                resultText += "\ntimeToComplete: " + TimeToComplete;
                resultText += "\nendUT: " + EndUT;
                if (null != ReferenceFrame) { resultText += "\nstartPosition: " + ReferenceFrame.WorldPositionAtCurrentTime(StartPosition); }

                if (null != ReferenceFrame) { resultText += "\nendPosition: " + ReferenceFrame.WorldPositionAtCurrentTime(EndPosition); }

                resultText += "\nendASL: " + EndASL;
                resultText += "\nendVelocity: " + EndVelocity.Longitude + "," + EndVelocity.Latitude + "," + EndVelocity.Radius;
                resultText += "\nmaxDragGees: " + MaxDragGees;
                resultText += "\ndeltaVExpended: " + DeltaVExpended;
                resultText += "\nmultiplierHasError: " + MultiplierHasError;
                resultText += "\nparachuteMultiplier: " + ParachuteMultiplier;
                resultText += "\n}";

                return resultText;
            }
        }
    }

    public interface IDescentSpeedPolicy
    {
        double MaxAllowedSpeed(Vector3d pos, Vector3d surfaceVel);
    }


    public struct AbsoluteVector
    {
        public double Latitude;
        public double Longitude;
        public double Radius;
        public double UT;
    }

    public class ReferenceFrame
    {
        private double _epoch;
        private Vector3d _lat0Lon0AtStart;
        private Vector3d _lat0Lon90AtStart;
        private Vector3d _lat90AtStart;
        private CelestialBody _referenceBody;

        public void UpdateAtCurrentTime(CelestialBody body)
        {
            _lat0Lon0AtStart = body.GetSurfaceNVector(0, 0);
            _lat0Lon90AtStart = body.GetSurfaceNVector(0, 90);
            _lat90AtStart = body.GetSurfaceNVector(90, 0);
            _epoch = Planetarium.GetUniversalTime();
            _referenceBody = body;
        }

        public AbsoluteVector ToAbsolute(Vector3d vector3d, double ut)
        {
            var absolute = new AbsoluteVector { Latitude = Latitude(vector3d) };

            double longitude = UtilMath.Rad2Deg * Math.Atan2(Vector3d.Dot(vector3d.normalized, _lat0Lon90AtStart),
                Vector3d.Dot(vector3d.normalized, _lat0Lon0AtStart));
            longitude -= 360 * (ut - _epoch) / _referenceBody.rotationPeriod;
            absolute.Longitude = Compat.Clamp180(longitude);

            absolute.Radius = vector3d.magnitude;

            absolute.UT = ut;

            return absolute;
        }

        public Vector3d WorldPositionAtCurrentTime(AbsoluteVector absolute) => _referenceBody.position + WorldVelocityAtCurrentTime(absolute);

        public Vector3d BodyPositionAtCurrentTime(AbsoluteVector absolute) => _referenceBody.position +
            absolute.Radius * _referenceBody.GetSurfaceNVector(absolute.Latitude,
                absolute.Longitude);

        public Vector3d WorldVelocityAtCurrentTime(AbsoluteVector absolute)
        {
            double now = Planetarium.GetUniversalTime();
            double unrotatedLongitude = Compat.Clamp360(absolute.Longitude - 360 * (now - absolute.UT) / _referenceBody.rotationPeriod);
            return absolute.Radius * _referenceBody.GetSurfaceNVector(absolute.Latitude, unrotatedLongitude);
        }

        public double Latitude(Vector3d vector3d) => UtilMath.Rad2Deg * Math.Asin(Vector3d.Dot(vector3d.normalized, _lat90AtStart));
    }
}
