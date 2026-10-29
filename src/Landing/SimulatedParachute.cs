// Adapted from MechJeb 2 (MechJeb2/FlyingSim/SimulatedParachute.cs), GPL-3.0: the simulation of a descent to the ground, with its drag, parachutes and the ground's height.
// The names and the numbers are MechJeb's; what differs is the namespace and the reading of the vessel's state, which comes from KSP here.
using System;
using UnityEngine;

namespace ReDefinition.Landing
{
    public class SimulatedParachute : SimulatedPart
    {
        private ModuleParachute para;

        private ModuleParachute.deploymentStates state;

        private double openningTime;

        private float deployLevel;
        public bool deploying;

        private bool willDeploy;

        private static readonly Pool<SimulatedParachute> pool = new Pool<SimulatedParachute>(Create, Reset);

        public new static int PoolSize => pool.Size;

        private static SimulatedParachute Create() => new SimulatedParachute();

        public override void Release()
        {
            foreach (DragCube cube in cubes.Cubes)
            {
                DragCubePool.Instance.Release(cube);
            }

            pool.Release(this);
        }

        private static void Reset(SimulatedParachute obj)
        {
        }

        public static SimulatedParachute Borrow(ModuleParachute mp, ReentrySimulation.SimCurves simCurve, double startTime, int limitChutesStage)
        {
            SimulatedParachute part = pool.Borrow();
            part.Init(mp.part, simCurve);
            part.Init(mp, startTime, limitChutesStage);
            return part;
        }

        private void Init(ModuleParachute mp, double startTime, int limitChutesStage)
        {
            para = mp;
            state = mp.deploymentState;

            willDeploy = limitChutesStage != -1 && para.part.inverseStage >= limitChutesStage;


            double timeSinceDeployment = 0;

            switch (mp.deploymentState)
            {
                case ModuleParachute.deploymentStates.SEMIDEPLOYED:
                    if (mp.Anim.isPlaying)
                        timeSinceDeployment = mp.Anim[mp.semiDeployedAnimation].time;
                    else
                        timeSinceDeployment = 10000000;
                    break;

                case ModuleParachute.deploymentStates.DEPLOYED:
                    if (mp.Anim.isPlaying)
                        timeSinceDeployment = mp.Anim[mp.fullyDeployedAnimation].time;
                    else
                        timeSinceDeployment = 10000000;
                    break;

                case ModuleParachute.deploymentStates.STOWED:
                case ModuleParachute.deploymentStates.ACTIVE:
                    timeSinceDeployment = 10000000;
                    break;

                default:
                    timeSinceDeployment = 10000000;
                    break;
            }

            openningTime = startTime - timeSinceDeployment;

        }

        public override Vector3d Drag(Vector3d vesselVelocity, double dragFactor, float mach)
        {
            if (state != ModuleParachute.deploymentStates.SEMIDEPLOYED && state != ModuleParachute.deploymentStates.DEPLOYED)
                return base.Drag(vesselVelocity, dragFactor, mach);

            return Vector3d.zero;
        }

        public override bool SimulateAndRollback(double altATGL, double altASL, double endASL, double pressure, double shockTemp, double time,
            double semiDeployMultiplier)
        {
            if (!willDeploy)
                return false;

            bool stateChanged = false;
            switch (state)
            {
                case ModuleParachute.deploymentStates.STOWED:
                    if (altATGL < semiDeployMultiplier * para.deployAltitude && shockTemp * para.machHeatMult < para.chuteMaxTemp * para.safeMult)
                    {
                        stateChanged = true;
                    }

                    break;
                case ModuleParachute.deploymentStates.ACTIVE:
                    if (pressure >= para.minAirPressureToOpen)
                    {
                        stateChanged = true;
                    }

                    break;
                case ModuleParachute.deploymentStates.SEMIDEPLOYED:
                    if (altATGL < para.deployAltitude)
                    {
                        stateChanged = true;
                    }

                    break;
            }

            return stateChanged;
        }

        public override bool Simulate(double altATGL, double altASL, double endASL, double pressure, double shockTemp, double time,
            double semiDeployMultiplier)
        {
            if (!willDeploy)
                return false;
            switch (state)
            {
                case ModuleParachute.deploymentStates.STOWED:
                    if (altATGL < semiDeployMultiplier * para.deployAltitude && shockTemp * para.machHeatMult < para.chuteMaxTemp * para.safeMult)
                    {
                        state = ModuleParachute.deploymentStates.ACTIVE;
                        if (pressure >= para.minAirPressureToOpen)
                        {
                            state = ModuleParachute.deploymentStates.SEMIDEPLOYED;
                            openningTime = time;
                        }
                    }

                    break;
                case ModuleParachute.deploymentStates.ACTIVE:
                    if (pressure >= para.minAirPressureToOpen)
                    {
                        state = ModuleParachute.deploymentStates.SEMIDEPLOYED;
                        openningTime = time;
                    }

                    break;
                case ModuleParachute.deploymentStates.SEMIDEPLOYED:
                    if (altATGL < para.deployAltitude)
                    {
                        state = ModuleParachute.deploymentStates.DEPLOYED;
                        openningTime = time;
                    }

                    break;
            }

            float normalizedTime;
            if (state == ModuleParachute.deploymentStates.SEMIDEPLOYED)
            {
                normalizedTime = (float)Math.Min((time - openningTime) / para.semiDeploymentSpeed, 1);
            }
            else if (state == ModuleParachute.deploymentStates.DEPLOYED)
            {
                normalizedTime = (float)Math.Min((time - openningTime) / para.deploymentSpeed, 1);
            }
            else
            {
                normalizedTime = 1;
            }

            if (normalizedTime < 1)
            {
                deploying = true;
            }
            else
            {
                deploying = false;
            }

            if (deploying && (state == ModuleParachute.deploymentStates.SEMIDEPLOYED || state == ModuleParachute.deploymentStates.DEPLOYED))
            {
                deployLevel = Mathf.Pow(normalizedTime, para.deploymentCurve);
            }
            else
            {
                deployLevel = 1;
            }

            switch (state)
            {
                case ModuleParachute.deploymentStates.STOWED:
                case ModuleParachute.deploymentStates.ACTIVE:
                case ModuleParachute.deploymentStates.CUT:
                    SetCubeWeight("PACKED", 1f);
                    SetCubeWeight("SEMIDEPLOYED", 0f);
                    SetCubeWeight("DEPLOYED", 0f);
                    break;

                case ModuleParachute.deploymentStates.SEMIDEPLOYED:
                    SetCubeWeight("PACKED", 1f - deployLevel);
                    SetCubeWeight("SEMIDEPLOYED", deployLevel);
                    SetCubeWeight("DEPLOYED", 0f);
                    break;

                case ModuleParachute.deploymentStates.DEPLOYED:
                    SetCubeWeight("PACKED", 0f);
                    SetCubeWeight("SEMIDEPLOYED", 1f - deployLevel);
                    SetCubeWeight("DEPLOYED", deployLevel);
                    break;
            }

            return deploying;
        }
    }
}
