using System;
using System.Linq.Expressions;
using System.Reflection;
using System.Reflection.Emit;
using ReDefinition.Core;
using UnityEngine;

namespace ReDefinition.Atmospheres
{
    // The bridge to WindAPI by Zondulus (https://github.com/Zondulus/WindAPI, CC0 1.0, `License.txt` of the repository; interface taken from
    // `WindAPI Source.cs` and `README.md` at commit 2dd45c40, version 1.0.0.0). WindAPI multiplexes the wind that FAR applies. The members
    // used, as that source declares them:
    //   interface WindAPI.IWindProvider { Vector3 GetWind(CelestialBody body, Part part, Vector3 position); string ProviderID { get; } }
    //   class WindAPI.WindManager : MonoBehaviour { static WindManager Instance { get; }; void RegisterProvider(IWindProvider);
    //     void DeregisterProvider(IWindProvider); Vector3 GetWindAtLocation(CelestialBody, Part, Vector3 position, IWindProvider ignoreProvider = null) }
    // GetWind returns a world-frame vector, the part may be null (trajectory queries), a provider that throws is disabled for the rest of
    // the flight session, and the manager is a flight-scene object made again for each flight (Instance is null outside the flight). The
    // bridge reaches all of it by reflection only: no reference to WindAPI.dll. If a type or member differs it stays off and says so once.
    // It does two things.
    //  - It reads the sum of the other providers at a place (our own provider ignored) as an IWeatherSource, "WindAPI", so that the wind
    //    that other mods put into FAR shows in the weather seam and in the forecast.
    //  - It registers a provider of our own whose GetWind returns the wind this mod applies to the part's vessel, so that FAR (which
    //    replaces the stock aerodynamics) gets our wind through the one way it has. WindAPI needs FAR: without FAR nothing applies the sum
    //    to a vehicle, and the plan then uses the stock route.
    // DEFERRED, each with its trigger (a copy of the mod is installed, or a fixture can be written from its source): the bridges to FAR's own
    // wind (FARAtmosphere), Kerbal Weather Project and CPWE. Until then those mods are only detected by assembly name (ForeignWindDetector).
    // Experimental: no copy of WindAPI is installed here, so the bridge has not run against the real assembly; the tests drive it with a
    // fixture that declares the members above.
    // What the emitted provider calls. Public because the provider lives in a dynamic assembly that may not reach internal types.
    public static class WindApiCallbacks
    {
        // The wind of our provider for a part: set by the owner of the bridge.
        public static Func<Part, Vector3> OurWind;

        public static Vector3 GetWindCallback(CelestialBody body, Part part, Vector3 position)
        {
            Func<Part, Vector3> f = OurWind;
            if (f == null) return Vector3.zero;
            try
            {
                return f(part);
            }
            catch (Exception)
            {
                return Vector3.zero;
            }
        }
    }

    internal sealed class WindApiBridge
    {
        internal const string ManagerType = "WindAPI.WindManager";
        internal const string ProviderType = "WindAPI.IWindProvider";

        // Reads the wind (world frame) of the other providers at a position.
        private Func<CelestialBody, Part, Vector3, object, Vector3> read;
        private Action<object> register, deregister;
        private Func<object> current;
        private object provider;
        // The manager our provider was registered with: the manager is made again for each flight, and a provider registered with the old
        // one is gone.
        private object registeredWith;
        // Failures of the read in a row; at FailureLimit the bridge disconnects.
        private int failureStreak;
        internal const int FailureLimit = 3;

        // Where the bridge's warnings go (the game log); the tests put a recorder here because the engine's logger needs the engine.
        internal static Action<string, string> Warn = CompatibilityLog.Warn;

        internal bool Connected { get; private set; }
        internal string Status { get; private set; }

        // The source the weather seam and the forecast ask; null until connected.
        internal IWeatherSource Source { get; private set; }

        private readonly string managerTypeName, providerTypeName;

        internal WindApiBridge() : this(ManagerType, ProviderType)
        {
        }

        // The type names are the ones WindAPI ships; the tests name a differently shaped fixture to see the bridge stay off.
        internal WindApiBridge(string managerTypeName, string providerTypeName)
        {
            this.managerTypeName = managerTypeName;
            this.providerTypeName = providerTypeName;
            Status = "not tried";
        }

        // Finds the types and builds the calls. The assembly search is by type name.
        internal bool Connect()
        {
            if (Connected) return true;
            try
            {
                Type manager = Find(managerTypeName), providerInterface = Find(providerTypeName);
                if (manager == null || providerInterface == null)
                {
                    Status = "WindAPI is not loaded";
                    return false;
                }
                // Every member by its exact signature: a WindAPI whose shape differs leaves the bridge off and says what is missing.
                PropertyInfo instance = manager.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static);
                MethodInfo get = manager.GetMethod("GetWindAtLocation", new[] { typeof(CelestialBody), typeof(Part), typeof(Vector3), providerInterface });
                MethodInfo reg = manager.GetMethod("RegisterProvider", new[] { providerInterface });
                MethodInfo dereg = manager.GetMethod("DeregisterProvider", new[] { providerInterface });
                PropertyInfo id = providerInterface.GetProperty("ProviderID");
                MethodInfo getWind = providerInterface.GetMethod("GetWind", new[] { typeof(CelestialBody), typeof(Part), typeof(Vector3) });
                if (instance == null || instance.PropertyType != manager || get == null || get.ReturnType != typeof(Vector3) || reg == null || dereg == null
                    || id == null || id.PropertyType != typeof(string) || id.GetGetMethod() == null || getWind == null || getWind.ReturnType != typeof(Vector3))
                {
                    Status = "WindAPI's API differs from the one this bridge knows (missing or different: "
                             + (instance == null || instance.PropertyType != manager ? "WindManager.Instance " : "")
                             + (get == null || get.ReturnType != typeof(Vector3) ? "GetWindAtLocation " : "")
                             + (reg == null ? "RegisterProvider " : "") + (dereg == null ? "DeregisterProvider " : "")
                             + (id == null || id.PropertyType != typeof(string) ? "ProviderID " : "")
                             + (getWind == null || getWind.ReturnType != typeof(Vector3) ? "GetWind" : "") + ")";
                    return false;
                }
                ParameterExpression b = Expression.Parameter(typeof(CelestialBody), "body");
                ParameterExpression p = Expression.Parameter(typeof(Part), "part");
                ParameterExpression pos = Expression.Parameter(typeof(Vector3), "position");
                ParameterExpression ignore = Expression.Parameter(typeof(object), "ignore");
                MemberExpression inst = Expression.Property(null, instance);
                read = Expression.Lambda<Func<CelestialBody, Part, Vector3, object, Vector3>>(
                    Expression.Condition(Expression.Equal(inst, Expression.Constant(null, manager)),
                        Expression.Constant(Vector3.zero),
                        Expression.Call(inst, get, b, p, pos, Expression.Convert(ignore, providerInterface))),
                    b, p, pos, ignore).Compile();
                current = Expression.Lambda<Func<object>>(Expression.Convert(inst, typeof(object))).Compile();
                ParameterExpression arg = Expression.Parameter(typeof(object), "provider");
                register = Expression.Lambda<Action<object>>(
                    Expression.IfThen(Expression.NotEqual(inst, Expression.Constant(null, manager)),
                        Expression.Call(inst, reg, Expression.Convert(arg, providerInterface))), arg).Compile();
                deregister = Expression.Lambda<Action<object>>(
                    Expression.IfThen(Expression.NotEqual(inst, Expression.Constant(null, manager)),
                        Expression.Call(inst, dereg, Expression.Convert(arg, providerInterface))), arg).Compile();
                provider = Activator.CreateInstance(EmitProvider(providerInterface));
                Source = new OthersSource(this);
                Connected = true;
                Status = "connected";
                return true;
            }
            catch (Exception e)
            {
                Status = "the bridge failed (" + CompatibilityLog.Reason(e) + ")";
                return false;
            }
        }

        // True while a manager exists that our provider is not registered with (the manager is made in the flight scene, once per flight).
        internal bool NeedsRegistration
        {
            get
            {
                if (!Connected) return false;
                object manager = Safe(current);
                return manager != null && !ReferenceEquals(manager, registeredWith);
            }
        }

        // Registers our provider with WindAPI's manager; false while no manager exists (outside the flight scene).
        internal bool Register()
        {
            if (!Connected) return false;
            object manager = Safe(current);
            if (manager == null) return false;
            if (ReferenceEquals(manager, registeredWith)) return true;
            try
            {
                register(provider);
                registeredWith = manager;
                return true;
            }
            catch (Exception e)
            {
                Fail("registering the provider", e);
                return false;
            }
        }

        internal void Deregister()
        {
            if (!Connected || registeredWith == null) return;
            try
            {
                deregister(provider);
            }
            catch (Exception e)
            {
                Warn("wind-windapi", "Atmospheres: the WindAPI provider could not be removed (" + CompatibilityLog.Reason(e) + ")");
            }
            registeredWith = null;
        }

        // The wind of the other providers at a world position, our own provider left out. A call that throws counts as a failure, is logged
        // once and gives zero; after FailureLimit in a row the bridge disconnects (the plan then drops the FAR route and says so).
        internal Vector3 OthersAt(CelestialBody body, Vector3 worldPosition)
        {
            if (!Connected) return Vector3.zero;
            try
            {
                Vector3 w = read(body, null, worldPosition, provider);
                failureStreak = 0;
                return w;
            }
            catch (Exception e)
            {
                Fail("reading the other providers' wind", e);
                return Vector3.zero;
            }
        }

        private static object Safe(Func<object> f)
        {
            try
            {
                return f();
            }
            catch (Exception)
            {
                return null;
            }
        }

        private void Fail(string what, Exception e)
        {
            string reason = CompatibilityLog.Reason(e);
            Warn("wind-windapi", "Atmospheres: WindAPI failed while " + what + " (" + reason + ")");
            if (++failureStreak < FailureLimit) return;
            Connected = false;
            Status = "switched off after " + FailureLimit + " failures in a row (" + reason + ")";
            Warn("wind-windapi-off", "Atmospheres: the WindAPI bridge is " + Status);
        }

        private static Type Find(string name)
        {
            foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    Type t = a.GetType(name, false);
                    if (t != null) return t;
                }
                catch (Exception)
                {
                    // An assembly that cannot answer is skipped.
                }
            }
            return null;
        }

        // A class that implements IWindProvider and answers from GetWindCallback. Emitted because the interface belongs to an assembly this one
        // does not reference.
        private static Type EmitProvider(Type providerInterface)
        {
            AssemblyBuilder asm = AppDomain.CurrentDomain.DefineDynamicAssembly(new AssemblyName("ReDefinition.WindApiProvider"), AssemblyBuilderAccess.Run);
            ModuleBuilder module = asm.DefineDynamicModule("main");
            TypeBuilder type = module.DefineType("ReDefinitionWindProvider", TypeAttributes.Public | TypeAttributes.Sealed, typeof(object), new[] { providerInterface });
            type.DefineDefaultConstructor(MethodAttributes.Public);

            MethodInfo idGetter = providerInterface.GetProperty("ProviderID").GetGetMethod();
            MethodBuilder id = type.DefineMethod(idGetter.Name, MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.Final | MethodAttributes.HideBySig | MethodAttributes.NewSlot | MethodAttributes.SpecialName, typeof(string), Type.EmptyTypes);
            ILGenerator il = id.GetILGenerator();
            il.Emit(OpCodes.Ldstr, "ReDefinition");
            il.Emit(OpCodes.Ret);
            type.DefineMethodOverride(id, idGetter);

            MethodInfo getWind = providerInterface.GetMethod("GetWind");
            MethodBuilder wind = type.DefineMethod("GetWind", MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.Final | MethodAttributes.HideBySig | MethodAttributes.NewSlot, typeof(Vector3), new[] { typeof(CelestialBody), typeof(Part), typeof(Vector3) });
            il = wind.GetILGenerator();
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Ldarg_2);
            il.Emit(OpCodes.Ldarg_3);
            il.Emit(OpCodes.Call, typeof(WindApiCallbacks).GetMethod("GetWindCallback", BindingFlags.Public | BindingFlags.Static));
            il.Emit(OpCodes.Ret);
            type.DefineMethodOverride(wind, getWind);
            return type.CreateType();
        }

        // The other providers' wind as a source of the weather seam.
        private sealed class OthersSource : WeatherSourceBase
        {
            private readonly WindApiBridge bridge;

            internal OthersSource(WindApiBridge bridge)
            {
                this.bridge = bridge;
            }

            public override string Name { get { return "WindAPI"; } }

            // Above the field (priority 0): the wind that other mods apply is the base the field's is added to.
            public override int Priority { get { return 50; } }

            public override bool TrySample(int bodyIndex, double latDeg, double lonDeg, double altM, double ut, out WeatherSample sample)
            {
                sample = WeatherSample.Clear(bodyIndex);
                if (!GameGlobals.Present || GameGlobals.Bodies == null || bodyIndex < 0 || bodyIndex >= GameGlobals.Bodies.Count) return false;
                CelestialBody body = GameGlobals.Bodies[bodyIndex];
                Vector3 pos = body.GetWorldSurfacePosition(latDeg, lonDeg, altM);
                Vector3 world = bridge.OthersAt(body, pos);
                if (world.sqrMagnitude < 1e-6f) return false;
                Vec3 e, n, up;
                WindService.Frame(body, latDeg, lonDeg, out e, out n, out up);
                var w = new Vec3(world.x, world.y, world.z);
                sample.WindWorld = new Vector3((float)Vec3.Dot(w, e), (float)Vec3.Dot(w, n), (float)Vec3.Dot(w, up));
                return true;
            }
        }
    }
}
