using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace DocLock
{
    // One thing wrong with a comment: the rule, and what it found.
    internal sealed class Finding
    {
        // The rule that found it, and what it found.
        public string Rule;
        public string Detail;
    }

    // The comment contract (docs/development/writing-code-comments.md) as checks that a machine can make. A
    // comment says more than the name, is no longer than its element deserves, can be read in one go, tells what
    // is and not what was, and is English. What it cannot judge -- whether the comment is true, and whether it
    // helps -- the reviews do (.doclock/review.md).
    internal static class Lint
    {
        // The most words a comment may have, by the kind of element: a member's, a type's, a file's.
        private const int MemberWords = 110;
        private const int TypeWords = 220;
        private const int FileWords = 320;

        // The longest sentence, in words, and the most breaks of thought (dashes, semicolons) in one comment.
        private const int SentenceWords = 40;
        private const int BreaksOfThought = 5;

        // Words that date a comment to an earlier state of the code or put it on a to-do list.
        private static readonly Regex History = new Regex(
            @"\b(previously|formerly|originally|used to be|was changed|has been changed|after the rewrite|in the past|we changed|todo|fixme|hack|xxx)\b",
            RegexOptions.IgnoreCase);

        // Common German words that English comments do not hold.
        private static readonly string[] German = { "und", "der", "die", "das", "nicht", "wird", "mit", "für", "ist", "nur", "oder", "wenn", "auch", "eine", "einen" };

        // Words that carry no content, left out when a comment is compared with the name it comments.
        private static readonly HashSet<string> Stop = new HashSet<string>(
            "a an the of for to in on at by is are be as it its this that with and or from into per each".Split(' '));

        // What is wrong with the element's comment; empty where nothing is.
        public static List<Finding> Check(Element e)
        {
            var found = new List<Finding>();
            if (e.Doc.Length == 0) return found;
            // XML tags are markup and not words, and the description of a parameter or a return value is reference, not part of the comment.
            string withoutReference = Regex.Replace(e.Doc, "<(param|returns|typeparam)[^>]*>.*?</(param|returns|typeparam)>", " ", RegexOptions.Singleline);
            string flat = Element.Normalise(Regex.Replace(withoutReference, "<[^>]+>", " "));
            int words = Element.Words(flat);

            bool file = e.Kind == "script" || e.Kind == "module" || e.Kind == "shader" || e.Kind == "config";
            bool type = e.Kind == "class" || e.Kind == "struct" || e.Kind == "interface" || e.Kind == "enum" || e.Kind == "pass";
            int limit = file ? FileWords : type ? TypeWords : MemberWords;
            if (words > limit) found.Add(new Finding { Rule = "too-long", Detail = words + " words; at most " + limit + " for a " + e.Kind });

            if (words < 14 && Restates(flat, e))
                found.Add(new Finding { Rule = "restates-name", Detail = "says little beyond '" + e.Name + "'" });

            foreach (string sentence in Sentences(flat))
            {
                int n = Element.Words(sentence);
                if (n > SentenceWords)
                {
                    found.Add(new Finding { Rule = "long-sentence", Detail = n + " words in '" + Brief(sentence) + "'" });
                    break;
                }
            }

            int breaks = Regex.Matches(flat, " -- ").Count + flat.Count(c => c == ';');
            if (breaks > BreaksOfThought) found.Add(new Finding { Rule = "dense", Detail = breaks + " dashes and semicolons" });

            Match past = History.Match(flat);
            if (past.Success) found.Add(new Finding { Rule = "history", Detail = "'" + past.Value + "'" });

            string lower = " " + Regex.Replace(flat.ToLowerInvariant(), @"[^\p{L}]+", " ") + " ";
            int german = German.Count(w => lower.Contains(" " + w + " "));
            if (german >= 3) found.Add(new Finding { Rule = "not-english", Detail = german + " German words" });

            return found;
        }

        // Whether nearly every content word of the comment is a word of the element's own name.
        private static bool Restates(string comment, Element e)
        {
            string last = e.Name;
            int cut = Math.Max(last.LastIndexOf('.'), last.LastIndexOf(':'));
            if (cut >= 0) last = last.Substring(cut + 1);
            var named = new HashSet<string>(Split(last + " " + e.Signature));
            var content = Split(comment).Where(w => !Stop.Contains(w)).ToList();
            if (content.Count == 0) return true;
            int inName = content.Count(w => named.Contains(w) || named.Contains(w.TrimEnd('s')) || named.Contains(w + "s"));
            return inName * 100 >= content.Count * 75;
        }

        // The lower-case words of a text, identifiers split at their capitals and underscores.
        private static IEnumerable<string> Split(string text)
        {
            string spaced = Regex.Replace(text, @"([a-z0-9])([A-Z])", "$1 $2");
            foreach (Match m in Regex.Matches(spaced, @"[A-Za-z][a-z]*|[0-9]+"))
                yield return m.Value.ToLowerInvariant();
        }

        // The sentences of a flattened comment, split at a full stop before a capital.
        private static IEnumerable<string> Sentences(string flat)
        {
            return Regex.Split(flat, @"(?<=[.!?])\s+(?=[A-Z])");
        }

        // The first words of a sentence, for the message.
        private static string Brief(string sentence)
        {
            return sentence.Length <= 70 ? sentence : sentence.Substring(0, 67) + "...";
        }

        // The findings of every documented element, keyed by id, with the fingerprint of the comment they judged.
        public static List<(Element element, Finding finding)> All(IEnumerable<Element> elements)
        {
            var result = new List<(Element, Finding)>();
            foreach (Element e in elements)
                foreach (Finding f in Check(e))
                    result.Add((e, f));
            return result;
        }
    }

    // The findings already known and accepted for the comments as they stand: a finding is new when its element's
    // comment changed since, or the element is new. The baseline shrinks as comments are rewritten.
    internal sealed class LintBaseline
    {
        // Each line: id, rule, fingerprint of the comment.
        private readonly HashSet<string> known = new HashSet<string>(StringComparer.Ordinal);
        private readonly string path;

        // Reads the baseline file of the repository, if there is one.
        public LintBaseline(string root)
        {
            path = Path.Combine(root, ".doclock", "lint-baseline.tsv");
            if (!File.Exists(path)) return;
            foreach (string line in File.ReadAllLines(path))
                if (line.Length > 0 && line[0] != '#') known.Add(line);
        }

        // The key a finding is known by.
        private static string Key(Element e, Finding f)
        {
            return e.Id + "\t" + f.Rule + "\t" + e.DocHash;
        }

        // Whether the finding was accepted for this comment.
        public bool Knows(Element e, Finding f)
        {
            return known.Contains(Key(e, f));
        }

        // Writes the baseline as the findings of the elements as they stand now.
        public static void Write(string root, List<(Element element, Finding finding)> findings)
        {
            var lines = new List<string>
            {
                "# The comment lint's accepted findings (tools/DocLock, lint): element id, rule, fingerprint of the comment.",
                "# A finding stays accepted while the comment is as it was; a comment written anew has to pass.",
            };
            lines.AddRange(findings.Select(f => Key(f.element, f.finding)).OrderBy(k => k, StringComparer.Ordinal));
            File.WriteAllLines(Path.Combine(root, ".doclock", "lint-baseline.tsv"), lines, new UTF8Encoding(false));
        }
    }
}
