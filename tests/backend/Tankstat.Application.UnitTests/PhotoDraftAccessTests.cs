using Tankstat.Domain.Photos;

namespace Tankstat.Application.UnitTests;

/// <summary>Whose uploads the current user may see (#313): a draft is its uploader's alone until it expires, a log photo's reading its uploader's.</summary>
public class PhotoDraftAccessTests
{
    [Fact]
    public async Task ADraft_IsTheUploadersAlone_UntilItExpires()
    {
        var w = new World();
        var alice = w.AddUser("alice@x.co");
        var bob = w.AddUser("bob@x.co");
        var vehicleId = Guid.NewGuid();
        var alices = PhotoDraft.Create(Guid.NewGuid(), alice.Id, vehicleId, alice.Id, w.Clock.GetUtcNow());
        var bobs = PhotoDraft.Create(Guid.NewGuid(), alice.Id, vehicleId, bob.Id, w.Clock.GetUtcNow());
        w.PhotoDrafts.Items.AddRange([alices, bobs]);
        w.Current.SignInAs(alice);

        Assert.True(await w.DraftAccess.IsMineAsync(alices, default));
        Assert.False(await w.DraftAccess.IsMineAsync(bobs, default));
        Assert.Same(alices, await w.DraftAccess.MineAsync(alices.Id, default));
        Assert.Null(await w.DraftAccess.MineAsync(bobs.Id, default));
        Assert.Null(await w.DraftAccess.MineAsync(Guid.NewGuid(), default));
        Assert.Equal([alices.Id], (await w.DraftAccess.MineAsync([alices.Id, bobs.Id, Guid.NewGuid()], default)).Select(d => d.Id));

        w.Clock.Advance(PhotoDraft.Lifetime);
        Assert.False(await w.DraftAccess.IsMineAsync(alices, default));
        Assert.Null(await w.DraftAccess.MineAsync(alices.Id, default));
        Assert.Empty(await w.DraftAccess.MineAsync([alices.Id], default));
    }

    [Fact]
    public async Task ALogPhoto_IsItsUploaders()
    {
        var w = new World();
        var alice = w.AddUser("alice@x.co");
        var bob = w.AddUser("bob@x.co");
        var photo = LogPhoto.Create(alice.Id, Guid.NewGuid(), LogType.Expense, Guid.NewGuid(), Guid.NewGuid(), bob.Id, w.Clock.GetUtcNow());

        w.Current.SignInAs(bob);
        Assert.True(await w.DraftAccess.IsUploaderAsync(photo, default));
        w.Current.SignInAs(alice);
        Assert.False(await w.DraftAccess.IsUploaderAsync(photo, default));
    }
}
