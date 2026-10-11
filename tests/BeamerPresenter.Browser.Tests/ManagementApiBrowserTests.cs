using BeamerPresenter.Application;
using BeamerPresenter.Domain;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;

namespace BeamerPresenter.Browser.Tests;

public sealed partial class PresenterBrowserTests
{
    [Fact]
    public async Task Developer_generates_key_and_swagger_executes_authenticated_news_request()
    {
        await using var context = await _browser!.NewContextAsync();
        var page = await context.NewPageAsync();
        await LoginManagementAsync(page);
        await page.Locator("a[href='/developer']").ClickAsync();
        await page.WaitForURLAsync($"{_baseAddress}/developer");
        await page.Locator("form[action='/developer/key/generate'] button").ClickAsync();
        var key = await page.Locator("#generated-api-key").InputValueAsync();
        Assert.Equal(64, key.Length);
        await page.GotoAsync($"{_baseAddress}/developer");
        Assert.DoesNotContain(key, await page.ContentAsync());
        await page.Locator("a[href='/swagger']").ClickAsync();
        await page.WaitForURLAsync($"{_baseAddress}/swagger/index.html");
        await page.GetByRole(AriaRole.Button, new() { Name = "Authorize", Exact = true }).First.ClickAsync();
        await page.Locator(".auth-container input").FillAsync(key);
        var authorize = page.Locator(".dialog-ux button.authorize");
        Assert.Equal("Apply credentials", await authorize.GetAttributeAsync("aria-label"));
        await authorize.ClickAsync();
        await page.Locator(".dialog-ux .btn-done").ClickAsync();
        var operation = page.Locator(".opblock-get").Filter(new() { Has = page.Locator(".opblock-summary-path[data-path='/api/v1/news']") });
        await operation.Locator(".opblock-summary").ClickAsync();
        await operation.GetByRole(AriaRole.Button, new() { Name = "Try it out", Exact = true }).ClickAsync();
        var response = await page.RunAndWaitForResponseAsync(
            () => operation.GetByRole(AriaRole.Button, new() { Name = "Execute", Exact = true }).ClickAsync(),
            response => response.Url == $"{_baseAddress}/api/v1/news");
        Assert.Equal(200, response.Status);
        Assert.Equal("[]", await response.TextAsync());
        Assert.Equal(key, await response.Request.HeaderValueAsync("X-API-Key"));
    }

    [Fact]
    public async Task News_editor_saves_changes_without_replacing_running_news()
    {
        var service = _application!.Services.GetRequiredService<INewsService>();
        var item = await service.AddAsync(new NewsItem { Title = "Original", Text = "Text", Mode = NewsMode.Ticker, Permanent = true });
        await using var context = await _browser!.NewContextAsync();
        var page = await context.NewPageAsync();
        await LoginManagementAsync(page);
        await page.Locator("a[href='/news']").ClickAsync();
        await page.Locator($"a[href='/news?edit={item.Id}']").ClickAsync();
        Assert.Equal("Original", await page.Locator("input[name='title']").InputValueAsync());
        await page.Locator("input[name='title']").FillAsync("Bearbeitet");
        await page.Locator("textarea[name='text']").FillAsync("Neue Nachricht");
        await page.GetByRole(AriaRole.Button, new() { Name = "Nur speichern", Exact = true }).ClickAsync();
        await page.WaitForURLAsync($"{_baseAddress}/news?news=updated");
        var saved = Assert.Single(await service.GetAllAsync());
        Assert.Equal(item.Id, saved.Id);
        Assert.Equal(item.CreatedUtc, saved.CreatedUtc);
        Assert.Equal("Bearbeitet", saved.Title);
        Assert.Equal("Neue Nachricht", saved.Text);
    }
}
