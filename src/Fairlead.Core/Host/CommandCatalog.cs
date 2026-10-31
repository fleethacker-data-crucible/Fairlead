using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Fairlead.Core.Host
{
    /// <summary>One CLI command as `fairlead commands --json` and GET /api/commands list it.</summary>
    public sealed class CommandInfo
    {
        public string Name { get; set; }
        public string Usage { get; set; }
        public string Group { get; set; }
        public string Description { get; set; }
        public List<string> Routes { get; set; }
        public bool Destructive { get; set; }

        public CommandInfo()
        {
            Routes = new List<string>();
        }
    }

    /// <summary>
    /// The command list, read from the help text in scripts\fairlead.ps1 (Get-HelpText), the same text the command line
    /// reads for `fairlead commands`, so the two lists cannot disagree. Keep this parser and Get-CommandList in step.
    /// </summary>
    public static class CommandCatalog
    {
        public const int SchemaVersion = 1;
        private static readonly Regex Route = new Regex(@"\b(GET|POST|PUT|DELETE) (/api[^\s,|]*)", RegexOptions.CultureInvariant);
        private static readonly Regex RouteText = new Regex(@"\b(GET|POST|PUT|DELETE) /api\S*", RegexOptions.CultureInvariant);
        private static readonly Regex Gap = new Regex(@"\s{2,}", RegexOptions.CultureInvariant);

        /// <summary>The script's path under the application folder, or null when it is not installed.</summary>
        public static string ScriptPath(string rootDirectory)
        {
            if (string.IsNullOrEmpty(rootDirectory)) return null;
            var path = Path.Combine(rootDirectory, "scripts", "fairlead.ps1");
            return File.Exists(path) ? path : null;
        }

        public static List<CommandInfo> FromScript(string scriptText)
        {
            var start = scriptText.IndexOf("function Get-HelpText", StringComparison.Ordinal);
            var open = start < 0 ? -1 : scriptText.IndexOf("@'", start, StringComparison.Ordinal);
            var close = open < 0 ? -1 : scriptText.IndexOf("'@", open + 2, StringComparison.Ordinal);
            if (close < 0) return new List<CommandInfo>();
            return FromHelp(scriptText.Substring(open + 2, close - open - 2));
        }

        public static List<CommandInfo> FromHelp(string help)
        {
            var entries = new List<KeyValuePair<string, string[]>>();
            var group = string.Empty;
            string[] current = null;
            foreach (var raw in help.Split('\n'))
            {
                var line = raw.TrimEnd('\r');
                if (line.Length > 0 && line[0] >= 'A' && line[0] <= 'Z' && !line.StartsWith("Options ", StringComparison.Ordinal) && !line.StartsWith("Use ", StringComparison.Ordinal))
                {
                    group = line.Trim();
                    current = null;
                    continue;
                }
                if (line.Length > 2 && line.StartsWith("  ", StringComparison.Ordinal) && !char.IsWhiteSpace(line[2]))
                {
                    current = new[] { group, line.Trim() };
                    entries.Add(new KeyValuePair<string, string[]>(group, current));
                    continue;
                }
                if (current != null && line.StartsWith("    ", StringComparison.Ordinal) && line.Trim().Length > 0) current[1] = current[1] + " " + line.Trim();
            }
            var result = new List<CommandInfo>();
            foreach (var e in entries)
            {
                var text = e.Value[1];
                var parts = Gap.Split(text);
                var usage = parts[0];
                var info = new CommandInfo { Usage = usage, Group = e.Key, Name = usage.Split(' ')[0], Destructive = text.IndexOf("--yes", StringComparison.Ordinal) >= 0 };
                foreach (Match m in Route.Matches(text)) info.Routes.Add(m.Groups[1].Value + " " + m.Groups[2].Value);
                info.Description = parts.Length > 1 ? RouteText.Replace(string.Join(" ", parts.Skip(1)), string.Empty).Trim() : string.Empty;
                result.Add(info);
            }
            return result;
        }

        public static object Document(List<CommandInfo> commands)
        {
            return new
            {
                schemaVersion = SchemaVersion,
                tool = "fairlead",
                globalOptions = new[] { "--json", "--url" },
                commands = commands.Select(c => new { name = c.Name, usage = c.Usage, group = c.Group, description = c.Description, routes = c.Routes, destructive = c.Destructive }).ToList(),
            };
        }
    }
}
