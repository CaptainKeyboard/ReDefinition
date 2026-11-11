using System;
using System.Linq.Expressions;
using System.Reflection;
using UnityEngine;

namespace ReDefinition.Atmospheres
{
    // H1: the wind goes into the aerodynamics through a ModularFlightIntegrator override of UpdateAerodynamics(Part). For the duration of
    // the base call the part's rigidbody velocity is lowered by the wind, so that the stock aerodynamics (or the replacement that
    // KSPCommunityFixes installs) compute dragVector, dragVectorMag, dragVectorDir, dragScalar, the dynamic pressure and the drag cube
    // direction against the moving air in one consistent set; then the velocity is put back. Measured in K-pre run 5 (premise P4): a
    // 20 m/s wind gave 594.1 m of canopy drift against 594.35 m predicted. The Krakensbane frame velocity is not touched, nor is the
    // vessel's srf_velocity, so autopilots and the predictors stay unaware of the wind.
    //
    // ModularFlightIntegrator is reached by reflection (MIT, sarbian, GameData/ModularFlightIntegrator/LICENSE.txt): RegisterUpdateAerodynamicsOverride
    // and BaseFIUpdateAerodynamics. The slot holds one function; the registration returns false when another mod holds it.
    internal static class WindHook
    {
        internal const string IntegratorType = "ModularFI.ModularFlightIntegrator";

        internal static bool Registered;
        internal static string Status = "not tried";
        internal static int Calls, Shifted, Failures;
        // After this many failures in a row the hook stops shifting velocities (it still calls the base).
        internal const int FailureLimit = 5;
        private static int failureStreak;
        internal static bool Disabled;

        private static Action<object, Part> baseUpdate;

        // Whether ModularFlightIntegrator is loaded (the window's injection text asks this).
        internal static bool IntegratorPresent
        {
            get
            {
                // The search walks every assembly; a found type is kept and a miss is repeated every few seconds only (a mod does not load
                // in the middle of a flight, but the first ticks come before every assembly is there).
                long now = System.Diagnostics.Stopwatch.GetTimestamp();
                if (integratorType != null) return true;
                if (searched && now - lastSearch < System.Diagnostics.Stopwatch.Frequency * 5) return false;
                searched = true;
                lastSearch = now;
                integratorType = FindIntegrator();
                return integratorType != null;
            }
        }

        private static Type integratorType;
        private static bool searched;
        private static long lastSearch;

        private static Type FindIntegrator()
        {
            foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type t;
                try
                {
                    t = a.GetType(IntegratorType, false);
                }
                catch (Exception)
                {
                    continue;
                }
                if (t != null) return t;
            }
            return null;
        }

        // Registers the override once. False where the integrator is absent, differs, or its slot is taken.
        internal static bool Register()
        {
            if (Registered) return true;
            try
            {
                Type integrator = FindIntegrator();
                if (integrator == null)
                {
                    Status = "ModularFlightIntegrator is not loaded";
                    return false;
                }
                Type delegateType = integrator.GetNestedType("voidPartDelegate", BindingFlags.Public | BindingFlags.NonPublic);
                MethodInfo register = integrator.GetMethod("RegisterUpdateAerodynamicsOverride", BindingFlags.Public | BindingFlags.Static);
                MethodInfo baseCall = integrator.GetMethod("BaseFIUpdateAerodynamics", BindingFlags.Public | BindingFlags.Instance);
                if (delegateType == null || register == null || baseCall == null)
                {
                    Status = "the integrator's API differs: delegate " + (delegateType != null) + ", register " + (register != null) + ", base " + (baseCall != null);
                    return false;
                }
                // A compiled call of the base method: no array and no boxing per part and tick.
                ParameterExpression self = Expression.Parameter(typeof(object), "integrator");
                ParameterExpression part = Expression.Parameter(typeof(Part), "part");
                baseUpdate = Expression.Lambda<Action<object, Part>>(Expression.Call(Expression.Convert(self, integrator), baseCall, part), self, part).Compile();
                MethodInfo hook = typeof(WindHook).GetMethod("UpdateAerodynamics", BindingFlags.Static | BindingFlags.NonPublic);
                Delegate function = Delegate.CreateDelegate(delegateType, hook);
                object result = register.Invoke(null, new object[] { function });
                Registered = result is bool && (bool)result;
                Status = Registered ? "registered" : "the slot is taken by another mod";
                return Registered;
            }
            catch (Exception e)
            {
                Status = "registration failed: " + e.GetType().Name + " " + e.Message;
                return false;
            }
        }

        // The override: shift the velocity, run the base, put the velocity back.
        private static void UpdateAerodynamics(object integrator, Part part)
        {
            Calls++;
            Rigidbody body = null;
            Vector3 velocity = Vector3.zero;
            bool shifted = false;
            VesselWindState state = null;
            Vessel vessel = null;
            try
            {
                vessel = part != null ? part.vessel : null;
                if (!Disabled && vessel != null && WindService.Plan.Route == WindRoute.Aero && WindService.Eligible(vessel))
                {
                    state = WindService.StateOf(vessel);
                    Vec3 w = WindService.WindFor(vessel);
                    // A part in the water is left alone: the shifted velocity would also make a current of the same size in the water's
                    // dynamic pressure, which the wind in the air must not.
                    if (w.Length > 1e-3 && !(part.submergedPortion > 0.0))
                    {
                        body = part.Rigidbody;
                        if (body != null)
                        {
                            velocity = body.velocity;
                            body.velocity = velocity - new Vector3((float)w.X, (float)w.Y, (float)w.Z);
                            shifted = true;
                            Shifted++;
                        }
                    }
                }
                failureStreak = 0;
            }
            catch (Exception e)
            {
                shifted = false;
                Failures++;
                if (++failureStreak >= FailureLimit && !Disabled)
                {
                    Disabled = true;
                    Status = "switched off after " + FailureLimit + " failures in a row (" + e.GetType().Name + ")";
                    AtmospheresLog.WarnOnce("wind hook " + Status);
                }
            }
            try
            {
                baseUpdate(integrator, part);
            }
            finally
            {
                if (shifted) body.velocity = velocity;
            }
            if (state != null && part != null) state.AddPart(part.dragScalar, part.dynamicPressurekPa);
        }
    }
}
