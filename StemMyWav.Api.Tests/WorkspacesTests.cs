using StemMyWav.Api.Separation;

namespace StemMyWav.Api.Tests;

public sealed class WorkspacesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "stemmywav-root-" + Guid.NewGuid().ToString("N"));

    public WorkspacesTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, true);

    private static void Age(string directory, TimeSpan age) =>
        Directory.SetLastWriteTimeUtc(directory, DateTime.UtcNow - age);

    private string Abandoned(TimeSpan age)
    {
        var directory = Path.Combine(_root, Workspaces.Prefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        File.WriteAllBytes(Path.Combine(directory, "vocals.wav"), "RIFFfake"u8.ToArray());
        Age(directory, age);
        return directory;
    }

    [Fact]
    public void Removes_a_directory_left_behind_by_an_earlier_process()
    {
        var orphan = Abandoned(TimeSpan.FromHours(3));

        Assert.Equal(1, new Workspaces(_root).RemoveOrphans(TimeSpan.FromHours(1)));
        Assert.False(Directory.Exists(orphan));
    }

    [Fact]
    public void Keeps_a_recent_directory_that_may_belong_to_another_instance()
    {
        var recent = Abandoned(TimeSpan.FromMinutes(5));

        Assert.Equal(0, new Workspaces(_root).RemoveOrphans(TimeSpan.FromHours(1)));
        Assert.True(Directory.Exists(recent));
    }

    [Fact]
    public void Never_removes_a_workspace_handed_out_by_this_process()
    {
        var workspaces = new Workspaces(_root);
        var live = workspaces.Create();
        // Selbst eine ungewöhnlich lange Trennung darf nicht eingesammelt werden.
        Age(live, TimeSpan.FromHours(9));

        Assert.Equal(0, workspaces.RemoveOrphans(TimeSpan.FromHours(1)));
        Assert.True(Directory.Exists(live));
    }

    [Fact]
    public void Ignores_directories_that_are_not_workspaces()
    {
        var unrelated = Path.Combine(_root, "something-else");
        Directory.CreateDirectory(unrelated);
        Age(unrelated, TimeSpan.FromDays(7));

        Assert.Equal(0, new Workspaces(_root).RemoveOrphans(TimeSpan.FromHours(1)));
        Assert.True(Directory.Exists(unrelated));
    }

    [Fact]
    public void Release_deletes_the_workspace_and_stops_protecting_it()
    {
        var workspaces = new Workspaces(_root);
        var work = workspaces.Create();
        File.WriteAllBytes(Path.Combine(work, "input.flac"), "fLaCfake"u8.ToArray());

        workspaces.Release(work);

        Assert.False(Directory.Exists(work));
        Assert.Equal(0, workspaces.RemoveOrphans(TimeSpan.FromHours(1)));
    }

    [Fact]
    public void Release_tolerates_a_workspace_that_is_already_gone()
    {
        var workspaces = new Workspaces(_root);
        var work = workspaces.Create();
        Directory.Delete(work, true);

        workspaces.Release(work);
    }

    [Fact]
    public void Collects_a_workspace_once_the_process_that_owned_it_is_gone()
    {
        // Ein Absturz: das Verzeichnis überlebt, die Instanz nicht.
        var crashed = new Workspaces(_root);
        var work = crashed.Create();
        Age(work, TimeSpan.FromHours(2));

        Assert.Equal(1, new Workspaces(_root).RemoveOrphans(TimeSpan.FromHours(1)));
        Assert.False(Directory.Exists(work));
    }
}
