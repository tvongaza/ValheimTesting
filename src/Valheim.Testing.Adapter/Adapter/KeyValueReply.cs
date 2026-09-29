// Source from the Valheim.Testing.Adapter package: compiled into a mod's game-side test adapter. No Unity or game
// types, so it also compiles into ordinary test projects.
#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Valheim.Testing.Adapter
{
    /// <summary>
    /// Reads the <c>key=value</c> fields of a console reply, for an adapter that wraps a mod's existing console command in
    /// an extension command and returns its answer as structured data. A value is a run of non-space characters, or
    /// <c>'quoted text'</c> that may contain spaces (returned without the quotes). Keys start with a letter and are
    /// case-sensitive; words that are not fields are ignored. A repeated key fails rather than picking one.
    /// </summary>
    public static class KeyValueReply
    {
        private static readonly Regex Fields = new Regex(@"(?:^|\s)([A-Za-z][A-Za-z0-9_]*)=(?:'([^']*)'(?=\s|$)|(\S*))",
            RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250));

        /// <summary>The fields of one line.</summary>
        public static IReadOnlyDictionary<string, string> Parse(string line)
        {
            if (line == null) throw new ArgumentNullException(nameof(line));
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (Match match in Fields.Matches(line))
            {
                string key = match.Groups[1].Value;
                if (values.ContainsKey(key)) throw new FormatException("Duplicate reply field: " + key);
                values.Add(key, match.Groups[2].Success ? match.Groups[2].Value : match.Groups[3].Value);
            }
            return values;
        }

        /// <summary>
        /// The fields of the one line that starts with <paramref name="prefix"/> (for example <c>"OK: MYMOD_STATUS "</c>).
        /// Any <c>ERROR:</c> line fails with that line, even beside a success; no such line or several fail too, so silence
        /// or a doubled reply is never read as an answer.
        /// </summary>
        public static IReadOnlyDictionary<string, string> Single(IEnumerable<string> lines, string prefix)
        {
            if (lines == null) throw new ArgumentNullException(nameof(lines));
            if (string.IsNullOrEmpty(prefix)) throw new ArgumentException("Name the reply's prefix.", nameof(prefix));
            string[] all = lines.ToArray();
            string? error = all.FirstOrDefault(line => line.StartsWith("ERROR:", StringComparison.Ordinal));
            if (error != null) throw new InvalidOperationException(error);
            string[] answers = all.Where(line => line.StartsWith(prefix, StringComparison.Ordinal)).ToArray();
            if (answers.Length != 1)
                throw new InvalidOperationException($"Expected one reply starting with \"{prefix}\"; found {answers.Length} in {all.Length} line(s).");
            return Parse(answers[0].Substring(prefix.Length));
        }
    }
}
