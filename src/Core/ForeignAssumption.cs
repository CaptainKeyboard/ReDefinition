using System;
using System.Collections.Generic;
using System.Reflection;

namespace ReDefinition.Core
{
    // What became of an assumption about another mod's code, checked against the
    // build installed.
    internal enum ForeignState
    {
        // The mod is not installed: nothing to hold.
        Absent,
        // Everything it names is there, and the methods it rests on are the code
        // it was checked against.
        Verified,
        // Everything it names is there, but the code is not the one it was checked
        // against: it may hold or not.
        Changed,
        // Something it names is missing: it cannot hold.
        Broken,
    }

    // What ReDefinition does where an assumption's code has changed.
    internal enum WhenChanged
    {
        // Goes on: what is named is checked, and a mistake would cost little.
        Keep,
        // Takes the way that does not rest on it: what rests on it could go
        // wrong unseen.
        Fallback,
    }

    // One thing ReDefinition takes for granted about another mod's code -- what it
    // names and what it reads out of it -- and how far that is checked:
    //
    // - Types, methods and fields by name, with the parameter types the code that
    //   calls them passes: missing, the assumption is Broken.
    // - String literals the mod's code must still load -- shader property and
    //   global names it sets, which ReDefinition reads or sets as well: missing, it
    //   is Broken, for a renamed one would read or set nothing, unseen.
    // - The methods whose behaviour it rests on beyond their names, from their
    //   source or decompiled: fingerprinted (ForeignCode), and compared with the
    //   fingerprints it was checked against. Another one is Changed, and
    //   WhenChanged decides. Where only a version can tell -- what a mod's shaders
    //   do --, the versions it was checked against.
    //
    // Evaluated once, on first use, from the loaded assemblies -- KSP loads every
    // plugin before the first addon starts. Free of Unity: the catalogue
    // (ForeignAssumptions) is checked outside the game too.
    internal sealed class ForeignAssumption
    {
        public readonly string Id;
        public readonly string Mod;
        // What ReDefinition does with it, in the words of the report.
        public readonly string Uses;

        public WhenChanged Policy { get; private set; }
        // What happens where it does not hold.
        public string Fallback { get; private set; }

        private readonly List<string> types = new List<string>();
        private readonly List<Func<MethodBase>> methods = new List<Func<MethodBase>>();
        private readonly List<string> methodNames = new List<string>();
        private readonly List<bool> fingerprinted = new List<bool>();
        private readonly List<string[]> fields = new List<string[]>();
        private readonly List<string[]> literals = new List<string[]>();
        private readonly List<string> testedFingerprints = new List<string>();
        private readonly List<string> testedVersions = new List<string>();

        private bool evaluated;
        private ForeignState state;
        private string problem;
        private string fingerprint;
        private string version;

        public ForeignAssumption(string id, string mod, string uses)
        {
            Id = id;
            Mod = mod;
            Uses = uses;
            Policy = WhenChanged.Keep;
        }

        // The mod counts as installed where the first type named is loaded.
        public ForeignAssumption Type(string fullName)
        {
            types.Add(fullName);
            return this;
        }

        // A method by name: with parameterTypes (full names) the overload taking
        // exactly those, without them the only one of that name. Fingerprinted.
        public ForeignAssumption Method(string type, string name, params string[] parameterTypes)
        {
            return AddMethod(type, name, parameterTypes, true);
        }

        // A method that must be there, without its code mattering: a hook's target
        // whose body the hook does not depend on.
        public ForeignAssumption Calls(string type, string name, params string[] parameterTypes)
        {
            return AddMethod(type, name, parameterTypes, false);
        }

        private ForeignAssumption AddMethod(string type, string name, string[] parameterTypes, bool fingerprint)
        {
            string[] wanted = parameterTypes != null && parameterTypes.Length > 0 ? parameterTypes : null;
            methods.Add(() => FindMethod(type, name, wanted));
            methodNames.Add(type + "." + name + (wanted != null ? "(" + string.Join(", ", wanted) + ")" : ""));
            fingerprinted.Add(fingerprint);
            return this;
        }

        // A field, and the type it must have (full name), or null for any.
        public ForeignAssumption Field(string type, string name, string fieldType = null)
        {
            fields.Add(new[] { type, name, fieldType });
            return this;
        }

        // A string some method of the type loads -- or, with type null, some method
        // of the first type's assembly.
        public ForeignAssumption Literal(string type, params string[] values)
        {
            foreach (string value in values) literals.Add(new[] { type, value });
            return this;
        }

        public ForeignAssumption Tested(params string[] fingerprints)
        {
            testedFingerprints.AddRange(fingerprints);
            return this;
        }

        // Where nothing can be fingerprinted -- a mod's shaders --, the versions
        // of its assembly that were checked.
        public ForeignAssumption TestedVersions(params string[] versions)
        {
            testedVersions.AddRange(versions);
            return this;
        }

        public ForeignAssumption OnChange(WhenChanged policy, string fallback)
        {
            Policy = policy;
            Fallback = fallback;
            return this;
        }

        public ForeignState State
        {
            get
            {
                Evaluate();
                return state;
            }
        }

        // Whether what rests on it may go on: verified, or changed where the
        // policy keeps it.
        public bool Holds
        {
            get
            {
                Evaluate();
                return state == ForeignState.Verified || (state == ForeignState.Changed && Policy == WhenChanged.Keep);
            }
        }

        // Why it is Broken or Changed; null otherwise.
        public string Problem
        {
            get
            {
                Evaluate();
                return problem;
            }
        }

        // The installed code's fingerprint, where methods are fingerprinted.
        public string Fingerprint
        {
            get
            {
                Evaluate();
                return fingerprint;
            }
        }

        public string Version
        {
            get
            {
                Evaluate();
                return version;
            }
        }

        private void Evaluate()
        {
            if (evaluated) return;
            evaluated = true;
            problem = null;
            fingerprint = null;
            version = null;
            try
            {
                Check();
            }
            catch (Exception e)
            {
                // Reflection into an assembly that cannot be read throws: what is
                // named cannot be relied on.
                state = ForeignState.Broken;
                problem = "unreadable: " + (e.InnerException ?? e).Message;
            }
        }

        private void Check()
        {

            Type first = types.Count > 0 ? TypeLookup.Find(types[0]) : null;
            if (first == null)
            {
                state = ForeignState.Absent;
                return;
            }
            version = ForeignCode.Version(first.Assembly);

            List<string> missing = new List<string>();
            foreach (string name in types)
                if (TypeLookup.Find(name) == null) missing.Add("type " + name);

            List<MethodBase> found = new List<MethodBase>();
            for (int i = 0; i < methods.Count; i++)
            {
                MethodBase method;
                try { method = methods[i](); }
                catch (Exception) { method = null; }
                if (method == null) missing.Add("method " + methodNames[i]);
                else if (fingerprinted[i]) found.Add(method);
            }

            foreach (string[] field in fields)
            {
                Type owner = TypeLookup.Find(field[0]);
                FieldInfo info = owner != null ? owner.GetField(field[1], TypeLookup.Any) : null;
                if (info == null || (field[2] != null && ForeignCode.TypeName(info.FieldType) != field[2]
                                     && !(info.FieldType.IsGenericType && info.FieldType.GetGenericTypeDefinition().FullName == field[2])))
                    missing.Add("field " + field[0] + "." + field[1] + (field[2] != null ? " of type " + field[2] : ""));
            }

            foreach (string[] literal in literals)
            {
                bool loaded;
                if (literal[0] == null)
                {
                    loaded = ForeignCode.AssemblyLoads(first.Assembly, literal[1]);
                }
                else
                {
                    Type owner = TypeLookup.Find(literal[0]);
                    loaded = owner != null && ForeignCode.TypeLoads(owner, literal[1]);
                }
                if (!loaded) missing.Add("\"" + literal[1] + "\" in " + (literal[0] ?? first.Assembly.GetName().Name));
            }

            if (missing.Count > 0)
            {
                state = ForeignState.Broken;
                problem = "missing: " + string.Join("; ", missing.ToArray());
                return;
            }

            if (found.Count > 0) fingerprint = ForeignCode.Fingerprint(found);
            bool codeKnown = found.Count == 0 || (fingerprint != null && testedFingerprints.Contains(fingerprint));
            bool versionKnown = testedVersions.Count == 0 || testedVersions.Contains(version);
            if (codeKnown && versionKnown)
            {
                state = ForeignState.Verified;
                return;
            }
            state = ForeignState.Changed;
            problem = !codeKnown
                ? "code " + (fingerprint ?? "unreadable") + " is not the one checked ("
                  + (testedFingerprints.Count > 0 ? string.Join(", ", testedFingerprints.ToArray()) : "none") + ")"
                : "version " + version + " is not one checked (" + string.Join(", ", testedVersions.ToArray()) + ")";
        }

        private static MethodBase FindMethod(string typeName, string name, string[] parameterTypes)
        {
            Type type = TypeLookup.Find(typeName);
            if (type == null) return null;
            MethodBase match = null;
            const BindingFlags all = TypeLookup.Any | BindingFlags.DeclaredOnly;
            List<MethodBase> candidates = new List<MethodBase>();
            if (name == ".ctor") candidates.AddRange(type.GetConstructors(all & ~BindingFlags.Static));
            else foreach (MethodInfo method in type.GetMethods(all)) if (method.Name == name) candidates.Add(method);
            foreach (MethodBase candidate in candidates)
            {
                if (parameterTypes == null)
                {
                    // The only one of that name, or no answer.
                    if (match != null) return null;
                    match = candidate;
                    continue;
                }
                ParameterInfo[] parameters = candidate.GetParameters();
                if (parameters.Length != parameterTypes.Length) continue;
                bool same = true;
                for (int i = 0; i < parameters.Length && same; i++)
                    same = ForeignCode.TypeName(parameters[i].ParameterType) == parameterTypes[i];
                if (same) return candidate;
            }
            return match;
        }
    }
}
