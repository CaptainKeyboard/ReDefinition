using System;
using System.Collections.Generic;
using System.Reflection;

namespace ReDefinition.Window
{
    // What KSP ships for its settings, read without touching the player's.
    // GameSettings.SetDefaultValues assigns every static field anew, 119 new KeyBindings among them,
    // and does nothing else (its IL in KSP 1.12.5: stores to its own fields and constructors without side effects).
    // So every field is saved, `read` runs over the defaults, and
    // every field is put back, the same objects included, whatever went
    // wrong. Nothing that holds one of KSP's bindings sees a change.
    // The result is the exception `read` threw, or null.
    internal static class KspShipped
    {
        // Runs `read` over the defaults SetDefaultValues sets, every static field of GameSettings put back afterwards.
        internal static Exception Read(Action read)
        {
            List<FieldInfo> fields = new List<FieldInfo>();
            foreach (FieldInfo field in typeof(GameSettings).GetFields(BindingFlags.Public | BindingFlags.NonPublic
                                                                          | BindingFlags.Static))
            {
                if (!field.IsLiteral && !field.IsInitOnly) fields.Add(field);
            }
            object[] saved = new object[fields.Count];
            for (int i = 0; i < fields.Count; i++) saved[i] = fields[i].GetValue(null);
            try
            {
                GameSettings.SetDefaultValues();
                read();
                return null;
            }
            catch (Exception e)
            {
                return e;
            }
            finally
            {
                for (int i = 0; i < fields.Count; i++) fields[i].SetValue(null, saved[i]);
            }
        }
    }
}
