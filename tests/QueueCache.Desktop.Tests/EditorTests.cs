using QueueCache.Desktop.ViewModels;
using QueueCache.Management;
using QueueCache.Operations;
using QueueCache.Operations.ManagedDisks;
using static Test;

/// <summary>Cache settings, New disk and the disk action forms.</summary>
internal static class EditorTests
{
    public static void Run()
    {
        var volumes = new VolumeFixture();
        var stick = volumes.Volumes[2];

        // Cache settings
        var add = new CacheSettingsViewModel(stick, volumes.NoCache with { Options = null }, false, 8192);
        Check(add.IsFast && add.Drain == (int)DrainAlgorithm.Idle && add.ApplyText == "Add cache" && add.Title == "Add a cache to S: Stick",
            "a new cache defaults to Fast and the Idle drain");
        Check(add.MemoryChoices[add.SelectedMemory] == "4 GiB" && add.AvailableText == "Up to 8 GiB available", "a new cache starts at 4 GiB of what is available");
        var edit = new CacheSettingsViewModel(volumes.Volumes[1], volumes.State, true, 8192);
        Check(edit.MemoryChoices[edit.SelectedMemory] == "4 GiB" && !edit.IsCustomMemory && edit.StartWithWindows && edit.ApplyText == "Save",
            "an existing cache selects its memory size");
        edit.SelectedMemory = edit.MemoryChoices.Count - 1;
        Check(edit.IsCustomMemory, "Custom size shows its input");
        edit.CustomMiB = 3000;
        Check(edit.Result()?.Configuration.BudgetMiB == 3000, "a custom size is applied");
        edit.CustomMiB = 0;
        Check(edit.Result() is null && edit.Error is not null, "an invalid size is refused with a reason");
        edit.CustomMiB = 3000;
        edit.Drain = 0;
        Check(edit.DrainText.StartsWith("Eager:") && !edit.ShowWatermarks && !edit.ShowAge && !edit.ShowIdle, "Eager hides start/stop levels, age and idle");
        edit.Drain = 1;
        Check(edit.DrainText.StartsWith("Balanced:") && edit.ShowWatermarks && edit.ShowAge && !edit.ShowIdle, "Balanced uses levels and age");
        edit.Drain = 2;
        Check(edit.DrainText.StartsWith("Idle:") && edit.ShowWatermarks && edit.ShowAge && edit.ShowIdle, "Idle adds the idle interval");
        edit.Drain = 3;
        Check(edit.DrainText.StartsWith("Deferred:") && !edit.ShowWatermarks && edit.ShowAge && !edit.ShowIdle && edit.MaximumAgeMs == 3600000,
            "Deferred uses only the age, up to one hour on a capable driver");
        edit.IsStrict = true;
        Check(!edit.IsFast && edit.Result()?.Configuration.Preset == CachePreset.Strict, "choosing Strict applies Strict");

        // New disk
        var service = new DiskFixture();
        var create = new CreateDiskViewModel(service, new HashSet<char> { 'C', 'R' });
        Settle(create.LoadAsync());
        var pure = create.Definition();
        pure.Validate();
        Check(pure.Mode == ManagedDiskMode.EphemeralRam && pure.ImagePath is null && pure.Cache is null && !create.ShowImage && !create.ShowCache,
            "a RAM disk needs no image and no cache settings");
        Check(!create.Letters.Contains("R:") && !create.Letters.Contains("C:") && create.Letter == "D:", "only free letters are offered");
        create.IsCachedImage = true;
        create.ImagePath = @"C:\Images\Backing.vhdx";
        create.CacheMiB = 64;
        Check(create.Definition().Cache?.BudgetMiB == 64 && create.Definition().CapacityBytes == 1024 * ManagedDiskDefinition.MiB && !create.ShowCheckpoint,
            "a disk image's cache size is independent of its capacity");
        Check(create.Definition() is { AcceptVolatileWrites: false, Cache.Preset: CachePreset.Strict }, "a disk image with a cache is Strict unless Fast is chosen");
        create.IsFast = true;
        Check(create.Definition() is { AcceptVolatileWrites: true, Cache.Preset: CachePreset.Fast }, "choosing Fast, which states the risk, is the acknowledgement");
        create.IsImage = true;
        create.ImagePath = @"C:\Images\Source.vhdx";
        create.CheckpointDirectory = @"C:\Images\Checkpoints";
        Check(create.SaveBeforeStop && !create.SaveDuringShutdown && create.ShowCheckpoint && !create.ShowCache,
            "an image in RAM saves before stopping by default; saving at shutdown is optional");
        create.UseExisting = true;
        Check(!create.CanCreate && !create.ShowCapacity && create.Status == "Inspect the image first.", "importing requires inspection and cannot override the capacity");
        Settle(create.InspectCommand.ExecuteAsync(null));
        var imported = create.Definition();
        imported.Validate();
        Check(imported.CapacityBytes == service.VirtualBytes && imported.SectorBytes == 4096 && create.CanCreate && create.InspectionText.Contains("no automatic formatting") &&
              !create.ShowLabel, "inspection binds the image's capacity and sector size and keeps its data");
        create.InitializeBlank = true;
        var blank = create.Definition();
        blank.Validate();
        Check(blank.InitializeBlankImage && blank.ExpectedBlankImage is not null && create.ShowLabel && create.InspectionText.Contains("every logical sector is checked"),
            "formatting a blank image is explicit and bound to the inspected identity");
        create.ReadOnly = true;
        Check(create.Definition() is { ReadOnly: true, SaveBeforeStopping: false, SaveDuringShutdown: false, InitializeBlankImage: false },
            "read-only turns off formatting and automatic saves");
        create.ImagePath = @"C:\Images\Replacement.vhdx";
        Check(create.Definition().CapacityBytes == 0 && !create.CanCreate, "a changed path invalidates the inspection at once");
        Settle(create.InspectCommand.ExecuteAsync(null));
        ManagedDiskRuntime? created = null;
        create.Created += (_, runtime) => created = runtime;
        Settle(create.CreateCommand.ExecuteAsync(null));
        Check(service.Creates == 1 && service.Created is { ReadOnly: true, Source: ManagedDiskSource.OpenExisting } && created is not null,
            "Create submits one typed definition and reports the ready disk");

        var unavailable = new CreateDiskViewModel(new DiskFixture { Available = false }, new HashSet<char>());
        Settle(unavailable.LoadAsync());
        foreach (var mode in Enum.GetValues<ManagedDiskMode>())
        {
            unavailable.Mode = mode;
            Check(!unavailable.CanCreate && unavailable.StatusIsError && unavailable.Status == "Native provider qualification is required.",
                $"an unavailable backend disables {mode} and says why");
        }
        var failing = new CreateDiskViewModel(new DiskFixture { FailCreation = true }, new HashSet<char>());
        Settle(failing.LoadAsync());
        created = null;
        failing.Created += (_, runtime) => created = runtime;
        Settle(failing.CreateCommand.ExecuteAsync(null));
        Check(created is null && failing.StatusIsError && failing.Status == "Native reservation refused.", "a failed creation stays visible and reports no disk");

        // Disk action forms
        var disks = DiskFixture.Sample();
        var image = disks.Records[0];
        var export = new DiskActionViewModel(disks, image, ManagedDiskAction.Export) { Path = @"C:\Images\export.vhdx" };
        Check(!export.Request().CommitExport, "Export keeps the startup image by default");
        export.CommitExport = true;
        Check(export.Request().CommitExport && export.Request().Expected?.WriteGeneration == 12, "Export changes the startup image only when chosen");
        var ramDefinition = ManagedDiskDefinition.New(ManagedDiskMode.EphemeralRam);
        var stopped = new ManagedDiskRecord(ramDefinition, new(ramDefinition.ResourceId, Guid.NewGuid(), 1, ramDefinition.Mode, ManagedDiskState.Stopped, 0, null));
        var settings = new DiskActionViewModel(disks, stopped, ManagedDiskAction.ConfigureStopped) { Letter = "S", CapacityGiB = 2 };
        Check(settings.Request() is { PreferredLetter: 'S', CapacityBytes: 2048 * ManagedDiskDefinition.MiB } && settings.ShowCapacity,
            "settings of a stopped RAM disk submit the letter and size");
        var delete = new DiskActionViewModel(disks, image, ManagedDiskAction.DeleteImage) { Path = @"D:\Disks\Old.vhdx" };
        Check(delete.IsDestructive && delete.Request().AcceptErase, "Delete image is marked destructive and its button is the acknowledgement");
        var readOnly = image with { Definition = image.Definition with { ReadOnly = true, SaveBeforeStopping = false } };
        Check(!new DiskActionViewModel(disks, readOnly, ManagedDiskAction.SetStartup).CanSaveAutomatically, "a read-only image cannot be set to save automatically");
        Console.WriteLine("Editor contracts passed.");
    }
}
