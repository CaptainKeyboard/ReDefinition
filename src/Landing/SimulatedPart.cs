// Adapted from MechJeb 2 (MechJeb2/FlyingSim/SimulatedPart.cs), GPL-3.0: the simulation of a descent to the ground, with its drag, parachutes and the ground's height.
// The names and the numbers are MechJeb's; what differs is the namespace and the reading of the vessel's state, which comes from KSP here.
using System.Collections.Generic;
using UnityEngine;

namespace ReDefinition.Landing
{
    public class SimulatedPart
    {
        protected readonly DragCubeList cubes = new DragCubeList();

        public double totalMass;
        public bool shieldedFromAirstream;
        public bool noDrag;
        public bool hasLiftModule;
        private double bodyLiftMultiplier;

        private ReentrySimulation.SimCurves simCurves;

        private QuaternionD vesselToPart;
        private QuaternionD partToVessel;

        private static readonly Pool<SimulatedPart> pool = new Pool<SimulatedPart>(Create, Reset);

        public static int PoolSize => pool.Size;

        private static SimulatedPart Create()
        {
            var part = new SimulatedPart();
            part.cubes.BodyLiftCurve = new PhysicsGlobals.LiftingSurfaceCurve();
            part.cubes.SurfaceCurves = new PhysicsGlobals.SurfaceCurvesList();
            return part;
        }

        public virtual void Release()
        {
            foreach (DragCube cube in cubes.Cubes)
            {
                DragCubePool.Instance.Release(cube);
            }

            pool.Release(this);
        }

        public static void Release(List<SimulatedPart> objList)
        {
            for (int i = 0; i < objList.Count; ++i)
            {
                objList[i].Release();
            }
        }

        private static void Reset(SimulatedPart obj)
        {
        }

        public static SimulatedPart Borrow(Part p, ReentrySimulation.SimCurves simCurve)
        {
            SimulatedPart part = pool.Borrow();
            part.Init(p, simCurve);
            return part;
        }

        protected void Init(Part p, ReentrySimulation.SimCurves _simCurves)
        {
            Rigidbody rigidbody = p.rb;

            totalMass = rigidbody == null ? 0 : rigidbody.mass;
            shieldedFromAirstream = p.ShieldedFromAirstream;

            noDrag = rigidbody == null && !PhysicsGlobals.ApplyDragToNonPhysicsParts;
            hasLiftModule = p.hasLiftModule;
            bodyLiftMultiplier = p.bodyLiftMultiplier * PhysicsGlobals.BodyLiftMultiplier;

            simCurves = _simCurves;

            CopyDragCubesList(p.DragCubes, cubes);
            cubes.ForceUpdate(true, true);

            partToVessel = Quaternion.LookRotation(p.vessel.GetTransform().InverseTransformDirection(p.transform.forward),
                p.vessel.GetTransform().InverseTransformDirection(p.transform.up));
            vesselToPart = Quaternion.Inverse(partToVessel);




        }

        public virtual Vector3d Drag(Vector3d vesselVelocity, double dragFactor, float mach)
        {
            if (shieldedFromAirstream || noDrag)
                return Vector3d.zero;

            Vector3d dragVectorDirLocal = -(vesselToPart * vesselVelocity).normalized;

            cubes.SetDrag(dragVectorDirLocal, mach);

            Vector3d drag = -vesselVelocity.normalized * cubes.AreaDrag * dragFactor;


            return drag;
        }

        public virtual Vector3d Lift(Vector3d vesselVelocity, double liftFactor)
        {
            if (shieldedFromAirstream || hasLiftModule)
                return Vector3d.zero;

            Vector3d liftV = partToVessel * ((Vector3d)cubes.LiftForce * bodyLiftMultiplier * liftFactor);

            Vector3d liftVector = liftV.ProjectOnPlane(vesselVelocity);


            return liftVector;
        }

        public virtual bool SimulateAndRollback(double altATGL, double altASL, double endASL, double pressure, double shockTemp, double time,
            double semiDeployMultiplier) =>
            false;

        public virtual bool Simulate(double altATGL, double altASL, double endASL, double pressure, double shockTemp, double time,
            double semiDeployMultiplier) =>
            false;

        public static class DragCubePool
        {
            public static Pool<DragCube> Instance { get; } = new Pool<DragCube>(
                () => new DragCube(), cube => { });
        }

        protected void CopyDragCubesList(DragCubeList source, DragCubeList dest)
        {
            dest.ClearCubes();

            dest.SetPart(source.Part);

            dest.None = source.None;

            dest.Procedural = false;

            for (int i = 0; i < source.Cubes.Count; i++)
            {
                DragCube c = DragCubePool.Instance.Borrow();
                CopyDragCube(source.Cubes[i], c);
                dest.Cubes.Add(c);
            }

            dest.SetDragWeights();

            for (int i = 0; i < 6; i++)
            {
                dest.WeightedArea[i] = source.WeightedArea[i];
                dest.WeightedDrag[i] = source.WeightedDrag[i];
                dest.AreaOccluded[i] = source.AreaOccluded[i];
                dest.WeightedDepth[i] = source.WeightedDepth[i];
            }

            dest.SetDragWeights();

            simCurves.CopyTo(dest);
        }

        protected static void CopyDragCube(DragCube source, DragCube dest)
        {
            dest.Name = source.Name;
            dest.Weight = source.Weight;
            dest.Center = source.Center;
            dest.Size = source.Size;
            for (int i = 0; i < source.Drag.Length; i++)
            {
                dest.Drag[i] = source.Drag[i];
                dest.Area[i] = source.Area[i];
                dest.Depth[i] = source.Depth[i];
                dest.DragModifiers[i] = source.DragModifiers[i];
            }
        }

        protected void SetCubeWeight(string name, float newWeight)
        {
            int count = cubes.Cubes.Count;
            if (count == 0)
            {
                return;
            }

            bool noChange = true;
            for (int i = count - 1; i >= 0; i--)
            {
                if (cubes.Cubes[i].Name == name && cubes.Cubes[i].Weight != newWeight)
                {
                    cubes.Cubes[i].Weight = newWeight;
                    noChange = false;
                }
            }

            if (noChange)
                return;

            cubes.SetDragWeights();
        }
    }
}
