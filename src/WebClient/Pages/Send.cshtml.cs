using System.Text.Json;
using System.Web;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace WebClient.Pages;

public class SendModel : PageModel
{
    private readonly IHttpClientFactory _httpClientFactory;

    public SendModel(IHttpClientFactory httpClientFactory) => _httpClientFactory = httpClientFactory;

    [BindProperty]
    public OutputModel Output { get; set; }

    public async Task<IActionResult> OnGetAsync(string payload)
    {
        using var client = _httpClientFactory.CreateClient();

        var greetingResponse = await client.GetAsync($"https://localhost:5003/send?payload={HttpUtility.UrlEncode(payload)}");

        Output = await JsonSerializer.DeserializeAsync<OutputModel>(
            await greetingResponse.Content.ReadAsStreamAsync(),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        HttpContext.Response.Headers.Add("TraceIdentifier", HttpContext.TraceIdentifier);

        return Page();
    }

    public record OutputModel(string Message);
}
