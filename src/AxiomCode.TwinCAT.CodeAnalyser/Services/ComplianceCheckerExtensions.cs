using System.Text.RegularExpressions;
using AxiomCode.TwinCAT.CodeAnalyser.Models;

namespace AxiomCode.TwinCAT.CodeAnalyser.Services;

/// <summary>
/// Compliance checks ported from the standalone twincat-validator-mcp project
/// into the in-process C# pipeline. Adds five new <see cref="ComplianceStandard"/>
/// categories alongside the existing IEC/OOP/ISA-88/PackML/GAMP-5 checks:
///
///   - <see cref="ComplianceStandard.ArchitecturalIntegrity"/> — extends cycles,
///     override discipline, abstract contracts, dispatch consistency
///   - <see cref="ComplianceStandard.NamingConventions"/> — ISA-88 prefixes,
///     identifier casing, reserved words
///   - <see cref="ComplianceStandard.CodeStructure"/> — file ending, property
///     VAR blocks, generic structural rules
///   - <see cref="ComplianceStandard.CodeStyle"/> — tabs, excessive blank lines
///   - <see cref="ComplianceStandard.XmlIntegrity"/> — GUID format and
///     project-wide GUID uniqueness
///
/// The checks are deliberately written against the parsed <see cref="TcProject"/>
/// model wherever possible. Where raw XML is required (GUID, indentation),
/// the source is read on-demand from <c>pou.FilePath</c>.
/// </summary>
public static class ComplianceCheckerExtensions
{
    // ── Module-level: 5 new standards ─────────────────────────────────────────

    public static StandardCompliance CheckArchitectural(TcPou pou, TcProject project)
    {
        var rules = new List<ComplianceRule>
        {
            CheckExtendsCycle(pou, project),
            CheckOverrideSignature(pou, project),
            CheckOverrideSuperCall(pou),
            CheckAbstractContract(pou, project),
            CheckAbstractInstantiation(pou, project),
            CheckDiamondInheritance(pou, project),
            CheckPropertyAccessorPairing(pou),
            CheckCompositionDepth(pou, project),
            CheckMethodCount(pou),
            CheckInterfaceContract(pou, project),
        };
        return Std(ComplianceStandard.ArchitecturalIntegrity, "Architectural Integrity",
            "Inheritance, override discipline, abstract contracts, and dispatch consistency.",
            rules);
    }

    public static StandardCompliance CheckNamingConventions(TcPou pou)
    {
        var rules = new List<ComplianceRule>
        {
            CheckIsa88PrefixCasing(pou),
            CheckMemberNameCasing(pou),
            CheckReservedWordCollision(pou),
        };
        return Std(ComplianceStandard.NamingConventions, "Naming Conventions",
            "ISA-88 prefix casing, identifier casing, and reserved-word collisions.",
            rules);
    }

    public static StandardCompliance CheckCodeStructure(TcPou pou)
    {
        var rules = new List<ComplianceRule>
        {
            CheckFileEndingNewline(pou),
            CheckPropertyHasNoVarBlocks(pou),
        };
        return Std(ComplianceStandard.CodeStructure, "Code Structure",
            "Source-file structural conventions enforced by the TwinCAT XAE editor and " +
            "downstream tooling.",
            rules);
    }

    public static StandardCompliance CheckCodeStyle(TcPou pou)
    {
        var rules = new List<ComplianceRule>
        {
            CheckIndentationWithTabs(pou),
            CheckExcessiveBlankLines(pou),
        };
        return Std(ComplianceStandard.CodeStyle, "Code Style",
            "Indentation, blank-line discipline, and other source-style rules.",
            rules);
    }

    public static StandardCompliance CheckXmlIntegrity(TcPou pou)
    {
        var rules = new List<ComplianceRule>
        {
            CheckGuidFormat(pou),
        };
        return Std(ComplianceStandard.XmlIntegrity, "XML Integrity",
            "GUID format and XML structural integrity.",
            rules);
    }

    // ── Project-level GUID uniqueness — needs cross-POU view ──────────────────

    public static StandardCompliance CheckProjectGuidUniqueness(TcProject project)
    {
        var seen = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var pou in project.POUs.Values)
        {
            var raw = TryReadRaw(pou);
            if (raw == null) continue;
            foreach (Match m in GuidRefRegex.Matches(raw))
            {
                var g = m.Groups[1].Value;
                if (string.IsNullOrEmpty(g)) continue;
                if (!seen.TryGetValue(g, out var list))
                { list = new(); seen[g] = list; }
                list.Add(pou.Name);
            }
        }

        var dupes = seen.Where(kv => kv.Value.Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1)
                        .Take(10).ToList();
        var rules = new List<ComplianceRule>
        {
            dupes.Count == 0
                ? Rule("xml.guid_uniqueness", "GUIDs are unique project-wide", ComplianceLevel.Pass)
                : Rule("xml.guid_uniqueness", "GUIDs duplicated across POUs", ComplianceLevel.Fail,
                    string.Join("; ", dupes.Select(d =>
                        $"{d.Key} appears in: {string.Join(", ", d.Value.Distinct().Take(4))}"))),
        };
        return Std(ComplianceStandard.XmlIntegrity, "XML Integrity (project)",
            "Project-wide GUID uniqueness check.", rules);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // ARCHITECTURAL INTEGRITY CHECKS
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>Detects circular EXTENDS chains by walking the inheritance graph.</summary>
    private static ComplianceRule CheckExtendsCycle(TcPou pou, TcProject project)
    {
        if (string.IsNullOrEmpty(pou.ExtendsType))
            return Rule("oop.extends_cycle", "Inheritance chain is acyclic", ComplianceLevel.NotApplicable);

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { pou.Name };
        var current = pou.ExtendsType;
        var path = new List<string> { pou.Name };
        while (!string.IsNullOrEmpty(current))
        {
            path.Add(current);
            if (!seen.Add(current))
                return Rule("oop.extends_cycle",
                    "Cyclic EXTENDS hierarchy detected", ComplianceLevel.Fail,
                    "Cycle: " + string.Join(" → ", path));
            if (!project.POUs.TryGetValue(current, out var parent)) break;
            current = parent.ExtendsType;
        }
        return Rule("oop.extends_cycle",
            "Inheritance chain is acyclic", ComplianceLevel.Pass);
    }

    /// <summary>Methods that override a base must keep the same parameter signature.</summary>
    private static ComplianceRule CheckOverrideSignature(TcPou pou, TcProject project)
    {
        if (string.IsNullOrEmpty(pou.ExtendsType) || !project.POUs.TryGetValue(pou.ExtendsType, out var parent))
            return Rule("oop.override_signature", "Override signature matches base",
                ComplianceLevel.NotApplicable);

        var mismatches = new List<string>();
        foreach (var m in pou.Methods)
        {
            var pm = parent.Methods.FirstOrDefault(p => p.Name.Equals(m.Name, StringComparison.OrdinalIgnoreCase));
            if (pm == null) continue;
            if (pm.Parameters.Count != m.Parameters.Count)
            {
                mismatches.Add($"{m.Name} (params {pm.Parameters.Count} → {m.Parameters.Count})");
                continue;
            }
            for (int i = 0; i < m.Parameters.Count; i++)
            {
                if (!pm.Parameters[i].DataType.Equals(m.Parameters[i].DataType, StringComparison.OrdinalIgnoreCase))
                {
                    mismatches.Add($"{m.Name}.{m.Parameters[i].Name} type {pm.Parameters[i].DataType} → {m.Parameters[i].DataType}");
                    break;
                }
            }
            if (!string.Equals(m.ReturnType ?? "", pm.ReturnType ?? "", StringComparison.OrdinalIgnoreCase))
                mismatches.Add($"{m.Name} return {pm.ReturnType} → {m.ReturnType}");
        }

        return mismatches.Count == 0
            ? Rule("oop.override_signature", "Override signatures match base", ComplianceLevel.Pass)
            : Rule("oop.override_signature", "Override signature mismatch detected",
                ComplianceLevel.Fail, string.Join("; ", mismatches.Take(4)));
    }

    /// <summary>Override methods should call SUPER^ — flags missing calls as a warning.</summary>
    private static ComplianceRule CheckOverrideSuperCall(TcPou pou)
    {
        if (string.IsNullOrEmpty(pou.ExtendsType))
            return Rule("oop.override_super_call", "SUPER^ calls in overrides",
                ComplianceLevel.NotApplicable);

        // Heuristic: any method whose name matches a likely "_<state>" or "_Alarms" pattern
        // is a probable override. Missing SUPER^ in its body is a warning, not a hard fail.
        var suspect = pou.Methods.Where(m =>
            m.Name.StartsWith("_", StringComparison.Ordinal)
            && !string.IsNullOrEmpty(m.Body)
            && !m.Body.Contains("SUPER^", StringComparison.OrdinalIgnoreCase))
            .Take(5)
            .Select(m => m.Name)
            .ToList();

        return suspect.Count == 0
            ? Rule("oop.override_super_call",
                "Probable overrides call SUPER^ where applicable", ComplianceLevel.Pass)
            : Rule("oop.override_super_call",
                "Possible override missing SUPER^ call", ComplianceLevel.Warning,
                "Methods (heuristic): " + string.Join(", ", suspect));
    }

    /// <summary>Concrete derivations of an abstract base must realise its abstract methods.</summary>
    private static ComplianceRule CheckAbstractContract(TcPou pou, TcProject project)
    {
        if (pou.IsAbstract || string.IsNullOrEmpty(pou.ExtendsType))
            return Rule("oop.abstract_contract",
                "Abstract methods realised", ComplianceLevel.NotApplicable);

        if (!project.POUs.TryGetValue(pou.ExtendsType, out var parent) || !parent.IsAbstract)
            return Rule("oop.abstract_contract",
                "Abstract methods realised", ComplianceLevel.NotApplicable);

        // Extract method names declared abstract in the parent. The existing
        // analyser doesn't carry per-method abstract flags; use a regex over
        // RawDeclaration as a best-effort fallback.
        var abstractNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrEmpty(parent.RawImplementation))
        {
            foreach (Match m in AbstractMethodRegex.Matches(parent.RawImplementation))
                abstractNames.Add(m.Groups[1].Value);
        }
        if (!string.IsNullOrEmpty(parent.RawDeclaration))
        {
            foreach (Match m in AbstractMethodRegex.Matches(parent.RawDeclaration))
                abstractNames.Add(m.Groups[1].Value);
        }
        if (abstractNames.Count == 0)
            return Rule("oop.abstract_contract",
                "Abstract methods realised", ComplianceLevel.NotApplicable);

        var realised = new HashSet<string>(pou.Methods.Select(m => m.Name), StringComparer.OrdinalIgnoreCase);
        var missing = abstractNames.Where(a => !realised.Contains(a)).ToList();

        return missing.Count == 0
            ? Rule("oop.abstract_contract",
                $"All {abstractNames.Count} abstract method(s) of {parent.Name} realised", ComplianceLevel.Pass)
            : Rule("oop.abstract_contract",
                $"{missing.Count} abstract method(s) not realised in {pou.Name}",
                ComplianceLevel.Fail, "Missing: " + string.Join(", ", missing.Take(4)));
    }

    /// <summary>Variables declared as instances of an abstract POU type are illegal.</summary>
    private static ComplianceRule CheckAbstractInstantiation(TcPou pou, TcProject project)
    {
        var bad = new List<string>();
        foreach (var v in pou.Variables)
        {
            var t = v.DataType.TrimEnd('[', ']').Replace(" REF", "").Replace(" PTR", "").Trim();
            if (project.POUs.TryGetValue(t, out var target) && target.IsAbstract)
                bad.Add($"{v.Name}: {t}");
        }

        return bad.Count == 0
            ? Rule("oop.abstract_instantiation", "No instances of abstract types", ComplianceLevel.Pass)
            : Rule("oop.abstract_instantiation", "Variable declared as an abstract type",
                ComplianceLevel.Fail, string.Join("; ", bad.Take(4)));
    }

    /// <summary>Multiple paths to the same base via composition can cause dispatch ambiguity.</summary>
    private static ComplianceRule CheckDiamondInheritance(TcPou pou, TcProject project)
    {
        if ((pou.ImplementsList?.Count ?? 0) < 2)
            return Rule("oop.diamond_inheritance",
                "No diamond pattern", ComplianceLevel.NotApplicable);

        // For each pair of implemented interfaces, check whether they share a common ancestor in the project's interface tree.
        var interfaces = pou.ImplementsList ?? new List<string>();
        var ancestors = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var iface in interfaces)
        {
            ancestors[iface] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (project.POUs.TryGetValue(iface, out var ip))
            {
                var current = ip.ExtendsType;
                while (!string.IsNullOrEmpty(current) && ancestors[iface].Add(current))
                {
                    if (project.POUs.TryGetValue(current, out var p)) current = p.ExtendsType;
                    else break;
                }
            }
        }

        var sharedPairs = new List<string>();
        var keys = ancestors.Keys.ToList();
        for (int i = 0; i < keys.Count; i++)
        {
            for (int j = i + 1; j < keys.Count; j++)
            {
                var shared = ancestors[keys[i]].Intersect(ancestors[keys[j]], StringComparer.OrdinalIgnoreCase).ToList();
                if (shared.Count > 0)
                    sharedPairs.Add($"{keys[i]} ↔ {keys[j]} via {string.Join(",", shared)}");
            }
        }
        return sharedPairs.Count == 0
            ? Rule("oop.diamond_inheritance",
                "No diamond inheritance detected", ComplianceLevel.Pass)
            : Rule("oop.diamond_inheritance",
                "Diamond inheritance: shared ancestor across implemented interfaces",
                ComplianceLevel.Warning, string.Join("; ", sharedPairs.Take(3)));
    }

    /// <summary>Each property should have at least one of Get/Set; pure data properties are flagged.</summary>
    private static ComplianceRule CheckPropertyAccessorPairing(TcPou pou)
    {
        var orphans = pou.Properties
            .Where(p => !p.HasGetter && !p.HasSetter)
            .Select(p => p.Name)
            .ToList();
        if (pou.Properties.Count == 0)
            return Rule("oop.property_accessor_pairing",
                "No properties to pair", ComplianceLevel.NotApplicable);
        return orphans.Count == 0
            ? Rule("oop.property_accessor_pairing",
                $"All {pou.Properties.Count} properties have ≥1 accessor", ComplianceLevel.Pass)
            : Rule("oop.property_accessor_pairing",
                $"{orphans.Count} property/properties have neither Get nor Set",
                ComplianceLevel.Fail, string.Join(", ", orphans.Take(5)));
    }

    /// <summary>Excessive composition depth signals an overly nested object graph.</summary>
    private static ComplianceRule CheckCompositionDepth(TcPou pou, TcProject project)
    {
        const int Threshold = 6;
        int Depth(string typeName, int depth, HashSet<string> seen)
        {
            if (!seen.Add(typeName) || depth > 20) return depth;
            if (!project.POUs.TryGetValue(typeName, out var p)) return depth;
            int max = depth;
            foreach (var v in p.Variables.Take(40))
            {
                var t = v.DataType.TrimEnd('[', ']').Replace(" REF", "").Replace(" PTR", "").Trim();
                if (project.POUs.ContainsKey(t))
                    max = Math.Max(max, Depth(t, depth + 1, seen));
            }
            return max;
        }
        var maxDepth = Depth(pou.Name, 1, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        return maxDepth <= Threshold
            ? Rule("oop.composition_depth",
                $"Composition depth = {maxDepth} (threshold {Threshold})", ComplianceLevel.Pass)
            : Rule("oop.composition_depth",
                $"Composition depth {maxDepth} exceeds threshold {Threshold}",
                ComplianceLevel.Warning,
                "Consider flattening nested object structure for clarity.");
    }

    /// <summary>POUs with too many methods signal weak cohesion.</summary>
    private static ComplianceRule CheckMethodCount(TcPou pou)
    {
        const int Threshold = 30;
        var n = pou.Methods.Count;
        return n <= Threshold
            ? Rule("oop.method_count",
                $"Method count = {n} (threshold {Threshold})", ComplianceLevel.Pass)
            : Rule("oop.method_count",
                $"Method count {n} exceeds threshold {Threshold}",
                ComplianceLevel.Warning,
                "Consider splitting responsibilities across smaller cohesive POUs.");
    }

    /// <summary>Implemented interfaces must have all their methods realised in this POU.</summary>
    private static ComplianceRule CheckInterfaceContract(TcPou pou, TcProject project)
    {
        if ((pou.ImplementsList?.Count ?? 0) == 0)
            return Rule("oop.interface_contract",
                "Interface contracts honoured", ComplianceLevel.NotApplicable);

        var realised = new HashSet<string>(pou.Methods.Select(m => m.Name), StringComparer.OrdinalIgnoreCase);
        var missing = new List<string>();
        foreach (var iface in pou.ImplementsList ?? new List<string>())
        {
            if (!project.POUs.TryGetValue(iface, out var ifaceDef)) continue;
            foreach (var m in ifaceDef.Methods)
                if (!realised.Contains(m.Name))
                    missing.Add($"{iface}.{m.Name}");
        }
        return missing.Count == 0
            ? Rule("oop.interface_contract",
                "All implemented interface methods present", ComplianceLevel.Pass)
            : Rule("oop.interface_contract",
                $"{missing.Count} interface method(s) not implemented",
                ComplianceLevel.Fail, string.Join(", ", missing.Take(5)));
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // NAMING CONVENTION CHECKS
    // ═══════════════════════════════════════════════════════════════════════════

    private static ComplianceRule CheckIsa88PrefixCasing(TcPou pou)
    {
        var name = pou.Name;
        var hasPrefix = name.StartsWith("UM_", StringComparison.Ordinal)
                     || name.StartsWith("EM_", StringComparison.Ordinal)
                     || name.StartsWith("CM_", StringComparison.Ordinal)
                     || name.StartsWith("DM_", StringComparison.Ordinal)
                     || name.StartsWith("FB_", StringComparison.Ordinal)
                     || name.StartsWith("I_",  StringComparison.Ordinal)
                     || name.StartsWith("E_",  StringComparison.Ordinal)
                     || name.StartsWith("ST_", StringComparison.Ordinal);

        // Lowercase prefix is wrong: "um_machine" not "UM_Machine"
        var lowerPrefixWrong = LowerPrefixRegex.IsMatch(name);
        if (lowerPrefixWrong)
            return Rule("naming.isa88_prefix_casing",
                "ISA-88 prefix uses lowercase letters", ComplianceLevel.Fail,
                $"Found '{name}' — expected uppercase prefix (UM_/EM_/CM_/DM_/FB_).");
        if (!hasPrefix && pou.PouType == PouType.FunctionBlock)
            return Rule("naming.isa88_prefix_casing",
                "Function block has no recognised ISA-88 prefix",
                ComplianceLevel.Warning,
                $"'{name}' — consider UM_/EM_/CM_/DM_/FB_ prefix.");
        return Rule("naming.isa88_prefix_casing",
            "ISA-88 prefix casing acceptable", ComplianceLevel.Pass);
    }

    private static ComplianceRule CheckMemberNameCasing(TcPou pou)
    {
        // Local variable names (excluding parameters and Hungarian-style _x)
        // should generally start with a lowercase letter for instances and
        // PascalCase for type aliases. Flag obvious offenders only.
        var bad = new List<string>();
        foreach (var v in pou.Variables.Where(v => v.Scope == VarScope.Local))
        {
            if (v.Name.Length == 0) continue;
            var c = v.Name[0];
            if (!char.IsLetter(c) && c != '_') bad.Add(v.Name);
        }
        return bad.Count == 0
            ? Rule("naming.member_casing",
                "Local variable names use letter or underscore prefix", ComplianceLevel.Pass)
            : Rule("naming.member_casing",
                $"{bad.Count} variable(s) start with a non-letter, non-underscore character",
                ComplianceLevel.Warning, string.Join(", ", bad.Take(4)));
    }

    private static ComplianceRule CheckReservedWordCollision(TcPou pou)
    {
        var hits = new List<string>();
        foreach (var v in pou.Variables)
            if (StReserved.Contains(v.Name))
                hits.Add(v.Name);
        return hits.Count == 0
            ? Rule("naming.reserved_words",
                "No identifiers collide with IEC 61131-3 reserved words", ComplianceLevel.Pass)
            : Rule("naming.reserved_words",
                $"{hits.Count} identifier(s) collide with reserved words",
                ComplianceLevel.Fail, string.Join(", ", hits.Take(5)));
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // CODE STRUCTURE CHECKS
    // ═══════════════════════════════════════════════════════════════════════════

    private static ComplianceRule CheckFileEndingNewline(TcPou pou)
    {
        var raw = TryReadRaw(pou);
        if (raw == null)
            return Rule("structure.file_ending",
                "File ending newline", ComplianceLevel.NotApplicable);
        return raw.EndsWith('\n')
            ? Rule("structure.file_ending",
                ".TcPOU file ends with newline", ComplianceLevel.Pass)
            : Rule("structure.file_ending",
                ".TcPOU file does not end with newline", ComplianceLevel.Warning,
                $"Last 32 chars: '{Truncate(raw, 32, fromEnd: true)}'");
    }

    private static ComplianceRule CheckPropertyHasNoVarBlocks(TcPou pou)
    {
        // Properties (Get/Set accessors) should not contain VAR blocks at the
        // declaration level — those belong on the FB. Detect by scanning the raw
        // source for `<Property` followed by a `<Declaration>` containing VAR.
        var raw = TryReadRaw(pou);
        if (raw == null || pou.Properties.Count == 0)
            return Rule("structure.property_var_blocks",
                "Property VAR blocks", ComplianceLevel.NotApplicable);

        var bad = PropertyVarRegex.Matches(raw)
            .Select(m => m.Groups[1].Value)
            .ToList();
        return bad.Count == 0
            ? Rule("structure.property_var_blocks",
                "No VAR blocks inside properties", ComplianceLevel.Pass)
            : Rule("structure.property_var_blocks",
                $"{bad.Count} property/properties contain VAR blocks",
                ComplianceLevel.Warning, string.Join(", ", bad.Take(4)));
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // CODE STYLE CHECKS
    // ═══════════════════════════════════════════════════════════════════════════

    private static ComplianceRule CheckIndentationWithTabs(TcPou pou)
    {
        var raw = TryReadRaw(pou);
        if (raw == null)
            return Rule("style.indentation_tabs",
                "Indentation style", ComplianceLevel.NotApplicable);

        var lines = raw.Replace("\r\n", "\n").Split('\n');
        int spaceLeading = 0, tabLeading = 0;
        foreach (var line in lines)
        {
            if (line.Length == 0 || char.IsWhiteSpace(line[0]) == false) continue;
            if (line[0] == '\t') tabLeading++;
            else if (line[0] == ' ') spaceLeading++;
        }
        if (tabLeading == 0 && spaceLeading == 0)
            return Rule("style.indentation_tabs",
                "No indented lines", ComplianceLevel.NotApplicable);
        var ratio = spaceLeading / (double)(tabLeading + spaceLeading);
        return ratio < 0.10
            ? Rule("style.indentation_tabs",
                "Indentation predominantly uses tabs", ComplianceLevel.Pass)
            : Rule("style.indentation_tabs",
                $"Mixed indentation: {ratio:P0} of indented lines use leading spaces",
                ComplianceLevel.Warning,
                "TwinCAT XAE prefers tab indentation in CDATA-embedded ST.");
    }

    private static ComplianceRule CheckExcessiveBlankLines(TcPou pou)
    {
        var raw = TryReadRaw(pou);
        if (raw == null)
            return Rule("style.excessive_blank_lines",
                "Blank-line discipline", ComplianceLevel.NotApplicable);
        var matches = BlankLinesRegex.Matches(raw);
        return matches.Count == 0
            ? Rule("style.excessive_blank_lines",
                "No runs of >3 consecutive blank lines", ComplianceLevel.Pass)
            : Rule("style.excessive_blank_lines",
                $"{matches.Count} run(s) of more than three consecutive blank lines",
                ComplianceLevel.Warning,
                "Consider tightening vertical whitespace.");
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // XML / GUID CHECKS
    // ═══════════════════════════════════════════════════════════════════════════

    private static ComplianceRule CheckGuidFormat(TcPou pou)
    {
        var raw = TryReadRaw(pou);
        if (raw == null)
            return Rule("xml.guid_format",
                "GUID format", ComplianceLevel.NotApplicable);
        var bad = new List<string>();
        foreach (Match m in GuidLikeRegex.Matches(raw))
        {
            if (!ValidGuidRegex.IsMatch(m.Value)) bad.Add(m.Value);
        }
        return bad.Count == 0
            ? Rule("xml.guid_format",
                "All GUIDs match the canonical format", ComplianceLevel.Pass)
            : Rule("xml.guid_format",
                $"{bad.Count} malformed GUID(s) detected",
                ComplianceLevel.Fail, string.Join(", ", bad.Take(3)));
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static readonly Regex AbstractMethodRegex = new(
        @"(?im)^\s*METHOD\s+(?:PUBLIC\s+|PROTECTED\s+|PRIVATE\s+|INTERNAL\s+)?ABSTRACT\s+(\w+)",
        RegexOptions.Compiled);
    private static readonly Regex LowerPrefixRegex = new(
        @"^(um|em|cm|dm|fb|i|e|st)_[a-z]", RegexOptions.Compiled);
    private static readonly Regex GuidLikeRegex = new(
        @"\{[0-9a-fA-F\-]{20,}\}", RegexOptions.Compiled);
    private static readonly Regex ValidGuidRegex = new(
        @"^\{[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\}$",
        RegexOptions.Compiled);
    private static readonly Regex GuidRefRegex = new(
        @"\{([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})\}",
        RegexOptions.Compiled);
    private static readonly Regex BlankLinesRegex = new(
        @"(\r?\n[ \t]*){4,}", RegexOptions.Compiled);
    private static readonly Regex PropertyVarRegex = new(
        @"<Property\s+Name=""([^""]+)""[^>]*>\s*<Declaration><!\[CDATA\[[^\]]*\bVAR\b[^\]]*END_VAR",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly HashSet<string> StReserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "AND", "OR", "NOT", "XOR", "MOD", "TO", "IF", "THEN", "ELSE", "ELSIF",
        "END_IF", "CASE", "OF", "END_CASE", "FOR", "WHILE", "REPEAT", "UNTIL",
        "END_FOR", "END_WHILE", "END_REPEAT", "EXIT", "CONTINUE", "RETURN",
        "TRUE", "FALSE", "VAR", "VAR_INPUT", "VAR_OUTPUT", "VAR_IN_OUT",
        "VAR_GLOBAL", "VAR_TEMP", "VAR_STAT", "END_VAR", "FUNCTION",
        "FUNCTION_BLOCK", "PROGRAM", "METHOD", "PROPERTY", "INTERFACE",
        "EXTENDS", "IMPLEMENTS", "ABSTRACT", "FINAL", "PUBLIC", "PROTECTED",
        "PRIVATE", "INTERNAL", "SUPER", "THIS", "ARRAY", "OF",
    };

    private static string? TryReadRaw(TcPou pou)
    {
        if (string.IsNullOrEmpty(pou.FilePath)) return null;
        // FilePath is project-relative; we don't have the project root here, so
        // try common locations: the path as-given, then nothing if not present.
        try
        {
            if (File.Exists(pou.FilePath)) return File.ReadAllText(pou.FilePath);
        }
        catch { }
        // Fallback: synthesise raw from the parsed declaration + implementation
        // to keep style/structure checks operational even when the source path
        // can't be resolved (e.g. when running from cached analysis).
        if (string.IsNullOrEmpty(pou.RawDeclaration) && string.IsNullOrEmpty(pou.RawImplementation))
            return null;
        return (pou.RawDeclaration ?? "") + "\n" + (pou.RawImplementation ?? "");
    }

    private static string Truncate(string s, int n, bool fromEnd = false)
    {
        if (s.Length <= n) return s;
        return fromEnd ? s.Substring(s.Length - n) : s.Substring(0, n);
    }

    private static ComplianceRule Rule(string id, string desc, ComplianceLevel level, string? detail = null) =>
        new() { RuleId = id, Description = desc, Level = level, Detail = detail };

    private static StandardCompliance Std(ComplianceStandard std, string label, string desc, List<ComplianceRule> rules) =>
        new() { Standard = std, Label = label, Description = desc, Rules = rules };
}
