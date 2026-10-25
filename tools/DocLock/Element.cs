using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;

namespace DocLock
{
    // One element of the code that must be documented: a type, a member, a function, a shader pass, a
    // script. Its id is stable across edits that keep its file, kind, name and signature; its code and its
    // documentation each have a fingerprint, which the lock holds (Lock.cs).
    internal sealed class Element
    {
        public string Path;          // relative to the repository, with forward slashes
        public string Language;      // cs, cpp, hlsl, shader, js, py, ps1
        public string Kind;          // type, method, field, enum member, function, macro, pass, script, ...
        public string Name;          // qualified where the language has scopes
        public string Signature = ""; // parameter types where overloads can share a name
        public int Line;             // 1-based, of the declaration
        public string Doc = "";      // the documentation as written, comment markers taken off
        public string Code = "";     // the code that the documentation describes, comments and spacing left out
        // Documented by the comment of the run of fields or members it stands in (the scanners' Group).
        public bool SharesDoc;
        // Documented elsewhere: a partial type's other part, a C++ function's declaration or definition.
        public bool DocumentedElsewhere;

        // The element's key in the lock: its file, kind, qualified name and signature.
        public string Id => Path + "|" + Kind + "|" + Name + (Signature.Length > 0 ? "(" + Signature + ")" : "");

        // The part of the repository it belongs to, for the coverage per area: src/<folder>, the proxy's
        // subfolders, tests/<project>, unity/<folder>, GameData/<mod>, or the top folder.
        public string Area
        {
            get
            {
                string[] parts = Path.Split('/');
                if (parts.Length > 2 && (parts[0] == "src" || parts[0] == "tests" || parts[0] == "unity" || parts[0] == "GameData"))
                    return parts[0] + "/" + parts[1] + (parts[0] == "src" && parts[1] == "DxgiProxy" && parts.Length > 3 ? "/" + parts[2] : "");
                return parts.Length > 1 ? parts[0] : "(root)";
            }
        }

        // The code's fingerprint, which the lock compares to tell code changed under its documentation.
        public string CodeHash => Hash(Code);
        // The documentation's fingerprint, its spacing evened out, so that rewrapping a comment does not change it.
        public string DocHash => Hash(Normalise(Doc));

        // A comment's words, which say whether it says more than the name.
        public static int Words(string text)
        {
            int words = 0;
            bool inWord = false;
            foreach (char c in text)
            {
                bool letter = char.IsLetterOrDigit(c);
                if (letter && !inWord) words++;
                inWord = letter;
            }
            return words;
        }

        // The text with every run of whitespace made one space and its ends trimmed.
        public static string Normalise(string text)
        {
            var b = new StringBuilder(text.Length);
            bool space = false;
            foreach (char c in text)
            {
                if (char.IsWhiteSpace(c))
                {
                    space = b.Length > 0;
                    continue;
                }
                if (space) b.Append(' ');
                space = false;
                b.Append(c);
            }
            return b.ToString();
        }

        // The first eight bytes of the text's SHA-256, as hex: short enough for the lock, long enough to tell changes
        // apart.
        public static string Hash(string text)
        {
            using (SHA256 sha = SHA256.Create())
            {
                byte[] bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(text));
                return BitConverter.ToString(bytes, 0, 8).Replace("-", "").ToLowerInvariant();
            }
        }
    }

    // Whether an element's documentation is enough: a comment of its own (or of the run it stands in, or
    // of its other declaration) that says more than its name.
    internal static class Rules
    {
        // The fewest words a comment needs to count as documentation.
        public const int LeastWords = 4;

        // Whether the element counts as documented: by a comment of its own, of its run, or of its other declaration.
        public static bool Documented(Element e)
        {
            if (e.SharesDoc || e.DocumentedElsewhere) return true;
            return Enough(e.Doc, e.Name);
        }

        // Whether a comment is enough for an element of that name: at least LeastWords words, and not the name alone.
        public static bool Enough(string doc, string name)
        {
            if (string.IsNullOrWhiteSpace(doc)) return false;
            if (Element.Words(doc) < LeastWords) return false;
            // Not only the name, split at its capitals or underscores.
            string bare = new string(Array.FindAll(doc.ToCharArray(), char.IsLetterOrDigit)).ToLowerInvariant();
            string last = name;
            int dot = Math.Max(name.LastIndexOf('.'), name.LastIndexOf(':'));
            if (dot >= 0) last = name.Substring(dot + 1);
            string bareName = new string(Array.FindAll(last.ToCharArray(), char.IsLetterOrDigit)).ToLowerInvariant();
            return bare != bareName;
        }
    }
}
