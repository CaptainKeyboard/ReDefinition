using System;
using System.Linq.Expressions;
using System.Reflection;
using System.Reflection.Emit;
using ReDefinition.Core;
using UnityEngine;

namespace ReDefinition.Atmospheres
{
    // The bridge to WindAPI (Zondulus, CC0 1.0; https://github.com/Zondulus/WindAPI). WindAPI multiplexes the wind that FAR applies: a mod
    // implements WindAPI.IWindProvider (ProviderID, GetWind(CelestialBody, Part, Vector3) returning a world-frame vector) and registers it
    // with WindAPI.WindManager.Instance.RegisterProvider; WindManager.Instance.GetWindAtLocation(body, part, position, ignoreProvider) sums
    // the providers. The bridge reaches both by reflection only: no reference to WindAPI.dll. It does two things.
    //  - It reads the sum of the other providers at a place (our own provider ignored) as an IWeatherSource, "WindAPI", so that the wind
    //    that other mods put into FAR shows in the weather seam and in the forecast.
    //  - It registers a provider of our own whose GetWind returns the wind this mod applies to the part's vessel, so that FAR (which
    //    replaces the stock aerodynamics) gets our wind through the one way it has. WindAPI needs FAR: without FAR nothing applies the sum
    //    to a vehicle, and the plan then uses the stock route.
    // Experimental: no copy of WindAPI is installed; the shape is the one in its README and source, and the tests drive it with a fixture of
    // the same shape. If a type or member is not found the bridge stays off and says so once.
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
        private object provider;

        internal bool Connected { get; private set; }
        internal string Status { get; private set; }

        // The source the weather seam and the forecast ask; null until connected.
        internal IWeatherSource Source { get; private set; }

        internal WindApiBridge()
        {
            Status = "not tried";
        }

        // Finds the types and builds the calls. The assembly search is by type name.
        internal bool Connect()
        {
            if (Connected) return true;
            try
            {
                Type manager = Find(ManagerType), providerInterface = Find(ProviderType);
                if (manager == null || providerInterface == null)
                {
                    Status = "WindAPI is not loaded";
                    return false;
                }
                PropertyInfo instance = manager.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static);
                MethodInfo get = manager.GetMethod("GetWindAtLocation");
                MethodInfo reg = manager.GetMethod("RegisterProvider");
                MethodInfo dereg = manager.GetMethod("DeregisterProvider");
                if (instance == null || get == null || reg == null || dereg == null)
                {
                    Status = "WindAPI's API differs from the one this bridge knows";
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

        // Registers our provider with WindAPI's manager (once the manager exists: it is made in the flight scene).
        internal void Register()
        {
            if (Connected) register(provider);
        }

        internal void Deregister()
        {
            if (Connected) deregister(provider);
        }

        // The wind of the other providers at a world position, our own provider left out.
        internal Vector3 OthersAt(CelestialBody body, Vector3 worldPosition)
        {
            return Connected ? read(body, null, worldPosition, provider) : Vector3.zero;
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
