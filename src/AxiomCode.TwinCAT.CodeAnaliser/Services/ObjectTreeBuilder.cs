using System.Text.RegularExpressions;
using AxiomCode.TwinCAT.CodeAnaliser.Models;

namespace AxiomCode.TwinCAT.CodeAnaliser.Services;

/// <summary>
/// Builds the ISA-88 object tree from GVL root instances, recursively
/// expanding FUNCTION_BLOCK type variables into child nodes.
/// </summary>
public static class ObjectTreeBuilder
{
    private const int MaxDepth = 20;

    // Regex to extract the first quoted string argument from constructor args
    // e.g. "'Lane A', TRUE, 42" => "Lane A"
    private static readonly Regex FirstStringArgRegex = new(
        @"'([^']*)'",
        RegexOptions.Compiled);

    /// <summary>
    /// Build the object tree for the project, starting from the "Objects" GVL.
    /// Populates <see cref="TcProject.ObjectTree"/>.
    /// </summary>
    public static void Build(TcProject project)
    {
        project.ObjectTree.Clear();

        // Find root GVLs: named "Objects" or containing "Objects"
        var rootGvls = project.GVLs.Values
            .Where(g => g.Name.Equals("Objects", StringComparison.OrdinalIgnoreCase) ||
                        g.Name.Contains("Objects", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (rootGvls.Count == 0)
            return;

        // Build a lookup of POU types that are FUNCTION_BLOCKs
        var fbPous = new Dictionary<string, TcPou>(StringComparer.OrdinalIgnoreCase);
        foreach (var pou in project.POUs.Values)
        {
            if (pou.PouType == PouType.FunctionBlock)
                fbPous[pou.Name] = pou;
        }

        foreach (var gvl in rootGvls)
        {
            foreach (var variable in gvl.Variables)
            {
                // Only expand variables whose type is a known FB
                if (!fbPous.TryGetValue(variable.DataType, out var pou))
                    continue;

                var rootPath = $"S0_{gvl.Name}.{variable.Name}";
                var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                var node = BuildNode(
                    variable,
                    pou,
                    rootPath,
                    fbPous,
                    project,
                    visited,
                    depth: 0);

                project.ObjectTree.Add(node);
            }
        }
    }

    /// <summary>
    /// Recursively build an <see cref="ObjectTreeNode"/> for a variable instance.
    /// </summary>
    private static ObjectTreeNode BuildNode(
        TcVariable variable,
        TcPou pou,
        string fullPath,
        Dictionary<string, TcPou> fbPous,
        TcProject project,
        HashSet<string> visited,
        int depth)
    {
        var node = new ObjectTreeNode
        {
            InstanceName = variable.Name,
            TypeName = pou.Name,
            FullPath = fullPath,
            IsReference = variable.IsReference,
            DisplayName = ExtractDisplayName(variable.ConstructorArgs),
            Layer = ResolveLayer(pou.Name),
            ExtendsChain = pou.InheritanceChain.Count > 0
                ? string.Join(" -> ", pou.InheritanceChain.Append(pou.Name))
                : pou.Name,
            HasAlarmsMethod = pou.Methods.Any(m =>
                m.Name.Equals("_Alarms", StringComparison.OrdinalIgnoreCase) ||
                m.Name.Equals("Alarms", StringComparison.OrdinalIgnoreCase))
        };

        // If this is a reference, do NOT recurse into children
        if (variable.IsReference)
            return node;

        // Depth guard
        if (depth >= MaxDepth)
            return node;

        // Cycle detection using full path
        if (!visited.Add(fullPath))
            return node;

        // Use AllVariables (includes inherited) to find child FBs
        var childVariables = pou.AllVariables.Count > 0 ? pou.AllVariables : pou.Variables;

        foreach (var childVar in childVariables)
        {
            if (!fbPous.TryGetValue(childVar.DataType, out var childPou))
                continue;

            var childPath = $"{fullPath}.{childVar.Name}";

            if (childVar.IsReference)
            {
                // References: create leaf node, do NOT recurse
                var refNode = new ObjectTreeNode
                {
                    InstanceName = childVar.Name,
                    TypeName = childPou.Name,
                    FullPath = childPath,
                    IsReference = true,
                    DisplayName = ExtractDisplayName(childVar.ConstructorArgs),
                    Layer = ResolveLayer(childPou.Name),
                    ExtendsChain = childPou.InheritanceChain.Count > 0
                        ? string.Join(" -> ", childPou.InheritanceChain.Append(childPou.Name))
                        : childPou.Name,
                    HasAlarmsMethod = childPou.Methods.Any(m =>
                        m.Name.Equals("Alarms", StringComparison.OrdinalIgnoreCase) ||
                        m.Name.Equals("M_Alarms", StringComparison.OrdinalIgnoreCase))
                };
                node.Children.Add(refNode);
            }
            else
            {
                // Owned instance: recurse
                var childNode = BuildNode(
                    childVar,
                    childPou,
                    childPath,
                    fbPous,
                    project,
                    visited,
                    depth + 1);

                node.Children.Add(childNode);
            }
        }

        // Remove from visited after processing to allow the same type
        // in different branches (sibling paths are fine, ancestor paths are cycles)
        visited.Remove(fullPath);

        return node;
    }

    /// <summary>
    /// Extract a display name from constructor args (first string literal).
    /// e.g. "'Lane A', TRUE" => "Lane A"
    /// </summary>
    private static string? ExtractDisplayName(string? constructorArgs)
    {
        if (string.IsNullOrWhiteSpace(constructorArgs))
            return null;

        var match = FirstStringArgRegex.Match(constructorArgs);
        return match.Success ? match.Groups[1].Value : null;
    }

    /// <summary>
    /// Determine the ISA-88 layer from the POU type name prefix.
    /// </summary>
    private static IsaLayer ResolveLayer(string typeName)
    {
        if (typeName.StartsWith("UM_", StringComparison.OrdinalIgnoreCase))
            return IsaLayer.UM;
        if (typeName.StartsWith("EM_", StringComparison.OrdinalIgnoreCase))
            return IsaLayer.EM;
        if (typeName.StartsWith("CM_", StringComparison.OrdinalIgnoreCase))
            return IsaLayer.CM;
        if (typeName.StartsWith("DM_", StringComparison.OrdinalIgnoreCase))
            return IsaLayer.DM;

        return IsaLayer.Other;
    }
}
