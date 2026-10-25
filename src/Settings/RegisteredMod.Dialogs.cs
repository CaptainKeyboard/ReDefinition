using System.Collections.Generic;
using System;

namespace ReDefinition.Settings
{
    // A mod's dialogs (DIALOG): a row with a button that opens one, drawn by
    // ReDefinition's window around the content the mod's method hands it. They hold
    // no value, so they stand apart from the settings: no profile, reset, store or
    // restore meets them.
    internal sealed partial class RegisteredMod
    {
        // The dialogs this build offers, as rows.
        internal IList<BundledSetting> Dialogs
        {
            get { return dialogs; }
        }

        // The dialog of that name; null where this build offers none.
        internal BundledSetting Dialog(string name)
        {
            string key = Id + "." + name;
            foreach (BundledSetting dialog in dialogs)
                if (dialog.Key == key) return dialog;
            return null;
        }

        // The content method checked against the build installed: one that is not
        // there, takes parameters or returns no dialog content is left out as a
        // setting's member is.
        private void AddDialog(DialogRegistration entry, string modWhere, List<string> problems)
        {
            string where = modWhere + ", dialog '" + entry.Name + "'";
            string problem;
            MemberPath content = MemberPath.Resolve(entry.Content, folder, out problem);
            if (content != null && !content.IsMethod) problem = entry.Content + " is no method";
            else if (content != null && !IsContent(content.ReturnType))
                problem = entry.Content + " returns " + content.ReturnType.Name + ", not a DialogGUIBase or an array of them";
            if (content == null || problem != null)
            {
                if (entry.Optional == null)
                {
                    problems.Add(where + ": content: " + problem + " -- left out.");
                    string title = Localized(entry.Title);
                    if (!rowsNotShown.Contains(title)) rowsNotShown.Add(title);
                }
                if (!IsTrue(entry.Optional)) Drop(entry.Name, entry.Optional ?? problem);
                return;
            }

            string titleText = Localized(entry.Title);
            BundledSetting dialog = new BundledSetting
            {
                Key = Id + "." + entry.Name,
                Title = titleText,
                Tooltip = Localized(entry.Tooltip),
                Control = SettingControl.Dialog,
                Section = entry.Section != null ? Localized(entry.Section) : null,
                Bundled = false,
                Window = ApplyWindow.Live,
                DialogButton = entry.Button != null ? Localized(entry.Button) : "Open ...",
                DialogHeading = entry.Heading != null ? Localized(entry.Heading) : titleText,
                DialogWidth = entry.Width,
                // Its button waits for what the method is called on: the mod's
                // instance made, its object in the scene.
                Applicable = content.Reachable,
                DialogContent = () => Content(content.Invoke()),
                Owner = this,
            };
            dialogs.Add(dialog);
        }

        // Whether a content method's return type is a DialogGUIBase or a sequence of them.
        private static bool IsContent(Type type)
        {
            return type != null && (typeof(DialogGUIBase).IsAssignableFrom(type)
                                    || typeof(IEnumerable<DialogGUIBase>).IsAssignableFrom(type));
        }

        // The content method's result as an array, nulls left out; null for anything else.
        private static DialogGUIBase[] Content(object result)
        {
            DialogGUIBase single = result as DialogGUIBase;
            if (single != null) return new[] { single };
            IEnumerable<DialogGUIBase> many = result as IEnumerable<DialogGUIBase>;
            if (many == null) return null;
            List<DialogGUIBase> list = new List<DialogGUIBase>();
            foreach (DialogGUIBase element in many)
                if (element != null) list.Add(element);
            return list.ToArray();
        }
    }
}
