using AxiomCode.TwinCAT.CodeAnalyser.Models;

namespace AxiomCode.TwinCAT.CodeAnalyser.Services;

/// <summary>
/// Resolves inheritance chains for all POUs in a project and
/// merges inherited variables into <see cref="TcPou.AllVariables"/>.
/// </summary>
public static class InheritanceResolver
{
    /// <summary>
    /// Walk the EXTENDS chain for every POU, populate InheritanceChain
    /// and AllVariables (merged from ancestors, child wins on conflict).
    /// </summary>
    public static void Resolve(TcProject project)
    {
        // Process each POU that has an EXTENDS clause
        foreach (var pou in project.POUs.Values)
        {
            ResolveChain(pou, project);
        }

        // For POUs with no EXTENDS, AllVariables is simply their own variables
        foreach (var pou in project.POUs.Values)
        {
            if (pou.AllVariables.Count == 0 && pou.InheritanceChain.Count == 0)
            {
                pou.AllVariables = new List<TcVariable>(pou.Variables);
            }
        }
    }

    /// <summary>
    /// Build the inheritance chain for a single POU, walking up through EXTENDS.
    /// Populates InheritanceChain (topmost ancestor first, self last)
    /// and AllVariables (merged from ancestors).
    /// </summary>
    private static void ResolveChain(TcPou pou, TcProject project)
    {
        // Already resolved
        if (pou.InheritanceChain.Count > 0 || pou.AllVariables.Count > 0)
            return;

        // No inheritance — nothing to resolve
        if (string.IsNullOrWhiteSpace(pou.ExtendsType))
        {
            pou.AllVariables = new List<TcVariable>(pou.Variables);
            return;
        }

        // Walk the chain upward, collecting ancestor names
        var chain = new List<string>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { pou.Name };
        var current = pou.ExtendsType;

        while (!string.IsNullOrWhiteSpace(current))
        {
            // Cycle detection
            if (visited.Contains(current))
                break;

            visited.Add(current);
            chain.Add(current);

            if (project.POUs.TryGetValue(current, out var parentPou))
            {
                // Ensure parent is resolved first (recursive)
                if (parentPou.AllVariables.Count == 0 && !string.IsNullOrWhiteSpace(parentPou.ExtendsType))
                {
                    ResolveChain(parentPou, project);
                }

                current = parentPou.ExtendsType;
            }
            else
            {
                // Unresolved parent type — record it and stop walking
                project.UnresolvedTypes.Add(current);
                break;
            }
        }

        // chain is [parent, grandparent, ...] — reverse to get topmost first
        chain.Reverse();

        // Store the chain: topmost ancestor first, does NOT include self
        pou.InheritanceChain = chain;

        // Build AllVariables: start from topmost ancestor, overlay each child's variables
        var mergedVars = new Dictionary<string, TcVariable>(StringComparer.OrdinalIgnoreCase);

        // Walk from the topmost resolved ancestor down to the POU itself
        foreach (var ancestorName in chain)
        {
            if (project.POUs.TryGetValue(ancestorName, out var ancestorPou))
            {
                MergeVariables(mergedVars, ancestorPou.Variables);
            }
        }

        // Finally overlay the POU's own variables (child wins on conflict)
        MergeVariables(mergedVars, pou.Variables);

        pou.AllVariables = mergedVars.Values.ToList();
    }

    /// <summary>
    /// Merge source variables into the target dictionary.
    /// Later entries overwrite earlier entries with the same name (child wins).
    /// </summary>
    private static void MergeVariables(Dictionary<string, TcVariable> target, List<TcVariable> source)
    {
        foreach (var variable in source)
        {
            target[variable.Name] = variable;
        }
    }
}
