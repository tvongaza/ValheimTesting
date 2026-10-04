using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Valheim.Testing.Bindings;

public enum BindingFindingKind
{
    /// <summary>The type is not in the checked assembly (nor forwarded from it).</summary>
    MissingType,
    /// <summary>No field of that name and type on the type or its base types: removed, renamed or retyped.</summary>
    MissingField,
    /// <summary>No method of that name and signature on the type or its base types: removed, renamed or changed.</summary>
    MissingMethod,
    /// <summary>The type exists but is not accessible from the mod.</summary>
    InaccessibleType,
    /// <summary>The field exists but is not accessible from the mod.</summary>
    InaccessibleField,
    /// <summary>The method exists but is not accessible from the mod.</summary>
    InaccessibleMethod,
}

/// <summary>One referenced type or member, with every place in the mod that uses it.</summary>
public sealed class BindingFinding
{
    public BindingFinding(BindingFindingKind kind, string assembly, string member, string? detail, bool accessDeclared, IReadOnlyList<string> usedBy)
    {
        Kind = kind; Assembly = assembly; Member = member; Detail = detail; AccessDeclared = accessDeclared; UsedBy = usedBy;
    }

    public BindingFindingKind Kind { get; }
    /// <summary>The checked assembly: where the reference was looked for, or where an inaccessible member is declared.</summary>
    public string Assembly { get; }
    /// <summary>The reference as the mod names it, for example <c>System.Void Terminal::InputText()</c> or <c>Terminal/Nested</c>.</summary>
    public string Member { get; }
    /// <summary>For a missing reference, what the checked assembly has instead, if anything; for an access finding, the member's access (<c>private</c>, say).</summary>
    public string? Detail { get; }
    /// <summary>For an access finding: the mod declares <c>IgnoresAccessChecksTo</c> for <see cref="Assembly"/>.</summary>
    public bool AccessDeclared { get; }
    /// <summary>Mod methods, fields, types or attributes that use the reference, such as <c>MyMod.Patches::Postfix(Terminal)</c>.</summary>
    public IReadOnlyList<string> UsedBy { get; }
    public bool IsMissing => Kind is BindingFindingKind.MissingType or BindingFindingKind.MissingField or BindingFindingKind.MissingMethod;
}

/// <summary>
/// An assembly the mod references and the number of distinct types and members it uses there: in
/// <see cref="BindingReport.Checked"/> a supplied one (with its <see cref="Path"/>), in <see cref="BindingReport.NotChecked"/>
/// one that was not supplied, so its references were not checked (no path).
/// </summary>
public sealed class ReferencedAssembly
{
    public ReferencedAssembly(string name, int references, string? path = null) { Name = name; References = references; Path = path; }
    public string Name { get; }
    public int References { get; }
    /// <summary>The supplied file that was checked; null for an assembly that was not supplied.</summary>
    public string? Path { get; }
}

public sealed class BindingReport
{
    public BindingReport(string modPath, string modAssembly, IReadOnlyList<BindingFinding> missing, IReadOnlyList<BindingFinding> access,
        IReadOnlyList<ReferencedAssembly> @checked, IReadOnlyList<ReferencedAssembly> notChecked, IReadOnlyList<string> missingRequired,
        IReadOnlyList<string> ignoresAccessChecksTo, IReadOnlyList<string> notes)
    {
        ModPath = modPath; ModAssembly = modAssembly; Missing = missing; Access = access; Checked = @checked; NotChecked = notChecked;
        MissingRequired = missingRequired; IgnoresAccessChecksTo = ignoresAccessChecksTo; Notes = notes;
    }

    public string ModPath { get; }
    public string ModAssembly { get; }
    /// <summary>References that would not bind at run time.</summary>
    public IReadOnlyList<BindingFinding> Missing { get; }
    /// <summary>References that bind but are not accessible from the mod. Expected for a mod built against publicized assemblies.</summary>
    public IReadOnlyList<BindingFinding> Access { get; }
    public IReadOnlyList<ReferencedAssembly> Checked { get; }
    public IReadOnlyList<ReferencedAssembly> NotChecked { get; }
    /// <summary>Required assemblies (see <see cref="BindingCheckOptions.RequiredAssemblies"/>) the mod references that were not supplied: the check is incomplete.</summary>
    public IReadOnlyList<string> MissingRequired { get; }
    /// <summary>Assemblies the mod names in <c>IgnoresAccessChecksTo</c> attributes.</summary>
    public IReadOnlyList<string> IgnoresAccessChecksTo { get; }
    /// <summary>Parts of the mod that could not be read completely, such as attribute arguments naming an enum in an unavailable assembly.</summary>
    public IReadOnlyList<string> Notes { get; }
    /// <summary>No missing references in the checked assemblies. Says nothing about <see cref="NotChecked"/> assemblies.</summary>
    public bool Binds => Missing.Count == 0;
    /// <summary>Access findings in assemblies the mod does not declare <c>IgnoresAccessChecksTo</c> for.</summary>
    public IEnumerable<BindingFinding> UndeclaredAccess => Access.Where(a => !a.AccessDeclared);

    /// <summary>Writes the report for a person or a CI log; at most <paramref name="maxUsers"/> users are listed per finding.</summary>
    public void WriteText(TextWriter output, int maxUsers = 10)
    {
        output.WriteLine($"Mod: {ModAssembly} ({ModPath})");
        foreach (ReferencedAssembly assembly in Checked)
            output.WriteLine($"Checked: {assembly.Name}, {assembly.References} references ({assembly.Path})");
        foreach (ReferencedAssembly assembly in NotChecked)
            output.WriteLine($"Not checked (not supplied): {assembly.Name}, {assembly.References} references");
        foreach (string required in MissingRequired)
            output.WriteLine($"INCOMPLETE: the mod references {required}, which was not supplied");
        foreach (BindingFinding finding in Missing)
        {
            output.WriteLine($"MISSING {Word(finding.Kind)} {finding.Member} in {finding.Assembly}");
            if (finding.Detail != null) output.WriteLine($"  instead: {finding.Detail}");
            WriteUsers(output, finding, maxUsers);
        }
        foreach (BindingFinding finding in Access)
        {
            string declared = finding.AccessDeclared
                ? $"the mod declares IgnoresAccessChecksTo(\"{finding.Assembly}\")"
                : $"the mod does not declare IgnoresAccessChecksTo(\"{finding.Assembly}\")";
            output.WriteLine($"ACCESS {(finding.AccessDeclared ? "info" : "warning")}: {Word(finding.Kind)} {finding.Member} is {finding.Detail} in {finding.Assembly}; {declared}");
            WriteUsers(output, finding, maxUsers);
        }
        foreach (string note in Notes) output.WriteLine("Note: " + note);
        int undeclared = UndeclaredAccess.Count();
        string access = Access.Count == 0 ? "no access findings" : $"{Access.Count} access findings ({undeclared} without IgnoresAccessChecksTo)";
        output.WriteLine(!Binds ? $"FAIL: {Missing.Count} missing references; {access}."
            : MissingRequired.Count > 0 ? $"INCOMPLETE: {string.Join(", ", MissingRequired)} not supplied; {access}."
            : $"PASS: every checked reference binds; {access}.");
    }

    private static void WriteUsers(TextWriter output, BindingFinding finding, int maxUsers)
    {
        foreach (string user in finding.UsedBy.Take(Math.Max(0, maxUsers))) output.WriteLine("  used by " + user);
        if (finding.UsedBy.Count > maxUsers) output.WriteLine($"  and {finding.UsedBy.Count - maxUsers} more");
    }

    private static string Word(BindingFindingKind kind) => kind switch
    {
        BindingFindingKind.MissingType or BindingFindingKind.InaccessibleType => "type",
        BindingFindingKind.MissingField or BindingFindingKind.InaccessibleField => "field",
        _ => "method",
    };
}
