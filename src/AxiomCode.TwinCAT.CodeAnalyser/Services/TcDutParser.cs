using System.Text.RegularExpressions;
using System.Xml.Linq;
using AxiomCode.TwinCAT.CodeAnalyser.Models;

namespace AxiomCode.TwinCAT.CodeAnalyser.Services;

/// <summary>
/// Parses TwinCAT 3 .TcDUT XML files into <see cref="TcDut"/> model objects.
/// Handles ENUM, STRUCT, UNION, and type alias declarations.
/// </summary>
public static class TcDutParser
{
    // ── Attribute extraction: {attribute 'name'} ────────────────────────
    private static readonly Regex AttributeRegex = new(
        @"\{attribute\s+'(?<attr>[^']+)'\}",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // ── TYPE header: TYPE Name : ────────────────────────────────────────
    private static readonly Regex TypeHeaderRegex = new(
        @"^\s*TYPE\s+(?<name>\w+)\s*:",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Multiline);

    // ── STRUCT / UNION keyword ──────────────────────────────────────────
    private static readonly Regex StructUnionRegex = new(
        @"^\s*(?<kind>STRUCT|UNION)\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Multiline);

    // ── Enum body: ( ... ) [BaseType] ; ─────────────────────────────────
    // Captures the content between parentheses and an optional base type
    private static readonly Regex EnumBodyRegex = new(
        @"\(\s*(?<body>[\s\S]*?)\s*\)\s*(?<basetype>\w+)?\s*;",
        RegexOptions.Compiled);

    // ── Individual enum member: Name [:= Value] [// comment] ────────────
    private static readonly Regex EnumMemberRegex = new(
        @"^\s*(?<name>\w+)\s*(?::=\s*(?<value>[^,/(*]+?))?\s*(?:(?://|(?:\(\*))(?<comment>.*?)(?:\*\))?)?\s*,?\s*$",
        RegexOptions.Compiled | RegexOptions.Multiline);

    /// <summary>
    /// Parse a .TcDUT file and return a <see cref="TcDut"/> or null on failure.
    /// </summary>
    /// <param name="filePath">Absolute path to the .TcDUT file.</param>
    /// <param name="projectBasePath">Project root for relative path computation.</param>
    public static TcDut? Parse(string filePath, string projectBasePath)
    {
        try
        {
            var doc = XDocument.Load(filePath);
            var dutElement = doc.Root?.Element("DUT");
            if (dutElement == null)
                return null;

            var declElement = dutElement.Element("Declaration");
            var declaration = TcPouParser.ExtractCdata(declElement);

            if (string.IsNullOrWhiteSpace(declaration))
                return null;

            var dut = new TcDut
            {
                Name = dutElement.Attribute("Name")?.Value
                       ?? Path.GetFileNameWithoutExtension(filePath),
                FilePath = GetRelativePath(filePath, projectBasePath),
                RawDeclaration = declaration
            };

            // ── Extract attributes ──────────────────────────────────
            foreach (Match m in AttributeRegex.Matches(declaration))
                dut.Attributes.Add(m.Groups["attr"].Value);

            // ── Determine DUT type and parse accordingly ────────────
            if (IsStructOrUnion(declaration, out var isUnion))
            {
                dut.DutType = isUnion ? DutType.Union : DutType.Struct;
                ParseStructMembers(declaration, dut);
            }
            else if (IsEnum(declaration))
            {
                dut.DutType = DutType.Enum;
                ParseEnumValues(declaration, dut);
            }
            else
            {
                // Type alias: TYPE Name : ExistingType; END_TYPE
                dut.DutType = DutType.Alias;
                ParseAlias(declaration, dut);
            }

            return dut;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[TcDutParser] Failed to parse '{filePath}': {ex.Message}");
            return null;
        }
    }

    // ════════════════════════════════════════════════════════════════════
    //  Detection helpers
    // ════════════════════════════════════════════════════════════════════

    /// <summary>Check if the declaration contains STRUCT or UNION.</summary>
    private static bool IsStructOrUnion(string declaration, out bool isUnion)
    {
        isUnion = false;
        var match = StructUnionRegex.Match(declaration);
        if (!match.Success)
            return false;

        isUnion = match.Groups["kind"].Value.Equals("UNION", StringComparison.OrdinalIgnoreCase);
        return true;
    }

    /// <summary>Check if the declaration contains an enum body — parenthesised list.</summary>
    private static bool IsEnum(string declaration)
    {
        // An enum has TYPE Name : ( ... ) pattern — look for '(' after the header
        var headerMatch = TypeHeaderRegex.Match(declaration);
        if (!headerMatch.Success)
            return false;

        var afterHeader = declaration[headerMatch.Index..];
        return EnumBodyRegex.IsMatch(afterHeader);
    }

    // ════════════════════════════════════════════════════════════════════
    //  ENUM parsing
    // ════════════════════════════════════════════════════════════════════

    private static void ParseEnumValues(string declaration, TcDut dut)
    {
        // Name already set from XML attribute — do not override here.
        // Find the enum body between ( ... )
        var bodyMatch = EnumBodyRegex.Match(declaration);
        if (!bodyMatch.Success)
            return;

        // Base type after closing paren, e.g. (...) UDINT ;
        if (bodyMatch.Groups["basetype"].Success &&
            !string.IsNullOrWhiteSpace(bodyMatch.Groups["basetype"].Value))
        {
            dut.BaseType = bodyMatch.Groups["basetype"].Value.Trim();
        }

        var body = bodyMatch.Groups["body"].Value;

        // Parse each enum member
        foreach (Match m in EnumMemberRegex.Matches(body))
        {
            var name = m.Groups["name"].Value;
            if (string.IsNullOrWhiteSpace(name))
                continue;

            // Skip keywords that might match (e.g. END_TYPE)
            if (name.Equals("END_TYPE", StringComparison.OrdinalIgnoreCase))
                continue;

            var enumVal = new TcEnumValue
            {
                Name = name,
                Value = m.Groups["value"].Success && !string.IsNullOrWhiteSpace(m.Groups["value"].Value)
                    ? m.Groups["value"].Value.Trim()
                    : null,
                Comment = m.Groups["comment"].Success && !string.IsNullOrWhiteSpace(m.Groups["comment"].Value)
                    ? m.Groups["comment"].Value.Trim()
                    : null
            };

            dut.EnumValues.Add(enumVal);
        }
    }

    // ════════════════════════════════════════════════════════════════════
    //  STRUCT / UNION parsing
    // ════════════════════════════════════════════════════════════════════

    private static void ParseStructMembers(string declaration, TcDut dut)
    {
        // Name already set from XML attribute — do not override here.
        // Find the content between STRUCT/UNION and END_STRUCT/END_UNION
        var structStart = StructUnionRegex.Match(declaration);
        if (!structStart.Success)
            return;

        var endKeyword = dut.DutType == DutType.Union ? "END_UNION" : "END_STRUCT";
        var endIdx = declaration.IndexOf(endKeyword, structStart.Index + structStart.Length,
            StringComparison.OrdinalIgnoreCase);
        if (endIdx < 0)
            endIdx = declaration.Length;

        var memberBlock = declaration[(structStart.Index + structStart.Length)..endIdx];

        // Reuse variable parsing from TcPouParser — treat as a VAR block's contents.
        // Comment-aware walk: capture leading // and (* *) comments, attach to next member.
        var lines = memberBlock.Split('\n');
        bool inBlockComment = false;
        var pendingComments = new List<string>();

        for (int i = 0; i < lines.Length; i++)
        {
            var rawLine = lines[i];

            if (inBlockComment)
            {
                var closeIdx = rawLine.IndexOf("*)", StringComparison.Ordinal);
                if (closeIdx < 0)
                {
                    var midText = rawLine.Trim();
                    if (midText.Length > 0) pendingComments.Add(midText);
                    continue;
                }
                var tail = rawLine.Substring(0, closeIdx).Trim();
                if (tail.Length > 0) pendingComments.Add(tail);
                inBlockComment = false;
                var afterClose = rawLine.Substring(closeIdx + 2);
                if (string.IsNullOrWhiteSpace(afterClose)) continue;
                rawLine = afterClose;
            }

            var trimmed = rawLine.Trim();
            if (trimmed.Length == 0) continue;

            if (trimmed.StartsWith("//"))
            {
                pendingComments.Add(trimmed.Substring(2).Trim());
                continue;
            }

            if (trimmed.StartsWith("(*"))
            {
                var closeIdx = trimmed.IndexOf("*)", 2, StringComparison.Ordinal);
                if (closeIdx >= 0)
                {
                    var body = trimmed.Substring(2, closeIdx - 2).Trim();
                    if (body.Length > 0) pendingComments.Add(body);
                    var after = trimmed.Substring(closeIdx + 2).Trim();
                    if (after.Length == 0) continue;
                    trimmed = after;
                }
                else
                {
                    inBlockComment = true;
                    var head = trimmed.Substring(2).Trim();
                    if (head.Length > 0) pendingComments.Add(head);
                    continue;
                }
            }

            // Skip END_STRUCT / END_UNION / END_TYPE lines
            if (trimmed.StartsWith("END_", StringComparison.OrdinalIgnoreCase))
            {
                pendingComments.Clear();
                continue;
            }

            var leading = pendingComments.Count == 0
                ? null
                : string.Join("\n", pendingComments.Where(s => !string.IsNullOrWhiteSpace(s))).Trim();
            pendingComments.Clear();

            var variable = TcPouParser.ParseVariableLine(trimmed, VarScope.Local);
            if (variable != null)
            {
                if (!string.IsNullOrWhiteSpace(leading))
                    variable.LeadingComment = leading;
                dut.Members.Add(variable);
            }
        }
    }

    // ════════════════════════════════════════════════════════════════════
    //  Alias parsing
    // ════════════════════════════════════════════════════════════════════

    private static void ParseAlias(string declaration, TcDut dut)
    {
        // TYPE Name : ExistingType; END_TYPE
        // Name already set from XML attribute — do not override here.
        var headerMatch = TypeHeaderRegex.Match(declaration);
        if (!headerMatch.Success)
            return;

        // Everything between the colon and the semicolon / END_TYPE is the base type
        var afterColon = declaration[(headerMatch.Index + headerMatch.Length)..];
        var semiIdx = afterColon.IndexOf(';');
        if (semiIdx >= 0)
        {
            var aliasType = afterColon[..semiIdx].Trim();
            // Strip END_TYPE if accidentally captured
            aliasType = Regex.Replace(aliasType, @"\s*END_TYPE\s*$", "", RegexOptions.IgnoreCase).Trim();
            if (!string.IsNullOrWhiteSpace(aliasType))
                dut.BaseType = aliasType;
        }
    }

    // ════════════════════════════════════════════════════════════════════
    //  Utility helpers
    // ════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Strip (* ... *) block comments from a line, tracking state across lines.
    /// </summary>
    private static string StripBlockComments(string line, ref bool inBlock)
    {
        var result = new System.Text.StringBuilder(line.Length);
        for (int i = 0; i < line.Length; i++)
        {
            if (inBlock)
            {
                if (i < line.Length - 1 && line[i] == '*' && line[i + 1] == ')')
                {
                    inBlock = false;
                    i++;
                }
                continue;
            }

            if (i < line.Length - 1 && line[i] == '(' && line[i + 1] == '*')
            {
                inBlock = true;
                i++;
                continue;
            }

            result.Append(line[i]);
        }
        return result.ToString();
    }

    /// <summary>Compute a project-relative path for display.</summary>
    private static string GetRelativePath(string filePath, string basePath)
    {
        try
        {
            return Path.GetRelativePath(basePath, filePath).Replace('\\', '/');
        }
        catch
        {
            return filePath;
        }
    }
}
