namespace WSGM.Tests;

public sealed class InstallerShutdownContractTests
{
    [Fact]
    public void UsbipRunPublishesAndConsumesItsOwnBoundedOutcomeInsteadOfTrustingExitZero()
    {
        string source = File.ReadAllText(
            Path.Combine(RepositoryRoot, "installer", "WSGM.iss"));
        string run = Slice(source, "[Run]", "[UninstallRun]");
        string report = Slice(
            source,
            "procedure ReportUsbipInstallOutcome();",
            "function WasShellRunning(): Boolean;");
        string restart = Slice(
            source,
            "function NeedRestart(): Boolean;",
            "function UsbipInstallStatusPath(): String;");

        Assert.Contains(
            "-StatusPath \"\"{commonappdata}\\WSGM\\usbip-install-status.ini\"\"",
            run,
            StringComparison.Ordinal);
        Assert.Contains("BeforeInstall: PrepareUsbipInstallOutcome", run, StringComparison.Ordinal);
        Assert.Contains("AfterInstall: ReportUsbipInstallOutcome", run, StringComparison.Ordinal);
        Assert.Contains("GetIniString('usbip', 'schemaVersion'", report, StringComparison.Ordinal);
        Assert.Contains("Outcome = 'installed'", report, StringComparison.Ordinal);
        Assert.Contains("Outcome = 'already-present'", report, StringComparison.Ordinal);
        Assert.Contains("Outcome = 'failed'", report, StringComparison.Ordinal);
        Assert.Contains("Outcome = 'blocked-newer-version'", report, StringComparison.Ordinal);
        Assert.Contains("UsbipOutcomeReported := True;", report, StringComparison.Ordinal);
        Assert.DoesNotContain("ExitCode", report, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("UsbipRebootRequired or not UsbipOutcomeReported", restart, StringComparison.Ordinal);
        Assert.DoesNotContain("WasUpgrade", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ForceFallbackPreservesShellAnchorUntilRecoveryAcknowledgement()
    {
        string source = File.ReadAllText(
            Path.Combine(RepositoryRoot, "installer", "WSGM.iss"));
        string forceStop = Slice(
            source,
            "procedure ForceStopRunningInstances();",
            "procedure WaitForShellAnchorRecovery();");
        string forceScope = Slice(
            source,
            "procedure ForceStopCurrentSessionImage(const ImageName: String);",
            "function CanInstallShellAnchor(): Boolean;");
        string waitForRecovery = Slice(
            source,
            "procedure WaitForShellAnchorRecovery();",
            "function StopRunningInstances(): Boolean;");
        string updateStop = Slice(
            source,
            "function StopRunningInstances(): Boolean;",
            "function StopRunningInstancesForUninstall(): Boolean;");
        string uninstallStop = Slice(
            source,
            "function StopRunningInstancesForUninstall(): Boolean;",
            "function ReplacementBlockersPresent(IncludeSteam: Boolean): Boolean;");
        string acknowledgedRetirement = From(
            waitForRecovery,
            "ForceStopCurrentSessionImage('WSGM.ShellAnchor.exe')");

        Assert.True(forceStop.Contains(
            "ForceStopCurrentSessionImage('WSGM.exe')",
            StringComparison.Ordinal));
        Assert.False(forceStop.Contains(
            "WSGM.ShellAnchor.exe",
            StringComparison.OrdinalIgnoreCase));
        Assert.True(forceScope.Contains(
            "ProcessIdToSessionIdK(GetCurrentProcessIdK(), SessionId)",
            StringComparison.Ordinal));
        Assert.True(forceScope.Contains(
            "Args := '/FI \"SESSION eq ' + IntToStr(SessionId) + '\" /IM \"' + ImageName + '\" /F';",
            StringComparison.Ordinal));
        Assert.True(forceScope.Contains(
            "refusing a cross-session force stop",
            StringComparison.Ordinal));
        Assert.False(source.Contains("/IM WSGM.exe", StringComparison.OrdinalIgnoreCase));
        Assert.False(source.Contains("/IM \"WSGM.exe\"", StringComparison.OrdinalIgnoreCase));
        Assert.False(source.Contains(
            "/IM WSGM.ShellAnchor.exe",
            StringComparison.OrdinalIgnoreCase));
        Assert.False(source.Contains(
            "/IM \"WSGM.ShellAnchor.exe\"",
            StringComparison.OrdinalIgnoreCase));
        AssertOrdered(updateStop, "ForceStopRunningInstances();", "WaitForShellAnchorRecovery();");
        AssertOrdered(uninstallStop, "ForceStopRunningInstances();", "WaitForShellAnchorRecovery();");
        Assert.True(waitForRecovery.Contains(
            "Local\\WSGM.ShellAnchor.RecoverySettled",
            StringComparison.Ordinal));
        AssertOrdered(
            waitForRecovery,
            "WaitForSingleObjectK(H, 5000)",
            "ForceStopCurrentSessionImage('WSGM.ShellAnchor.exe')");
        AssertOrdered(
            waitForRecovery,
            "if WaitResult = 0 then",
            "ForceStopCurrentSessionImage('WSGM.ShellAnchor.exe')");
        AssertOrdered(
            acknowledgedRetirement,
            "ForceStopCurrentSessionImage('WSGM.ShellAnchor.exe')",
            "CreateFileW(AnchorPath");
        AssertOrdered(
            acknowledgedRetirement,
            "CreateFileW(AnchorPath",
            "CloseHandleK(Probe)");
        Assert.DoesNotContain("DeleteFile(AnchorPath)", waitForRecovery, StringComparison.Ordinal);
        Assert.False(forceScope.Contains(" /T ", StringComparison.OrdinalIgnoreCase));
        Assert.True(source.Contains(
            "CloseApplicationsFilterExcludes=WSGM.ShellAnchor.exe",
            StringComparison.Ordinal));
        Assert.True(source.Contains(
            "Source: \"{#AppPublishDir}\\WSGM.exe\"; DestDir: \"{app}\"; "
                + "DestName: \"WSGM.ShellAnchor.exe\"; Flags: ignoreversion restartreplace "
                + "uninsrestartdelete; Check: CanInstallShellAnchor",
            StringComparison.Ordinal));
        Assert.True(source.Contains(
            "Result := ShellAnchorReplacementSafe or not WizardSilent();",
            StringComparison.Ordinal));
        Assert.True(waitForRecovery.Contains(
            "ShellAnchorReplacementSafe := False;",
            StringComparison.Ordinal));
    }

    [Fact]
    public void DevicePackagePublication_HoldsPackageAndOwnerReservationsAcrossStopAndSwap()
    {
        string source = File.ReadAllText(
            Path.Combine(RepositoryRoot, "installer", "WSGM.iss"));
        string acquireGate = Slice(
            source,
            "function AcquireDevicePackageSlotGate(): Boolean;",
            "procedure ReleaseDevicePublicationReservations();");
        string reserveOwner = Slice(
            source,
            "function ReserveDeviceOwner(): Boolean;",
            "function InspectDeviceDirectory(const Path, Description: String;");
        string prepare = Slice(
            source,
            "function PrepareToInstall(var NeedsRestart: Boolean): String;",
            "procedure DeinitializeSetup();");
        string postInstall = Slice(
            source,
            "procedure CurStepChanged(CurStep: TSetupStep);",
            "// WSGM is almost certainly running during an update");

        Assert.True(acquireGate.Contains(
            "'Global\\WSGM.DevicePackageSlot'",
            StringComparison.Ordinal));
        Assert.True(acquireGate.Contains(
            "WaitForSingleObjectK(DevicePackageGateHandle, 5000)",
            StringComparison.Ordinal));
        Assert.True(reserveOwner.Contains(
            "'Global\\WSGM.DeviceOwner'",
            StringComparison.Ordinal));
        Assert.True(reserveOwner.Contains(
            "CreationError = ErrorAlreadyExists",
            StringComparison.Ordinal));
        Assert.True(reserveOwner.Contains(
            "CloseHandleK(DeviceOwnerHandle)",
            StringComparison.Ordinal));
        Assert.True(reserveOwner.Contains(
            "DeviceOwnerHandle := 0;",
            StringComparison.Ordinal));
        AssertOrdered(prepare, "AcquireDevicePackageSlotGate()", "not StopLogonService()");
        AssertOrdered(prepare, "not StopLogonService()", "StopRunningInstances()");
        AssertOrdered(prepare, "StopRunningInstances()", "ReserveDeviceOwner()");
        AssertOrdered(prepare, "ReserveDeviceOwner()", "CleanupStaleDevicePluginStaging()");
        AssertOrdered(
            postInstall,
            "ReplaceDevicePluginSlot();",
            "ReleaseDevicePublicationReservations();");
        Assert.True(source.Contains(
            "procedure DeinitializeSetup();",
            StringComparison.Ordinal));
    }

    [Fact]
    public void DevicePackagePublication_UsesFixedSiblingsAndDeselectRetiresEveryRecoveryRoot()
    {
        string source = File.ReadAllText(
            Path.Combine(RepositoryRoot, "installer", "WSGM.iss"));
        string installDelete = Slice(source, "[InstallDelete]", "[Code]");
        string cleanup = Slice(
            source,
            "function CleanupStaleDevicePluginStaging(): Boolean;",
            "procedure ReplaceDevicePluginSlot();");
        string replacement = Slice(
            source,
            "procedure ReplaceDevicePluginSlot();",
            "procedure CurStepChanged(CurStep: TSetupStep);");
        string deselection = Slice(
            replacement,
            "if not WizardIsComponentSelected('device') then",
            "if not StagingExists then");
        string preflight = Slice(
            replacement,
            "// Validate every move/delete target before changing any slot state.",
            "if not WizardIsComponentSelected('device') then");
        string publication = From(replacement, "HadInstalled := InstalledExists;");

        Assert.True(source.Contains(
            "DestDir: \"{autopf}\\WSGM\\DevicePlugins\\.staging\"",
            StringComparison.Ordinal));
        Assert.False(installDelete.Contains(
            "DevicePlugins\\.previous",
            StringComparison.Ordinal));
        Assert.True(cleanup.Contains(
            "AddBackslash(Root) + '.staging'",
            StringComparison.Ordinal));
        Assert.True(cleanup.Contains(
            "AddBackslash(Root) + '.installed.staging-*'",
            StringComparison.Ordinal));
        Assert.True(cleanup.Contains(
            "Root, 'Device Plugin slot parent', RootExists",
            StringComparison.Ordinal));
        Assert.True(cleanup.Contains(
            "EnumerationError <> ErrorNoMoreFiles",
            StringComparison.Ordinal));
        Assert.False(cleanup.Contains("DirExists(Root)", StringComparison.Ordinal));
        AssertOrdered(
            cleanup,
            "LegacyStagingExists) then Exit;",
            "DeleteInspectedDeviceDirectory(");
        Assert.True(replacement.Contains(
            "Previous := AddBackslash(Root) + '.previous';",
            StringComparison.Ordinal));
        Assert.True(replacement.Contains(
            "LegacyPrevious := AddBackslash(Root) + '.installed.previous';",
            StringComparison.Ordinal));
        Assert.True(replacement.Contains(
            "RenameFile(LegacyPrevious, Previous)",
            StringComparison.Ordinal));
        Assert.True(preflight.Contains(
            "LegacyReviewed, 'Legacy reviewed package root', LegacyReviewedExists",
            StringComparison.Ordinal));
        Assert.True(preflight.Contains(
            "Root, 'Device Plugin slot parent', RootExists",
            StringComparison.Ordinal));
        AssertOrdered(
            replacement,
            "LegacyReviewed, 'Legacy reviewed package root', LegacyReviewedExists",
            "HadInstalled := InstalledExists;");
        AssertOrdered(
            replacement,
            "LegacyReviewed, 'Legacy reviewed package root', LegacyReviewedExists",
            "Staging, 'Device Plugin staging root', StagingExists");
        AssertOrdered(
            replacement,
            "LegacyReviewed, 'Legacy reviewed package root', LegacyReviewedExists",
            "RenameFile(Staging, Installed)");
        Assert.True(deselection.Contains(
            "Installed, 'Installed Device Plugin slot', InstalledExists",
            StringComparison.Ordinal));
        Assert.True(deselection.Contains(
            "LegacyPrevious, 'Legacy Device Plugin recovery root'",
            StringComparison.Ordinal));
        AssertOrdered(
            deselection,
            "LegacyPrevious, 'Legacy Device Plugin recovery root'",
            "Installed, 'Installed Device Plugin slot', InstalledExists");
        AssertOrdered(
            publication,
            "RenameFile(Installed, Previous)",
            "RenameFile(Staging, Installed)");
        AssertOrdered(
            publication,
            "RenameFile(Staging, Installed)",
            "Previous, 'Device Plugin recovery root', PreviousExists");
        AssertOrdered(
            publication,
            "if not RenameFile(Previous, Installed) then",
            "restored the previous active slot");
        Assert.True(publication.Contains(
            "The previous package remains in the recovery directory.",
            StringComparison.Ordinal));
        Assert.True(installDelete.Contains(
            "Type: files; Name: \"{app}\\libviiper.dll\"",
            StringComparison.Ordinal));
        Assert.True(installDelete.Contains(
            "Type: files; Name: \"{app}\\VIIPER-NOTICE.md\"",
            StringComparison.Ordinal));
        Assert.True(installDelete.Contains(
            "Type: files; Name: \"{app}\\VIIPER-LICENSE.txt\"",
            StringComparison.Ordinal));
        Assert.True(installDelete.Contains(
            "Type: files; Name: \"{app}\\USBip-0.9.7.7-x64.exe\"",
            StringComparison.Ordinal));
        Assert.True(source.Contains(
            "Source: \"{#AppPublishDir}\\*.dll\"; DestDir: \"{app}\"; "
                + "Excludes: \"libviiper.dll\"; Flags: ignoreversion",
            StringComparison.Ordinal));
        Assert.True(source.Contains(
            "Source: \"{#AppPublishDir}\\libviiper.dll\"; DestDir: \"{app}\"; "
                + "Flags: ignoreversion; Components: controller",
            StringComparison.Ordinal));
        Assert.True(source.Contains(
            "Source: \"{#AppPublishDir}\\VIIPER-LICENSE.txt\"; DestDir: \"{app}\"; "
                + "Flags: ignoreversion; Components: controller",
            StringComparison.Ordinal));
        Assert.True(source.Contains(
            "Source: \"{#AppPublishDir}\\VIIPER-NOTICE.md\"; DestDir: \"{app}\"; "
                + "Flags: ignoreversion; Components: controller",
            StringComparison.Ordinal));
        Assert.True(source.Contains(
            "Source: \"{#AppPublishDir}\\USBip-0.9.7.7-x64.exe\"; DestDir: \"{app}\"; "
                + "Flags: ignoreversion; Components: controller",
            StringComparison.Ordinal));
        Assert.True(source.Contains(
            "Source: \"{#AppPublishDir}\\HidHide_1.5.230_x64.exe\"; DestDir: \"{app}\"; "
                + "Flags: ignoreversion; Components: controller",
            StringComparison.Ordinal));
    }

    [Fact]
    public void Uninstall_HoldsPackageAndOwnerReservationsThroughUninstallDelete()
    {
        string source = File.ReadAllText(
            Path.Combine(RepositoryRoot, "installer", "WSGM.iss"));
        string initialize = Slice(
            source,
            "function InitializeUninstall(): Boolean;",
            "procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);");
        string ownerRefusal = From(initialize, "if not ReserveDeviceOwner() then");
        string deinitialize = From(source, "procedure DeinitializeUninstall();");
        string uninstallDelete = Slice(source, "[UninstallDelete]", "[InstallDelete]");

        AssertOrdered(
            initialize,
            "InspectLogonServiceState(",
            "not StopLogonService()");
        AssertOrdered(
            initialize,
            "StopRunningInstancesForUninstall()",
            "ReserveDeviceOwner()");
        AssertOrdered(initialize, "ReserveDeviceOwner()", "Result := True;");
        AssertOrdered(
            ownerRefusal,
            "ReleaseDevicePublicationReservations();",
            "RestoreStoppedUninstallRuntime();");
        Assert.True(source.Contains(
            "if CurUninstallStep = usUninstall then",
            StringComparison.Ordinal));
        Assert.True(uninstallDelete.Contains(
            "Type: filesandordirs; Name: \"{autopf}\\WSGM\"",
            StringComparison.Ordinal));
        AssertOrdered(
            deinitialize,
            "ReleaseDevicePublicationReservations();",
            "if not UninstallMutationStarted then");
        Assert.True(deinitialize.Contains(
            "RestoreStoppedUninstallRuntime();",
            StringComparison.Ordinal));
    }

    [Fact]
    public void RefusalAndCancel_RestoreOnlyTheCapturedRunningLogonService()
    {
        string source = File.ReadAllText(
            Path.Combine(RepositoryRoot, "installer", "WSGM.iss"));
        string inspection = Slice(
            source,
            "function InspectLogonServiceState(var Exists, Running: Boolean): Boolean;",
            "function StopLogonService(): Boolean;");
        string restore = Slice(
            source,
            "procedure RestoreStoppedServiceAndRuntime(const Operation: String;",
            "function PrepareToInstall(var NeedsRestart: Boolean): String;");
        string setup = Slice(
            source,
            "function PrepareToInstall(var NeedsRestart: Boolean): String;",
            "procedure DeinitializeSetup();");
        string uninstall = Slice(
            source,
            "function InitializeUninstall(): Boolean;",
            "procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);");

        Assert.True(inspection.Contains(
            "OpenSCManagerW(0, 0, ScManagerConnect)",
            StringComparison.Ordinal));
        Assert.True(inspection.Contains(
            "OpenServiceW(Manager, 'WSGMLogonService', ServiceQueryStatus)",
            StringComparison.Ordinal));
        Assert.True(inspection.Contains(
            "if OpenError = ErrorServiceDoesNotExist then",
            StringComparison.Ordinal));
        Assert.True(inspection.Contains(
            "if Status.CurrentState = ServiceStopped then",
            StringComparison.Ordinal));
        Assert.True(inspection.Contains(
            "if Status.CurrentState = ServiceRunning then",
            StringComparison.Ordinal));
        Assert.True(inspection.Contains(
            "unverified transitional state",
            StringComparison.Ordinal));
        Assert.True(restore.Contains(
            "if ServiceExisted and ServiceWasRunning then",
            StringComparison.Ordinal));
        AssertOrdered(
            setup,
            "InspectLogonServiceState(SetupServiceExisted, SetupServiceWasRunning)",
            "if SetupServiceWasRunning and not StopLogonService() then");
        AssertOrdered(
            setup,
            "if SetupServiceWasRunning and not StopLogonService() then",
            "StopRunningInstances()");
        Assert.True(source.Contains(
            "'Setup rollback', SetupServiceExisted, SetupServiceWasRunning,",
            StringComparison.Ordinal));
        AssertOrdered(
            uninstall,
            "InspectLogonServiceState(",
            "if UninstallServiceWasRunning and not StopLogonService() then");
        AssertOrdered(
            uninstall,
            "if UninstallServiceWasRunning and not StopLogonService() then",
            "StopRunningInstancesForUninstall()");
        Assert.True(source.Contains(
            "'Uninstall rollback', UninstallServiceExisted, UninstallServiceWasRunning,",
            StringComparison.Ordinal));
    }

    [Fact]
    public void SetupRefusalRetryAndCancel_PreserveAndRestoreTheInitialRuntimeMode()
    {
        string source = File.ReadAllText(
            Path.Combine(RepositoryRoot, "installer", "WSGM.iss"));
        string serviceHostSource = File.ReadAllText(
            Path.Combine(RepositoryRoot, "src", "WSGM.LogonService", "ServiceHost.cs"));
        string serviceInstallerSource = File.ReadAllText(
            Path.Combine(RepositoryRoot, "src", "WSGM.LogonService", "ServiceInstaller.cs"));
        string restore = Slice(
            source,
            "procedure RestoreStoppedServiceAndRuntime(const Operation: String;",
            "function PrepareToInstall(var NeedsRestart: Boolean): String;");
        string prepare = Slice(
            source,
            "function PrepareToInstall(var NeedsRestart: Boolean): String;",
            "procedure DeinitializeSetup();");
        string ownerRefusal = Slice(
            prepare,
            "if not ReserveDeviceOwner() then",
            "if not CleanupStaleDevicePluginStaging() then");
        string blockerRefusal = Slice(
            prepare,
            "if FileExists(ExpandConstant('{app}\\WSGM.exe')) and ReplacementBlockersPresent(True) then",
            "if not ReserveDeviceOwner() then");
        string stagingRefusal = From(prepare, "if not CleanupStaleDevicePluginStaging() then");
        string deinitialize = Slice(
            source,
            "procedure DeinitializeSetup();",
            "function InitializeUninstall(): Boolean;");
        string stepChanged = Slice(
            source,
            "procedure CurStepChanged(CurStep: TSetupStep);",
            "// WSGM is almost certainly running during an update");

        AssertOrdered(
            prepare,
            "if not SetupRuntimeClassificationCaptured then",
            "WasShell := CheckForMutexes('WSGM.Shell')");
        AssertOrdered(
            prepare,
            "WasRunning := StopRunningInstances() or WasShell;",
            "SetupRuntimeClassificationCaptured := True;");
        Assert.True(prepare.Contains(
            "else if StopRunningInstances() then",
            StringComparison.Ordinal));
        Assert.Equal(3, CountOccurrences(prepare, "RestoreStoppedSetupRuntime();"));
        AssertOrdered(
            blockerRefusal,
            "ReleaseDevicePublicationReservations();",
            "RestoreStoppedSetupRuntime();");
        AssertOrdered(
            ownerRefusal,
            "ReleaseDevicePublicationReservations();",
            "RestoreStoppedSetupRuntime();");
        AssertOrdered(
            stagingRefusal,
            "ReleaseDevicePublicationReservations();",
            "RestoreStoppedSetupRuntime();");
        Assert.True(restore.Contains(
            "ServicePath := ExpandConstant('{autopf}\\WSGM\\WSGM.LogonService.exe');",
            StringComparison.Ordinal));
        Assert.True(restore.Contains(
            "Exec(ServicePath, '--install'",
            StringComparison.Ordinal));
        Assert.False(restore.Contains("'start WSGMLogonService'", StringComparison.Ordinal));
        Assert.True(serviceInstallerSource.Contains(
            "if (!StartForInstall(service))",
            StringComparison.Ordinal));
        Assert.True(serviceInstallerSource.Contains(
            "NativeMethods.StartServiceW(service, 1, (nint)argv)",
            StringComparison.Ordinal));
        Assert.True(serviceHostSource.Contains(
            "if (IsInstallStart(argc, argv))",
            StringComparison.Ordinal));
        AssertOrdered(
            serviceHostSource,
            "if (IsInstallStart(argc, argv))",
            "SessionLauncher.CatchUpExistingSessions();");
        Assert.True(restore.Contains(
            "if RuntimeWasShell then",
            StringComparison.Ordinal));
        Assert.True(restore.Contains(
            "Arguments := '--shell'",
            StringComparison.Ordinal));
        Assert.True(restore.Contains(
            "Arguments := '--settings'",
            StringComparison.Ordinal));
        Assert.True(restore.Contains(
            "if not RuntimeWasRunning then Exit;",
            StringComparison.Ordinal));
        Assert.False(source.Contains("installer-rollback-no-device", StringComparison.Ordinal));
        Assert.False(source.Contains("InstallerRollback.DeviceOwnerRetained", StringComparison.Ordinal));
        // Gated on the post-install having completed, not on installation having started. The
        // [Run] restart entries execute only after a successful setup, so a failure during [Files]
        // or a RaiseException out of ReplaceDevicePluginSlot must still restore the shell,
        // Settings, and the logon service that setup stopped.
        AssertOrdered(
            deinitialize,
            "ReleaseDevicePublicationReservations();",
            "if not SetupPostInstallCompleted then");
        Assert.True(deinitialize.Contains(
            "RestoreStoppedSetupRuntime();",
            StringComparison.Ordinal));
        Assert.False(
            deinitialize.Contains("SetupInstallStarted", StringComparison.Ordinal),
            "Setup restoration must not be suppressed merely because installation began.");
        AssertOrdered(
            stepChanged,
            "ReplaceDevicePluginSlot();",
            "SetupShutdownApplied := False;");
        // Both flags are set only on the success path, after publication actually returned.
        AssertOrdered(
            stepChanged,
            "SetupShutdownApplied := False;",
            "SetupPostInstallCompleted := True;");
    }

    private static string RepositoryRoot
    {
        get
        {
            DirectoryInfo? directory = new(AppContext.BaseDirectory);
            while (directory is not null
                && !File.Exists(Path.Combine(directory.FullName, "WSGM.slnx")))
            {
                directory = directory.Parent;
            }

            Assert.NotNull(directory);
            return directory.FullName;
        }
    }

    private static string Slice(string source, string startMarker, string endMarker)
    {
        int start = source.IndexOf(startMarker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Installer marker was not found: {startMarker}");
        int end = source.IndexOf(endMarker, start + startMarker.Length, StringComparison.Ordinal);
        Assert.True(end > start, $"Installer marker was not found after {startMarker}: {endMarker}");
        return source[start..end];
    }

    private static string From(string source, string marker)
    {
        int start = source.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Installer marker was not found: {marker}");
        return source[start..];
    }

    private static void AssertOrdered(string source, string first, string second)
    {
        int firstIndex = source.IndexOf(first, StringComparison.Ordinal);
        Assert.True(firstIndex >= 0, $"Installer operation was not found: {first}");
        int secondIndex = source.IndexOf(
            second,
            firstIndex + first.Length,
            StringComparison.Ordinal);
        Assert.True(secondIndex > firstIndex, $"Installer operation must follow {first}: {second}");
    }

    private static int CountOccurrences(string source, string value)
    {
        int count = 0;
        int index = 0;
        while ((index = source.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }
        return count;
    }
}
