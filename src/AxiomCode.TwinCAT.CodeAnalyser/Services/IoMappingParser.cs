using System.Text.RegularExpressions;
using AxiomCode.TwinCAT.CodeAnalyser.Models;

namespace AxiomCode.TwinCAT.CodeAnalyser.Services;

/// <summary>
/// Extracts IO mappings (AT bindings) from all GVL and POU variables
/// and populates <see cref="TcProject.AllIoMappings"/>.
/// </summary>
public static class IoMappingParser
{
    // Matches the direction prefix of an AT binding:
    //   %I  => Input
    //   %Q  => Output
    //   %M  => Memory
    private static readonly Regex DirectionRegex = new(
        @"^%([IQM])",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Scan all GVL and POU variables for AT bindings and populate
    /// <see cref="TcProject.AllIoMappings"/>.
    /// </summary>
    public static void Extract(TcProject project)
    {
        project.AllIoMappings.Clear();

        // Scan GVL variables
        foreach (var gvl in project.GVLs.Values)
        {
            foreach (var variable in gvl.Variables)
            {
                if (string.IsNullOrWhiteSpace(variable.AtBinding))
                    continue;

                var mapping = CreateMapping(variable);
                mapping.SourceGvl = gvl.Name;
                mapping.SourcePou = null;

                project.AllIoMappings.Add(mapping);
            }
        }

        // Scan POU variables (including inherited via AllVariables)
        foreach (var pou in project.POUs.Values)
        {
            var variables = pou.AllVariables.Count > 0 ? pou.AllVariables : pou.Variables;

            foreach (var variable in variables)
            {
                if (string.IsNullOrWhiteSpace(variable.AtBinding))
                    continue;

                var mapping = CreateMapping(variable);
                mapping.SourceGvl = "";
                mapping.SourcePou = pou.Name;

                project.AllIoMappings.Add(mapping);
            }
        }
    }

    /// <summary>
    /// Create an <see cref="IoMapping"/> from a variable with an AT binding.
    /// </summary>
    private static IoMapping CreateMapping(TcVariable variable)
    {
        return new IoMapping
        {
            VariableName = variable.Name,
            DataType = variable.DataType,
            AtBinding = variable.AtBinding ?? "",
            Direction = ParseDirection(variable.AtBinding),
            Comment = variable.Comment
        };
    }

    /// <summary>
    /// Parse the IO direction from an AT binding string.
    /// %I* = Input, %Q* = Output, %M* = Memory.
    /// </summary>
    private static IoDirection ParseDirection(string? atBinding)
    {
        if (string.IsNullOrWhiteSpace(atBinding))
            return IoDirection.Unknown;

        var match = DirectionRegex.Match(atBinding);
        if (!match.Success)
            return IoDirection.Unknown;

        return match.Groups[1].Value.ToUpperInvariant() switch
        {
            "I" => IoDirection.Input,
            "Q" => IoDirection.Output,
            "M" => IoDirection.Memory,
            _ => IoDirection.Unknown
        };
    }
}
