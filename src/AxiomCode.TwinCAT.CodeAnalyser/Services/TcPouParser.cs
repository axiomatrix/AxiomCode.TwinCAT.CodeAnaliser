using System.Text.RegularExpressions;
using System.Xml.Linq;
using AxiomCode.TwinCAT.CodeAnalyser.Models;

namespace AxiomCode.TwinCAT.CodeAnalyser.Services;

/// <summary>
/// Parses TwinCAT 3 .TcPOU XML files into <see cref="TcPou"/> model objects.
/// Handles FUNCTION_BLOCK, PROGRAM, FUNCTION, and INTERFACE declarations
/// including variable blocks, methods, and properties.
/// </summary>
public static class TcPouParser
{
    // ── POU header regex ────────────────────────────────────────────────
    // Matches: [ABSTRACT] FUNCTION_BLOCK|PROGRAM|FUNCTION|INTERFACE Name [EXTENDS Base] [IMPLEMENTS I1, I2]
    private static readonly Regex HeaderRegex = new(
        @"^\s*(?<abstract>ABSTRACT\s+)?" +
        @"(?<type>FUNCTION_BLOCK|PROGRAM|FUNCTION|INTERFACE)\s+" +
        @"(?<name>\w+)" +
        @"(?:\s+EXTENDS\s+(?<extends>\w+))?" +
        @"(?:\s+IMPLEMENTS\s+(?<implements>.+?))?$",
        RegexOptions.Compiled | RegexOptions.Multiline | RegexOptions.IgnoreCase);

    // ── Function return type: FUNCTION Name : ReturnType ────────────────
    private static readonly Regex FunctionReturnRegex = new(
        @"^\s*FUNCTION\s+\w+\s*:\s*(?<ret>\S+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // ── VAR block start ─────────────────────────────────────────────────
    private static readonly Regex VarBlockStartRegex = new(
        @"^\s*VAR(?:_(?<suffix>INPUT|OUTPUT|IN_OUT|STAT|TEMP|GLOBAL))?" +
        @"(?:\s+(?<qual>CONSTANT|PERSISTENT|RETAIN))?\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // ── Method declaration header ───────────────────────────────────────
    private static readonly Regex MethodHeaderRegex = new(
        @"^\s*METHOD\s+(?:(?<vis>PUBLIC|PROTECTED|PRIVATE|INTERNAL)\s+)?" +
        @"(?:(?<abstract>ABSTRACT)\s+)?" +
        @"(?<name>\w+)" +
        @"(?:\s*:\s*(?<ret>\S+))?\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // ── Property declaration header ─────────────────────────────────────
    private static readonly Regex PropertyHeaderRegex = new(
        @"^\s*PROPERTY\s+(?:(?<vis>PUBLIC|PROTECTED|PRIVATE|INTERNAL)\s+)?" +
        @"(?<name>\w+)" +
        @"\s*:\s*(?<type>.+?)\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Parse a .TcPOU file and return a <see cref="TcPou"/> or null on failure.
    /// </summary>
    /// <param name="filePath">Absolute path to the .TcPOU file.</param>
    /// <param name="projectBasePath">Project root for relative path computation.</param>
    public static TcPou? Parse(string filePath, string projectBasePath)
    {
        try
        {
            var doc = XDocument.Load(filePath);
            var pouElement = doc.Root?.Element("POU");
            if (pouElement == null)
                return null;

            var pou = new TcPou
            {
                Name = pouElement.Attribute("Name")?.Value ?? Path.GetFileNameWithoutExtension(filePath),
                FilePath = GetRelativePath(filePath, projectBasePath)
            };

            // ── Declaration ─────────────────────────────────────────
            var declElement = pouElement.Element("Declaration");
            var declaration = ExtractCdata(declElement);
            pou.RawDeclaration = declaration;

            if (!string.IsNullOrWhiteSpace(declaration))
            {
                ParsePouHeader(declaration, pou);
                pou.Variables.AddRange(ParseVarBlocks(declaration));
            }

            // ── Implementation ──────────────────────────────────────
            var implElement = pouElement.Element("Implementation")?.Element("ST");
            pou.RawImplementation = ExtractCdata(implElement);

            // ── Methods ─────────────────────────────────────────────
            foreach (var methodEl in pouElement.Elements("Method"))
            {
                var method = ParseMethod(methodEl);
                if (method != null)
                    pou.Methods.Add(method);
            }

            // ── Properties ──────────────────────────────────────────
            foreach (var propEl in pouElement.Elements("Property"))
            {
                var prop = ParseProperty(propEl);
                if (prop != null)
                    pou.Properties.Add(prop);
            }

            return pou;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[TcPouParser] Failed to parse '{filePath}': {ex.Message}");
            return null;
        }
    }

    // ════════════════════════════════════════════════════════════════════
    //  POU header parsing
    // ════════════════════════════════════════════════════════════════════

    private static void ParsePouHeader(string declaration, TcPou pou)
    {
        var match = HeaderRegex.Match(declaration);
        if (!match.Success)
            return;

        pou.IsAbstract = match.Groups["abstract"].Success;

        pou.PouType = match.Groups["type"].Value.ToUpperInvariant() switch
        {
            "FUNCTION_BLOCK" => PouType.FunctionBlock,
            "PROGRAM" => PouType.Program,
            "FUNCTION" => PouType.Function,
            "INTERFACE" => PouType.Interface,
            _ => PouType.FunctionBlock
        };

        pou.Name = match.Groups["name"].Value;

        if (match.Groups["extends"].Success)
            pou.ExtendsType = match.Groups["extends"].Value;

        if (match.Groups["implements"].Success)
        {
            pou.ImplementsList = match.Groups["implements"].Value
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList();
        }
    }

    // ════════════════════════════════════════════════════════════════════
    //  VAR block parsing (shared with TcDutParser via internal access)
    // ════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Parse all VAR blocks from a declaration string and return the variables found.
    /// </summary>
    internal static List<TcVariable> ParseVarBlocks(string declaration)
    {
        var variables = new List<TcVariable>();
        var lines = declaration.Split('\n');

        VarScope? currentScope = null;
        bool inBlockComment = false;

        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];

            // Track block comments across lines
            line = StripBlockComments(line, ref inBlockComment);
            if (inBlockComment)
                continue;

            var trimmed = line.Trim();

            // Skip empty / comment-only lines
            if (string.IsNullOrWhiteSpace(trimmed) || trimmed.StartsWith("//"))
                continue;

            // Check for END_VAR
            if (trimmed.StartsWith("END_VAR", StringComparison.OrdinalIgnoreCase))
            {
                currentScope = null;
                continue;
            }

            // Check for VAR block start
            var varMatch = VarBlockStartRegex.Match(trimmed);
            if (varMatch.Success)
            {
                currentScope = ResolveScope(
                    varMatch.Groups["suffix"].Value,
                    varMatch.Groups["qual"].Value);
                continue;
            }

            // Parse variable line if inside a block
            if (currentScope.HasValue)
            {
                var variable = ParseVariableLine(trimmed, currentScope.Value);
                if (variable != null)
                    variables.Add(variable);
            }
        }

        return variables;
    }

    /// <summary>
    /// Parse a single variable declaration line.
    /// Handles: simple, init, reference, pointer, array, AT binding, FB constructors, comments.
    /// </summary>
    internal static TcVariable? ParseVariableLine(string line, VarScope scope)
    {
        // Strip trailing inline comment
        string? comment = null;
        var commentIdx = FindInlineComment(line);
        if (commentIdx >= 0)
        {
            comment = line[commentIdx..].TrimStart('/', ' ', '\t');
            // Also strip (* ... *) style
            if (comment.StartsWith("(*"))
                comment = comment[2..];
            if (comment.EndsWith("*)"))
                comment = comment[..^2];
            comment = comment.Trim();
            line = line[..commentIdx].TrimEnd();
        }

        // Remove trailing semicolon
        line = line.TrimEnd().TrimEnd(';').TrimEnd();

        if (string.IsNullOrWhiteSpace(line))
            return null;

        // Split on first colon — name part : type part
        var colonIdx = line.IndexOf(':');
        if (colonIdx < 0)
            return null;

        var namePart = line[..colonIdx].Trim();
        var typePart = line[(colonIdx + 1)..].Trim();

        // ── Extract AT binding from name part ───────────────────────
        string? atBinding = null;
        var atMatch = Regex.Match(namePart, @"^(\w+)\s+AT\s+(%[IQM]\*?(?:\.\d+)*)", RegexOptions.IgnoreCase);
        string varName;
        if (atMatch.Success)
        {
            varName = atMatch.Groups[1].Value;
            atBinding = atMatch.Groups[2].Value;
        }
        else
        {
            varName = namePart;
        }

        // Validate variable name
        if (!Regex.IsMatch(varName, @"^\w+$"))
            return null;

        var variable = new TcVariable
        {
            Name = varName,
            Scope = scope,
            AtBinding = atBinding,
            Comment = string.IsNullOrEmpty(comment) ? null : comment
        };

        // ── Parse type part ─────────────────────────────────────────
        ParseTypePart(typePart, variable);

        return variable;
    }

    /// <summary>
    /// Parse the type portion of a variable declaration, handling REFERENCE TO,
    /// POINTER TO, ARRAY, initial values, and FB constructor arguments.
    /// </summary>
    private static void ParseTypePart(string typePart, TcVariable variable)
    {
        var remaining = typePart;

        // ── REFERENCE TO ────────────────────────────────────────────
        if (remaining.StartsWith("REFERENCE TO ", StringComparison.OrdinalIgnoreCase))
        {
            variable.IsReference = true;
            remaining = remaining[13..].TrimStart();
        }
        // ── POINTER TO ──────────────────────────────────────────────
        else if (remaining.StartsWith("POINTER TO ", StringComparison.OrdinalIgnoreCase))
        {
            variable.IsPointer = true;
            remaining = remaining[11..].TrimStart();
        }

        // ── ARRAY[bounds] OF Type ───────────────────────────────────
        var arrayMatch = Regex.Match(remaining,
            @"^ARRAY\s*\[(?<bounds>.+?)\]\s+OF\s+(?<type>.+)$",
            RegexOptions.IgnoreCase);
        if (arrayMatch.Success)
        {
            variable.IsArray = true;
            variable.ArrayBounds = arrayMatch.Groups["bounds"].Value.Trim();
            remaining = arrayMatch.Groups["type"].Value.Trim();
        }

        // ── Initial value (split on :=) ─────────────────────────────
        // Must handle balanced parentheses for FB constructors:
        //   Type(arg1, arg2)        → constructor, no init value
        //   Type := value           → init value
        //   Type := FB(arg1, arg2)  → init value with parens

        var assignIdx = FindAssignment(remaining);
        if (assignIdx >= 0)
        {
            variable.InitialValue = remaining[(assignIdx + 2)..].Trim();
            remaining = remaining[..assignIdx].Trim();
        }

        // ── FB constructor args: Type(args) ─────────────────────────
        // Only if no := was found (i.e. the parens are part of construction, not init)
        var ctorStart = remaining.IndexOf('(');
        if (ctorStart > 0 && assignIdx < 0)
        {
            var ctorEnd = FindClosingParen(remaining, ctorStart);
            if (ctorEnd > ctorStart)
            {
                variable.ConstructorArgs = remaining[(ctorStart + 1)..ctorEnd].Trim();
                remaining = remaining[..ctorStart].Trim();
            }
        }

        variable.DataType = remaining.Trim();
    }

    // ════════════════════════════════════════════════════════════════════
    //  Method parsing
    // ════════════════════════════════════════════════════════════════════

    private static TcMethod? ParseMethod(XElement methodEl)
    {
        var name = methodEl.Attribute("Name")?.Value;
        if (string.IsNullOrEmpty(name))
            return null;

        var declaration = ExtractCdata(methodEl.Element("Declaration"));
        var body = ExtractCdata(methodEl.Element("Implementation")?.Element("ST"));

        var method = new TcMethod
        {
            Name = name,
            FolderPath = methodEl.Attribute("FolderPath")?.Value,
            RawDeclaration = declaration,
            Body = body
        };

        // Parse header line for visibility and return type
        if (!string.IsNullOrWhiteSpace(declaration))
        {
            var headerMatch = MethodHeaderRegex.Match(declaration);
            if (headerMatch.Success)
            {
                if (headerMatch.Groups["vis"].Success)
                {
                    method.Visibility = headerMatch.Groups["vis"].Value.ToUpperInvariant() switch
                    {
                        "PROTECTED" => Visibility.Protected,
                        "PRIVATE" => Visibility.Private,
                        "INTERNAL" => Visibility.Internal,
                        _ => Visibility.Public
                    };
                }

                if (headerMatch.Groups["ret"].Success)
                    method.ReturnType = headerMatch.Groups["ret"].Value;
            }

            // Parse VAR blocks inside the method declaration
            var allVars = ParseVarBlocks(declaration);
            foreach (var v in allVars)
            {
                if (v.Scope is VarScope.Input or VarScope.Output or VarScope.InOut)
                    method.Parameters.Add(v);
                else
                    method.LocalVars.Add(v);
            }
        }

        return method;
    }

    // ════════════════════════════════════════════════════════════════════
    //  Property parsing
    // ════════════════════════════════════════════════════════════════════

    private static TcProperty? ParseProperty(XElement propEl)
    {
        var name = propEl.Attribute("Name")?.Value;
        if (string.IsNullOrEmpty(name))
            return null;

        var declaration = ExtractCdata(propEl.Element("Declaration"));

        var prop = new TcProperty
        {
            Name = name,
            FolderPath = propEl.Attribute("FolderPath")?.Value,
            RawDeclaration = declaration
        };

        // Parse property type from declaration
        if (!string.IsNullOrWhiteSpace(declaration))
        {
            var headerMatch = PropertyHeaderRegex.Match(declaration);
            if (headerMatch.Success)
                prop.DataType = headerMatch.Groups["type"].Value.Trim();
        }

        // Getter
        var getEl = propEl.Element("Get");
        if (getEl != null)
        {
            prop.HasGetter = true;
            prop.GetBody = ExtractCdata(getEl.Element("Implementation")?.Element("ST"));
        }

        // Setter
        var setEl = propEl.Element("Set");
        if (setEl != null)
        {
            prop.HasSetter = true;
            prop.SetBody = ExtractCdata(setEl.Element("Implementation")?.Element("ST"));
        }

        return prop;
    }

    // ════════════════════════════════════════════════════════════════════
    //  Utility helpers
    // ════════════════════════════════════════════════════════════════════

    /// <summary>Extract text from a CDATA-bearing XML element.</summary>
    internal static string ExtractCdata(XElement? element)
    {
        if (element == null) return "";
        // XElement.Value already unwraps CDATA
        return element.Value.Replace("\r\n", "\n").Replace("\r", "\n");
    }

    /// <summary>Resolve VAR block suffix/qualifier to <see cref="VarScope"/>.</summary>
    private static VarScope ResolveScope(string suffix, string qualifier)
    {
        // Check qualifier first
        if (!string.IsNullOrEmpty(qualifier))
        {
            var q = qualifier.ToUpperInvariant();
            if (q == "CONSTANT") return VarScope.Constant;
            if (q == "PERSISTENT") return VarScope.Persistent;
        }

        // Then suffix
        return suffix.ToUpperInvariant() switch
        {
            "INPUT" => VarScope.Input,
            "OUTPUT" => VarScope.Output,
            "IN_OUT" => VarScope.InOut,
            "STAT" => VarScope.Stat,
            "TEMP" => VarScope.Temp,
            _ => VarScope.Local
        };
    }

    /// <summary>
    /// Find the index of an inline comment (// or (*) that is not inside a string literal.
    /// Returns -1 if no comment found.
    /// </summary>
    private static int FindInlineComment(string line)
    {
        bool inString = false;
        for (int i = 0; i < line.Length - 1; i++)
        {
            if (line[i] == '\'')
            {
                inString = !inString;
                continue;
            }
            if (inString) continue;

            if (line[i] == '/' && line[i + 1] == '/')
                return i;
            if (line[i] == '(' && line[i + 1] == '*')
                return i;
        }
        return -1;
    }

    /// <summary>
    /// Find the := assignment operator, skipping those inside parentheses (FB constructor args).
    /// Returns the index of ':' in ':=' or -1.
    /// </summary>
    private static int FindAssignment(string text)
    {
        int depth = 0;
        for (int i = 0; i < text.Length - 1; i++)
        {
            if (text[i] == '(') depth++;
            else if (text[i] == ')') depth--;
            else if (depth == 0 && text[i] == ':' && text[i + 1] == '=')
                return i;
        }
        return -1;
    }

    /// <summary>
    /// Find the closing parenthesis matching the opening one at <paramref name="openIdx"/>.
    /// Returns the index of ')' or -1.
    /// </summary>
    private static int FindClosingParen(string text, int openIdx)
    {
        int depth = 0;
        for (int i = openIdx; i < text.Length; i++)
        {
            if (text[i] == '(') depth++;
            else if (text[i] == ')')
            {
                depth--;
                if (depth == 0) return i;
            }
        }
        return -1;
    }

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
                    i++; // skip ')'
                }
                continue;
            }

            if (i < line.Length - 1 && line[i] == '(' && line[i + 1] == '*')
            {
                inBlock = true;
                i++; // skip '*'
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
