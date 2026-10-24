using System.Text;
using Mendrain.Core.Models;
using Mendrain.Core.Services;

namespace Mendrain.Core.Tests;

public class SavedPasswordTests
{
    private const string Secret = "Tr0ub4dor & spaces ünï";

    [Fact]
    public void Saved_password_round_trips_and_is_not_stored_as_plaintext()
    {
        using var temp = new TempDir();
        var store = new SavedPasswordStore(temp.Combine("passwords.json"));

        Assert.True(store.Save(@"C:\x\holiday.zip", Secret));

        var entry = Assert.Single(store.List());
        Assert.Equal("holiday.zip", entry.Label);
        Assert.Equal(Secret, store.Reveal(entry.Id));
        Assert.DoesNotContain(Secret, entry.ToString());

        var bytes = File.ReadAllBytes(temp.Combine("passwords.json"));
        Assert.Equal(-1, IndexOf(bytes, Encoding.UTF8.GetBytes(Secret)));
        Assert.Equal(-1, IndexOf(bytes, Encoding.Unicode.GetBytes(Secret)));
        Assert.DoesNotContain(Secret, File.ReadAllText(temp.Combine("passwords.json")));
    }

    [Fact]
    public void Saving_the_same_password_again_updates_instead_of_duplicating()
    {
        using var temp = new TempDir();
        var store = new SavedPasswordStore(temp.Combine("passwords.json"));
        store.Save("a.zip", Secret);
        store.Save("b.rar", "other");
        store.Save("c.7z", Secret);

        var list = store.List();
        Assert.Equal(2, list.Count);
        Assert.Equal("c.7z", list[0].Label);
        Assert.Equal(["c.7z", "a.zip"], list[0].Archives);
        Assert.Single(store.FindFor(@"D:\somewhere\A.ZIP"));
        Assert.Empty(store.FindFor("unrelated.zip"));
    }

    [Fact]
    public void Remove_and_clear()
    {
        using var temp = new TempDir();
        var store = new SavedPasswordStore(temp.Combine("passwords.json"));
        store.Save("a.zip", "one");
        store.Save("b.zip", "two");

        Assert.True(store.Remove(store.List()[0].Id));
        Assert.Equal(["a.zip"], store.List().Select(p => p.Label));
        Assert.True(store.Clear());
        Assert.Empty(store.List());
        Assert.False(File.Exists(temp.Combine("passwords.json")));
    }

    [Fact]
    public void Missing_file_is_empty_without_a_problem()
    {
        using var temp = new TempDir();
        var store = new SavedPasswordStore(temp.Combine("nothing", "passwords.json"));
        Assert.Empty(store.List());
        Assert.Null(store.Problem);
        Assert.Null(store.Reveal("nope"));
    }

    [Fact]
    public void One_damaged_entry_does_not_lose_the_others()
    {
        using var temp = new TempDir();
        var path = temp.Combine("passwords.json");
        var store = new SavedPasswordStore(path);
        store.Save("good.zip", Secret);

        // Append a broken entry (bad base64) and an entry missing its secret.
        var json = File.ReadAllText(path).Replace("\"entries\": [", "\"entries\": [ {\"id\":\"x\",\"label\":\"bad.zip\",\"secret\":\"%%%\"}, {\"id\":\"y\"},");
        File.WriteAllText(path, json);

        var list = store.List();
        Assert.Equal(["good.zip"], list.Select(p => p.Label));
        Assert.Contains("2 saved passwords could not be read", store.Problem);
        Assert.Equal(Secret, store.Reveal(list[0].Id));

        // Writing keeps the unreadable entries in the file untouched.
        store.Save("new.zip", "another");
        Assert.Equal(2, store.List().Count);
        Assert.Contains("bad.zip", File.ReadAllText(path));
        Assert.Contains("\"y\"", File.ReadAllText(path));
    }

    [Fact]
    public void Undecryptable_entry_reveals_null()
    {
        using var temp = new TempDir();
        var path = temp.Combine("passwords.json");
        var store = new SavedPasswordStore(path);
        store.Save("a.zip", Secret);
        var id = store.List()[0].Id;

        // Valid base64, but not DPAPI data for this user (e.g. copied from another account).
        var text = File.ReadAllText(path);
        var start = text.IndexOf("\"secret\": \"", StringComparison.Ordinal) + 11;
        var end = text.IndexOf('"', start);
        File.WriteAllText(path, text[..start] + Convert.ToBase64String(new byte[64]) + text[end..]);

        Assert.Single(store.List());
        Assert.Null(store.Reveal(id));
    }

    [Fact]
    public void Corrupted_file_is_set_aside_not_overwritten()
    {
        using var temp = new TempDir();
        var path = temp.Combine("passwords.json");
        File.WriteAllText(path, "{ not json");
        var store = new SavedPasswordStore(path);

        Assert.Empty(store.List());
        Assert.NotNull(store.Problem);

        Assert.True(store.Save("a.zip", Secret));
        Assert.Single(store.List());
        var aside = Assert.Single(Directory.GetFiles(temp.Path, "passwords.json.unreadable-*"));
        Assert.Equal("{ not json", File.ReadAllText(aside));

        Assert.True(store.Clear());
        Assert.Empty(Directory.GetFiles(temp.Path));
    }

    // ---------------------------------------------------------------- batch integration

    private static List<ArchiveJob> Jobs(params string[] fixtures) => fixtures.Select(f => new ArchiveJob(Fixtures.Get(f))).ToList();

    [Fact]
    public async Task Batch_uses_the_password_saved_for_that_archive_without_asking()
    {
        using var temp = new TempDir();
        var store = new SavedPasswordStore(temp.Combine("store", "passwords.json"));
        store.Save("encrypted.rar", Fixtures.Password);
        var dest = temp.Combine("out");
        Directory.CreateDirectory(dest);
        var prompts = 0;
        var jobs = Jobs("encrypted.rar", "sample.zip");

        await new BatchExtractService(savedPasswords: store)
            .RunAsync(jobs, dest, _ => { prompts++; return Task.FromResult<PasswordAnswer?>(null); }, CancellationToken.None);

        Assert.Equal([JobStatus.Completed, JobStatus.Completed], jobs.Select(j => j.Status));
        Assert.Equal(0, prompts);
        Fixtures.AssertSampleContent(Path.Combine(dest, "encrypted"));
    }

    [Fact]
    public async Task Saved_password_for_another_archive_name_is_not_tried()
    {
        using var temp = new TempDir();
        var store = new SavedPasswordStore(temp.Combine("store", "passwords.json"));
        store.Save("unrelated.zip", Fixtures.Password);
        var tried = new List<string?>();
        var service = new BatchExtractService((req, _, _) =>
        {
            tried.Add(req.Password);
            throw new ArchiveError(ArchiveErrorKind.PasswordRequired, "needs password");
        }, store);

        var jobs = new List<ArchiveJob> { new(temp.Combine("encrypted.rar")) };
        await service.RunAsync(jobs, temp.Path, _ => Task.FromResult<PasswordAnswer?>(null), CancellationToken.None);

        Assert.Equal([null], tried);
        Assert.Equal(JobStatus.Skipped, jobs[0].Status);
    }

    [Fact]
    public async Task Wrong_saved_password_falls_back_to_asking_and_new_password_is_saved_after_success()
    {
        using var temp = new TempDir();
        var store = new SavedPasswordStore(temp.Combine("store", "passwords.json"));
        store.Save("encrypted.7z", "stale");
        var dest = temp.Combine("out");
        Directory.CreateDirectory(dest);
        var requests = new List<PasswordRequest>();
        var answers = new Queue<PasswordAnswer?>([new("stale", Save: true), new(Fixtures.Password, Save: true)]);
        var jobs = Jobs("encrypted.7z");

        await new BatchExtractService(savedPasswords: store)
            .RunAsync(jobs, dest, r => { requests.Add(r); return Task.FromResult(answers.Dequeue()); }, CancellationToken.None);

        Assert.Equal(JobStatus.Completed, jobs[0].Status);
        Assert.Equal(BatchExtractService.SavedPasswordFailedMessage, requests[0].Reason);
        // Re-entering the failed password is refused without another extraction attempt.
        Assert.Equal(BatchExtractService.AlreadyTriedMessage, requests[1].Reason);
        Assert.Equal(2, requests.Count);
        var saved = store.FindFor("encrypted.7z").Select(p => store.Reveal(p.Id)).ToList();
        Assert.Contains(Fixtures.Password, saved);
    }

    [Fact]
    public async Task Password_is_not_saved_when_unchecked_or_when_it_fails()
    {
        using var temp = new TempDir();
        var store = new SavedPasswordStore(temp.Combine("store", "passwords.json"));
        var dest = temp.Combine("out");
        Directory.CreateDirectory(dest);

        // Correct but not ticked: used for this run only.
        await new BatchExtractService(savedPasswords: store).RunAsync(Jobs("encrypted.rar"), dest,
            _ => Task.FromResult<PasswordAnswer?>(new(Fixtures.Password)), CancellationToken.None);
        // Ticked but wrong every time: never saved.
        var jobs = Jobs("encrypted-zipcrypto.zip");
        await new BatchExtractService(savedPasswords: store).RunAsync(jobs, dest,
            r => Task.FromResult<PasswordAnswer?>(new($"wrong{r.Attempt}", Save: true)), CancellationToken.None);

        Assert.Equal(JobStatus.Failed, jobs[0].Status);
        Assert.Empty(store.List());
        Assert.False(File.Exists(temp.Combine("store", "passwords.json")));
    }

    private static int IndexOf(byte[] haystack, byte[] needle)
    {
        for (var i = 0; i <= haystack.Length - needle.Length; i++)
            if (haystack.AsSpan(i, needle.Length).SequenceEqual(needle))
                return i;
        return -1;
    }
}
