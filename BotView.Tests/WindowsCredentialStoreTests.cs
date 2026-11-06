using BotView.Models;
using BotView.Services.Trading.Accounts;

namespace BotView.Tests;

/// <summary>Exercises real Windows storage with synthetic keys and unique, always-cleaned profile IDs.</summary>
[Trait("Category", "WindowsCredentialManager")]
public sealed class WindowsCredentialStoreTests
{
    /// <summary>Closing and recreating the provider retains both keys, including Unicode and embedded zeroes.</summary>
    [Fact]
    public void CredentialsSurviveProviderDisposalAndRemainIndependentOfReadCopies()
    {
        var profileId = Guid.NewGuid();
        using var cleanup = new WindowsCredentialStore();

        try
        {
            using (ICredentialStore writer = new WindowsCredentialStore())
            using (var credentials = new AccountCredentials("fake-key-ключ\0🔑", "fake-secret-секрет\0🔒"))
            {
                writer.Write(profileId, credentials);
            }

            using ICredentialStore reader = new WindowsCredentialStore();
            Assert.True(reader.Contains(profileId));

            using (var copy = reader.Read(profileId))
            {
                Assert.NotNull(copy);
                Assert.Equal("fake-key-ключ\0🔑", copy.ApiKey);
                Assert.Equal("fake-secret-секрет\0🔒", copy.Secret);
            }

            using var anotherCopy = reader.Read(profileId);
            Assert.NotNull(anotherCopy);
            Assert.Equal("fake-secret-секрет\0🔒", anotherCopy.Secret);
        }
        finally
        {
            cleanup.Delete(profileId);
        }
    }

    /// <summary>Replacement and deletion notify sessions and never modify another profile's keys.</summary>
    [Fact]
    public void ReplacingAndDeletingKeysPreservesOtherProfilesAndNotifiesSessions()
    {
        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();
        using var store = new WindowsCredentialStore();
        var changes = new List<Guid>();
        store.CredentialsChanged += changes.Add;

        try
        {
            using var original = new AccountCredentials("fake-original-key", "fake-original-secret");
            using var replacement = new AccountCredentials("fake-new-key", "fake-new-secret");
            store.Write(firstId, original);
            store.Write(secondId, original);
            using var oldCopy = store.Read(firstId);

            store.Write(firstId, replacement);
            using (var updated = store.Read(firstId))
            {
                Assert.NotNull(updated);
                Assert.Equal(replacement.ApiKey, updated.ApiKey);
                Assert.Equal(replacement.Secret, updated.Secret);
            }

            Assert.Equal(original.Secret, oldCopy!.Secret);
            store.Delete(firstId);
            store.Delete(firstId);
            Assert.Null(store.Read(firstId));
            Assert.False(store.Contains(firstId));

            using var unaffected = store.Read(secondId);
            Assert.Equal(original.Secret, unaffected!.Secret);
            Assert.Equal(new[] { firstId, secondId, firstId, firstId, firstId }, changes);
        }
        finally
        {
            store.Delete(firstId);
            store.Delete(secondId);
        }
    }

    /// <summary>An oversized replacement leaves previously saved keys intact and emits no change.</summary>
    [Fact]
    public void OversizedReplacementDoesNotLoseExistingCredentials()
    {
        var profileId = Guid.NewGuid();
        using var store = new WindowsCredentialStore();

        try
        {
            using var original = new AccountCredentials("fake-original-key", "fake-original-secret");
            store.Write(profileId, original);
            var notified = false;
            store.CredentialsChanged += _ => notified = true;

            using var oversized = new AccountCredentials("fake-key", new string('я', 2560));
            var error = Assert.Throws<ArgumentException>(() => store.Write(profileId, oversized));
            Assert.DoesNotContain(oversized.ApiKey, error.Message);
            Assert.False(notified);

            using var retained = store.Read(profileId);
            Assert.Equal(original.Secret, retained!.Secret);
        }
        finally
        {
            store.Delete(profileId);
        }
    }

    /// <summary>Disposed providers reject all operations while disposal itself remains idempotent.</summary>
    [Fact]
    public void DisposedStoreRejectsOperations()
    {
        using var store = new WindowsCredentialStore();
        store.Dispose();

        var profileId = Guid.NewGuid();
        using var credentials = new AccountCredentials("fake-key", "fake-secret");
        Assert.Throws<ObjectDisposedException>(() => store.Contains(profileId));
        Assert.Throws<ObjectDisposedException>(() => store.Read(profileId));
        Assert.Throws<ObjectDisposedException>(() => store.Write(profileId, credentials));
        Assert.Throws<ObjectDisposedException>(() => store.Delete(profileId));
    }
}
