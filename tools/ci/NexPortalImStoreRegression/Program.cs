using System;
using System.IO;
using NexVerse.Server.Api;

string dir = Path.Combine(Path.GetTempPath(), "portal-im-regression-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(dir);
try
{
    string file = Path.Combine(dir, "inbox.sqlite");
    Guid alice = Guid.NewGuid(), bob = Guid.NewGuid();
    Guid first = Guid.NewGuid(), second = Guid.NewGuid();
    NexCitizenImStore store = new NexCitizenImStore(file, 30);
    Check(!store.IsEnabled(alice) && !store.IsEnabled(bob), "opt-in must be off by default");
    Check(!store.Append(first, alice, bob, "Alice", "do not store me", "portal"), "no recording without opt-in");
    Check(store.Read(alice, 0, 100).Count == 0, "no preconsent records");

    store.SetEnabled(alice, true);
    Check(store.IsEnabled(alice), "Alice opted in");
    Check(store.Append(first, alice, bob, "Alice", "test 1", "portal"), "opt-in sender saved");
    Check(store.Read(alice, 0, 100).Count == 1, "sender has one message");
    Check(store.Read(bob, 0, 100).Count == 0, "recipient did not opt in");
    Check(!store.Append(first, alice, bob, "Alice", "test 1", "portal"), "replayed event idempotent");

    store.SetEnabled(bob, true);
    Check(store.Append(second, bob, alice, "Bob", "reply", "viewer"), "viewer reply archived");
    var a = store.Read(alice, 0, 100);
    var b = store.Read(bob, 0, 100);
    Check(a.Count == 2 && b.Count == 1, "only opted-in owner entries");
    Check(a[1].source == "viewer" && a[1].message == "reply", "viewer reply visible");
    Check(store.Read(alice, a[0].seq, 100).Count == 1, "cursor incremental");
    Check(store.Read(alice, 0, 100, bob).Count == 2, "authorized peer filter");
    Check(store.Conversations(alice, 20).Count == 1, "one private conversation");

    NexCitizenImStore afterRestart = new NexCitizenImStore(file, 30);
    Check(afterRestart.IsEnabled(bob), "settings survive process restart");
    Check(afterRestart.Read(bob, 0, 100).Count == 1, "messages survive restart");

    store.SetEnabled(alice, false);
    Check(!store.IsEnabled(alice) && store.Read(alice, 0, 100).Count == 0,
        "opt-out purges all Alice messages");
    Check(store.Read(bob, 0, 100).Count == 1, "opt-out does not erase Bob's copy");
    store.DeleteHistory(bob);
    Check(store.Read(bob, 0, 100).Count == 0 && store.IsEnabled(bob),
        "separate delete-history leaves Bob's opt-in setting intact");
    Console.WriteLine("Portal IM SQLite store runtime regression: OK");
}
finally
{
    try { Directory.Delete(dir, true); } catch (IOException) {}
}

static void Check(bool good, string description)
{
    if (!good) throw new InvalidOperationException(description);
}
