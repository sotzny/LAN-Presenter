using BeamerPresenter.Application;
using BeamerPresenter.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace BeamerPresenter.Infrastructure.Tests;

public sealed class ApiKeyServiceTests
{
    [Fact]
    public async Task Api_key_survives_restart_is_hashed_and_is_not_overwritten_by_stale_desktop_settings()
    {
        var directory = Path.Combine(Path.GetTempPath(), "BeamerPresenter.ApiKeyTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        const string key = "test-api-key-at-least-32-characters-long";
        try
        {
            PresenterDatabase.GetConfiguredWebPort(directory);
            await using (var provider = new ServiceCollection().AddPresenterInfrastructure(directory).BuildServiceProvider())
            {
                var keys = provider.GetRequiredService<IApiKeyService>();
                var settings = provider.GetRequiredService<IPresenterSettingsService>();
                Assert.False(await keys.HasKeyAsync());
                Assert.False(await keys.VerifyAsync(key));
                var stale = await settings.GetAsync();
                await keys.SetKeyAsync(key);
                var first = await settings.GetAsync();
                Assert.NotEqual(key, first.ApiKeyHash);
                Assert.NotNull(first.ApiKeySalt);
                await keys.SetKeyAsync(key);
                var second = await settings.GetAsync();
                Assert.NotEqual(first.ApiKeySalt, second.ApiKeySalt);
                Assert.NotEqual(first.ApiKeyHash, second.ApiKeyHash);
                stale.WebPort = 9876;
                await settings.SaveAsync(stale);
                Assert.True(await keys.VerifyAsync(key));
                Assert.False(await keys.VerifyAsync("wrong"));
                Assert.False(await keys.VerifyAsync(new string('x', 513)));
                Assert.False(await keys.VerifyAsync(""));
            }
            await using (var provider = new ServiceCollection().AddPresenterInfrastructure(directory).BuildServiceProvider())
            {
                var keys = provider.GetRequiredService<IApiKeyService>();
                Assert.True(await keys.VerifyAsync(key));
                await keys.DisableAsync();
                Assert.False(await keys.HasKeyAsync());
                Assert.False(await keys.VerifyAsync(key));
            }
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(directory, recursive: true); }
    }

    [Theory]
    [InlineData("")]
    [InlineData("short")]
    [InlineData("test-api-key-with-space-in-the- middle")]
    [InlineData("test-api-key-with-control-character\n")]
    public async Task Invalid_keys_are_rejected_without_enabling_api(string key)
    {
        var directory = Path.Combine(Path.GetTempPath(), "BeamerPresenter.ApiKeyTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            PresenterDatabase.GetConfiguredWebPort(directory);
            await using var provider = new ServiceCollection().AddPresenterInfrastructure(directory).BuildServiceProvider();
            var keys = provider.GetRequiredService<IApiKeyService>();
            await Assert.ThrowsAsync<ArgumentException>(() => keys.SetKeyAsync(key));
            Assert.False(await keys.HasKeyAsync());
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(directory, recursive: true); }
    }
}
