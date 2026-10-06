using QueueCache.Desktop.Services;
using QueueCache.Desktop.ViewModels;
using QueueCache.Operations.ManagedDisks;
using static Test;

/// <summary>Virtual disks: which actions apply, and what each destructive action asks first.</summary>
internal static class VirtualDiskTests
{
    public static void Run()
    {
        var disks = DiskFixture.Sample();
        var dialogs = new FakeDialogs();
        var monitor = new DashboardMonitor(new VolumeFixture(), disks, dialogs, availableRam: () => 8UL << 30, usedLetters: () => new HashSet<char>());
        Settle(monitor.RefreshAsync());
        var image = monitor.VirtualDisks.Single(d => d.IsImage);
        var ram = monitor.VirtualDisks.Single(d => d.IsRamDisk);
        var cached = monitor.VirtualDisks.Single(d => d.IsCachedImage);

        Check(image.Title == "I: Builds" && image.Kind == "Image in RAM" && image.HasUnsavedChanges && image.IsVolatile &&
              image.SaveStatus?.EndsWith("changes since then are only in RAM") == true, "an image in RAM shows when it was saved and that changes are only in RAM");
        Check(image.ShowSave && image.CanSave && image.IsReadyState && !image.CanStart, "a ready image can be saved");
        Check(ram.IsVolatile && ram.Headline == "Erased when stopped" && ram.StopText == "Stop…", "a RAM disk says it is erased when stopped");
        Check(cached.IsStoppedState && cached.CanStart && !cached.IsVolatile && cached.CanEditStopped && cached.CanForget && !cached.CanFormat,
            "a stopped Strict disk image can start, be configured or forgotten, not formatted");
        Check(monitor.OverallHealth == Health.Attention && monitor.HealthDetail.Contains("1 disk has unsaved changes"), "the summary names unsaved changes");
        Check(image.Facts.Any(f => f.Label == "When stopped" && f.Value == "Saves first, then stops") &&
              image.Facts.Any(f => f.Label == "Access path"), "details say what happens when the disk stops");

        // Stopping an image: save is the first choice; discard is explicit and carries the generation that was shown.
        dialogs.WhileAsking = _ =>
        {
            // The disk changes while the question is open; the request must still name what the user saw.
            var record = disks.Records[0];
            image.Apply(record with { Runtime = record.Runtime! with { WriteGeneration = 13 } }, DateTimeOffset.Now);
        };
        dialogs.Answers.Enqueue("discard");
        Settle(image.StopCommand.ExecuteAsync(null));
        var question = dialogs.Asked[^1];
        var stop = disks.Requests[^1];
        Check(question.Choices[0] is { Id: "save", Kind: ChoiceKind.Primary } && question.Choices[1] is { Id: "discard", Kind: ChoiceKind.Destructive } &&
              question.Message.Contains("only in RAM"), "stopping an image offers Save and stop first, discarding as a separate destructive choice");
        Check(stop.StopIntent == ManagedDiskStopIntent.DiscardThenStop && stop.AcceptDiscard && stop.Expected?.WriteGeneration == 12,
            "discarding carries the RAM generation that was displayed, not a newer one");
        dialogs.WhileAsking = null;
        dialogs.Answers.Enqueue("save");
        Settle(image.StopCommand.ExecuteAsync(null));
        Check(disks.Requests[^1] is { StopIntent: ManagedDiskStopIntent.SaveThenStop, AcceptDiscard: false }, "Save and stop never discards");
        var count = disks.Requests.Count;
        Settle(image.StopCommand.ExecuteAsync(null));
        Check(disks.Requests.Count == count, "cancelling the question does nothing");

        dialogs.Answers.Enqueue("discard");
        Settle(ram.StopCommand.ExecuteAsync(null));
        Check(dialogs.Asked[^1].Choices.Single() is { Text: "Erase and stop", Kind: ChoiceKind.Destructive } &&
              disks.Requests[^1] is { StopIntent: ManagedDiskStopIntent.DiscardThenStop, AcceptDiscard: true }, "stopping a RAM disk asks to erase it");

        dialogs.Answers.Enqueue("format");
        Settle(image.FormatCommand.ExecuteAsync(null));
        Check(dialogs.Asked[^1].Message.Contains("only the RAM copy") && dialogs.Asked[^1].Message.Contains("imported image remains unchanged") &&
              disks.Requests[^1] is { Action: ManagedDiskAction.Format, AcceptErase: true }, "Format explains that the image file is unchanged until saved");

        dialogs.Answers.Enqueue("forget");
        Settle(cached.ForgetCommand.ExecuteAsync(null));
        Check(dialogs.Asked[^1].Message.Contains("image files are kept") && disks.Requests[^1].Action == ManagedDiskAction.RemoveDefinition,
            "Forget says the image files are kept");

        Settle(cached.StartCommand.ExecuteAsync(null));
        Check(disks.Requests[^1].Action == ManagedDiskAction.Start && cached.NoticeText == "Done by the fake broker.", "Start runs and reports on the disk");

        // A disk that needs recovery offers Recover and blocks Save.
        disks.Records[0] = disks.Records[0] with { Runtime = disks.Records[0].Runtime! with { State = ManagedDiskState.RecoveryRequired }, LastError = "Frozen checkpoint needs reconciliation." };
        Settle(monitor.SampleVirtualDisksAsync());
        Check(image.NeedsRecovery && image.CanRecover && !image.CanSave && image.Health == Health.Error && image.Problem == "Frozen checkpoint needs reconciliation.",
            "a disk that needs recovery offers Recover and blocks Save");

        // New disk: the created disk is selected and announced.
        var page = new VirtualDisksViewModel(monitor);
        dialogs.CreateDisk = editor =>
        {
            var definition = editor.Definition() with { PreferredLetter = 'N', Label = "New" };
            disks.Records.Add(new(definition, new(definition.ResourceId, Guid.NewGuid(), 1, definition.Mode, ManagedDiskState.Ready, 1, null, "N:")));
            return disks.Records[^1].Runtime;
        };
        Settle(page.NewDiskCommand.ExecuteAsync(null));
        Check(page.Selected?.Title == "N: New" && page.Selected.NoticeText == "N: is ready to use.", "a new disk is selected and says it is ready");
        monitor.Close();
        Console.WriteLine("Virtual disk contracts passed.");
    }
}
