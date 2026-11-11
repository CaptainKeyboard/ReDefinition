// The wrapper pattern of Kerbal Alarm Clock's API (a type lookup through the loaded assemblies, the APIInstance field, the CreateAlarm and
// DeleteAlarm methods called through reflection, the version check against 3.0.0.5, APIReady) follows KACWrapper.cs of
// https://github.com/TriggerAu/KerbalAlarmClock (MIT, Copyright (c) 2014 David Tregoning); see docs/credits-and-licences.md.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using ReDefinition.Core;

namespace ReDefinition.Atmospheres.Forecast
{
    // What the bridge asks of Kerbal Alarm Clock. The reflection implementation (KacReflection) talks to the real mod; a test gives a fake.
    internal interface IKacApi
    {
        // Whether the mod's API answers (found, new enough, APIReady).
        bool Ready { get; }
        // Creates a raw alarm and returns its id, or null where it could not.
        string CreateAlarm(string name, double ut, KacAction action);
        bool DeleteAlarm(string id);
    }

    // The action of an alarm when it fires: the two the setting alertWarpStop picks between.
    internal enum KacAction
    {
        MessageOnly = 0,
        KillWarp = 1
    }

    // Kerbal Alarm Clock through reflection, so that nothing links against it: off where the assembly is absent or older than the API
    // (3.0.0.5), and then silent. One line is logged when it is found and one when it is too old.
    internal sealed class KacReflection : IKacApi
    {
        private const string TypeName = "KerbalAlarmClock.KerbalAlarmClock";
        private static readonly Version MinimumVersion = new Version(3, 0, 0, 5);
        // The numbers of KACWrapper's AlarmTypeEnum.Raw and AlarmActionEnum.MessageOnly / KillWarp.
        private const int RawAlarm = 0;
        private const int ActionMessageOnly = 2;
        private const int ActionKillWarp = 4;

        // The types are looked up until they are found (a mod that loads later is found later); the instance is read again at every ask,
        // since KAC makes its add-on object again in each scene. Nothing is logged while the mod is absent or its instance not made yet.
        private bool bound;
        private bool announced;
        private bool tooOld;
        private bool membersFound;
        private Type kacType;
        private Type alarmType;
        private object instance;
        private FieldInfo instanceField;
        private FieldInfo apiReady;
        private MethodInfo create;
        private MethodInfo delete;
        private FieldInfo alarmsField;
        private FieldInfo idField;
        private FieldInfo nameField;
        private PropertyInfo timeProperty;
        private PropertyInfo actionProperty;
        private readonly Action<string> say;

        internal KacReflection(Action<string> say)
        {
            this.say = say ?? (s => { });
        }

        public bool Ready
        {
            get
            {
                if (!bound) Bind();
                if (!bound || tooOld || !membersFound) return false;
                try
                {
                    instance = instanceField != null ? instanceField.GetValue(null) : null;
                }
                catch (Exception)
                {
                    instance = null;
                }
                if (instance == null) return false;
                if (!announced)
                {
                    announced = true;
                    say("Kerbal Alarm Clock " + kacType.Assembly.GetName().Version + " found, storm alarms are available");
                }
                return ApiReady();
            }
        }

        private bool ApiReady()
        {
            try
            {
                return apiReady != null && (bool)apiReady.GetValue(null);
            }
            catch (Exception)
            {
                return false;
            }
        }

        // Finds the mod's types and members. Absence is silent and tried again at the next ask; the version and the members are judged once.
        private void Bind()
        {
            try
            {
                kacType = TypeLookup.Find(TypeName);
                if (kacType == null) return;
                bound = true;
                if (kacType.Assembly.GetName().Version.CompareTo(MinimumVersion) < 0)
                {
                    tooOld = true;
                    say("Kerbal Alarm Clock " + kacType.Assembly.GetName().Version + " is older than 3.0.0.5, no alarms are made");
                    return;
                }
                alarmType = TypeLookup.Find("KerbalAlarmClock.KACAlarm");
                instanceField = kacType.GetField("APIInstance", BindingFlags.Public | BindingFlags.Static);
                apiReady = kacType.GetField("APIReady", BindingFlags.Public | BindingFlags.Static);
                create = kacType.GetMethod("CreateAlarm", BindingFlags.Public | BindingFlags.Instance);
                delete = kacType.GetMethod("DeleteAlarm", BindingFlags.Public | BindingFlags.Instance);
                alarmsField = kacType.GetField("alarms", BindingFlags.Public | BindingFlags.Static);
                if (alarmType != null)
                {
                    idField = alarmType.GetField("ID");
                    nameField = alarmType.GetField("Name");
                    timeProperty = alarmType.GetProperty("AlarmTimeUT");
                    actionProperty = alarmType.GetProperty("AlarmActionConvert");
                }
                membersFound = instanceField != null && apiReady != null && create != null && delete != null;
                if (!membersFound) say("Kerbal Alarm Clock was found but its API did not match, no alarms are made");
            }
            catch (Exception e)
            {
                membersFound = false;
                bound = true;
                say("Kerbal Alarm Clock could not be bound (" + CompatibilityLog.Reason(e) + ")");
            }
        }

        public string CreateAlarm(string name, double ut, KacAction action)
        {
            if (!Ready) return null;
            try
            {
                // KAC keeps its alarms in the save; one made in an earlier session of the same game is found again, not made twice.
                string known = FindAlarm(name, ut);
                if (known != null) return known;
                string id = (string)create.Invoke(instance, new object[] { RawAlarm, name, ut });
                if (id != null) SetAction(id, action == KacAction.KillWarp ? ActionKillWarp : ActionMessageOnly);
                return id;
            }
            catch (Exception e)
            {
                say("Kerbal Alarm Clock refused an alarm (" + CompatibilityLog.Reason(e) + ")");
                return null;
            }
        }

        public bool DeleteAlarm(string id)
        {
            if (!Ready || id == null) return false;
            try
            {
                return (bool)delete.Invoke(instance, new object[] { id });
            }
            catch (Exception e)
            {
                say("Kerbal Alarm Clock could not delete an alarm (" + CompatibilityLog.Reason(e) + ")");
                return false;
            }
        }

        // The id of an alarm of that name within a minute of that time, or null.
        private string FindAlarm(string name, double ut)
        {
            if (alarmsField == null || idField == null || nameField == null || timeProperty == null) return null;
            try
            {
                var list = alarmsField.GetValue(null) as System.Collections.IEnumerable;
                if (list == null) return null;
                foreach (object alarm in list)
                {
                    if ((string)nameField.GetValue(alarm) != name) continue;
                    if (Math.Abs((double)timeProperty.GetValue(alarm, null) - ut) < 60.0) return (string)idField.GetValue(alarm);
                }
            }
            catch (Exception)
            {
                // Without the list a new alarm is made.
            }
            return null;
        }

        // The action is a property of the alarm object in KAC's list, found by its id.
        private void SetAction(string id, int action)
        {
            if (alarmsField == null || idField == null || actionProperty == null) return;
            try
            {
                var list = alarmsField.GetValue(null) as System.Collections.IEnumerable;
                if (list == null) return;
                foreach (object alarm in list)
                {
                    if ((string)idField.GetValue(alarm) != id) continue;
                    actionProperty.SetValue(alarm, action, null);
                    return;
                }
            }
            catch (Exception)
            {
                // The alarm exists with KAC's default action; the property is a refinement.
            }
        }
    }

    // The alarms the forecast makes in Kerbal Alarm Clock: one at "storm ETA minus the lead time" for each watched site whose user level is
    // reached within the horizon, named "Storm approaching: <site>". The alarms made here are deleted when the forecast generation
    // changes, or when the storm's ETA moves, and are made again from the new numbers. Ids are kept in memory only (KAC saves its alarms
    // itself); a switched-off bridge deletes what it made.
    internal sealed class KacBridge
    {
        private sealed class Made
        {
            internal string Id;
            internal double EtaUt;
            internal double AlarmUt;
            internal KacAction Action;
        }

        // How far the ETA may move before the alarm is made again, in seconds.
        internal const double MoveToleranceSeconds = 60.0;

        private readonly IKacApi api;
        private readonly Dictionary<ulong, Made> made = new Dictionary<ulong, Made>();
        private long generation = long.MinValue;

        internal KacBridge(IKacApi api)
        {
            if (api == null) throw new ArgumentNullException("api");
            this.api = api;
        }

        // The number of alarms this bridge holds.
        internal int Count { get { return made.Count; } }

        internal bool Holds(ulong siteKey)
        {
            return made.ContainsKey(siteKey);
        }

        internal double AlarmUtOf(ulong siteKey)
        {
            Made m;
            return made.TryGetValue(siteKey, out m) ? m.AlarmUt : double.NaN;
        }

        // The key that stands for a watched place in this bridge: the same while the place moves (a vessel in flight crosses a tenth of a
        // degree every few kilometres, and its alarm is still one alarm). choice is 1 for the Space Centre, 3 for the vessel.
        internal static ulong PlaceKey(int choice, int bodyIndex)
        {
            return ForecastMath.Mix(ForecastMath.Mix(0x4B4143UL, (long)choice), (long)bodyIndex);
        }

        // Brings the alarms in line with what the sites foresee. keys name the places, eta is the first time the user's level is reached
        // at the site (NaN for none), as the engine's last run gives it. Returns true when an alarm was made or deleted.
        internal bool Sync(bool enabled, long forecastGeneration, double nowUt, double leadHours, KacAction action, IList<ForecastSite> sites,
            IList<ulong> keys, IList<double> etaUts)
        {
            bool changed = false;
            if (!enabled || !api.Ready)
            {
                if (made.Count > 0) changed = DeleteAll() || changed;
                generation = forecastGeneration;
                return changed;
            }
            if (generation != forecastGeneration)
            {
                // The inputs of the forecast changed: every alarm is a statement about a forecast that no longer holds.
                if (generation != long.MinValue) changed = DeleteAll() || changed;
                generation = forecastGeneration;
            }
            var wanted = new HashSet<ulong>();
            for (int i = 0; i < sites.Count; i++)
            {
                double eta = etaUts[i];
                ulong key = keys[i];
                double alarmUt = eta - leadHours * 3600.0;
                bool want = !double.IsNaN(eta) && alarmUt > nowUt;
                Made existing;
                made.TryGetValue(key, out existing);
                if (want) wanted.Add(key);
                if (existing != null && existing.AlarmUt <= nowUt)
                {
                    // It has fired: it is KAC's to handle, only the id is dropped.
                    made.Remove(key);
                    existing = null;
                }
                if (existing != null && (!want || Math.Abs(existing.EtaUt - eta) > MoveToleranceSeconds || existing.Action != action))
                {
                    api.DeleteAlarm(existing.Id);
                    made.Remove(key);
                    existing = null;
                    changed = true;
                }
                if (want && existing == null)
                {
                    string name = ForecastStrings.KacAlarmPrefix + (string.IsNullOrEmpty(sites[i].Name) ? "site" : sites[i].Name);
                    string id = api.CreateAlarm(name, alarmUt, action);
                    if (id != null)
                    {
                        made[key] = new Made { Id = id, EtaUt = eta, AlarmUt = alarmUt, Action = action };
                        changed = true;
                    }
                }
            }
            // A site that is no longer watched has no alarm; one whose alarm time has passed is KAC's to handle, the id is dropped.
            var drop = new List<ulong>();
            foreach (KeyValuePair<ulong, Made> kv in made)
            {
                if (kv.Value.AlarmUt <= nowUt) drop.Add(kv.Key);
                else if (!wanted.Contains(kv.Key)) drop.Add(kv.Key);
            }
            for (int i = 0; i < drop.Count; i++)
            {
                Made m = made[drop[i]];
                if (m.AlarmUt > nowUt)
                {
                    api.DeleteAlarm(m.Id);
                    changed = true;
                }
                made.Remove(drop[i]);
            }
            return changed;
        }

        // Deletes every alarm this bridge made.
        internal bool DeleteAll()
        {
            bool any = made.Count > 0;
            foreach (Made m in made.Values) api.DeleteAlarm(m.Id);
            made.Clear();
            return any;
        }

        internal static string AlarmName(ForecastSite site)
        {
            return ForecastStrings.KacAlarmPrefix + (string.IsNullOrEmpty(site.Name) ? "site" : site.Name);
        }

        internal static string Describe(double alarmUt, double etaUt)
        {
            return "alarm at " + alarmUt.ToString("0", CultureInfo.InvariantCulture) + ", storm at " + etaUt.ToString("0", CultureInfo.InvariantCulture);
        }
    }
}
