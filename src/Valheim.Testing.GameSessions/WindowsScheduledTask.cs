using System.Globalization;
using System.Text;
using Valheim.Testing.Game;

namespace Valheim.Testing.GameSessions;

/// <summary>An exact Windows Task Scheduler definition journalled by a run and removable by its recovery.</summary>
internal sealed record WindowsScheduledTask(string Label, string Description, string Executable, string Launcher, string WorkingDirectory,
    string PrincipalUserId)
{
    internal const string Prefix = "ValheimTesting-detached-";
    private const int ActionType = 0, ActionsCount = 1, LogonType = 3, RunLevel = 0, TriggersCount = 0;
    private const string ExecutionTimeLimit = "PT0S";
    internal string Arguments => "-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -File \"" + Launcher + "\"";

    private (string Key, string Value)[] OwnershipFields() =>
    [
        ("task", Label), ("description", Description), ("principalUserId", PrincipalUserId),
        ("principalLogonType", Text(LogonType)), ("principalRunLevel", Text(RunLevel)),
        ("actionsCount", Text(ActionsCount)), ("actionType", Text(ActionType)), ("executable", Executable),
        ("launcher", Launcher), ("workingDirectory", WorkingDirectory), ("arguments", Arguments), ("triggersCount", Text(TriggersCount)),
        ("allowDemandStart", "true"), ("disallowStartIfOnBatteries", "false"), ("enabled", "true"),
        ("executionTimeLimit", ExecutionTimeLimit), ("hidden", "true"), ("stopIfGoingOnBatteries", "false"),
    ];

    internal JournalEntry Entry(string kind, params (string Key, string Value)[] more) =>
        JournalEntry.Of(kind, [.. OwnershipFields(), .. more]);

    internal static WindowsScheduledTask FromJournal(IReadOnlyDictionary<string, string> fields, string run)
    {
        var task = new WindowsScheduledTask(fields.GetValueOrDefault("task") ?? "", fields.GetValueOrDefault("description") ?? "",
            fields.GetValueOrDefault("executable") ?? "", fields.GetValueOrDefault("launcher") ?? "",
            fields.GetValueOrDefault("workingDirectory") ?? "", fields.GetValueOrDefault("principalUserId") ?? "");
        bool exact = task.OwnershipFields().All(field => fields.GetValueOrDefault(field.Key) == field.Value);
        if (!RunJournal.SafeName(run) || task.Label != Prefix + run || task.Description != "ValheimTesting detached run " + run ||
            task.Executable.Length == 0 || task.Launcher.Length == 0 || task.WorkingDirectory.Length == 0 || task.PrincipalUserId.Length == 0 || !exact)
            throw new InvalidDataException("the journal does not name this run's exact detached scheduled task");
        return task;
    }

    internal enum State { Missing, Running, Stopped, NotStarted, Mismatch }
    internal sealed record RegistrationContext(string Executable, string PrincipalUserId);

    internal static async Task<RegistrationContext> FindRegistrationContextAsync(IGameHost host, TimeSpan timeout,
        CancellationToken cancellation = default)
    {
        var result = (await host.RunAsync(RegistrationContextScript, new Dictionary<string, string>(), timeout, cancellation).ConfigureAwait(false))
            .EnsureSuccess($"Finding PowerShell and its identity on {host.Name}");
        string executable = InteractiveClient.Line(result.Stdout, "VT-PWSH ") ?? "";
        string encodedIdentity = InteractiveClient.Line(result.Stdout, "VT-IDENTITY ") ?? "";
        string identity;
        try { identity = Encoding.UTF8.GetString(Convert.FromBase64String(encodedIdentity)); }
        catch (FormatException) { throw new HostOperationException($"PowerShell on {host.Name} returned an invalid identity", result); }
        if (executable.Length == 0 || executable.IndexOfAny(['\r', '\n']) >= 0 || identity.Length == 0 || identity.IndexOfAny(['\r', '\n']) >= 0)
            throw new HostOperationException($"PowerShell on {host.Name} did not identify its executable and user", result);
        return new(executable, identity);
    }

    internal async Task RegisterAsync(IGameHost host, TimeSpan timeout, CancellationToken cancellation = default)
    {
        var result = (await host.RunAsync(RegisterScript, Variables(), timeout, cancellation).ConfigureAwait(false))
            .EnsureSuccess($"Registering detached task {Label} on {host.Name}");
        if (InteractiveClient.Line(result.Stdout, "VT-TASK ") != "registered")
            throw new HostOperationException($"Unexpected reply while registering detached task {Label} on {host.Name}", result);
    }

    internal async Task<State> InspectAsync(IGameHost host, TimeSpan timeout, CancellationToken cancellation = default)
    {
        var result = (await host.RunAsync(InspectScript, Variables(), timeout, cancellation).ConfigureAwait(false))
            .EnsureSuccess($"Inspecting detached task {Label} on {host.Name}");
        return InteractiveClient.Line(result.Stdout, "VT-TASK ") switch
        {
            "missing" => State.Missing, "owned running" => State.Running, "owned stopped" => State.Stopped,
            "owned not-started" => State.NotStarted, "mismatch" => State.Mismatch,
            _ => throw new HostOperationException($"Unexpected reply while inspecting detached task {Label} on {host.Name}", result),
        };
    }

    internal async Task<bool> RetireAsync(IGameHost host, TimeSpan timeout, CancellationToken cancellation = default)
    {
        var result = (await host.RunAsync(RetireScript, Variables(), timeout, cancellation).ConfigureAwait(false))
            .EnsureSuccess($"Removing detached task {Label} on {host.Name}");
        return InteractiveClient.Line(result.Stdout, "VT-TASK ") switch
        {
            "removed" => true, "missing" => false,
            "mismatch" => throw new InvalidOperationException($"Task {Label} no longer has this run's exact definition."),
            _ => throw new HostOperationException($"Unexpected reply while removing detached task {Label} on {host.Name}", result),
        };
    }

    internal Dictionary<string, string> Variables() => OwnershipFields().ToDictionary(field => field.Key switch
    {
        "workingDirectory" => "working",
        _ => field.Key,
    }, field => field.Value, StringComparer.Ordinal);

    private static string Text(int value) => value.ToString(CultureInfo.InvariantCulture);

    // The current desktop token is preflighted by NativeSmoke. TASK_CREATE refuses an existing name rather than updating it.
    internal static readonly string RegistrationContextScript = """
        'VT-PWSH ' + [Environment]::ProcessPath
        $name = [Security.Principal.WindowsIdentity]::GetCurrent().Name
        'VT-IDENTITY ' + [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($name))
        """.ReplaceLineEndings("\n");

    // One builder and one ownership predicate are shared by registration, status inspection, retirement and the direct verifier test.
    // Every action is covered by the exact count plus its complete executable definition. No trigger is registered:
    // the detached task is started only through the explicit demand-start call below.
    internal static readonly string DefinitionFunctions = """
        function ConvertTo-VtSid([string]$identity) {
            try { return [Security.Principal.SecurityIdentifier]::new($identity).Value }
            catch { return ([Security.Principal.NTAccount]::new($identity).Translate([Security.Principal.SecurityIdentifier])).Value }
        }
        function Set-VtTaskDefinition([object]$definition) {
            $definition.RegistrationInfo.Description = $description
            $definition.Principal.UserId = $principalUserId
            $definition.Principal.LogonType = [int]$principalLogonType
            $definition.Principal.RunLevel = [int]$principalRunLevel
            $settings = $definition.Settings
            $settings.AllowDemandStart = [bool]::Parse($allowDemandStart)
            $settings.DisallowStartIfOnBatteries = [bool]::Parse($disallowStartIfOnBatteries)
            $settings.Enabled = [bool]::Parse($enabled)
            $settings.ExecutionTimeLimit = $executionTimeLimit
            $settings.Hidden = [bool]::Parse($hidden)
            $settings.StopIfGoingOnBatteries = [bool]::Parse($stopIfGoingOnBatteries)
            $action = $definition.Actions.Create([int]$actionType)
            $action.Path = $executable
            $action.Arguments = $arguments
            $action.WorkingDirectory = $working
        }
        function Test-VtTaskDefinition([object]$definition) {
            if ($definition.RegistrationInfo.Description -cne $description -or
                (ConvertTo-VtSid ([string]$definition.Principal.UserId)) -cne (ConvertTo-VtSid $principalUserId) -or
                [int]$definition.Principal.LogonType -ne [int]$principalLogonType -or
                [int]$definition.Principal.RunLevel -ne [int]$principalRunLevel -or
                [int]$definition.Actions.Count -ne [int]$actionsCount -or [int]$definition.Triggers.Count -ne [int]$triggersCount) { return $false }
            $action = $definition.Actions.Item(1)
            if ([int]$action.Type -ne [int]$actionType -or $action.Path -cne $executable -or $action.Arguments -cne $arguments -or
                $action.WorkingDirectory -cne $working) { return $false }
            $settings = $definition.Settings
            if ([bool]$settings.AllowDemandStart -ne [bool]::Parse($allowDemandStart) -or
                [bool]$settings.DisallowStartIfOnBatteries -ne [bool]::Parse($disallowStartIfOnBatteries) -or
                [bool]$settings.Enabled -ne [bool]::Parse($enabled) -or $settings.ExecutionTimeLimit -cne $executionTimeLimit -or
                [bool]$settings.Hidden -ne [bool]::Parse($hidden) -or
                [bool]$settings.StopIfGoingOnBatteries -ne [bool]::Parse($stopIfGoingOnBatteries)) { return $false }
            return $true
        }
        """.ReplaceLineEndings("\n");

    private static readonly string OpenTask = """
        $ErrorActionPreference = 'Stop'
        $service = New-Object -ComObject Schedule.Service
        $service.Connect()
        $folder = $service.GetFolder('\')
        $taskPath = '\' + $task
        $matches = @($folder.GetTasks(1) | Where-Object { $_.Path -ceq $taskPath })
        if ($matches.Count -eq 0) { 'VT-TASK missing'; exit 0 }
        if ($matches.Count -ne 1) { throw 'Task Scheduler returned more than one task at the exact path.' }
        $found = $matches[0]
        """.ReplaceLineEndings("\n");

    internal static readonly string RegisterScript = ("""
        $ErrorActionPreference = 'Stop'
        $service = New-Object -ComObject Schedule.Service
        $service.Connect()
        $folder = $service.GetFolder('\')
        $definition = $service.NewTask(0)
        """ + "\n" + DefinitionFunctions + "\n" + """
        Set-VtTaskDefinition $definition
        if (-not (Test-VtTaskDefinition $definition)) { throw 'The detached task definition did not match the journal before registration.' }
        $registered = $folder.RegisterTaskDefinition($task, $definition, 2, $principalUserId, $null, [int]$principalLogonType)
        [void]$registered.Run($null)
        'VT-TASK registered'
        """).ReplaceLineEndings("\n");

    internal static readonly string InspectScript = (OpenTask + "\n" + DefinitionFunctions + "\n" + """
        if (-not (Test-VtTaskDefinition $found.Definition)) { 'VT-TASK mismatch'; exit 0 }
        if ($found.State -in @(2, 4) -or [int64]$found.LastTaskResult -in @(0x41301, 0x41325)) { 'VT-TASK owned running' }
        elseif ([int64]$found.LastTaskResult -eq 0x41303) { 'VT-TASK owned not-started' }
        else { 'VT-TASK owned stopped' }
        """).ReplaceLineEndings("\n");

    internal static readonly string RetireScript = (OpenTask + "\n" + DefinitionFunctions + "\n" + """
        if (-not (Test-VtTaskDefinition $found.Definition)) { 'VT-TASK mismatch'; exit 0 }
        if ($found.State -in @(2, 4)) {
            $found.Stop(0)
            $deadline = [DateTime]::UtcNow.AddSeconds(15)
            while ($found.State -in @(2, 4)) {
                if ([DateTime]::UtcNow -ge $deadline) { throw 'The detached task did not stop within 15 seconds.' }
                Start-Sleep -Milliseconds 100
            }
            while ([IO.File]::Exists($launcher)) {
                try {
                    $stream = [IO.File]::Open($launcher, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::None)
                    $stream.Dispose()
                    break
                } catch [IO.IOException] {
                    if ([DateTime]::UtcNow -ge $deadline) { throw 'The detached task did not release its launcher within 15 seconds.' }
                    Start-Sleep -Milliseconds 100
                }
            }
        }
        $folder.DeleteTask($found.Path, 0)
        'VT-TASK removed'
        """).ReplaceLineEndings("\n");

    // Builds the real COM definition in memory, proves it matches, then proves one extra action makes the production predicate fail.
    internal static readonly string DefinitionVerifierProbe = ("""
        $ErrorActionPreference = 'Stop'
        $service = New-Object -ComObject Schedule.Service
        $service.Connect()
        $definition = $service.NewTask(0)
        """ + "\n" + DefinitionFunctions + "\n" + """
        Set-VtTaskDefinition $definition
        if (-not (Test-VtTaskDefinition $definition)) { throw 'The expected definition was refused.' }
        $extra = $definition.Actions.Create(0)
        $extra.Path = $executable
        $extra.Arguments = '-NoProfile -Command exit 0'
        $extra.WorkingDirectory = $working
        if (Test-VtTaskDefinition $definition) { throw 'A changed definition with another action was accepted.' }
        'VT-TASK changed-definition-refused'
        """).ReplaceLineEndings("\n");
}
